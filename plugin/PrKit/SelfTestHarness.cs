using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace PrKit
{
    // The glue between a mod's [SelfTest] checks (SelfTests.cs) and the kit's test harness (Harness.cs), so a mod
    // adds two lines instead of writing its own (bc-seeds' list, 2026-11-21):
    //
    //   SelfTestHarness.Install(typeof(Plugin).Assembly, Log.LogInfo);   // once, with the harness (Harness.Start)
    //   SelfTestHarness.Update();                                         // every frame, from the mod's runner
    //
    // It registers the harness command `selftest SPEC` (names, a prefix `bc.data.*`, or `*`): it starts the checks and
    // answers when they're done ("selftest done: N passed, M failed [...]"), polled each frame; the harness gives up
    // after 120 s, so for longer runs ask again with `selftest` alone (a request: `until "selftest done" 900 repeat
    // selftest`). Default fixtures, kept if the mod set its own: "field" (the field map shown) and "battle" (a battle
    // on; the harness's `encount` starts one).
    public static class SelfTestHarness
    {
        static Assembly asm;
        static Action<string> log;

        // tests a mod makes at run time beside its [SelfTest] methods (BC: bc-battle's registered battle tests as
        // bc.battle.<name>), listed with them for `selftest SPEC`
        public static Func<IEnumerable<SelfTests.Entry>> Extra;

        // every test `selftest` can pick: the mod's [SelfTest] methods and Extra's, by name
        public static List<SelfTests.Entry> All()
        {
            var all = SelfTests.Discover(asm);
            if (Extra != null)
                all = all.Concat(Extra() ?? Enumerable.Empty<SelfTests.Entry>()).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
            return all;
        }

        public static void Install(Assembly mod, Action<string> logLine)
        {
            asm = mod;
            log = logLine ?? (_ => { });
            if (!SelfTests.Fixtures.ContainsKey("field"))
                SelfTests.Fixtures["field"] = t => new[] { t.Until(() => Harness.Scene() == "field", 120, "the field map") };
            if (!SelfTests.Fixtures.ContainsKey("battle"))
                SelfTests.Fixtures["battle"] = t => new[] { t.Until(() => Harness.Scene() == "battle", 120, "a battle (harness: encount)") };
            Harness.Register("selftest", Command,
                "selftest SPEC | selftest: run this mod's [SelfTest] checks (names, a prefix x.*, or *) and answer when done; alone: the last run's state");
        }

        // (args, tick): null = not yet (the harness asks again next frame)
        static string Command(string[] args, int tick)
        {
            if (tick == 0 && args.Length > 0)
            {
                if (SelfTests.Running) return "selftest: a run is going on (" + SelfTests.Summary() + ")";
                try
                {
                    var picked = SelfTests.Select(All(), string.Join(",", args));
                    if (picked.Count == 0) return "selftest: nothing matches " + string.Join(" ", args);
                    SelfTests.Start(picked, log);
                    log($"[selftest] running {string.Join(" ", picked.Select(p => p.Name))}");
                }
                catch (Exception e) { return "selftest error: " + e.Message; }
            }
            return SelfTests.Running ? null : SelfTests.Summary();
        }

        // every frame: the runner on the game's clock
        public static void Update()
        {
            if (!SelfTests.Running) return;
            try { SelfTests.Update(UnityEngine.Time.time); }
            catch (Exception e) { Hooks.WarnOnce("selftest", e); }
        }
    }
}
