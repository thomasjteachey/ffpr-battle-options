using System;
using System.IO;

namespace PrKit.Seeds
{
    // A seed mod's data: its generator (generator/, with its own Python), its baseline (baseline/:
    // vanilla copies of every file a seed changes) and its seeds (seeds/), in BepInEx/<Mod>, beside
    // BepInEx/plugins and not inside it.
    //
    // Why (2026-11-18, the user on the Steam Deck: "the startup is still INCREDIBLY slow", "the battle
    // entrance hitches for a long time too"): whenever .NET can't find an assembly, BepInEx's resolver
    // (Utility.TryResolveDllAssembly) lists every folder under BepInEx/plugins and tests each for the
    // DLL, and its plugin scan (TypeLoader.FindPluginTypes) lists every file there. Il2CppInterop asks
    // for a missing Il2CppSystem.Private.CoreLib at every class registration (each mod's runner), so
    // with Worlds Collide's ~13,700 data folders under plugins every registration, and the first
    // battle, took ~6.5 s on the Deck (Wine looks each name up case-insensitively, folder by folder;
    // ~0.6 s on the PC).
    //
    // An older install's folders under plugins/<Mod> are moved here first thing at start (Migrate).
    // Where both exist this folder's copy is the one just installed: it stays, taking from the old copy
    // only the parts made on the player's machine that it lacks, and the old copy goes. Seeds are merged
    // (a seed already here keeps its copy; the other goes to BepInEx/<backups>).
    // Worlds Collide's WcDataDir (0.6.4), in the kit since 2026-11-20.
    public sealed class ModDataDir
    {
        public readonly string Root, Tag;
        public string Generator => Path.Combine(Root, "generator");
        public string Baseline => Path.Combine(Root, "baseline");
        public string Seeds => Path.Combine(Root, "seeds");

        public ModDataDir(string modFolder, string tag)
        {
            Root = Path.Combine(BepInEx.Paths.BepInExRootPath, modFolder);
            Tag = tag;
        }

        // shipped: (part, the sub-folders made on the player's machine) of an older install's plugin folder
        public void Migrate(string pluginFolder, (string Part, string[] Made)[] shipped, string backupsName)
        {
            try
            {
                Directory.CreateDirectory(Root);
                foreach (var (part, made) in shipped)
                {
                    var old = Path.Combine(pluginFolder, part);
                    if (!Directory.Exists(old)) continue;
                    var now = Path.Combine(Root, part);
                    if (!Directory.Exists(now))
                    {
                        Directory.Move(old, now);
                        Kit.Info($"{Tag} data: {part} moved out of the plugin folder to {now}");
                        continue;
                    }
                    int kept = 0;
                    foreach (var m in made)
                    {
                        var from = Path.Combine(old, m);
                        var to = Path.Combine(now, m);
                        if (Directory.Exists(from) && !Directory.Exists(to)) { Directory.Move(from, to); kept++; }
                    }
                    Directory.Delete(old, true);
                    Kit.Info($"{Tag} data: the plugin folder's old {part} removed ({now} is the installed one" +
                             (kept > 0 ? $"; {kept} part(s) made on this machine moved into it)" : ")"));
                }
                var oldSeeds = Path.Combine(pluginFolder, "seeds");
                if (Directory.Exists(oldSeeds))
                {
                    if (!Directory.Exists(Seeds))
                    {
                        Directory.Move(oldSeeds, Seeds);
                        Kit.Info($"{Tag} data: seeds moved out of the plugin folder to {Seeds}");
                    }
                    else
                    {
                        int moved = 0, kept = 0;
                        var backup = Path.Combine(BepInEx.Paths.BepInExRootPath, backupsName,
                                                  "moved-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                        foreach (var entry in Directory.GetFileSystemEntries(oldSeeds))
                        {
                            var name = Path.GetFileName(entry);
                            var dest = Path.Combine(Seeds, name);
                            if (!Directory.Exists(dest) && !File.Exists(dest)) { Move(entry, dest); moved++; continue; }
                            Directory.CreateDirectory(backup);
                            Move(entry, Path.Combine(backup, name));
                            kept++;
                        }
                        Directory.Delete(oldSeeds, true);
                        Kit.Info($"{Tag} data: {moved} seed entries moved to {Seeds}" +
                                 (kept > 0 ? $"; {kept} already there (the plugin folder's copies are in {backup})" : ""));
                    }
                }
            }
            catch (Exception e)
            {
                Kit.Error($"{Tag} data: moving the mod's folders out of the plugin folder failed: {e.Message}");
            }
        }

        static void Move(string from, string to)
        {
            if (Directory.Exists(from)) Directory.Move(from, to);
            else File.Move(from, to);
        }
    }
}
