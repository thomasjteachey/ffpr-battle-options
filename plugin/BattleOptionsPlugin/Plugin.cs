using System;
using System.IO;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using PrKit;
using UnityEngine;

namespace BattleOptions
{
    // Battle Options: three battle settings in the game's own Config menu. An add-on: it installs
    // beside the unmodded game, Brave New World, Worlds Collide or HD-2D, and changes no game data.
    //  * Battle Mode gets a third choice, Pause: Wait, and every ATB gauge, cast time and status timer
    //    also stops while a command is being chosen or an action plays out (PauseAtb.cs)
    //  * Cast Bars: a character's ATB gauge drains through the wait between a chosen command and
    //    its action (CastBars.cs; Brave New World's Wait Gauge, made a setting for every game)
    //  * Enemy ATB: each enemy's ATB gauge under it (EnemyAtb.cs)
    // The rows: ConfigMenu.cs. The choices live in BepInEx/config/prmod.battleoptions.cfg
    // [Settings]; under Pause the game's own config keeps Wait, so removing the mod leaves Wait.
    // It names only the engine code all the Pixel Remasters share (Last.*) and finds each game's own
    // Config and ATB classes at runtime, so FINAL FANTASY IV and V PR can load it too (FF6 is the
    // one tested; FF1-III have no ATB).
    [BepInPlugin(Guid, "Battle Options", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "prmod.battleoptions";
        public const string Version = "0.2.0";
        internal static FeatureHost Features;

        public override void Load()
        {
            // the start's cost, for the log's [load] line (the Steam Deck's console phase was slow:
            // 2026-11-18, about 26 s of Battle Options alone)
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var laps = new System.Collections.Generic.List<string>();
            long lapAt = 0;
            void Lap(string name) { long now = clock.ElapsedMilliseconds; laps.Add($"{name} {now - lapAt}"); lapAt = now; }

            Kit.Init(Log, "Battle Options");
            // A batch-mode launch is a developer/test run. Mute immediately, before the request
            // file and feature host are processed, and keep it muted in the runner below. This
            // also makes a malformed or wrong request file fail silently instead of playing the
            // title music through a supposedly headless run.
            if (TestRun.Headless) TestRun.Mute();
            if (!Game.Require(PrGame.FF4, PrGame.FF5, PrGame.FF6)) return;   // the ATB games
            Settings.Bind(Config);
            Features = new FeatureHost(Config, new Harmony(Guid));
            Lap("config");
            // Test runs of its own only (BepInEx/battleoptions_test.request, tools/battle_options_test.py):
            // beside another kit mod (Brave New World), that mod's runs keep the kit's own request.
            try
            {
                var mine = TestRun.RequestPath("battleoptions_test");
                Features.TestRuns = File.Exists(mine);
                if (Features.TestRuns)
                {
                    File.Copy(mine, TestRun.RequestPath(TestRun.RequestName), true);
                    File.Delete(mine);
                }
            }
            catch (Exception e) { Kit.Warn($"[test] request: {e.Message}"); Features.TestRuns = false; }
            Features.Add(new ConfigMenu());
            Features.Add(new PauseAtb());
            Features.Add(new CastBars());
            Features.Add(new EnemyAtb());
            Features.ApplyAll();
            if (TestRun.Running) TestCommands.Register();
            Lap("hooks");
            AddComponent<BattleOptionsRunner>();
            Lap("runner");
            Kit.Info($"[settings] Pause {Settings.Pause.Value}, Cast Bars {Settings.CastBars.Value}, Enemy ATB {Settings.EnemyAtb.Value}");
            Kit.Info($"[load] {clock.Elapsed.TotalSeconds:0.00} s (ms: {string.Join(", ", laps)})");
        }
    }

    // Unity calls this every frame; it ticks the features. Every plugin needs its own runner class
    // with its own name: IL2CPP registers injected classes by name, two of one name would clash.
    public class BattleOptionsRunner : MonoBehaviour
    {
        public BattleOptionsRunner(IntPtr ptr) : base(ptr) { }
        void Update()
        {
            if (TestRun.Headless) TestRun.Mute();
            Plugin.Features?.Update();
        }
        void OnGUI() => Plugin.Features?.OnGUI();
    }
}
