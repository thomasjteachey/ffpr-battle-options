using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;

namespace PrKit
{
    // A mod's own saves, apart from the game's and other mods' (the user, 2026-11-19: "wc/bnw need to be
    // using different save directories than the vanilla ones"). The game keeps every file in one folder,
    // Documents/My Games/FINAL FANTASY <n> PR/Steam/<Steam id> (SaveSlotManager.GetSavePath), each named
    // base64(sha256(name + ".sav")) with '/' as '=' (FileOperationUtility.ConvertSha256): slot1..slot20,
    // slot21 (autosave), slot22 (suspend), system_slot (which slots exist), PictureBook (the bestiary),
    // System (the settings). Steam Cloud syncs only that folder (FF6's rule: pattern *, not recursive, at
    // most 100 files), so instead of a subfolder (which wouldn't reach the Deck) a mod's saves get names of
    // their own there: "<Tag>_slot3.sav" and so on (the user chose that over real subfolders). The settings
    // stay shared. On a mod's first start its save list begins as a copy of the shared one (nothing is
    // moved or deleted: the game's own files stay as they are); from then on each list is its own.
    //
    //   ModSaves.Apply(harmony, "WorldsCollide");   in the plugin's Load
    public static class ModSaves
    {
        public static string Tag { get; private set; }
        public static string LastNote = "";
        // the mod's headless test runs (they read saves, never write: no first-start copy either)
        public static Func<bool> IsTestRun = () => TestRun.Running;
        // tests: the game's save folder is this one instead (a copy of the player's), so a test run can do a
        // mod's first start for real (the copy, then loading through the mod's names) without touching
        // the player's folder or Steam Cloud; set before the game first looks for a save: by a mod's own
        // request (BNW's probe: savesandbox=<dir>) or the kit's (prkit_test.request savesandbox=<dir>, read
        // here). The test runner's sandbox: pr/build/save_sandbox/<name> (copies of the Deck's saves, 2026-11-20)
        public static string Sandbox;
        static bool sandboxAsked;
        static string SandboxDir
        {
            get
            {
                if (string.IsNullOrEmpty(Sandbox) && !sandboxAsked && TestRun.Running)
                {
                    sandboxAsked = true;
                    if (TestRun.Settings.TryGetValue("savesandbox", out var d) && !string.IsNullOrEmpty(d))
                    {
                        if (Directory.Exists(d)) Sandbox = d;
                        else Kit.Warn($"[saves] savesandbox {d}: no such folder; the game's own saves (read only)");
                    }
                }
                return Sandbox;
            }
        }
        static bool migrated;
        static readonly HashSet<string> Names = new HashSet<string> { "system_slot.sav", "PictureBook.sav" };

        static ModSaves()
        {
            for (int i = 1; i <= 22; i++) Names.Add($"slot{i}.sav");
        }

        public static bool Apply(Harmony h, string tag)
        {
            if (string.IsNullOrEmpty(tag) || tag.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            // Il2CppInterop renames namespaces starting with "System": the game's Systems.Files is Il2CppSystems.Files
            var util = Hooks.TypeByName("Il2CppSystems.Files.FileOperationUtility") ?? Hooks.TypeByName("Systems.Files.FileOperationUtility");
            var m = util == null ? null : AccessTools.Method(util, "ConvertSha256");
            if (m == null)
            {
                Kit.Warn("[saves] FileOperationUtility.ConvertSha256 not found: this mod shares the game's saves");
                return false;
            }
            Tag = tag;
            h.Patch(m, prefix: new HarmonyMethod(typeof(ModSaves), nameof(ConvertSha256_Prefix)));
            var path = AccessTools.Method(typeof(Last.Management.SaveSlotManager), "GetSavePath");
            if (path != null) h.Patch(path, postfix: new HarmonyMethod(typeof(ModSaves), nameof(GetSavePath_Postfix)));
            Kit.Info($"[saves] {tag}'s own saves: slots, autosave and bestiary as \"{tag}_<name>.sav\" in the game's save folder (Steam Cloud syncs them; settings shared)");
            return true;
        }

        // the game's own file name hash (ConvertSha256)
        public static string FileName(string name)
        {
            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(name))).Replace("/", "=");
        }

