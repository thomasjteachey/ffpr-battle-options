using System;
using System.Collections.Generic;
using HarmonyLib;
using Last.Battle;
using Last.Data.Master;
using Last.Management;
using Last.Management.Audio;

namespace PrKit.Snes
{
    // A mod's SNES songs in place of the game's tracks, in game (any Pixel Remaster: Last.* is the shared
    // engine). The mod gives a song pack and its rules (SetMusic: SongPack + BgmRules, from its data or a
    // seed); this plays them on a SnesMusicPlayer as the game asks for its tracks:
    //
    //   SnesBgm.Tag = "[mymod music]";
    //   SnesBgm.On = () => myModActive;
    //   SnesBgm.Patch(harmony);                     // once
    //   SnesBgm.SetMusic(SongPack.Load(dir), BgmRules.Load(File.ReadAllText(Path.Combine(dir, "bgm.json"))));
    //   SnesBgm.Update();                           // every frame (a runner's Update)
    //   SnesBgm.RegisterHarness("mymusic");         // optional: a harness command
    //
    // Every BGM request still goes to the game's AudioManager (its own state queries keep answering); where
    // the rules give one of the pack's songs, the game's instance on that channel is muted and the player
    // plays the song in step: starts (with the game's fade-in), stops and fades, a battle song over the
    // field's (Suspend/Resume: the field song carries on where it was after the fight, with FF6's driver's
    // own fade-in), the battle's song per monster party (BattleController.PlayBGM's id rewritten: a party
    // that keeps the field song, or has its own where the game keeps the field's), a song's next part
    // (rules.Jumps), the game's pitch, the master and BGM volume settings, the audio driver's pauses. A
    // slot holding one of the game's own songs (rules.Songs[slot].GameTrack) has the game play that track.
    // A mod's new song has a PR id the game has no track for (convention: 1000 + its number, in map.csv's
    // bgm_id and scripts' PlayBGM) and a rules.Tracks entry: the game loads and plays the carrier for it
    // (rules.Carrier, else its first BGM), muted. A script's wait for a track's asset (ReadyBGM, LoadBGM) and the
    // game's own asset loads and checks by id follow that: they name the track the game loaded for the id (AssetId).
    public static class SnesBgm
    {
        public static string Tag = "[snes bgm]";
        public static string Name = "music";      // the harness state's first word(s): "<Name> on|off"
        public static Func<bool> On = () => true;
        public static SnesMusicPlayer Player { get; private set; }
        public static SongPack Pack { get; private set; }
        public static BgmRules Rules { get; private set; }
        static bool Active => Pack != null && Rules != null && (On?.Invoke() ?? true);

        // the channels' bookkeeping (game-free, BgmChannels) and the player behind it
        sealed class PlayerPort : BgmChannels.IPlayer
        {
            public void Play(int slot, float fadeIn) => Player?.Play(slot, fadeIn);
            public void Stop(float fadeOut) => Player?.Stop(fadeOut);
            public void Suspend(float fadeOut) => Player?.Suspend(fadeOut);
            public void Resume() => Player?.Resume();
            public void DropPaused() => Player?.DropPaused();
            public void Jump() => Player?.ConditionalJump();
        }
        static readonly BgmChannels chans = new BgmChannels { Player = new PlayerPort(), Note = Note };
        static float masterVol = 1f, bgmVol = 0.5f;
        static AudioManager am;
        static int pendingBattleSlot = int.MinValue;
        static int battleParty = -1;     // the last battle's monster party, for its victory fanfare (rules.Fanfares)
        static readonly Queue<string> recent = new Queue<string>();

        // The pack and rules to play (null, null: the game's own music again). The player starts on the
        // first call: silent in test runs (no sound card; it still renders at its real pace).
        public static void SetMusic(SongPack pack, BgmRules rules)
        {
            if (pack == null || rules == null)
            {
                Pack = null; Rules = null;
                Player?.SetPack(null);
                Unmute();
                Note("off: the game's own music");
                return;
            }
            Rules = rules;
            Pack = pack;
            chans.Rules = rules;
            if (Player == null)
            {
                Player = new SnesMusicPlayer(pack) { Volume = masterVol * bgmVol };
                bool ok = Player.Start(silent: TestRun.Running);
                Note(ok ? $"player started ({(Player.Silent ? "silent: a test run" : "waveOut 32 kHz")})" : $"no sound output: {Player.OutputError}");
            }
            else Player.SetPack(pack);
            Note($"{pack.Songs.Count} songs, {pack.SampleCount} samples, {rules.Tracks.Count} tracks, {rules.Maps.Count} areas, {rules.Battles.Count} battles");
        }

