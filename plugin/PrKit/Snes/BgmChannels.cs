using System;
using System.Collections.Generic;

namespace PrKit.Snes
{
    // The game's two BGM channels (0 the field's, 1 a battle's and the like) as the SNES player follows
    // them: which holds one of the pack's songs, which is suspended under another, and what the player
    // must do for each of the game's requests. Game-free (Snes.Tests drives it with a fake player);
    // SnesBgm feeds it from its hooks.
    public sealed class BgmChannels
    {
        public interface IPlayer
        {
            void Play(int slot, float fadeIn);
            void Stop(float fadeOut);
            void Suspend(float fadeOut);       // keep the song where it is, silent
            void Resume();                     // the kept song again (the driver's own fade-in)
            void DropPaused();
            void Jump();                       // the song's conditional jump
        }

        public sealed class Chan { public int Pr = -1, Slot = -1, Serial; public bool Ours, Suspended, Restart; }

        public readonly Chan[] Chans = { new Chan(), new Chan() };
        public int Active = -1;      // the channel whose song the player renders
        public int Paused = -1;      // the channel whose song the player keeps suspended
        public IPlayer Player;
        public BgmRules Rules;
        public Action<string> Note = _ => { };
        public int Plays, GameTracks, Jumps;

        public bool Ours(int ch) => ch >= 0 && ch < 2 && Chans[ch].Ours;
        public int PlayingSlot(int ch) => Ours(ch) && Active == ch ? Chans[ch].Slot : -1;

        // The game's track for each of its requests: the PR id asked for -> the one it was told to play instead (the
        // game's own track for one of its songs, the carrier behind a new song). The interpreter's asset waits
        // (ReadyBGM, LoadBGM) poll the asked-for id's asset; the game loaded this one (SnesBgm.AssetId).
        readonly Dictionary<int, int> played = new Dictionary<int, int>();
        public void Played(int pr, int gameId) { if (gameId == pr) played.Remove(pr); else played[pr] = gameId; }
        public int PlayedFor(int pr) => played.TryGetValue(pr, out int id) ? id : pr;

        // the track whose asset the game loads for a request of pr: the one it was last told to play for it, else the
        // carrier for a new song's id (newSong: no track of its own, e.g. T-Edition's 1000 + slot), else pr
        public int AssetFor(int pr, Func<int, bool> newSong, Func<int> carrier)
        {
            int id = PlayedFor(pr);
            return id == pr && newSong != null && newSong(pr) ? carrier() : id;
        }

