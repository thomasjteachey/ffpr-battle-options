using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HarmonyLib;
using Last.Management;

namespace PrKit.Seeds
{
    // What every seed has (a mod's seed class adds its own data: Worlds Collide's checks, objectives...)
    public class SeedInfo
    {
        public string Id, Dir, SeedText = "", Flags = "", Preset = "", Hash = "", Created = "", Version = "";
        public string[] Files = Array.Empty<string>();
        public int Rows;
        public double Seconds;
    }

    // A seed mod's seeds made in game (its data folder's seeds/<id>/, written by its generator: the
    // shared porter's full tables, text and changed files, plus the seed's json) and which one the
    // running game has: its master rows and text are set live (LiveData, full tables, so the previous
    // seed is undone), its changed files (chest maps, AI scripts, whole data files) copied into the
    // mod's Magicite folder over the baseline ones (tools/seed_live.py; Magicite reads them each time
    // the game loads one). Each save remembers the seed it was played with: loading it brings that
    // seed back. State: seeds/state.json {active, activeFiles, slots: {slot: id}}.
    // Worlds Collide's WcSeeds (2026-11-05..19), in the kit since 2026-11-20; the mod keeps what is
    // its own (its seed's contents, what a New Game gets, its own rules on saving) as callbacks.
    public sealed class SeedLibrary
    {
        public string Tag = "[seed]", TestTag = "[test]";
        public string JsonName = "seed.json";
        public Func<string> Root = () => "", ModDir = () => "", BaselineDir = () => "";
        public Func<string, string, SeedInfo> Parse;               // (id, folder) -> the mod's seed (from JsonName), or null
        public Func<bool> ModOn = () => true;                      // the mod's data is installed (else seeds are off)
        public Func<bool> TestRunning = () => TestRun.Running;     // a headless self-test: no saves
        public Func<int, bool> AllowsSave = _ => false;            // ... except the slots the test writes itself
        public Func<bool> RefuseSaves = () => false;               // the seed's own no-saving rule (WC's -nosaves)
        public string RefuseNote = "no saving";                    // its name in the log
        public Action<SeedInfo> BeforeApply;                       // repairs to a seed's files before it's applied
        public Action<SeedInfo> AfterApply;                        // the seed (null: none) is now the game's
        public Action<string> Warn;                                // a message for the player (startup apply failed)
        public Action OnLoading;                                   // a save is being loaded (not a New Game)
        public Func<SeedInfo, string> Describe = s => s == null ? "none" : string.IsNullOrEmpty(s.Hash) ? s.SeedText : $"{s.SeedText} ({s.Hash})";
        public Func<string, bool> EvictAlso = _ => false;          // more cached assets (by name) to drop when files change
        public int Keep = 30;

        public SeedInfo Active;
        string[] activeFiles = Array.Empty<string>();
        readonly Dictionary<int, string> slots = new Dictionary<int, string>();
        bool startupApplied;
        float startupRetryAt;
        int startupTries;
        public int StartupFailed = -1;    // the startup apply's failed tables (-1: not done)
        public bool StartupDone => startupApplied;
        public string LastApply = "";

        string StatePath => Path.Combine(Root(), "state.json");

        static SeedLibrary current;       // the one the save hooks serve (one seed mod per plugin)