        // --- hooks ----------------------------------------------------------------------------------
        public static void Patch(Harmony h)
        {
            var t = typeof(AudioManager);
            var ctl = typeof(StaticChannelController);
            Hooks.Patch(h, t, "PlayOnStaticChannel", typeof(SnesBgm), nameof(Play_Prefix), nameof(Play_Postfix),
                new[] { typeof(AudioType), typeof(int), typeof(int), typeof(float), typeof(float), typeof(float), typeof(bool) });
            Hooks.Patch(h, t, "Stop", typeof(SnesBgm), nameof(StopCh_Prefix), null, new[] { ctl, typeof(int), typeof(float), typeof(bool) });
            Hooks.Patch(h, t, "Stop", typeof(SnesBgm), nameof(StopId_Prefix), null, new[] { typeof(int), typeof(float) });
            Hooks.Patch(h, t, "Suspend", typeof(SnesBgm), nameof(Suspend_Prefix), null, new[] { ctl, typeof(int), typeof(float) });
            Hooks.Patch(h, t, "Resume", typeof(SnesBgm), nameof(Resume_Prefix), nameof(Resume_Postfix), new[] { ctl, typeof(int), typeof(float) });
            Hooks.Patch(h, t, "Pitch", typeof(SnesBgm), nameof(Pitch_Prefix), null, new[] { ctl, typeof(int), typeof(float), typeof(float) });
            Hooks.Patch(h, t, "SetVolume", typeof(SnesBgm), nameof(SetVolume_Prefix), null,
                new[] { typeof(AudioManager.SeadCategory), typeof(float), typeof(float) });
            Hooks.Patch(h, t, "SuspendDriver", typeof(SnesBgm), nameof(SuspendDriver_Prefix));
            Hooks.Patch(h, t, "ResumeDriver", typeof(SnesBgm), nameof(ResumeDriver_Prefix));
            Hooks.Patch(h, typeof(BattleController), "PlayBGM", typeof(SnesBgm), nameof(BattleBgm_Prefix), null, new[] { typeof(int) });
            // a mod's new songs by ids the game has no track for: it loads and checks the carrier instead
            // (its own code dereferences the track's asset unchecked)
            Hooks.Patch(h, t, "RequestLoadBGMAsset", typeof(SnesBgm), nameof(LoadBgm_Prefix), null, new[] { typeof(int) });
            Hooks.Patch(h, t, "AssetIsReady", typeof(SnesBgm), nameof(AssetIsReady_Prefix), null, new[] { typeof(AudioType), typeof(int) });
            // the event scripts' BGM asset waits (ReadyBGM, LoadBGM): Audio.<>c's bool(int handle) lambdas
            PatchPoll(h, "_ReadyBGM_b__", nameof(ReadyPoll_Prefix), nameof(ReadyPoll_Postfix));
            PatchPoll(h, "_LoadBGM_b__", nameof(LoadPoll_Prefix), nameof(LoadPoll_Postfix));
        }

        // by name prefix: the lambdas' numbers are the compiler's (bool(int): the poll, not the request or the handle getter)
        static void PatchPoll(Harmony h, string name, string prefix, string postfix)
        {
            var c = typeof(Last.Interpreter.Instructions.Audio.__c);
            foreach (var m in AccessTools.GetDeclaredMethods(c))
            {
                var ps = m.GetParameters();
                if (m.Name.StartsWith(name) && m.ReturnType == typeof(bool) && ps.Length == 1 && ps[0].ParameterType == typeof(int))
                {
                    Hooks.Patch(h, m, typeof(SnesBgm), prefix, postfix);
                    return;
                }
            }
            Kit.Warn($"[patch] Audio.<>c: no {name}* poll; that script wait isn't covered");
        }

        // a track of the game's to stand behind songs whose own PR id has none (rules.Carrier, else the
        // game's first BGM)
        static int carrierCache = -1;
        static int CarrierId()
        {
            if (Rules != null && Rules.Carrier >= 0 && Master.Get<BgmAsset>(Rules.Carrier) != null) return Rules.Carrier;
            if (carrierCache < 0)
            {
                var all = Master.All<BgmAsset>();
                if (all != null) foreach (var kv in all) { carrierCache = kv.Key; break; }
            }
            return carrierCache < 0 ? 1 : carrierCache;
        }