        public static void GetSavePath_Postfix(ref string __result)
        {
            var sandbox = SandboxDir;
            if (string.IsNullOrEmpty(sandbox)) return;
            var real = __result;
            if (SamePath(sandbox, real))
            {
                // never the player's own folder: a sandboxed run copies files into it (a mod's first start)
                Kit.Warn($"[saves] the sandbox {sandbox} is the game's own save folder: ignored (the run reads the shared saves)");
                Sandbox = null;
                return;
            }
            Hooks.NoteOnce("saves-sandbox", () => $"[saves] test sandbox: {sandbox} instead of {real}");
            __result = sandbox.TrimEnd('/', '\\') + "/";
        }

        static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        static bool Testing => string.IsNullOrEmpty(SandboxDir) && IsTestRun();

        // A save's time: its TimeStamp is text in the format of the machine that wrote it ("03/15/2022 21:39:57",
        // "6/11/2022 11:07:07 AM"), so compared as text a 2022 save can pass for newer than a 2026 one
        public static DateTime When(Last.Management.SaveSlotData sd)
        {
            var s = sd?.TimeStamp;
            if (string.IsNullOrEmpty(s)) return DateTime.MinValue;
            foreach (var c in new[] { System.Globalization.CultureInfo.InvariantCulture, System.Globalization.CultureInfo.CurrentCulture })
                if (DateTime.TryParse(s, c, System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var t)) return t;
            return DateTime.MinValue;
        }

        // the newest save with at least `members` in its party (test runs: a real party, not the opening's), not
        // on a world map when another will do: a BNW probe battle started on the world map right after loading
        // (the PC's slot 11, map 1) ended with a second game process, twice on 2026-11-20 (cause still open)
        public static Last.Management.SaveSlotData NewestWithParty(int members = 3)
        {
            Last.Management.SaveSlotData best = null, bestWorld = null;
            var all = Last.Management.SaveSlotManager.Instance()?.SaveSlotDataList;
            if (all == null) return null;
            foreach (var sd in all)
            {
                if (sd == null || string.IsNullOrEmpty(sd.TimeStamp)) continue;
                int n = 0;
                if (sd.PartyList != null) foreach (var pc in sd.PartyList) if (pc != null) n++;
                if (n < members) continue;
                if (IsWorldMap(MapId(sd))) { if (bestWorld == null || When(sd) > When(bestWorld)) bestWorld = sd; }
                else if (best == null || When(sd) > When(best)) best = sd;
            }
            return best ?? bestWorld;
        }

