using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace PrKit
{
    // Self-tests found by name (architecture roadmap item 4, 2026-11-20): a mod's in-game check is one static method
    // marked [SelfTest] in any file of its plugin, found by reflection over the plugin's own assembly, so a new check
    // never means editing a shared test file (WcTest.cs, BNW's probe). Game-free: the runner is driven by the plugin
    // (Update with its game clock) and PrKit.Tests runs it on a fake clock.
    //
    //   [SelfTest("atb.fill", Needs = "battle", Doc = "the SNES's fill rates")]
    //   static IEnumerable<TestStep> AtbFill(TestContext t)
    //   {
    //       yield return t.Until(() => Battle.Ready, 30, "a battle");   // polled each frame until true (or it fails)
    //       t.Check(Math.Abs(rate - want) < 0.01, $"fill {rate:0.000}/s");
    //       yield return t.Wait(2.0);                                   // game seconds
    //   }
    //
    // Start: a request's `selftest=atb.fill,turn_cycle` (or `*`, or a prefix `atb.*`): SelfTests.Start(assembly, spec,
    // log); then SelfTests.Update(now) each frame until SelfTests.Done. `Needs` names a fixture the mod registers
    // (SelfTests.Fixtures["battle"] = t => ...steps that get a battle going...), run before that test.
    // Lines: "<tag> PASS|FAIL <test>: <what>", and per test "[selftest] <test> pass|fail (<checks> checks, <failed> failed)".
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class SelfTestAttribute : Attribute
    {
        public SelfTestAttribute(string name) { Name = name; }
        public string Name { get; }
        public string Needs { get; set; } = "";
        public string Doc { get; set; } = "";
        public double Timeout { get; set; } = 300;   // game seconds for the whole test, its fixture included
    }

    // A step of a test: polled each frame until Done; Failure set when it gave up
    public sealed class TestStep
    {
        internal Func<double, bool> Poll;   // game time -> done
        internal string What;
        internal double Limit = double.PositiveInfinity;   // absolute game time to give up at
        internal double Start = double.NaN;
    }

    public sealed class TestContext
    {
        public string Name { get; }
        public int Checks { get; private set; }
        public int Failed { get; private set; }
        public double Now => now();
        internal readonly Action<string> Log;
        readonly Func<double> now;

        internal TestContext(string name, Action<string> log, Func<double> now) { Name = name; Log = log; this.now = now; }

        public bool Check(bool ok, string what)
        {
            Checks++;
            if (!ok) Failed++;
            Log($"{(ok ? "PASS" : "FAIL")} {Name}: {what}");
            return ok;
        }

        public void Note(string what) => Log($"note {Name}: {what}");

        // polled each frame until true; after `timeout` game seconds the test fails ("timed out: <what>") and stops
        public TestStep Until(Func<bool> cond, double timeout = 30, string what = null) =>
            new TestStep { Poll = _ => cond(), What = what ?? "a condition", Limit = timeout };

        // `seconds` of game time
        public TestStep Wait(double seconds) => new TestStep { Poll = null, What = $"wait {seconds:0.##} s", Limit = seconds };
    }

    public static class SelfTests
    {
        public sealed class Entry
        {
            public string Name;
            public SelfTestAttribute Info;
            public MethodInfo Method;
        }

        public sealed class Result
        {
            public string Name;
            public int Checks, Failed;
            public string Error;      // set when it stopped early (a timeout, an exception, no fixture)
            public bool Passed => Error == null && Failed == 0;
        }

        // a mod's fixtures by name ("battle", "field", "newgame", "seed"): steps that bring the game there
        public static readonly Dictionary<string, Func<TestContext, IEnumerable<TestStep>>> Fixtures =
            new Dictionary<string, Func<TestContext, IEnumerable<TestStep>>>(StringComparer.OrdinalIgnoreCase);

        public static string Tag = "[selftest]";

        // every [SelfTest] method of an assembly: static, taking a TestContext, returning IEnumerable<TestStep>
        public static List<Entry> Discover(Assembly asm)
        {
            var found = new List<Entry>();
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var t in types)
                foreach (var m in t.GetMethods(any))
                {
                    var a = m.GetCustomAttribute<SelfTestAttribute>();
                    if (a == null) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 1 || ps[0].ParameterType != typeof(TestContext) || !typeof(IEnumerable<TestStep>).IsAssignableFrom(m.ReturnType))
                        throw new InvalidOperationException($"[SelfTest(\"{a.Name}\")] {t.FullName}.{m.Name}: must be static IEnumerable<TestStep> (TestContext)");
                    if (found.Any(e => string.Equals(e.Name, a.Name, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException($"[SelfTest(\"{a.Name}\")] twice ({t.FullName}.{m.Name})");
                    found.Add(new Entry { Name = a.Name, Info = a, Method = m });
                }
            return found.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        }

        // "a,b" (names), "*" (all), "atb.*" (a prefix); an unknown name is an error, not silently skipped
        public static List<Entry> Select(IEnumerable<Entry> all, string spec)
        {
            var list = all.ToList();
            var picked = new List<Entry>();
            foreach (var raw in (spec ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var s = raw.Trim();
                var hits = s == "*" ? list
                    : s.EndsWith("*") ? list.Where(e => e.Name.StartsWith(s.TrimEnd('*'), StringComparison.OrdinalIgnoreCase)).ToList()
                    : list.Where(e => string.Equals(e.Name, s, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count == 0)
                    throw new ArgumentException($"no self-test '{s}' (have: {string.Join(", ", list.Select(e => e.Name))})");
                foreach (var e in hits)
                    if (!picked.Contains(e)) picked.Add(e);
            }
            return picked;
        }

        // ---- the runner: one test at a time, its fixture first, a step polled per Update ----
        static List<Entry> queue;
        static int index;
        static Action<string> log;
        static double clock;
        static TestContext ctx;
        static IEnumerator<TestStep> steps;
        static TestStep step;
        static double testLimit;
        public static readonly List<Result> Results = new List<Result>();
        public static bool Running => queue != null && index < queue.Count;
        public static bool Done => queue != null && index >= queue.Count;

        public static void Start(IEnumerable<Entry> tests, Action<string> logLine)
        {
            queue = tests.ToList();
            index = 0;
            log = logLine ?? (_ => { });
            Results.Clear();
            ctx = null;
            steps = null;
            step = null;
        }

        public static List<Entry> Start(Assembly asm, string spec, Action<string> logLine)
        {
            var picked = Select(Discover(asm), spec);
            Start(picked, logLine);
            return picked;
        }

        // one line for a harness reply or a log: "selftest done: 3 passed, 1 failed [bc.x (timed out: ...)]"
        public static string Summary()
        {
            if (queue == null) return "selftest: no run yet";
            if (Running) return $"selftest running: {Results.Count} of {queue.Count} done";
            var bad = Results.Where(r => !r.Passed).Select(r => $"{r.Name} ({r.Error ?? $"{r.Failed} of {r.Checks} failed"})").ToList();
            return $"selftest done: {Results.Count(r => r.Passed)} passed, {bad.Count} failed" +
                   (bad.Count > 0 ? " [" + string.Join("; ", bad) + "]" : "");
        }

        // each frame, with the game's clock (seconds); returns true while tests remain
        public static bool Update(double now)
        {
            clock = now;
            if (!Running) return false;
            for (int guard = 0; guard < 1000 && Running; guard++)
            {
                if (ctx == null && !Begin()) continue;
                if (clock > testLimit) { Stop($"timed out after {queue[index].Info.Timeout:0} s"); continue; }
                if (step != null)
                {
                    if (double.IsNaN(step.Start)) step.Start = clock;
                    bool done;
                    try { done = step.Poll == null ? clock - step.Start >= step.Limit : step.Poll(clock); }
                    catch (Exception e) { Stop($"{e.GetType().Name} in '{step.What}': {e.Message}"); continue; }
                    if (!done)
                    {
                        if (step.Poll != null && clock - step.Start > step.Limit) { Stop($"timed out: {step.What}"); continue; }
                        return true;   // the next frame
                    }
                    step = null;
                }
                bool more;
                try { more = steps.MoveNext(); }
                catch (Exception e) { Stop($"{e.GetType().Name}: {e.Message}"); continue; }
                if (!more) { Stop(null); continue; }
                step = steps.Current ?? new TestStep { Poll = _ => true, What = "(none)" };
                step.Start = double.NaN;
            }
            return Running;
        }

        static bool Begin()
        {
            var e = queue[index];
            ctx = new TestContext(e.Name, line => log($"{Tag} {line}"), () => clock);
            testLimit = clock + e.Info.Timeout;
            log($"{Tag} start {e.Name}{(e.Info.Needs != "" ? $" (needs {e.Info.Needs})" : "")}");
            IEnumerable<TestStep> body;
            try { body = (IEnumerable<TestStep>)e.Method.Invoke(null, new object[] { ctx }); }
            catch (TargetInvocationException x) { Stop($"{x.InnerException?.GetType().Name}: {x.InnerException?.Message}"); return false; }
            if (e.Info.Needs != "")
            {
                if (!Fixtures.TryGetValue(e.Info.Needs, out var fixture)) { Stop($"no fixture '{e.Info.Needs}' registered"); return false; }
                try { body = fixture(ctx).Concat(body); }
                catch (Exception x) { Stop($"fixture '{e.Info.Needs}': {x.GetType().Name}: {x.Message}"); return false; }
            }
            steps = body.GetEnumerator();
            step = null;
            return true;
        }

        static void Stop(string error)
        {
            var e = queue[index];
            var r = new Result { Name = e.Name, Checks = ctx?.Checks ?? 0, Failed = ctx?.Failed ?? 0, Error = error };
            Results.Add(r);
            if (error != null) log($"{Tag} FAIL {e.Name}: {error}");
            log($"{Tag} {e.Name} {(r.Passed ? "pass" : "fail")} ({r.Checks} checks, {r.Failed} failed{(error != null ? ", stopped" : "")})");
            try { steps?.Dispose(); } catch { }
            ctx = null;
            steps = null;
            step = null;
            index++;
        }
    }
}