        static bool NewSongId(int id) => Active && Master.Ready && id > 0 && Rules.Tracks.ContainsKey(id) && Master.Get<BgmAsset>(id) == null;

        // The track whose asset the game loads for a request of pr: the one it was last told to play for pr (the
        // game's own track for one of the mod's songs, the carrier behind a new song: chans.PlayedFor), else the
        // carrier for a new song's id, else pr. Every load of a BGM asset by id and every wait on one go through
        // this, so a load and its wait always name the same asset.
        static int AssetId(int pr) => chans.AssetFor(pr, NewSongId, CarrierId);

        public static void LoadBgm_Prefix(ref int __0)
        {
            try { if (Active) __0 = AssetId(__0); }
            catch (Exception e) { Hooks.WarnOnce("snes-bgm-load", e); }
        }

        public static void AssetIsReady_Prefix(AudioType __0, ref int __1)
        {
            try { if (__0 == AudioType.BGM && Active) __1 = AssetId(__1); }
            catch (Exception e) { Hooks.WarnOnce("snes-bgm-ready", e); }
        }

        // The event scripts' waits: ReadyBGM (after PlayBGM: FF6's New Game opening sc_e_0001, the opera's sc_e_1030)
        // and LoadBGM poll AssetInstance.Get(BGM, the script's id).isReady, which only the game's own loads make true.
        // Where the game loaded another track for that id, they poll that one (AssetId), as the game's own path polls
        // the track it loaded: else the wait never ends (a new song's id has no asset at all: Get throws).
        static int pollAsked = int.MinValue, pollFrom, pollLast;
        static bool pollReady, pollSlow;

        public static void ReadyPoll_Prefix(ref int __0, out int __state) => PollPrefix(ref __0, out __state);
        public static void LoadPoll_Prefix(ref int __0, out int __state) => PollPrefix(ref __0, out __state);
        public static void ReadyPoll_Postfix(int __0, int __state, bool __result) => PollNote("ReadyBGM", __state, __0, __result);
        public static void LoadPoll_Postfix(int __0, int __state, bool __result) => PollNote("LoadBGM", __state, __0, __result);

        static void PollPrefix(ref int id, out int asked)
        {
            asked = id;
            try { if (Active) id = AssetId(id); }
            catch (Exception e) { Hooks.WarnOnce("snes-bgm-poll", e); }
        }

        // a line when a wait ends ("ReadyBGM 1 ready"), and one if it's still waiting after 600 frames
        static void PollNote(string what, int asked, int id, bool ready)
        {
            try
            {
                if (!Active) return;
                int f = UnityEngine.Time.frameCount;
                if (asked != pollAsked || f > pollLast + 2) { pollAsked = asked; pollFrom = f; pollReady = pollSlow = false; }
                pollLast = f;
                string track = id != asked ? $" (the game's track {id})" : "";
                if (ready && !pollReady) { pollReady = true; Note($"{what} {asked} ready{track}{(f > pollFrom ? $" after {f - pollFrom} frames" : "")}"); }
                else if (!ready && !pollSlow && f - pollFrom >= 600) { pollSlow = true; Note($"{what} {asked}: still waiting for track {id}'s asset after {f - pollFrom} frames"); }
            }
            catch (Exception e) { Hooks.WarnOnce("snes-bgm-pollnote", e); }
        }

        static bool IsBgm(AudioManager m, StaticChannelController c) =>
            m != null && c != null && m.bgmChannel != null && m.bgmChannel.Pointer == c.Pointer;

        // the battle's own song (rules.Battles by monster party): keep the field's, or play one where the
        // game keeps the field's
        public static void BattleBgm_Prefix(ref int __0)
        {
            try
            {
                if (!Active) return;
                int party = BattlePlugManager.Instance()?.MonsterParty?.Id ?? 0;
                battleParty = party;
                if (!Rules.Battles.TryGetValue(party, out int slot)) return;
                if (slot < 0)
                {
                    if (__0 != 0) Note($"battle (party {party}): keeps the field's song (the game's was {__0})");
                    __0 = 0;
                    return;
                }
                pendingBattleSlot = slot;
                if (__0 == 0) __0 = FirstTrackOf(slot);
            }
            catch (Exception e) { Hooks.WarnOnce("snes-bgm-battle", e); }
        }