        // The game asks channel ch for its track pr (fade: its fade-in); choice: what the rules say.
        // Returns the PR id the game should play (the request, or the game's own track for one of its
        // songs). Ours(ch) afterwards: the game's instance on ch must be silent.
        public int Play(int ch, int pr, float fade, BgmRules.Choice choice)
        {
            var c = Chans[ch];
            switch (choice.Kind)
            {
                case BgmRules.Kind.Silent:
                    // nothing plays (a battle without a fanfare): ours, so the game's instance is muted
                    if (Active >= 0) { Player?.Stop(fade); Chans[Active].Ours = false; Active = -1; }
                    c.Pr = pr; c.Slot = -1; c.Ours = true; c.Suspended = false;
                    Note($"ch{ch} PR {pr}: silence ({choice.Why})");
                    return pr;
                case BgmRules.Kind.Jump:
                    Player?.Jump();
                    c.Pr = pr;
                    Jumps++;
                    Note($"ch{ch} PR {pr}: slot {choice.Slot:X2}'s next part (its conditional jump)");
                    return pr;
                case BgmRules.Kind.GameTrack:
                case BgmRules.Kind.Game:
                    if (Active == ch) { Player?.Stop(fade); Active = -1; }
                    if (Paused == ch) { Player?.DropPaused(); Paused = -1; }
                    c.Pr = pr; c.Slot = choice.Slot; c.Ours = false; c.Suspended = false;
                    if (choice.Kind == BgmRules.Kind.GameTrack)
                    {
                        GameTracks++;
                        Note($"ch{ch} PR {pr} -> slot {choice.Slot:X2} {Rules?.SongName(choice.Slot)}: the game's own track {choice.PrBgm} ({choice.Why})");
                        return choice.PrBgm;
                    }
                    Note($"ch{ch} PR {pr}: the game's own ({choice.Why})");
                    return pr;
            }
            // one of the pack's songs
            if (Active >= 0 && Active != ch) Chans[Active].Ours = false;    // the other channel's song gives way
            if (Paused == ch && c.Slot != choice.Slot) { Player?.DropPaused(); Paused = -1; }
            if (Paused == ch) Paused = -1;            // the same song: the driver resumes it where it was
            if (Paused >= 0 && Rules != null && Rules.PauseSongs.Count > 0 && !Rules.PauseSongs.Contains(choice.Slot))
            {
                // not one of the driver's pausing songs: the kept song is lost, and starts over afterwards
                Player?.DropPaused();
                Chans[Paused].Restart = true;
                Note($"ch{ch}: slot {choice.Slot:X2} doesn't keep ch{Paused}'s song (FF6's pause list): it starts over after");
                Paused = -1;
            }
            Player?.Play(choice.Slot, fade);
            Active = ch;
            c.Pr = pr; c.Slot = choice.Slot; c.Ours = true; c.Suspended = false; c.Restart = false;
            Plays++;
            string title = Rules != null && Rules.Songs.TryGetValue(choice.Slot, out var s) && s.Title != null ? $" \"{s.Title}\"" : "";
            Note($"ch{ch} PR {pr} ({choice.Why}) -> slot {choice.Slot:X2} {Rules?.SongName(choice.Slot)}{title}{(fade > 0 ? $", fade in {fade:0.##} s" : "")}");
            return pr;
        }

        public void Stop(int ch, float fade)
        {
            var c = Chans[ch];
            if (Active == ch) { Player?.Stop(fade); Active = -1; Note($"ch{ch} stop{(fade > 0 ? $", fade out {fade:0.##} s" : "")}"); }
            if (Paused == ch) { Player?.DropPaused(); Paused = -1; }
            c.Pr = -1; c.Slot = -1; c.Ours = false; c.Suspended = false; c.Restart = false;
        }

        public void StopSerial(int serial, float fade)
        {
            for (int i = 0; i < 2; i++)
                if (serial > 0 && Chans[i].Serial == serial) Stop(i, fade);
        }

        public void Suspend(int ch, float fade)
        {
            var c = Chans[ch];
            c.Suspended = true;
            if (Active != ch || !c.Ours) return;
            Player?.Suspend(fade);
            if (Paused >= 0 && Paused != ch) Chans[Paused].Ours = false;     // the driver keeps one song
            Paused = ch;
            Active = -1;
            Note($"ch{ch} suspend (slot {c.Slot:X2} kept){(fade > 0 ? $", fade out {fade:0.##} s" : "")}");
        }

        public void Resume(int ch, float fade)
        {
            var c = Chans[ch];
            if (!c.Suspended) return;
            c.Suspended = false;
            if (c.Ours && c.Restart)
            {
                if (Active >= 0 && Active != ch) { Player?.Stop(0); Chans[Active].Ours = false; }
                c.Restart = false;
                Player?.Play(c.Slot, 0);
                Active = ch;
                Note($"ch{ch} resume: slot {c.Slot:X2} starts over (it wasn't kept)");
                return;
            }
            if (Paused != ch || !c.Ours) return;
            if (Active >= 0) { Player?.Stop(0); Chans[Active].Ours = false; Active = -1; }
            Player?.Resume();
            Active = ch;
            Paused = -1;
            Note($"ch{ch} resume slot {c.Slot:X2} where it was (the driver's fade-in)");
        }

        // forget every song (the game's own music again); the caller unmutes the game's instances first
        public void Reset()
        {
            foreach (var c in Chans) { c.Pr = -1; c.Slot = -1; c.Ours = false; c.Suspended = false; c.Restart = false; }
            Active = Paused = -1;
            played.Clear();
        }

        public string Describe(int i) =>
            $"ch{i} PR {Chans[i].Pr} slot {(Chans[i].Slot >= 0 ? Chans[i].Slot.ToString("X2") : "-")}" +
            (Chans[i].Ours ? " ours" : Chans[i].Slot >= 0 ? " the game's" : "") + (Chans[i].Suspended ? " suspended" : "");
    }
}
