using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using Il2CppSystem.Input;
using Last.Battle;
using Last.Data.User;
using Last.Management;
using Last.Map;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PrKit
{
    // Play the game headless by commands: one line in, one line back, over 127.0.0.1:47811 (the
    // client is tools/game_harness.py). Started by a test run's request (harness=1; TestRun.cs),
    // so saves are blocked and the sound is off. Generalised from BNW's GameHarness (2026-11-07).
    //
    // Built in (any PR):
    //   ping | help | state | press <key> [hold frames] | wait <frames> | skipmsg on|off | newgame
    //   | slots | load [slot] | warp <map> [point] | script <name> | speed [x] | battle <party> | encount <party>
    //   | log | shot <file.png> [width [height]] | shotinfo | title | idle <minutes> | fast on|off | quit
    // A test run's game also skips the opening logos (SkipLogosInTestRun: batch mode only, never a player's game).
    // Clients: any number at once (each its own thread; their commands run one at a time on the main
    // thread, in arrival order), and a new one any time after another left: the test runner keeps one game
    // up across its batches ("warm", tools/test_batch.py, 2026-11-20). "idle <minutes>" makes a game with no
    // client and no command for that long quit by itself (the mod's own "quit"), and quit at once when
    // Wow.exe starts (the user plays: no idle test game in the way).
    // Keys: action cancel menu up down left right pageup pagedown switchleft switchright start select
    // shortcut. A press answers the game's own input checks (InputListener.GetKeyDown/GetKey/GetKeyUp,
    // AnyKey*) for that key: down on its first frame, held `hold` frames (3), up on the next; the
    // reply comes once it's released and 3 quiet frames passed.
    //
    // A mod adds its own:  Harness.Register("party", args => "...", "party: the party's levels");
    // a handler that returns null is called again every frame (with tick = 1, 2, ...) until it
    // answers or two minutes pass:  Harness.Register("untilbattle", (args, tick) => InBattle ? "in battle" : null, ...)
    // and adds to "state":  Harness.StateParts.Add(() => $"espers {n}");
    public static class Harness
    {
        public const int DefaultPort = 47811;
        public static bool Active { get; private set; }
        public static int Port { get; private set; } = DefaultPort;
        public static bool SkipMessages;   // a script's message box completes at once (skipmsg on)
        public static readonly List<Func<string>> StateParts = new List<Func<string>>();

        public delegate string Handler(string[] args, int tick);

        sealed class Call
        {
            public string Line;
            public string[] Args;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public string Reply;
            public int Tick;
            public Handler Handler;
            public float Since;
        }

        static readonly ConcurrentQueue<Call> calls = new ConcurrentQueue<Call>();
        static readonly Dictionary<string, (Handler Run, string Help)> commands =
            new Dictionary<string, (Handler, string)>(StringComparer.OrdinalIgnoreCase);
        static Call pending;
        static Thread thread;

        // the warm game's idle quit: connected clients, the last command or connection change (Environment.TickCount64)
        static int clients;
        static long lastActivity = Environment.TickCount64;
        public static float IdleQuitSeconds;   // > 0: quit after this long with no client and no command ("idle <minutes>")
        static long wowCheckAt;
        static bool idleQuitting;
        static void Touch() => Interlocked.Exchange(ref lastActivity, Environment.TickCount64);

        // the press under way: its key (by pointer), first frame, release frame, quiet-until frame
        static IntPtr pressKey = IntPtr.Zero;
        static int pressFrom, pressUntil, quietUntil, waitUntil;

        static readonly Dictionary<string, Func<Key>> Keys = new Dictionary<string, Func<Key>>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = () => Key.Action, ["cancel"] = () => Key.Cancel, ["menu"] = () => Key.Menu,
            ["up"] = () => Key.Up, ["down"] = () => Key.Down, ["left"] = () => Key.Left, ["right"] = () => Key.Right,
            ["pageup"] = () => Key.PageUp, ["pagedown"] = () => Key.PageDown, ["switchleft"] = () => Key.SwitchLeft,
            ["switchright"] = () => Key.SwitchRight, ["start"] = () => Key.Start, ["select"] = () => Key.Select,
            ["shortcut"] = () => Key.Shortcut,
        };

        public static void Register(string name, Func<string[], string> run, string help) =>
            commands[name] = ((args, tick) => tick == 0 ? run(args) ?? "" : "", help);

        public static void Register(string name, Handler run, string help) => commands[name] = (run, help);

        public static void Start(Harmony h, int port = DefaultPort)
        {
            if (Active) return;
            Active = true;
            Port = port;
            RegisterBuiltIns();
            int n = 0;
            foreach (var m in typeof(InputListener).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         .Where(m => m.ReturnType == typeof(bool)))
            {
                string name = m.Name switch
                {
                    "GetKeyDown" => nameof(KeyDown_Prefix), "GetKey" => nameof(Key_Prefix), "GetKeyUp" => nameof(KeyUp_Prefix),
                    "AnyKeyDown" => nameof(AnyDown_Prefix), "AnyKey" => nameof(Any_Prefix), "AnyKeyUp" => nameof(AnyUp_Prefix),
                    _ => null,
                };
                if (name == null) continue;
                try { h.Patch(m, prefix: new HarmonyMethod(typeof(Harness), name)); n++; }
                catch (Exception e) { Kit.Warn($"[harness] {m.Name} not hooked: {e.Message}"); }
            }
            HookMessages(h);
            HookBattleLog(h);
            SkipLogosInTestRun(h);
            HookSystemCalls(h);
            thread = new Thread(Listen) { IsBackground = true, Name = "prkit-harness" };
            thread.Start();
            Kit.Info($"[harness] listening on 127.0.0.1:{Port}; {n} input checks hooked");
        }

        // --- the network side (its own thread): a line in, the main thread's reply out ----------------

        static void Listen()
        {
            TcpListener listener;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, Port);
                listener.Start();
            }
            catch (Exception e) { Kit.Warn($"[harness] can't listen on {Port}: {e.Message}"); return; }
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception e)
                {
                    Kit.Info($"[harness] accept: {e.Message}");
                    Thread.Sleep(200);
                    continue;
                }
                // each client its own thread: a second client (the runner's next batch, a quick look with
                // game_harness.py) is served at once instead of waiting in the backlog behind the first
                new Thread(() => Serve(client)) { IsBackground = true, Name = "prkit-harness-client" }.Start();
            }
        }

        static void Serve(TcpClient client)
        {
            Interlocked.Increment(ref clients);
            Touch();
            try
            {
                using (client)
                {
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (line.Length == 0) continue;
                        var call = new Call { Line = line };
                        calls.Enqueue(call);
                        if (!call.Done.Wait(TimeSpan.FromSeconds(130))) call.Reply = "error: timed out";
                        writer.WriteLine((call.Reply ?? "").Replace("\n", " \\n "));
                    }
                }
            }
            catch (Exception e) { Kit.Info($"[harness] connection: {e.Message}"); }
            finally
            {
                Interlocked.Decrement(ref clients);
                Touch();
            }
        }

        // the warm game: no client and no command for IdleQuitSeconds -> the mod's own quit; Wow.exe -> at once
        static void IdleCheck()
        {
            if (IdleQuitSeconds <= 0 || idleQuitting || Volatile.Read(ref clients) > 0 || pending != null || !calls.IsEmpty) return;
            long now = Environment.TickCount64;
            string why = null;
            if (now - Interlocked.Read(ref lastActivity) > IdleQuitSeconds * 1000f) why = $"idle {IdleQuitSeconds / 60f:0.#} min with no client";
            else if (now >= wowCheckAt)
            {
                wowCheckAt = now + 10000;
                if (WowRunning()) why = "Wow.exe started (the user plays)";
            }
            if (why == null) return;
            idleQuitting = true;
            Kit.Info($"[harness] {why}: quitting");
            try
            {
                if (commands.TryGetValue("quit", out var q)) q.Run(new string[0], 0);
                else TestRun.Quit(why);
            }
            catch (Exception e)
            {
                Kit.Warn($"[harness] quit: {e.Message}");
                Application.Quit();
            }
        }

        static bool WowRunning()
        {
            try
            {
                var ps = System.Diagnostics.Process.GetProcessesByName("Wow");
                foreach (var p in ps) p.Dispose();
                return ps.Length > 0;
            }
            catch (Exception) { return false; }
        }

        // --- the main thread: one command at a time (TestRun.Update calls this) -----------------------

        public static void Update()
        {
            if (!Active) return;
            IdleCheck();
            int f = Time.frameCount;
            if (pending != null)
            {
                if (pending.Handler != null)
                {
                    Step(pending, f);
                    if (pending != null) return;
                }
                else
                {
                    if (f < quietUntil || f < waitUntil) return;
                    Answer(pending, pending.Line.StartsWith("wait", StringComparison.OrdinalIgnoreCase) ? $"waited (frame {f})" : $"pressed (frame {f})");
                    pending = null;
                    pressKey = IntPtr.Zero;
                }
            }
            while (pending == null && calls.TryDequeue(out var c))
            {
                c.Args = c.Line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                c.Since = Time.realtimeSinceStartup;
                var name = c.Args[0].ToLowerInvariant();
                if (name == "press" && BattleOpening())
                {
                    // a press in a battle's opening reaches the field too (BNW's dream-fight door runs pressed action while
                    // the battle opened and stalled; runs that only waited played): held until Play like other commands
                    c.Handler = (args, tick) =>
                    {
                        if (BattleOpening() && Time.realtimeSinceStartup - c.Since < 60f) return null;
                        string err = StartPress(c.Args, Time.frameCount);
                        if (err != null) return err;
                        c.Handler = null;   // from here the press runs as any other (Pump's press branch answers it)
                        return null;
                    };
                    pending = c;
                    Step(c, f);
                    continue;
                }
                if (name == "press" || name == "wait")
                {
                    string err = name == "press" ? StartPress(c.Args, f) : StartWait(c.Args, f);
                    if (err != null) Answer(c, err);
                    else pending = c;
                    continue;
                }
                if (!commands.TryGetValue(name, out var cmd))
                {
                    Answer(c, $"error: unknown command '{c.Args[0]}' (help lists them)");
                    continue;
                }
                c.Handler = SafeInOpening.Contains(name) ? cmd.Run : AfterOpening(c, cmd.Run);
                pending = c;
                Step(c, f);
            }
        }

        // A battle's opening (set-up phases and the Preemptive!/Back attack! messages) isn't a place for commands: in
        // two tests (2026-11-21, T-Edition's battle 72 and BNW's dream fight) commands run in it (shot, damage and steal
        // simulations, enemy HP) and by the preemptive message the field's map script held the script slot in place of
        // the battle's own, so the battle never left its opening. Runs that waited for Play went through. So a command
        // sent then waits for the battle's Play phase (60 s at most), except the read-only ones below.
        static readonly HashSet<string> SafeInOpening = new HashSet<string> { "ping", "help", "state", "log", "aistat", "quit" };

        static Handler AfterOpening(Call c, Handler run)
        {
            int startedAt = -1;
            return (args, tick) =>
            {
                if (startedAt < 0)
                {
                    if (BattleOpening() && Time.realtimeSinceStartup - c.Since < 60f) return null;
                    startedAt = tick;
                }
                return run(args, tick - startedAt);
            };
        }

        // a battle in its set-up phases or opening messages (the phases tools/test_batch.py waits out too)
        static bool BattleOpening()
        {
            if (!InBattle) return false;
            try
            {
                var phase = Object.FindObjectOfType<BattleController>()?.stateMachine?.Current.ToString() ?? "";
                return phase == "None" || phase == "CreateObj" || phase.StartsWith("Init", StringComparison.Ordinal) ||
                       phase.StartsWith("Load", StringComparison.Ordinal) || phase.StartsWith("Start", StringComparison.Ordinal) ||
                       phase.EndsWith("Mes", StringComparison.Ordinal);
            }
            catch (Exception) { return false; }
        }

        static void Step(Call c, int frame)
        {
            string reply;
            try { reply = c.Handler(c.Args.Skip(1).ToArray(), c.Tick++); }
            catch (Exception e) { reply = $"error: {e.GetType().Name}: {e.Message}"; }
            if (reply == null && Time.realtimeSinceStartup - c.Since > 120) reply = "error: no answer in 120 s";
            if (reply == null) return;
            Answer(c, reply);
            if (pending == c) pending = null;
        }

        static void Answer(Call c, string text)
        {
            c.Reply = text;
            Touch();
            c.Done.Set();
        }

        static string StartPress(string[] parts, int frame)
        {
            if (parts.Length < 2 || !Keys.TryGetValue(parts[1], out var key)) return $"error: keys are {string.Join(" ", Keys.Keys)}";
            int hold = parts.Length > 2 && int.TryParse(parts[2], out var hv) ? Math.Max(1, hv) : 3;
            pressKey = key().Pointer;
            pressFrom = frame + 1;
            pressUntil = pressFrom + hold;
            quietUntil = pressUntil + 3;
            waitUntil = 0;
            return null;
        }

        static string StartWait(string[] parts, int frame)
        {
            int n = parts.Length > 1 && int.TryParse(parts[1], out var wv) ? Math.Max(1, wv) : 30;
            waitUntil = frame + n;
            quietUntil = 0;
            pressKey = IntPtr.Zero;
            return null;
        }

        // --- built-in commands (each its own method: one a game lacks a type for fails alone) ---------

        static void RegisterBuiltIns()
        {
            void Add(string name, Func<string[], string> run, string help)
            {
                if (!commands.ContainsKey(name)) Register(name, run, help);   // a mod's own wins
            }
            void AddTick(string name, Handler run, string help)
            {
                if (!commands.ContainsKey(name)) Register(name, run, help);
            }
            Add("ping", _ => "pong", "ping: pong");
            Add("help", _ => "press <key> [hold] | wait <frames> | " + string.Join(" | ", commands.Values.Select(c => c.Help)), "help: this list");
            Add("state", _ => State(), "state: scene, map, battle, script");
            Add("skipmsg", a =>
            {
                SkipMessages = a.Length > 0 && a[0].Equals("on", StringComparison.OrdinalIgnoreCase);
                return $"messages {(SkipMessages ? "skipped" : "shown")}";
            }, "skipmsg on|off: scripts' message boxes complete at once");
            AddTick("newgame", NewGame, "newgame: New Game on the title screen, chosen again every 1.5 s until the title takes it " +
                    "(it ignores it during its intro); answers once the title window is gone (100 s at most)");
            AddTick("title", Title, "title: back to the title screen (MainGame.GotoTitle, the game's own way back; time scale 1); " +
                    "answers once the title window is up (60 s at most). Not in battle");
            Add("fast", Fast, "fast on|off: a test run's quick game: Active battles, the fastest battle speed and battle " +
                "messages, no frame-rate cap; off puts the earlier values back (nothing is saved: a test run blocks saves)");
            Add("idle", a =>
            {
                if (a.Length > 0 && float.TryParse(a[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) && m >= 0)
                    IdleQuitSeconds = m * 60f;
                Touch();
                return IdleQuitSeconds > 0 ? $"idle quit after {IdleQuitSeconds / 60f:0.#} min with no client (or when Wow.exe starts)" : "idle quit off";
            }, "idle <minutes>: quit after that many minutes with no client and no command, or when Wow.exe starts then (0: off)");
            Add("slots", _ => Slots(), "slots: the save slots (read only)");
            Add("load", Load, "load [slot|party] [goto]: the latest save, a slot, or the newest with 3+ in its party (at the title the title's load; goto: the in-game load)");
            AddTick("warp", Warp, "warp <map> [point] [nofade]: to a map's entrance, as ChangeMap does (nofade: EventGotoMap's skipFade), " +
                                  "once the field has settled (no script or map change on its way; 20 s at most)");
            Add("script", RunScript, "script <name>: an event script of the current map");
            Add("speed", a =>
            {
                if (a.Length > 0 && float.TryParse(a[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) && x > 0 && x <= 100)
                    Time.timeScale = x;
                return $"time scale {Time.timeScale}";
            }, "speed [x]: the game's time scale (Unity's Time.timeScale, 1 = normal; a long scene or the opening goes faster)");
            AddTick("battle", BattleTick, "battle <party>: a real battle with any monster party, as EncountBoss; answers once the " +
                "battle's own script (sc_b_...) runs, or an error if none does (the game would call that battle won at once)");
            Add("encount", Encount, "encount <party>: a random-encounter style battle");
            Add("log", _ => TakeLog(), "log: battle actions since the last log");
            Add("enemyhp", EnemyHp, "enemyhp <hp> [unique]: set the battle's enemies' HP (one by its unique id), so a real hit finishes them");
            Add("shot", Shot, "shot <file.png> [width [height]] [layers]: the game's picture to a PNG, drawn from its cameras " +
                "('layers': each off-screen layer too) (a windowless run with graphics: probe_headless.ps1 -Graphics)");
            Add("shotinfo", _ => ShotInfo(), "shotinfo: the screen, every camera (target, viewport), image showing a texture and root canvas (mode, camera): what shot draws");
            Add("quit", _ => { TestRun.Quit("harness: quit"); return "quitting"; }, "quit: end the run");
        }

        // newgame: the title's own New Game command (TitleWindowController.SetCommand, as WcTest and BNW's probe
        // choose it), asked again every 1.5 s while the title stays up (during its intro it's ignored: the test
        // runner used to wait a fixed 25 s first) and not while its state machine already says NewGame; the
        // reply comes once the title window is gone (2026-11-20: the runner's boot polls instead of sleeping)
        static int ngTries;
        static float ngStart, ngLast, ngNewSince = -1f;

        static string NewGame(string[] a, int tick)
        {
            var tw = Object.FindObjectOfType<Last.UI.KeyInput.TitleWindowController>();
            float now = Time.realtimeSinceStartup;
            if (tick == 0)
            {
                if (tw == null) return "error: no title window";
                ngTries = 0;
                ngStart = now;
                ngLast = -100f;
                ngNewSince = -1f;
            }
            if (tw == null) return $"new game chosen (the title took it: {ngTries} {(ngTries == 1 ? "try" : "tries")}, {now - ngStart:0.0} s)";
            string st = TitleState(tw);
            // The idle title's state is None throughout; a New Game asked in its first seconds (its intro) leaves the
            // state at NewGame with the title still up, ignored (live, 2026-11-20: "2 tries, 75.1 s", the second when
            // the attract demo reset the state ~74 s after the title). So again every 1.5 s, and also when it has said
            // NewGame for 4 s with the title still up. (tools/test_batch.py asks 12 s after the title appears.)
            if (st == "NewGame" && ngNewSince < 0) ngNewSince = now;
            if (st != "NewGame") ngNewSince = -1f;
            bool stuck = st == "NewGame" && now - ngNewSince >= 4f;
            if ((st != "NewGame" || stuck) && now - ngLast >= 1.5f)
            {
                var set = typeof(Last.UI.KeyInput.TitleWindowController).GetMethod("SetCommand",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (set == null) return "error: no TitleWindowController.SetCommand";
                var ps = set.GetParameters();
                set.Invoke(tw, new object[] { Last.Defaine.TitleCommandId.NewGame, Enum.ToObject(ps[1].ParameterType, 2), false, true });
                ngTries++;
                ngLast = now;
            }
            if (now - ngStart > 100f) return $"error: the title didn't take New Game ({ngTries} tries, title state {st})";
            return null;
        }

        // the title window's state (its StateMachine<State>.Current: Select, NewGame, ...), by reflection: the same
        // in every PR's interop, or "?"
        static string TitleState(Object tw)
        {
            try
            {
                var sm = tw?.GetType().GetProperty("stateMachine")?.GetValue(tw);
                return sm?.GetType().GetProperty("Current")?.GetValue(sm)?.ToString() ?? "?";
            }
            catch (Exception) { return "?"; }
        }

        // title: MainGame.GotoTitle (the game's own way back to the title), then wait for the title window. A warm
        // game's next request starts a fresh New Game from there instead of a whole new launch.
        // The field's own way is its sub-scene state GotoTitle (SubSceneManagerMainGame.State, through
        // MainGame.ChangeState), behind a fade-out; MainGame.GotoTitle() called alone did nothing in 60 s (live,
        // 2026-11-20). So: the screen faded out (as the title's Load window does, ModSaves.LoadAtTitle), the state
        // change, and GotoTitle() itself 15 s later if the title isn't up by then.
        static float titleAsked;
        static bool titleDirect;

        static string Title(string[] a, int tick)
        {
            float now = Time.realtimeSinceStartup;
            var tw = Object.FindObjectOfType<Last.UI.KeyInput.TitleWindowController>();
            var mg = Object.FindObjectOfType<MainGame>();
            if (tick == 0)
            {
                if (tw != null) return "title window up already";
                if (InBattle && !StaleBattle()) return "error: in battle (end it first)";
                if (mg == null) return "error: no MainGame";
                Time.timeScale = 1f;
                titleAsked = now;
                titleDirect = false;
                try { Il2CppSystem.Fade.FadeManager.Instance?.FadeOut(0.3f, new Color(0, 0, 0, 1)); }
                catch (Exception e) { Kit.Info($"[harness] title: fade: {e.Message}"); }
                var change = typeof(MainGame).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "ChangeState" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType.IsEnum);
                if (change != null)
                {
                    var ps = change.GetParameters();
                    var args = new object[ps.Length];
                    args[0] = Enum.Parse(ps[0].ParameterType, "GotoTitle");
                    for (int i = 1; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
                    try { change.Invoke(mg, args); }
                    catch (Exception e) { Kit.Info($"[harness] title: ChangeState(GotoTitle): {e.InnerException?.Message ?? e.Message}"); titleDirect = true; }
                }
                else titleDirect = true;
                if (!titleDirect) return null;
            }
            if (tw != null) return $"back at the title ({now - titleAsked:0.0} s{(titleDirect ? ", GotoTitle()" : "")})";
            if (!titleDirect && now - titleAsked > 15f || titleDirect && tick == 0)
            {
                titleDirect = true;
                var go = typeof(MainGame).GetMethod("GotoTitle", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (mg != null && go != null) go.Invoke(mg, null);
            }
            if (now - titleAsked > 60f) return "error: no title window 60 s after the field's GotoTitle";
            return null;
        }

        static string Slots()
        {
            var list = SaveSlotManager.Instance()?.SaveSlotDataList;
            if (list == null) return "error: no slot list";
            var outs = new List<string>();
            foreach (var sd in list)
            {
                if (sd == null || string.IsNullOrEmpty(sd.TimeStamp)) continue;
                var who = new List<string>();
                if (sd.PartyList != null) foreach (var pc in sd.PartyList) if (pc != null) who.Add($"{pc.Name} {pc.Parameter?.BaseLevel}");
                outs.Add($"{sd.Id}: {sd.TimeStamp} {sd.CurrentArea}/{sd.CurrentLocation} [{string.Join(", ", who)}]");
            }
            return outs.Count == 0 ? "no saves" : string.Join(" | ", outs);
        }

        static string Load(string[] a)
        {
            // read only: the run's saves are blocked
            // "load [slot|party] [goto]": a slot, the newest save with 3+ in its party, or the latest save;
            // at the title the title's own load (ModSaves.LoadAtTitle: its fade-out was the missing step that
            // left every headless title load stalled until 2026-11-20), on the field (or "goto") the in-game load
            var mgr = SaveSlotManager.Instance();
            if (mgr == null) return "error: no save manager";
            SaveSlotData slot = null;
            if (a.Length > 0 && int.TryParse(a[0], out var sid)) slot = mgr.GetTargetSaveSlotData(sid);
            else if (a.Length > 0 && a[0] == "party")
            {
                slot = ModSaves.NewestWithParty(3);
                if (slot == null) return "error: no save with 3+ in its party";
            }
            else slot = mgr.GetLatestSaveSlotData(false, false);
            if (slot == null) return "error: no such save";
            var tw = Object.FindObjectOfType<Last.UI.KeyInput.TitleWindowController>();
            if (tw == null || a.Contains("goto"))
            {
                mgr.GotoLoadSaveData(slot);   // on the field: the in-game load
                return $"loading save {slot.Id} ({slot.TimeStamp}) on the field";
            }
            var why = ModSaves.LoadAtTitle(slot);
            return why == null ? $"loading save {slot.Id} ({slot.TimeStamp}) from the title" : $"error: {why}";
        }

        // warp, polled: the game's own map change first. A warp sent while one is on its way (a script's ChangeMap after
        // its last line: BC's New Game opening, sc_e_0001's ChangeMap 3, warped at the hand-off) raced it, and the
        // game's change never finished (bc-10, 2026-11-21); runs that waited reached map 3. So the warp waits while a map
        // change is in flight (no battle, a field map, the sub-scene not ChangeMap / FieldReady / Init, for 0.1 s; see
        // FieldBusy: a running event doesn't wait); after 20 s it warps anyway and says what was still going on.
        static float warpAsked, warpSettled;

        static string Warp(string[] a, int tick)
        {
            int map = a.Length > 0 && int.TryParse(a[0], out var mv) ? mv : -1;
            int point = a.Length > 1 && int.TryParse(a[1], out var pv) ? pv : 1;
            // nofade: no fade out/in. A warp from the harness has no event around it to finish a fade; in a graphics run
            // (BNW's dream test, PC 2026-11-21) the field stayed without player control after one, and scripts were refused
            bool skipFade = a.Any(x => x.Equals("nofade", StringComparison.OrdinalIgnoreCase));
            float now = Time.realtimeSinceStartup;
            if (tick == 0) { warpAsked = now; warpSettled = -1; }
            var busy = FieldBusy();
            string note = "";
            if (busy != null)
            {
                warpSettled = -1;
                if (now - warpAsked < 20f) return null;
                note = $" (the field hadn't settled in 20 s: {busy})";
            }
            else
            {
                if (warpSettled < 0) warpSettled = now;
                if (now - warpSettled < 0.1f) return null;
            }
            var ep = Object.FindObjectOfType<MainGame>()?.eventProcedure;
            if (ep == null || map < 0) return "error: no event procedure or map";
            var ld = new LoadData();
            ld.NextMapData(map, point, (ViewType)0);
            ep.EventGotoMap(ld, skipFade, false);
            float waited = now - warpAsked;
            return $"warping to map {map} point {point}{(skipFade ? " (no fade)" : "")}" +
                   (note != "" ? note : waited > 0.4f ? $" (after {waited:0.0} s for the field to settle)" : "");
        }

        // why the field isn't ready for a harness warp (null: it is). A map's own script (sc_map_*) can stay alive the
        // whole time the player walks it (map 112's sc_map_20160: every state for 40 s, BNW's atb-configcheck,
        // 2026-11-21), so a script counts only when it isn't one; the main game's sub-scene state says the rest.
        static string FieldBusy()
        {
            // Only a map change in flight waits (the BC race). Waiting out a running event or message did harm: BNW's
            // warps sent in its opening then fired at the 20 s cap, mid-scene, and map 112's own script never let go
            // (QA, bnw-final, 2026-11-21), where a warp sent at once had always come up clean. So those warp at once.
            if (InBattle) return "a battle";
            if (Object.FindObjectOfType<global::FieldMap>() == null) return "no field map";
            var st = FieldState();
            if (st == SubSceneManagerMainGame.State.ChangeMap || st == SubSceneManagerMainGame.State.FieldReady ||
                st == SubSceneManagerMainGame.State.Init)
                return $"the field's state is {st}";
            return null;
        }

        // the main game's sub-scene (Player = the player has the field; ChangeMap, FieldReady, Event, Message ...), null
        // when there's none or it can't be read
        static SubSceneManagerMainGame.State? FieldState()
        {
            try
            {
                var mg = Object.FindObjectOfType<MainGame>();
                return mg == null ? (SubSceneManagerMainGame.State?)null : mg.CurrentState<SubSceneManagerMainGame.State>();
            }
            catch (Exception e) { Hooks.WarnOnce("harness-field-state", e); return null; }
        }

        static string RunScript(string[] a)
        {
            if (a.Length < 1) return "error: script <name>";
            var why = MapScripts.Start(a[0], cancelReserved: true);
            return why == null ? $"script {a[0]} started" : $"error: {why}";
        }

        static string Battle(string[] a)
        {
            int pid = a.Length > 0 && int.TryParse(a[0], out var bv) ? bv : -1;
            if (Master.Get<Last.Data.Master.MonsterParty>(pid) == null) return $"error: no party {pid}";
            if (Object.FindObjectOfType<FieldMap>()?.fieldController == null) return "error: not on the field";
            var integ = Last.Interpreter.Integrator.Instance;
            // a map's own script (sc_map_*) can live as long as the map (map 112's sc_map_20160): not an event in the way
            if (integ != null && integ.IsMainCoreAlive() && !(integ.scriptName ?? "").StartsWith("sc_map_", StringComparison.Ordinal))
                return $"error: a script runs ({integ.scriptName})";
            var mc = new Last.Interpreter.MainCore();
            var op = new Last.Interpreter.Instructions.Format.Operands();
            op.iValues = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(8);
            op.rValues = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<double>(8);
            op.sValues = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(8);
            for (int k = 0; k < 8; k++) op.sValues[k] = "";
            op.iValues[0] = pid;
            var mn = new Last.Interpreter.Instructions.Format.Mnemonic { mnemonic = "EncountBoss", operands = op };
            mc.currentInstruction = mn;
            Last.Interpreter.Instructions.External.Misc.EncountBoss(mc);
            return $"battle {pid} started";
        }

        // battle, polled: started as above, then the reply waits for the battle's own script. A battle whose script never
        // ran is judged won at once by the game (BattleEndJugment: a win whenever no battle script runs), with nothing
        // defeated. Seen 2026-11-20/21 (BNW boss 446 at speed 3; BC's battle 25 after a single warp; T-Edition's battle 72
        // stuck in its opening): `state` right after showed no "script sc_b_..." where good runs showed sc_b_none. Players'
        // battles start from the field's own encounters and events; this is the harness's way in, so it says so loudly
        // (an error a runner can retry from a settled field) instead of a fake win.
        static int btPid;
        static float btStart, btInBattle;

        static string BattleTick(string[] a, int tick)
        {
            float now = Time.realtimeSinceStartup;
            if (tick == 0)
            {
                var r = Battle(a);
                if (r.StartsWith("error")) return r;
                btPid = int.Parse(a[0]);
                btStart = now;
                btInBattle = -1f;
                return null;
            }
            var bm = BattlePlugManager.Instance();
            if (bm == null || !bm.IsBattle())
                return now - btStart > 30f ? $"error: battle {btPid} didn't start in 30 s" : null;
            if (btInBattle < 0f) btInBattle = now;
            var integ = Last.Interpreter.Integrator.Instance;
            string script = integ != null && integ.IsMainCoreAlive() ? integ.scriptName ?? "" : "";
            if (script.StartsWith("sc_b_", StringComparison.Ordinal))
                return $"battle {btPid} started (script {script})";
            if (now - btInBattle > 5f)
                return $"error: battle {btPid} started without its battle script (none like sc_b_ ran in 5 s; running: " +
                       $"{(script.Length > 0 ? script : "none")}): the game calls such a battle won at once; start it again from a settled field";
            return null;
        }

        // test runs: each script's system calls, once per script and call, so an ErrorFactorBase out of Misc.SystemCall
        // ("Invalid System Call": a fight started bare that its story setup should have prepared, BC's and BNW's Vargas
        // via `battle 436`, 2026-11-21) is named by the last line before it. A prefix only: the game's call runs as ever.
        static readonly HashSet<string> sysCallsSeen = new HashSet<string>();

        static void HookSystemCalls(Harmony h)
        {
            var m = AccessTools.Method(typeof(Last.Interpreter.Instructions.External.Misc), "SystemCall");
            if (m != null) Hooks.Patch(h, m, typeof(Harness), prefix: nameof(SystemCall_Prefix));
        }

        public static void SystemCall_Prefix(Last.Interpreter.MainCore __0)
        {
            try
            {
                var sv = __0?.currentInstruction?.operands?.sValues;
                string call = sv != null && sv.Length > 0 ? sv[0] : "?";
                var integ = Last.Interpreter.Integrator.Instance;
                string script = integ != null && integ.IsMainCoreAlive() ? integ.scriptName : "?";
                if (sysCallsSeen.Count < 2000 && sysCallsSeen.Add(script + "|" + call))
                    Kit.Info($"[harness] system call {call} in {script}");
            }
            catch (Exception) { }
        }

        static string Encount(string[] a)
        {
            int pid = a.Length > 0 && int.TryParse(a[0], out var pv) ? pv : -1;
            var fc = Object.FindObjectOfType<FieldMap>()?.fieldController;
            var party = Master.Get<Last.Data.Master.MonsterParty>(pid);
            if (fc == null) return "error: not on the field";
            if (party == null) return $"error: no party {pid}";
            fc.Last_Map_IMapEncounter_ExcuteEncount(party, party.BattleBackgroundAssetId);
            return $"encounter {pid} started";
        }

        // --- what the game shows ---------------------------------------------------------------------

        public static bool InBattle
        {
            get
            {
                var b = BattlePlugManager.Instance();
                return b != null && b.IsBattle();
            }
        }

        public static string Scene()
        {
            if (InBattle && !StaleBattle()) return "battle";
            if (Object.FindObjectOfType<FieldMap>()?.fieldController != null) return "field";
            if (Object.FindObjectOfType<Last.UI.KeyInput.TitleWindowController>() != null) return "title";
            return "other";
        }

        // --- test runs only: the opening logos ------------------------------------------------------------
        // ~13 s of a ~32 s start to the title (PORTING.md "WC startup time"): SceneSplash goes to the title when its
        // Update sees loadFinished, messageIsReady and animFinished; OnSplashAnimationFinished sets the last one and
        // asks for the title. Here it's called as soon as the game has loaded, and the logos' own later call dropped.
        // The user had the PLAYER-facing skip removed (2026-11-12: "remove the skip logo stuff"): this one runs only
        // in a headless test run (Application.isBatchMode: tools/probe_headless.ps1, kit.py run), never in a player's
        // game, and only from a test run's start (Harness.Start, the BNW probe's). One kit copy does it per process.
        static int splashStep;   // 0 waiting for the load, 1 asked for the title, 2 done

        public static void SkipLogosInTestRun(Harmony h)
        {
            if (!TestRun.Headless) return;
            if (AppDomain.CurrentDomain.GetData("prkit.testrun.logoskip") != null) return;
            AppDomain.CurrentDomain.SetData("prkit.testrun.logoskip", "on");
            var upd = AccessTools.Method(typeof(SceneSplash), "Update");
            var fin = AccessTools.Method(typeof(SceneSplash), "OnSplashAnimationFinished");
            if (upd == null || fin == null) { Kit.Info("[harness] no SceneSplash.Update: the logos play"); return; }
            try
            {
                h.Patch(upd, prefix: new HarmonyMethod(typeof(Harness), nameof(SplashUpdate_Prefix)));
                h.Patch(fin, prefix: new HarmonyMethod(typeof(Harness), nameof(SplashFinished_Prefix)));
                Kit.Info("[harness] test run: the opening logos will be skipped");
            }
            catch (Exception e) { Kit.Warn($"[harness] logo skip not hooked: {e.Message}"); }
        }

        public static void SplashUpdate_Prefix(SceneSplash __instance)
        {
            if (splashStep != 0 || __instance == null) return;
            try
            {
                if (!__instance.loadFinished || !__instance.messageIsReady || __instance.animFinished) return;
                splashStep = 1;
                __instance.OnSplashAnimationFinished();
                splashStep = 2;
                Kit.Info($"[harness] test run: the opening logos skipped ({Time.realtimeSinceStartup:0.0} s after the start)");
            }
            catch (Exception e) { splashStep = 2; Kit.Warn($"[harness] logo skip: {e.Message}"); }
        }

        // the logos' own call after ours (a second title request) is dropped; ours (step 1) goes through
        public static bool SplashFinished_Prefix() => splashStep != 2;

        // --- fast on|off: a test run's quick game ---------------------------------------------------------
        // Active battle mode (Wait stops time in open menus), the battle speed whose factor is largest
        // (BattleUtility.GetBattleSpeedByConfigValue), the battle-message speed whose time is shortest
        // (Message.battleMessageSpeedTable), and no frame cap (targetFrameRate -1, vSync 0). The config is never saved
        // in a test run (TestRun.BlockSaves); "off" puts the values from before "on" back.
        static int[] fastSaved;

        static string Fast(string[] a)
        {
            bool on = a.Length == 0 || !a[0].Equals("off", StringComparison.OrdinalIgnoreCase);
            var cfg = UserDataManager.Instance()?.Config;
            if (!on)
            {
                if (fastSaved == null) return "fast off already";
                if (cfg != null)
                {
                    cfg.BattleModeIndex = fastSaved[0];
                    cfg.BattleSpeed = fastSaved[1];
                    cfg.BattleMessageSpeed = fastSaved[2];
                }
                Application.targetFrameRate = fastSaved[3];
                QualitySettings.vSyncCount = fastSaved[4];
                fastSaved = null;
                return "fast off: battle mode, battle speed, battle messages and frame rate as before";
            }
            if (cfg == null) return "error: no config yet (at the title or later)";
            if (fastSaved == null)
                fastSaved = new[] { cfg.BattleModeIndex, cfg.BattleSpeed, cfg.BattleMessageSpeed, Application.targetFrameRate, QualitySettings.vSyncCount };
            var notes = new List<string>();
            cfg.BattleModeIndex = 0;
            int bestSpeed = cfg.BattleSpeed;
            float bestFactor = float.MinValue;
            var factors = new List<string>();
            for (int i = 0; i < 10; i++)
            {
                float f;
                try { f = BattleUtility.GetBattleSpeedByConfigValue(i); }
                catch (Exception) { break; }
                factors.Add($"{i}:{f:0.###}");
                if (f > bestFactor) { bestFactor = f; bestSpeed = i; }
            }
            cfg.BattleSpeed = bestSpeed;
            notes.Add($"battle speed {fastSaved[1]} -> {bestSpeed} (factors {string.Join(" ", factors)})");
            try
            {
                var table = Last.Interpreter.Instructions.Message.battleMessageSpeedTable;
                int bestMsg = cfg.BattleMessageSpeed;
                float bestTime = float.MaxValue;
                var times = new List<string>();
                for (int i = 0; i < 10; i++)
                {
                    if (table == null || !table.ContainsKey(i)) continue;
                    float t = table[i];
                    times.Add($"{i}:{t:0.###}");
                    if (t < bestTime) { bestTime = t; bestMsg = i; }
                }
                if (times.Count > 0) cfg.BattleMessageSpeed = bestMsg;
                notes.Add($"battle messages {fastSaved[2]} -> {cfg.BattleMessageSpeed} (times {(times.Count > 0 ? string.Join(" ", times) : "not loaded yet")})");
            }
            catch (Exception e) { notes.Add($"battle messages: {e.GetType().Name}"); }
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
            notes.Add($"frame rate cap {fastSaved[3]} -> none, vSync {fastSaved[4]} -> 0");
            return "fast on: Active battles; " + string.Join("; ", notes);
        }

        // the battle flag left up with no battle: no units and no battle phase (live, 2026-11-20: the title's attract
        // demo went back to the title mid-battle, and "scene battle" stayed through the next New Game's field)
        static bool StaleBattle()
        {
            try
            {
                var b = BattlePlugManager.Instance();
                if ((b?.GetPlayerUnits()?.Length ?? 0) > 0 || (b?.GetEnemyUnits()?.Length ?? 0) > 0) return false;
                var phase = Object.FindObjectOfType<BattleController>()?.stateMachine?.Current.ToString();
                return phase == null || phase == "None";
            }
            catch (Exception) { return false; }
        }

        // --- shot: the game's picture as a PNG -----------------------------------------------------
        // A windowless run (-batchmode with a graphics device: probe_headless.ps1 -Graphics) has no
        // screen, and Unity draws no camera on its own there, so the frame is drawn here the way the
        // game draws it (FrontRenderTarget; the HD-2D plugin's notes):
        //   1. the off-screen cameras in depth order into their layers: the field (CameraTileMap, then
        //      CameraFieldMain) into 'Field', the world map into 'BirdView', battles into 'Battle', the
        //      UI into 'Overlay'. Each layer is cleared first (a camera that only clears depth, like the
        //      fades, would otherwise pile up), and a UI camera the game renders by hand (disabled, but
        //      an active canvas draws through it: CameraUI) is drawn too;
        //   2. the screen cameras into one texture: CameraFrontFilter draws the game's composite
        //      (_MainGameTex + _OverlayTex) and the frame around it;
        //   3. canvases drawn straight to the screen, on top, through a camera of our own that sees
        //      only their layers (far from the map, so no world object gets in);
        //   4. scaled to the size asked for (a width alone keeps the screen's shape), then the PNG.
        // 'layers' also writes each layer as <name>_layer<i>_<layer>.png (with its alpha).
        // The user, 2026-11-20: "use the PC to take screenshots" (of BNW's dialogue; never the desktop),
        // then "finish the screenshot command; we'll need that for later".
        static string Shot(string[] a)
        {
            if (a.Length < 1) return "error: shot <file.png> [width [height]] [layers]";
            string path = a[0];
            bool layers = a.Skip(1).Any(x => x.Equals("layers", StringComparison.OrdinalIgnoreCase));
            int sw = Math.Max(16, Screen.width), sh = Math.Max(16, Screen.height);
            var rt = new RenderTexture(sw, sh, 24);
            var restore = new List<(Canvas canvas, RenderMode mode, Camera cam, float plane)>();
            GameObject temp = null;
            try
            {
                // cameras the active canvases draw through (a disabled one among them is rendered by hand)
                var canvasCams = new HashSet<IntPtr>();
                foreach (var cv in Object.FindObjectsOfType<Canvas>())
                    if (cv != null && cv.isRootCanvas && cv.enabled && cv.gameObject.activeInHierarchy && cv.worldCamera != null)
                        canvasCams.Add(cv.worldCamera.Pointer);
                var cams = new List<Camera>();
                foreach (var c in Resources.FindObjectsOfTypeAll<Camera>())
                {
                    if (c == null || !c.gameObject.scene.IsValid() || !c.gameObject.activeInHierarchy) continue;
                    if (c.enabled || (c.targetTexture != null && canvasCams.Contains(c.Pointer))) cams.Add(c);
                }
                cams.Sort((x, y) => x.depth.CompareTo(y.depth));
                // 1. the layers
                var layerRts = new List<RenderTexture>();
                int offCams = 0;
                foreach (var c in cams)
                {
                    var t = c.targetTexture;
                    if (t == null) continue;
                    if (!layerRts.Any(l => l.Pointer == t.Pointer))
                    {
                        layerRts.Add(t);
                        Clear(t);
                    }
                    c.Render();
                    offCams++;
                }
                // 2. the screen cameras
                bool first = true;
                int screenCams = 0;
                foreach (var c in cams)
                {
                    if (c.targetTexture != null || !c.enabled) continue;
                    var oldFlags = c.clearFlags;
                    if (!first && c.clearFlags == CameraClearFlags.Skybox) c.clearFlags = CameraClearFlags.Depth;
                    c.targetTexture = rt;
                    try { c.Render(); }
                    finally { c.targetTexture = null; c.clearFlags = oldFlags; }
                    first = false;
                    screenCams++;
                }
                // 3. canvases drawn straight to the screen
                var overlayCanvases = new List<Canvas>();
                foreach (var cv in Object.FindObjectsOfType<Canvas>())
                    if (cv != null && cv.isRootCanvas && cv.enabled && cv.gameObject.activeInHierarchy && cv.renderMode == RenderMode.ScreenSpaceOverlay)
                        overlayCanvases.Add(cv);
                if (overlayCanvases.Count > 0)
                {
                    temp = new GameObject("prkit-shot-ui");
                    temp.transform.position = new Vector3(50000f, 50000f, -50000f);
                    var ui = temp.AddComponent<Camera>();
                    ui.enabled = false;
                    ui.clearFlags = CameraClearFlags.Depth;
                    ui.orthographic = true;
                    ui.nearClipPlane = 0.01f;
                    ui.farClipPlane = 1000f;
                    int mask = 0;
                    foreach (var cv in overlayCanvases)
                        foreach (var tr in cv.GetComponentsInChildren<Transform>(true)) mask |= 1 << tr.gameObject.layer;
                    ui.cullingMask = mask;
                    overlayCanvases.Sort((x, y) => x.sortingOrder.CompareTo(y.sortingOrder));
                    foreach (var cv in overlayCanvases)
                    {
                        restore.Add((cv, cv.renderMode, cv.worldCamera, cv.planeDistance));
                        cv.renderMode = RenderMode.ScreenSpaceCamera;
                        cv.worldCamera = ui;
                        cv.planeDistance = 100f;
                    }
                    Canvas.ForceUpdateCanvases();
                    ui.targetTexture = rt;
                    ui.Render();
                    ui.targetTexture = null;
                }
                // 4. the PNG (and the layers)
                var nums = a.Skip(1).Where(x => int.TryParse(x, out _)).Select(int.Parse).ToList();
                int ow = nums.Count > 0 ? Math.Max(16, nums[0]) : sw;
                int oh = nums.Count > 1 ? Math.Max(16, nums[1]) : (int)Math.Round((double)ow * sh / sw);
                var outRt = rt;
                if (ow != sw || oh != sh)
                {
                    outRt = new RenderTexture(ow, oh, 0);
                    Graphics.Blit(rt, outRt);
                }
                File.WriteAllBytes(path, Png(outRt, out bool blank));
                if (outRt != rt) Free(ref outRt);
                var notes = new List<string>();
                if (layers)
                {
                    string stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", Path.GetFileNameWithoutExtension(path));
                    for (int i = 0; i < layerRts.Count; i++)
                    {
                        string lp = $"{stem}_layer{i}_{layerRts[i].name}.png";
                        File.WriteAllBytes(lp, Png(layerRts[i], out _, alpha: true));
                        notes.Add(Path.GetFileName(lp));
                    }
                }
                return $"shot {path}: {ow}x{oh} from {sw}x{sh}{(blank ? " (one colour: a fade, or nothing drawn)" : "")}; {offCams} layer cameras into " +
                       $"{layerRts.Count} layers, {screenCams} screen cameras, {overlayCanvases.Count} screen canvases" +
                       (notes.Count > 0 ? "; " + string.Join(", ", notes) : "");
            }
            catch (Exception e) { return $"error: {e.GetType().Name}: {e.Message}"; }
            finally
            {
                foreach (var (cv, mode, cam, plane) in restore)
                    if (cv != null) { cv.renderMode = mode; cv.worldCamera = cam; cv.planeDistance = plane; }
                if (temp != null) Object.Destroy(temp);
                Free(ref rt);
            }
        }

        static void Clear(RenderTexture t)
        {
            var was = RenderTexture.active;
            RenderTexture.active = t;
            GL.Clear(true, true, Color.clear);
            RenderTexture.active = was;
        }

        static void Free(ref RenderTexture t)
        {
            if (t == null) return;
            t.Release();
            Object.Destroy(t);
            t = null;
        }

        // a render texture's pixels as a PNG (alpha kept for a layer); blank: one colour throughout
        static byte[] Png(RenderTexture src, out bool blank, bool alpha = false)
        {
            var was = RenderTexture.active;
            RenderTexture.active = src;
            var tex = new Texture2D(src.width, src.height, alpha ? TextureFormat.RGBA32 : TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            tex.Apply();
            RenderTexture.active = was;
            blank = true;
            var px = tex.GetPixels32();
            for (int i = 1; i < px.Length && blank; i += 97)
                if (px[i].r != px[0].r || px[i].g != px[0].g || px[i].b != px[0].b || px[i].a != px[0].a) blank = false;
            byte[] png = ImageConversion.EncodeToPNG(tex);
            Object.Destroy(tex);
            return png;
        }

        static string ShotInfo()
        {
            var sb = new StringBuilder($"screen {Screen.width}x{Screen.height}");
            try
            {
                foreach (var c in Resources.FindObjectsOfTypeAll<Camera>())
                {
                    if (c == null || !c.gameObject.scene.IsValid()) continue;   // prefabs aren't in a scene
                    var t = c.targetTexture;
                    sb.Append($" | cam {c.name} on {c.enabled && c.gameObject.activeInHierarchy} depth {c.depth} target {(t == null ? "screen" : $"{t.name} {t.width}x{t.height}")}" +
                              $" rect {c.pixelRect.width}x{c.pixelRect.height} ortho {c.orthographic}/{c.orthographicSize:0.##} mask {c.cullingMask:x} clear {c.clearFlags}");
                }
                foreach (var ri in Object.FindObjectsOfType<UnityEngine.UI.RawImage>())
                {
                    if (ri == null || ri.texture == null) continue;
                    var root = ri.canvas != null ? ri.canvas.rootCanvas : null;
                    sb.Append($" | image {ri.name} shows {ri.texture.name} {ri.texture.width}x{ri.texture.height} on {ri.enabled && ri.gameObject.activeInHierarchy}" +
                              $" canvas {(root == null ? "-" : root.name + " " + root.renderMode)} material {(ri.material == null || ri.material.shader == null ? "-" : ri.material.shader.name)}");
                }
                foreach (var cv in Object.FindObjectsOfType<Canvas>())
                {
                    if (cv == null || !cv.isRootCanvas) continue;
                    sb.Append($" | canvas {cv.name} {cv.renderMode} cam {(cv.worldCamera == null ? "-" : cv.worldCamera.name)} on {cv.enabled && cv.gameObject.activeInHierarchy} order {cv.sortingOrder}");
                }
            }
            catch (Exception e) { sb.Append($" | error {e.GetType().Name}: {e.Message}"); }
            return sb.ToString();
        }

        static string EnemyHp(string[] a)
        {
            if (!InBattle) return "error: not in battle";
            if (a.Length < 1 || !int.TryParse(a[0], out int hp) || hp < 1) return "error: enemyhp <hp >= 1> [unique]";
            int only = a.Length > 1 && int.TryParse(a[1], out int u) ? u : -1;
            var done = new List<string>();
            foreach (var e in BattlePlugManager.Instance().GetEnemyUnits() ?? new BattleEnemyData[0])
            {
                var p = e?.BattleUnitDataInfo?.Parameter;
                if (p == null || p.CurrentHP <= 0 || (only >= 0 && e.UniqueId != only)) continue;
                p.CurrentHP = Math.Min(hp, p.ConfirmedMaxHp());
                done.Add($"{Who(e)} (unique {e.UniqueId}) {p.CurrentHP}");
            }
            return done.Count == 0 ? "no enemy changed" : "enemy HP: " + string.Join(", ", done);
        }

        static string State()
        {
            var sb = new StringBuilder();
            sb.Append($"frame {Time.frameCount}; scene {Scene()}; map {UserDataManager.Instance()?.CurrentMapId}");
            var scenes = new List<string>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                scenes.Add(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).name);
            sb.Append($"; unity scenes [{string.Join(" ", scenes)}]");
            // the title screen: its idle demo runs on a field of its own, so "scene" can say field there
            var ktw = Object.FindObjectOfType<Last.UI.KeyInput.TitleWindowController>();
            if (ktw != null) sb.Append($"; title window ({TitleState(ktw)})");
            else if (Object.FindObjectOfType<Last.UI.Touch.TitleWindowController>() != null) sb.Append("; title window");
            // (the title screen's battle manager throws from GetPlayerUnits: state must still answer there,
            // or a client waiting for "scene title" never sees it, 2026-11-20)
            try
            {
                var manager = BattlePlugManager.Instance();
                if (manager != null && !InBattle)
                    sb.Append($"; battle manager up, not in battle ({manager.GetPlayerUnits()?.Length ?? 0} players, {manager.GetEnemyUnits()?.Length ?? 0} enemies)");
            }
            catch (Exception) { sb.Append("; battle manager not ready"); }   // (no exception name: test runners grep for those)
            try
            {
                var bc = Object.FindObjectOfType<BattleController>();
                if (bc != null) sb.Append($"; battle phase {bc.stateMachine?.Current}");
            }
            catch (Exception e) { sb.Append($"; battle phase ? ({e.GetType().Name})"); }
            if (InBattle)
            {
                var battle = BattlePlugManager.Instance();
                sb.Append("; players [" + string.Join(", ", (battle.GetPlayerUnits() ?? new BattlePlayerData[0]).Select(u => $"{Who(u)} {Hp(u)}")) + "]");
                sb.Append("; enemies [" + string.Join(", ", (battle.GetEnemyUnits() ?? new BattleEnemyData[0]).Select(u => $"{Who(u)} {Hp(u)}")) + "]");
                try { sb.Append($"; end judgment {battle.BattleEndJugment?.GetEndJugment()}"); }
                catch (Exception) { }
            }
            var script = Last.Interpreter.Integrator.Instance;
            if (script != null && script.IsMainCoreAlive()) sb.Append($"; script {script.scriptName}");
            sb.Append($"; messages {(SkipMessages ? "skipped" : "shown")}");
            // the main game's sub-scene (Player: the player has the field); after "messages" so older patterns still match
            if (Scene() == "field") { var fs = FieldState(); if (fs != null) sb.Append($"; field state {fs}"); }
            foreach (var part in StateParts)
            {
                try { sb.Append("; ").Append(part()); }
                catch (Exception e) { sb.Append($"; ? ({e.GetType().Name})"); }
            }
            return sb.ToString();
        }

        public static string Who(BattleUnitData u)
        {
            if (u == null) return "-";
            try
            {
                var e = u.TryCast<BattleEnemyData>();
                if (e != null) return $"monster {e.MonsterMaster?.Id}";
                return u.GetOwnedCharacterData()?.Name ?? "?";
            }
            catch (Exception) { return "?"; }
        }

        public static string Hp(BattleUnitData u)
        {
            var p = u?.BattleUnitDataInfo?.Parameter;
            if (p == null) return "?";
            var text = $"{p.CurrentHP}/{p.ConfirmedMaxHp()}";
            try
            {
                var list = u.BattleUnitDataInfo.GetConfirmedConditionType();
                if (list != null && list.Count > 0)
                {
                    var names = new List<string>();
                    foreach (var c in list) names.Add(c.ToString());
                    text += " {" + string.Join(" ", names) + "}";
                }
            }
            catch (Exception) { }
            return text;
        }

        // --- skipmsg: the script message instruction completes at once ------------------------------

        static void HookMessages(Harmony h)
        {
            var msg = Hooks.TypeByName("Last.Interpreter.Instructions.Message");
            var m = msg == null ? null : AccessTools.Method(msg, "Msg");
            if (m == null) { Kit.Info("[harness] no Message.Msg: skipmsg does nothing"); return; }
            try { h.Patch(m, prefix: new HarmonyMethod(typeof(Harness), nameof(Msg_Prefix))); }
            catch (Exception e) { Kit.Warn($"[harness] Message.Msg not hooked: {e.Message}"); }
        }

        public static bool Msg_Prefix(ref int __result)
        {
            if (!SkipMessages) return true;
            __result = 0;
            return false;
        }

        // --- battle log: every action as it's built, HP after each ------------------------------------
        // Actions are built by the game's own Serial.FF<n>.Calc.FunctionProvider (looked up by name).

        static readonly List<string> battleLog = new List<string>();

        static void HookBattleLog(Harmony h)
        {
            var provider = Hooks.TypeByName($"Serial.FF{(int)Game.Current}.Calc.FunctionProvider");
            var act = provider == null ? null : AccessTools.Method(provider, "CreateActFunction");
            if (act != null) Hooks.Patch(h, act, typeof(Harness), prefix: nameof(Act_Prefix));
            else Kit.Info("[harness] no FunctionProvider.CreateActFunction in this game: no action log");
            var atb = Hooks.TypeByName("Last.Battle.BattleProgressATB");
            var end = atb == null ? null : AccessTools.Method(atb, "ActExectionEndDelegate");
            if (end != null) Hooks.Patch(h, end, typeof(Harness), postfix: nameof(ActEnd_Postfix));
        }

        public static void Note(string line)
        {
            line = $"[{Time.frameCount}] {line}";
            lock (battleLog)
            {
                battleLog.Add(line);
                if (battleLog.Count > 400) battleLog.RemoveAt(0);
            }
            Kit.Info($"[harness] {line}");
        }

        static string TakeLog()
        {
            lock (battleLog)
            {
                var text = battleLog.Count == 0 ? "(no actions)" : string.Join(" \\n ", battleLog);
                battleLog.Clear();
                return text;
            }
        }

        public static void Act_Prefix(BattleActData battleActData)
        {
            if (!Active || battleActData == null) return;
            try
            {
                var ids = new List<string>();
                if (battleActData.abilityList != null)
                    foreach (var a in battleActData.abilityList) if (a != null) ids.Add(a.Id.ToString());
                var targets = new List<string>();
                if (battleActData.TargetUnitDatas != null)
                    foreach (var t in battleActData.TargetUnitDatas) if (t != null) targets.Add($"{Who(t)} {Hp(t)}");
                Note($"{Who(battleActData.AttackUnitData)} {(battleActData.isCounter ? "counters with" : "uses")} ability [{string.Join(" ", ids)}] " +
                     $"(command {battleActData.Command?.Id}) on [{string.Join(", ", targets)}]");
            }
            catch (Exception e) { Note($"act: {e.GetType().Name}"); }
        }

        public static void ActEnd_Postfix(BattleUnitData battleUnitData, bool isCounter)
        {
            if (!Active) return;
            try
            {
                var battle = BattlePlugManager.Instance();
                Note($"  after {Who(battleUnitData)}'s {(isCounter ? "counter" : "act")}: players [" +
                     string.Join(", ", (battle?.GetPlayerUnits() ?? new BattlePlayerData[0]).Select(u => $"{Who(u)} {Hp(u)}")) + "]; enemies [" +
                     string.Join(", ", (battle?.GetEnemyUnits() ?? new BattleEnemyData[0]).Select(u => $"{Who(u)} {Hp(u)}")) + "]");
            }
            catch (Exception e) { Note($"act end: {e.GetType().Name}"); }
        }

        // --- the injected presses ---------------------------------------------------------------------

        static bool Is(Key key) => pressKey != IntPtr.Zero && key != null && key.Pointer == pressKey;

        public static bool KeyDown_Prefix(Key key, ref bool __result)
        {
            if (!Is(key) || Time.frameCount != pressFrom) return true;
            __result = true;
            return false;
        }

        public static bool Key_Prefix(Key key, ref bool __result)
        {
            int f = Time.frameCount;
            if (!Is(key) || f < pressFrom || f >= pressUntil) return true;
            __result = true;
            return false;
        }

        public static bool KeyUp_Prefix(Key key, ref bool __result)
        {
            if (!Is(key) || Time.frameCount != pressUntil) return true;
            __result = true;
            return false;
        }

        public static bool AnyDown_Prefix(ref bool __result)
        {
            if (pressKey == IntPtr.Zero || Time.frameCount != pressFrom) return true;
            __result = true;
            return false;
        }

        public static bool Any_Prefix(ref bool __result)
        {
            int f = Time.frameCount;
            if (pressKey == IntPtr.Zero || f < pressFrom || f >= pressUntil) return true;
            __result = true;
            return false;
        }

        public static bool AnyUp_Prefix(ref bool __result)
        {
            if (pressKey == IntPtr.Zero || Time.frameCount != pressUntil) return true;
            __result = true;
            return false;
        }
    }
}