        static int FirstTrackOf(int slot)
        {
            foreach (var kv in Rules.Tracks) if (kv.Value == slot && Master.Get<BgmAsset>(kv.Key) != null) return kv.Key;
            return CarrierId();
        }

        public static bool Play_Prefix(AudioManager __instance, AudioType __0, ref int __1, int __2, float __3, ref int __result)
        {
            if (__0 != AudioType.BGM) return true;
            try
            {
                am = __instance;
                if (!Active || __2 < 0 || __2 > 1) { pendingBattleSlot = int.MinValue; return true; }
                int pr = __1;
                var ch = Rules.Resolve(pr, __2, MapScripts.MapId, __2 == 1 ? pendingBattleSlot : int.MinValue,
                                       chans.PlayingSlot(__2), s => Pack.Songs.ContainsKey(s), battleParty);
                pendingBattleSlot = int.MinValue;
                if (__2 == 0 && pr != Rules.FanfareTrack) battleParty = -1;   // back on the field: no battle's fanfare
                __1 = chans.Play(__2, pr, __3, ch);
                if (chans.Ours(__2) && Master.Ready && Master.Get<BgmAsset>(__1) == null)
                    __1 = CarrierId();                          // a new song: the carrier behind it, muted
                chans.Played(pr, __1);                          // what the script's ReadyBGM pr waits for (AssetId)
                return true;
            }
            catch (Exception e) { Hooks.WarnOnce("snes-bgm-play", e); return true; }
        }

        public static void Play_Postfix(AudioManager __instance, AudioType __0, int __2, int __result)
        {
            if (__0 != AudioType.BGM || __2 < 0 || __2 > 1) return;
            chans.Chans[__2].Serial = __result;
            if (chans.Ours(__2)) MuteNow(__instance, __2);
        }

        public static void StopCh_Prefix(AudioManager __instance, StaticChannelController __0, int __1, float __2)
        {
            try
            {
                if (!IsBgm(__instance, __0) || __1 < 0 || __1 > 1) return;
                chans.Stop(__1, __2);
            }
            catch (Exception e) { Hooks.WarnOnce("snes-bgm-stop", e); }
        }

        public static void StopId_Prefix(int __0, float __1) => chans.StopSerial(__0, __1);

        public static void Suspend_Prefix(AudioManager __instance, StaticChannelController __0, int __1, float __2)
        {
            try
            {
                if (!IsBgm(__instance, __0) || __1 < 0 || __1 > 1) return;
                chans.Suspend(__1, __2);
            }
            catch (Exception e) { Hooks.WarnOnce("snes-bgm-suspend", e); }
        }

        public static void Resume_Prefix(AudioManager __instance, StaticChannelController __0, int __1, float __2)
        {
            try
            {
                if (!IsBgm(__instance, __0) || __1 < 0 || __1 > 1) return;
                if (__1 == 0) battleParty = -1;                  // the field song back: no battle's fanfare
                chans.Resume(__1, __2);
            }
            catch (Exception e) { Hooks.WarnOnce("snes-bgm-resume", e); }
        }

        public static void Resume_Postfix(AudioManager __instance, StaticChannelController __0, int __1)
        {
            if (!IsBgm(__instance, __0) || __1 < 0 || __1 > 1) return;
            if (chans.Ours(__1)) MuteNow(__instance, __1);
        }

        // the game's pitch for a channel (1 = as written) -> the driver's pitch and tempo ratios
        public static void Pitch_Prefix(AudioManager __instance, StaticChannelController __0, int __1, float __2)
        {
            if (!IsBgm(__instance, __0) || __1 != chans.Active || Player == null) return;
            int r = __2 >= 1f ? Math.Min(127, (int)Math.Round((__2 - 1f) * 128)) : Math.Max(128, (int)Math.Round(__2 * 256)) & 0xff;
            Player.Pitch(r);
            Player.Tempo(r);
            Note($"ch{__1} pitch {__2:0.##} (ratio {r:X2})");
        }

        public static void SetVolume_Prefix(AudioManager.SeadCategory __0, float __1)
        {
            if (__0 == AudioManager.SeadCategory.Master) masterVol = __1;
            else if (__0 == AudioManager.SeadCategory.BGM) bgmVol = __1;
            else return;
            if (Player != null) Player.Volume = masterVol * bgmVol;
        }

        public static void SuspendDriver_Prefix() { if (Player != null) Player.Paused = true; }
        public static void ResumeDriver_Prefix() { if (Player != null) Player.Paused = false; }