        // the map a save stands on (its mapData's "mapId"), -1 when unknown
        public static int MapId(Last.Management.SaveSlotData sd)
        {
            var m = System.Text.RegularExpressions.Regex.Match(sd?.mapData ?? "", @"""mapId""\s*:\s*(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        // FF6's world maps (map.csv: Map_10010 the World of Balance, Map_10020 the World of Ruin, Map_10010_1)
        public static bool IsWorldMap(int mapId) => Game.Current == PrGame.FF6 && (mapId == 1 || mapId == 2 || mapId == 575);

        // --- loading a save at the title (headless test runs: the kit harness's "load", BNW's probe) ----------
        // As the title's Load window does it (LoadGameWindowController's confirm, decompiled in tools/decomp/load
        // and tools/decomp/titleload): a fade-out, the slot read (SaveSlotManager.LoadAsync), LoadData
        // .RestoreSaveData, MapLoadProcessor.Start, then the title's SubSceneLoadingWait(false), which loads the
        // field's scene and switches to it only once the screen has faded out (FadeManager.IsStateFadeOut: with
        // useFade false the caller fades). The harness and WcTest left the fade out, so the switch never came:
        // "the title's load reads the save and stalls" (2026-11-07..20), and the title's idle demo (map 609) later
        // passed for a loaded field. The in-game load (GotoLoadSaveData) does nothing at the title.
        // The rest of the Load window's part, also missing until then: its slot read (SaveListController
        // .LoadSlotAsync) runs UserDataManager.Initialize first and OwnedCharacterClient.UpdateChracterName after,
        // and while it waits for the switch its UpdateWait runs MapLoadProcessor.Update every frame (the map's
        // load tasks; tools/ghidra/callers.py: only the Load/New Game windows and the field's own LoadMap do).
        // With the fade alone the switch came in ~1 s headless, then the field never got ready (scene other, map 0).
        // Returns null once the load has started, else why it can't.
        public static string LoadAtTitle(Last.Management.SaveSlotData slot)
        {
            var mgr = Last.Management.SaveSlotManager.Instance();
            var tw = UnityEngine.Object.FindObjectOfType<Last.UI.KeyInput.TitleWindowController>();
            if (slot == null || mgr == null) return "no such save";
            if (tw == null) return "not at the title";
            var fade = Il2CppSystem.Fade.FadeManager.Instance;
            if (fade == null) return "no fade manager";
            if (titleHooks == null)
            {
                var upd = AccessTools.Method(typeof(Last.UI.KeyInput.TitleWindowController), "Update");
                if (upd == null) return "no TitleWindowController.Update to run the map load from";
                titleHooks = new Harmony("prkit.modsaves.titleload");
                titleHooks.Patch(upd, postfix: new HarmonyMethod(typeof(ModSaves), nameof(TitleUpdate_Postfix)));
            }
            pumpTitle = IntPtr.Zero;
            int id = slot.Id;
            var black = new UnityEngine.Color(0, 0, 0, 1);
            fade.FadeOut(0.5f, black);   // the Load window's own fade (0.5 s)
            Last.Management.UserDataManager.Instance()?.Initialize();
            Action<bool> loaded = ok =>
            {
                try
                {
                    if (!ok)
                    {
                        fade.FadeIn(0.5f, black);
                        Kit.Warn($"[saves] save {id} didn't load");
                        return;
                    }
                    var ld = new Last.Map.LoadData();
                    ld.RestoreSaveData();
                    Last.Map.MapLoadProcessor.Instance.Start(ld, true, false);
                    pumpTitle = tw.Pointer;   // from now on the title's Update runs the map load, until the switch
                    tw.StartCoroutine(tw.SubSceneLoadingWait(false));
                    new Last.Management.OwnedCharacterClient().UpdateChracterName();
                    Kit.Info($"[saves] save {id} read; loading its map {ld.MapId}");
                }
                catch (Exception e) { Kit.Warn($"[saves] save {id}: {e.Message}"); }
            };
            tw.StartCoroutine(mgr.LoadAsync(id, loaded, false));
            return null;
        }

        static Harmony titleHooks;
        static IntPtr pumpTitle = IntPtr.Zero;   // the title whose Update runs the map load (its Load window's UpdateWait)

        public static void TitleUpdate_Postfix(Last.UI.KeyInput.TitleWindowController __instance)
        {
            if (pumpTitle == IntPtr.Zero || __instance == null || __instance.Pointer != pumpTitle) return;
            try { Last.Map.MapLoadProcessor.Instance?.Update(); }
            catch (Exception e)
            {
                pumpTitle = IntPtr.Zero;
                Kit.Warn($"[saves] the title's map load: {e.Message}");
            }
        }

        public static void ConvertSha256_Prefix(ref string targetName)
        {
            try
            {
                if (Tag == null || targetName == null || !Names.Contains(targetName)) return;
                if (!migrated) Migrate();
                if (!mineExist && Testing) return;   // a test run before the mod's first real start: the shared list, read only
                targetName = $"{Tag}_{targetName}";
            }
            catch (Exception e) { Hooks.WarnOnce("saves-name", e); }
        }

        static bool mineExist;

        // the first time the game looks for a save: the mod's list is the shared one copied, unless it has its own
        static void Migrate()
        {
            migrated = true;
            string dir = null;
            try { dir = Last.Management.SaveSlotManager.Instance()?.GetSavePath(); } catch (Exception) { }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { migrated = false; return; }   // not yet known: next time
            Kit.Info($"[saves] save folder {dir}");
            mineExist = File.Exists(Path.Combine(dir, FileName($"{Tag}_system_slot.sav")));
            if (mineExist) { LastNote = "own saves found"; return; }
            if (Testing) { LastNote = "test run: the shared saves, not copied"; Kit.Info($"[saves] {LastNote}"); return; }
            int copied = 0;
            foreach (var name in Names)
            {
                var from = Path.Combine(dir, FileName(name));
                var to = Path.Combine(dir, FileName($"{Tag}_{name}"));
                if (!File.Exists(from) || File.Exists(to)) continue;
                File.Copy(from, to);
                copied++;
            }
            mineExist = true;
            LastNote = $"first start: the shared save list copied as {Tag}'s own ({copied} files; the game's own files untouched)";
            Kit.Info($"[saves] {LastNote}");
        }
    }
}