        public void Init()
        {
            current = this;
            Directory.CreateDirectory(Root());
            try
            {
                if (File.Exists(StatePath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
                    var r = doc.RootElement;
                    var id = r.TryGetProperty("active", out var a) ? a.GetString() : null;
                    if (r.TryGetProperty("activeFiles", out var af))
                        activeFiles = af.EnumerateArray().Select(x => x.GetString()).ToArray();
                    if (r.TryGetProperty("slots", out var s))
                        foreach (var p in s.EnumerateObject())
                            if (int.TryParse(p.Name, out int slot)) slots[slot] = p.Value.GetString();
                    if (!string.IsNullOrEmpty(id)) Active = Load(id);
                }
            }
            catch (Exception e) { Kit.Warn($"{Tag} {StatePath}: {e.Message}"); }
            // counting, not All(): that parses every seed's json, seconds with a few large seeds, and
            // startup needs only the active seed (the screen loads the list when it's opened)
            Kit.Info($"{Tag} {Count()} seeds; active: {(Active == null ? "none (vanilla data)" : Describe(Active))}");
            if (ModOn()) SyncFiles();
        }

        // Make the Magicite folder's files the active seed's (baseline where it changes none):
        // deploy mirrors the baseline over the folder, and nothing reads these files before
        // the game loads a map or a battle, long after this.
        void SyncFiles()
        {
            try
            {
                var basefiles = Path.Combine(BaselineDir(), "files");
                if (!Directory.Exists(basefiles)) { Kit.Warn($"{Tag} no baseline at {basefiles}"); return; }
                var mine = new HashSet<string>(Active?.Files ?? Array.Empty<string>());
                int copied = 0, total = 0;
                foreach (var full in Directory.GetFiles(basefiles, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(basefiles, full).Replace(Path.DirectorySeparatorChar, '/');
                    var src = mine.Contains(rel) ? Path.Combine(Active.Dir, "files", rel) : full;
                    var dst = Path.Combine(ModDir(), rel);
                    total++;
                    if (File.Exists(dst) && Same(src, dst)) continue;
                    Copy(src, dst);
                    copied++;
                }
                activeFiles = mine.ToArray();
                Kit.Info($"{Tag} Magicite files: {copied} of {total} brought in line with {(Active == null ? "the baseline" : Describe(Active))}");
            }
            catch (Exception e) { Kit.Warn($"{Tag} file sync: {e.Message}"); }
        }

        static bool Same(string a, string b)
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }

        public SeedInfo Load(string id)
        {
            var dir = Path.Combine(Root(), id);
            if (!File.Exists(Path.Combine(dir, JsonName))) return null;
            try { return Parse(id, dir); }
            catch (Exception e)
            {
                Kit.Warn($"{Tag} {Path.Combine(dir, JsonName)}: {e.Message}");
                return null;
            }
        }

        // the fields every seed json has: seed, flags, preset, hash, created, version, files, tables, seconds
        public static void ReadCommon(SeedInfo s, JsonElement r, string versionKey = "version")
        {
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
            s.SeedText = S("seed"); s.Flags = S("flags"); s.Preset = S("preset"); s.Hash = S("hash");
            s.Created = S("created"); s.Version = S(versionKey);
            if (r.TryGetProperty("files", out var f)) s.Files = f.EnumerateArray().Select(x => x.GetString()).ToArray();
            if (r.TryGetProperty("tables", out var t)) s.Rows = t.EnumerateObject().Sum(p => p.Value.GetInt32());
            if (r.TryGetProperty("seconds", out var sec) && sec.ValueKind == JsonValueKind.Number) s.Seconds = sec.GetDouble();
        }

        public int Count()
        {
            var root = Root();
            if (!Directory.Exists(root)) return 0;
            return Directory.EnumerateDirectories(root)
                .Count(d => !d.EndsWith(".work", StringComparison.OrdinalIgnoreCase)
                         && File.Exists(Path.Combine(d, JsonName)));
        }

        // newest first
        public List<SeedInfo> All()
        {
            var root = Root();
            if (!Directory.Exists(root)) return new List<SeedInfo>();
            return Directory.GetDirectories(root)
                .Where(d => !d.EndsWith(".work"))
                .Select(d => Load(Path.GetFileName(d))).Where(s => s != null)
                .OrderByDescending(s => s.Id, StringComparer.Ordinal).ToList();
        }

        public static string NewId(string seed)
        {
            var clean = new string(seed.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').Take(24).ToArray());
            return $"{DateTime.Now:yyyyMMdd-HHmmss}-{(clean.Length > 0 ? clean : "seed")}";
        }

        public string DirFor(string id) => Path.Combine(Root(), id);

        // --- applying ---------------------------------------------------------------------

        // Make `s` the running game's seed (null: vanilla data where a seed had changed it).
        public string Apply(SeedInfo s)
        {
            if (!LiveData.Ready) return "the game's data isn't loaded yet";
            try
            {
                var files = SwapFiles(s);
                string data;
                if (s != null) BeforeApply?.Invoke(s);
                if (s != null) data = LiveData.Apply(s.Dir).ToString();
                else
                {
                    // vanilla: every managed table/text back to the baseline copies
                    var vanilla = Path.Combine(BaselineDir(), "vanilla_data");
                    data = Directory.Exists(vanilla) ? LiveData.Apply(vanilla).ToString() : "no vanilla tables to restore";
                }
                int evicted = Evict(files);
                Active = s;
                activeFiles = s?.Files ?? Array.Empty<string>();
                Save();
                LastApply = $"{(s == null ? "vanilla" : Describe(s))}: {data}; {files.Count} files, {evicted} cached assets dropped";
                Kit.Info($"{Tag} applied " + LastApply);
                AfterApply?.Invoke(s);
                return null;
            }
            catch (Exception e)
            {
                Kit.Error($"{Tag} apply failed: {e}");
                return e.Message;
            }
        }

        // copy the seed's changed files into the Magicite folder, and the baseline back for files the
        // previous seed had changed and this one doesn't
        List<string> SwapFiles(SeedInfo s)
        {
            var touched = new List<string>();
            var want = new HashSet<string>(s?.Files ?? Array.Empty<string>());
            foreach (var rel in activeFiles.Where(f => !want.Contains(f)))
            {
                var src = Path.Combine(BaselineDir(), "files", rel);
                if (File.Exists(src)) { Copy(src, Path.Combine(ModDir(), rel)); touched.Add(rel); }
                else Kit.Warn($"{Tag} no baseline copy of {rel}");
            }
            foreach (var rel in want)
            {
                Copy(Path.Combine(s.Dir, "files", rel), Path.Combine(ModDir(), rel));
                touched.Add(rel);
            }
            return touched;
        }

        static void Copy(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            File.Copy(from, to, true);
        }

        // Assets the game already loaded keep their old contents in ResourceManager's cache;
        // drop the entries for files we just replaced so the next load reads the new ones.
        int Evict(List<string> files)
        {
            if (files.Count == 0) return 0;
            var rm = ResourceManager.Instance;
            var dic = rm?.completeAssetDic;
            if (dic == null) return 0;
            var stems = new HashSet<string>(files.Select(f => Path.GetFileNameWithoutExtension(f)), StringComparer.OrdinalIgnoreCase);
            var maps = new HashSet<string>(files.Select(f => f.Split('/').Reverse().Skip(1).FirstOrDefault() ?? ""), StringComparer.OrdinalIgnoreCase);
            var drop = new List<string>();
            foreach (var key in dic.Keys)
            {
                var k = key.Replace('\\', '/');
                var last = k.Split('/').Last();
                var parent = k.Split('/').Reverse().Skip(1).FirstOrDefault() ?? "";
                if (last.StartsWith("sc_ai_bnw_") && stems.Contains(last)) drop.Add(key);
                else if (last == "entity_default" && maps.Contains(parent)) drop.Add(key);
                else if (stems.Contains(last) && EvictAlso(last)) drop.Add(key);
            }
            foreach (var key in drop) dic.Remove(key);
            return drop.Count;
        }

        void Save()
        {
            try
            {
                Directory.CreateDirectory(Root());
                var state = new Dictionary<string, object>
                {
                    ["active"] = Active?.Id ?? "",
                    ["activeFiles"] = activeFiles,
                    ["slots"] = slots.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                };
                // write a temporary file and swap it in: a crash mid-write (a power loss leaves zeros)
                // keeps the previous state instead of an unreadable one
                var tmp = StatePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
                if (File.Exists(StatePath)) File.Replace(tmp, StatePath, null);
                else File.Move(tmp, StatePath);
            }
            catch (Exception e) { Kit.Warn($"{Tag} {StatePath}: {e.Message}"); }
        }

        public void Prune()
        {
            var keep = new HashSet<string>(slots.Values) { Active?.Id ?? "" };
            foreach (var s in All().Skip(Keep).Where(s => !keep.Contains(s.Id)))
            {
                try { Directory.Delete(s.Dir, true); Kit.Info($"{Tag} removed old seed {s.Id}"); }
                catch (Exception e) { Kit.Warn($"{Tag} {s.Dir}: {e.Message}"); }
            }
        }

        // The player's Delete in the seed list: the seed's folder goes. Not the current seed, and not
        // one a save still uses (that save would load without its seed's data). Returns why not, or
        // null once it's gone.
        public string Delete(SeedInfo s)
        {
            if (s == null || !Directory.Exists(s.Dir)) return "that seed is already gone";
            if (s.Id == Active?.Id) return "it's the current seed: play another seed (or none) first";
            var saves = slots.Where(kv => kv.Value == s.Id && SaveExists(kv.Key)).Select(kv => kv.Key).OrderBy(k => k).ToList();
            if (saves.Count > 0)
                return $"{string.Join(", ", saves.Select(SlotName))} {(saves.Count == 1 ? "uses" : "use")} it: " +
                       "save over that with another seed first";
            try { Directory.Delete(s.Dir, true); }
            catch (Exception e) { return e.Message; }
            Kit.Info($"{Tag} deleted seed {s.Id}");
            return null;
        }

        static string SlotName(int slot) => slot == SaveSlotManager.AutoSlotId ? "the autosave"
            : slot == SaveSlotManager.SuspendedSlotId ? "the quick save" : $"save slot {slot}";

        // whether a recorded save is still there (the game's own list; when it can't tell, yes)
        bool SaveExists(int slot)
        {
            if (testSlots.Contains(slot)) return true;
            try
            {
                var have = SaveSlotManager.Instance()?.ExistenceSaveSlots;
                if (have != null && have.ContainsKey(slot)) return have[slot];
            }
            catch (Exception) { }
            return true;
        }

        // the headless test: a save recorded for a seed, in memory only (a slot number the game
        // doesn't have, so no real save's record is touched)
        readonly HashSet<int> testSlots = new HashSet<int>();
        public void TestSlot(int slot, string id)
        {
            if (slot <= SaveSlotManager.LoadMaxSlotCount) throw new ArgumentException($"slot {slot} is a real save slot");
            if (id == null) { slots.Remove(slot); testSlots.Remove(slot); }
            else { slots[slot] = id; testSlots.Add(slot); }
        }

        public string SlotSeed(int slot) => slots.TryGetValue(slot, out var id) ? id : null;

        // the active seed's tables/text into the game once its data has loaded (its files are
        // already in the Magicite folder from when it was applied)
        public void Update()
        {
            if (startupApplied || !ModOn() || !LiveData.Ready) return;
            if (Active == null) { StartupFailed = 0; startupApplied = true; return; }
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < startupRetryAt) return;
            var r = LiveData.Apply(Active.Dir);   // tables already set are skipped on a retry
            LastApply = $"{Describe(Active)}: {r}";
            if (r.Failed != 0 && ++startupTries < 30)
            {
                // the game still loading its tables: again in 2 s (a minute at most)
                startupRetryAt = now + 2f;
                Kit.Info($"{Tag} seed {LastApply} (startup, try {startupTries}: again in 2 s)");
                return;
            }
            startupApplied = true;
            StartupFailed = r.Failed;
            Kit.Info($"{Tag} seed {LastApply} (startup{(startupTries > 0 ? $", try {startupTries + 1}" : "")})");
            if (r.Failed != 0) Warn?.Invoke("Couldn't load this seed's data at startup: restart the game, or pick the seed again on the mod's screen.");
        }

        // --- saves ------------------------------------------------------------------------

        public void Patch(Harmony h)
        {
            current = this;
            var t = typeof(SaveSlotManager);
            foreach (var m in t.GetMethods().Where(m => m.Name == "Save" && m.GetParameters().FirstOrDefault()?.ParameterType == typeof(int)))
            {
                // the overload with a callback (autosaves) must hear "done", or whoever waits on it waits forever
                bool withCallback = m.GetParameters().Any(p => p.Name == "callback");
                h.Patch(m, prefix: new HarmonyMethod(typeof(SeedLibrary), withCallback ? nameof(NoSaveInTestCallback) : nameof(NoSaveInTest)),
                        postfix: new HarmonyMethod(typeof(SeedLibrary), nameof(Saved)));
            }
            var load = t.GetMethod("GotoLoadSaveData");
            if (load != null) h.Patch(load, prefix: new HarmonyMethod(typeof(SeedLibrary), nameof(Loading)));
            // Title-screen Continue/Load uses SaveSlotManager.LoadAsync directly (not GotoLoadSaveData):
            // without this marker, leaving the title after an autosave load looked like a New Game
            foreach (var m in t.GetMethods().Where(m => m.Name == "LoadAsync"))
                h.Patch(m, prefix: new HarmonyMethod(typeof(SeedLibrary), nameof(LoadingAsync)));
            var auto = AccessTools.Method(typeof(global::Last.Interpreter.Instructions.External.Misc), "AutoSave");
            if (auto != null) h.Patch(auto, prefix: new HarmonyMethod(typeof(SeedLibrary), nameof(AutoSave_Prefix)));
            // the game's own data beside the slots, which test runs mustn't write either: the bestiary
            // (a won battle records its monsters) and the config
            foreach (var dt in new[] { typeof(PictureBookSaveManager), typeof(SystemConfigManager) })
                foreach (var m in dt.GetMethods().Where(m => m.Name == "Save"))
                    h.Patch(m, prefix: new HarmonyMethod(typeof(SeedLibrary), m.ReturnType == typeof(bool) ? nameof(NoDataSaveInTestBool) : nameof(NoDataSaveInTest)));
            // ... and the bestiary's updates, which write it without its hooked Save
            foreach (var m in typeof(PictureBookSaveManager).GetMethods()
                         .Where(m => m.Name is "Registration" or "UpdateDefeatCount" or "AddTotalSubjugationCount" or "UpdateScenarioFlags"))
                h.Patch(m, prefix: new HarmonyMethod(typeof(SeedLibrary), nameof(NoDataSaveInTest)));
        }

        static bool Testing => current != null && current.TestRunning();

        public static bool NoDataSaveInTest(System.Reflection.MethodBase __originalMethod)
        {
            if (!Testing) return true;
            Kit.Info($"{current.TestTag} {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name} skipped (test run)");
            return false;
        }

        public static bool NoDataSaveInTestBool(System.Reflection.MethodBase __originalMethod, ref bool __result)
        {
            if (!Testing) return true;
            Kit.Info($"{current.TestTag} {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name} skipped (test run)");
            __result = true;
            return false;
        }

        // A script's AutoSave instruction that won't save (a test run, the seed's no-saving rule) ends
        // at once. Skipping only the save (its callback told "done") left the instruction waiting: the
        // Blackjack's boarding arrival (sc_e_BJinboard_1) saves before it fades in, and stayed black
        static bool AutoSave_Prefix(ref int __result)
        {
            if (current == null) return true;
            bool test = Testing, refuse = !test && current.RefuseSaves();
            if (!test && !refuse) return true;
            Kit.Info(test ? $"{current.TestTag} a script's autosave skipped (test run)" : $"{current.Tag} {current.RefuseNote}: a script's autosave skipped");
            __result = 0;
            return false;
        }

        static bool NoSaveInTest(int slotId)
        {
            if (current == null) return true;
            if (!Testing && current.RefuseSaves()) { Kit.Info($"{current.Tag} {current.RefuseNote}: save to slot {slotId} refused"); return false; }
            if (!Testing) return true;
            if (current.AllowsSave(slotId)) return true;
            Kit.Info($"{current.TestTag} save to slot {slotId} skipped (test run)");
            return false;
        }

        static bool NoSaveInTestCallback(int slotId, Il2CppSystem.Action<bool> callback)
        {
            if (current == null) return true;
            if (!Testing && current.RefuseSaves())
            {
                Kit.Info($"{current.Tag} {current.RefuseNote}: autosave to slot {slotId} skipped");
                try { callback?.Invoke(true); } catch (Exception e) { Kit.Warn($"{current.Tag} save callback: {e.Message}"); }
                return false;
            }
            if (!Testing) return true;
            if (current.AllowsSave(slotId)) return true;
            Kit.Info($"{current.TestTag} save to slot {slotId} skipped (test run), reported done");
            try { callback?.Invoke(true); } catch (Exception e) { Kit.Warn($"{current.TestTag} save callback: {e.Message}"); }
            return false;
        }

        static void Saved(int slotId)
        {
            var lib = current;
            if (lib == null) return;
            if (Testing && !lib.AllowsSave(slotId)) return;
            try
            {
                // "" = saved in this mod with no seed: loading it goes back to the game's own data
                if (lib.ModOn()) lib.slots[slotId] = lib.Active?.Id ?? "";
                else lib.slots.Remove(slotId);
                lib.Save();
                Kit.Info($"{lib.Tag} saved slot {slotId}: seed {(lib.ModOn() && lib.Active != null ? lib.Active.Id : "none")}");
            }
            catch (Exception e) { Kit.Warn($"{lib.Tag} save hook: {e.Message}"); }
        }

        static void Loading(SaveSlotData saveData)
        {
            var lib = current;
            if (lib == null) return;
            lib.OnLoading?.Invoke();
            try
            {
                if (!lib.ModOn() || saveData == null) return;
                int slot = saveData.Id;
                var id = lib.SlotSeed(slot);
                Kit.Info($"{lib.Tag} loading slot {slot}: its seed {id ?? "(not recorded)"}; active {lib.Active?.Id ?? "none"}");
                if (id == "")
                {
                    if (lib.Active == null) return;
                    var verr = lib.Apply(null);
                    if (verr != null) Kit.Warn($"{lib.Tag} slot {slot} was saved without a seed; couldn't go back to the game's own data: {verr}");
                    return;
                }
                if (id == null || id == lib.Active?.Id) return;
                var s = lib.Load(id);
                if (s == null) { Kit.Warn($"{lib.Tag} slot {slot}'s seed {id} is gone; keeping {lib.Active?.Id ?? "vanilla"}"); return; }
                var err = lib.Apply(s);
                if (err != null) Kit.Warn($"{lib.Tag} couldn't bring back seed {id}: {err}");
            }
            catch (Exception e) { Kit.Warn($"{lib.Tag} load hook: {e.Message}"); }
        }

        static void LoadingAsync()
        {
            var lib = current;
            if (lib == null) return;
            lib.OnLoading?.Invoke();
            Kit.Info($"{lib.Tag} loading a save through Continue/LoadAsync");
        }
    }
}