        // --- the game's instances of songs the player plays: silent ---------------------------------
        static void MuteNow(AudioManager m, int ch)
        {
            try
            {
                var inst = m?.bgmChannel?.channels?[ch];
                if (inst == null || inst.serial != chans.Chans[ch].Serial) return;
                inst.SetVolume(0f, 0f);
            }
            catch (Exception e) { Hooks.WarnOnce("snes-bgm-mute", e); }
        }

        static void Unmute()
        {
            for (int i = 0; i < 2; i++)
                if (chans.Ours(i))
                {
                    try { am?.bgmChannel?.channels?[i]?.SetVolume(1f, 0.5f); } catch { }
                }
            chans.Reset();
        }

        static int frames;

        public static void Update()
        {
            if (am == null || ++frames % 10 != 0) return;
            for (int i = 0; i < 2; i++)
                if (chans.Ours(i)) MuteNow(am, i);    // the game's fades and resumes bring its volume back
        }

        // --- log and harness ------------------------------------------------------------------------
        static void Note(string m)
        {
            Kit.Info($"{Tag} {m}");
            lock (recent)
            {
                recent.Enqueue($"{UnityEngine.Time.frameCount}: {m}");
                while (recent.Count > 12) recent.Dequeue();
            }
        }

        public static string State()
        {
            var p = Player;
            string ch(int i) => chans.Describe(i);
            string song = p == null || p.CurrentSlot < 0 ? "none" : $"{p.CurrentSlot:X2} {Rules?.SongName(p.CurrentSlot)}";
            return $"{Name} {(Active ? "on" : "off")}; {ch(0)}; {ch(1)}; player {song}" +
                   (p == null ? "" : $" (paused {(p.PausedSlot >= 0 ? p.PausedSlot.ToString("X2") : "-")}{(p.Paused ? ", driver paused" : "")}); " +
                    $"voices {p.ActiveVoices}; level peak {p.RawPeak} rms {p.RawRms} (after volume {p.PeakLevel}/{p.RmsLevel}, volume {p.Volume:0.##}); " +
                    $"rendered {p.FramesRendered / (double)SnesMusicPlayer.Rate:0.0} s, load {p.Load * 100:0.00}% of a core, " +
                    $"late ticks {p.LateTicks}, underruns {p.Underruns}{(p.Silent ? ", silent (test run)" : "")}" +
                    (p.LastWarning != null ? $"; driver: {p.LastWarning}" : "")) +
                   $"; plays {chans.Plays}, the game's own tracks {chans.GameTracks}, jumps {chans.Jumps}";
        }

        public static string Log() { lock (recent) return string.Join(" | ", recent); }

        // name [play SLOT | stop [fade] | suspend | resume | jump | log | state]
        public static void RegisterHarness(string name) =>
            Harness.Register(name, Cmd, $"{name} [play SLOT | stop [fade] | suspend | resume | jump | vol V | log | state]: the mod's SNES music " +
                                        "(PrKit.Snes): each BGM channel, the player's song, levels before/after volume, CPU load, late ticks");

        static string Cmd(string[] a, int tick)
        {
            if (a.Length == 0 || a[0] == "state") return State();
            if (a[0] == "log") return Log();
            if (Player == null) return "error: no SNES music loaded";
            switch (a[0])
            {
                case "play":
                    if (a.Length < 2) return "error: play SLOT (hex)";
                    int slot = Convert.ToInt32(a[1], 16);
                    if (!Player.HasSong(slot)) return $"error: no song {slot:X2}";
                    Player.Play(slot);
                    return $"playing {slot:X2} {Rules?.SongName(slot)}";
                case "stop": Player.Stop(a.Length > 1 ? float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture) : 0); return "stopped";
                case "suspend": Player.Suspend(); return "suspended";
                case "resume": Player.Resume(); return "resumed";
                case "jump": Player.ConditionalJump(); return "conditional jump";
                case "vol":
                    // the game's own BGM volume setting, through its AudioManager (as the config screen sets it)
                    if (am == null || a.Length < 2) return "error: vol V (0..1), after the game has played a BGM";
                    float v = float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture);
                    am.SetVolume(AudioManager.SeadCategory.BGM, v, 0f);
                    return $"BGM volume {v:0.##}: the player's volume now {Player.Volume:0.##}";
            }
            return "error: [play SLOT | stop [fade] | suspend | resume | jump | vol V | log | state]";
        }
    }
}
