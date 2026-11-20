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
        // the player's folder or Steam Cloud; set before the game first looks for a save
        public static string Sandbox;
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
            if (string.IsNullOrEmpty(Sandbox)) return;
            var real = __result;
            Hooks.NoteOnce("saves-sandbox", () => $"[saves] test sandbox: {Sandbox} instead of {real}");
            __result = Sandbox.TrimEnd('/', '\\') + "/";
        }

        static bool Testing => string.IsNullOrEmpty(Sandbox) && IsTestRun();

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
