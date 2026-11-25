using System;
using System.Collections.Generic;
using System.Linq;

namespace PrKit.Ff6
{
    // The weapons that allow Runic and Bushido from a randomizer's seed (tools/seed_info.py command_weapons: PR
    // weapon content ids per command, from the SNES's weapon bits). The PR keeps both lists in code
    // (Serial.FF6.Management.BlockMagicSwordData / KillingSwordData .ValidWeaponContentId, vanilla's SNES bits):
    // set to the seed's while one is active, the game's own back without. Off until a mod sets Seed; it calls Poll
    // each frame (it applies once per seed change, when the game's providers are up). Worlds Collide's
    // WcItems.Apply (2026-11-05, -stra), in the kit since 2026-11-20 for Beyond Chaos (`i`).
    public static class SnesCommandWeapons
    {
        public static Func<string> SeedKey = () => "";                  // the active seed's id ("": none)
        public static Func<string, int[]> Seed = command => null;       // "runic" / "bushido" -> content ids, or null
        public static string Tag = "[kit]";
        public static string Last = "";

        static string appliedFor;
        static int frames;
        static readonly Dictionary<string, int[]> original = new Dictionary<string, int[]>();   // the game's own

        // what a command's list should be: the seed's, else the game's own
        public static int[] Want(int[] gameOwn, int[] seed) => seed ?? gameOwn;

        static Il2CppSystem.Collections.Generic.List<int> GameList(string command)
        {
            var pm = Serial.ProviderManager.Instance;
            if (pm == null) return null;
            return command switch
            {
                "runic" => pm.BlockMagicSwordProvider?.serialData?.TryCast<Serial.FF6.Management.BlockMagicSwordData>()?.ValidWeaponContentId,
                "bushido" => pm.KillingSwordProvider?.serialData?.TryCast<Serial.FF6.Management.KillingSwordData>()?.ValidWeaponContentId,
                _ => null,
            };
        }

        // every second or so (a mod's per-frame Update): applies once per seed change
        public static void Poll()
        {
            if (++frames % 60 != 0) return;
            try { Apply(); }
            catch (Exception e) { Hooks.WarnOnce("command-weapons", e); }
        }

        // false while the game's providers aren't up (try again later)
        public static bool Apply()
        {
            var key = SeedKey() ?? "";
            if (key == appliedFor) return true;
            var notes = new List<string>();
            foreach (var command in new[] { "runic", "bushido" })
            {
                var list = GameList(command);
                if (list == null) return false;
                var now = Enumerable.Range(0, list.Count).Select(i => list[i]).OrderBy(x => x).ToArray();
                if (!original.ContainsKey(command)) original[command] = now;
                var want = Want(original[command], key != "" ? Seed(command) : null);
                if (now.SequenceEqual(want.OrderBy(x => x))) { notes.Add($"{command}: {now.Length}"); continue; }
                list.Clear();
                foreach (var id in want) list.Add(id);
                notes.Add($"{command}: {now.Length} -> {want.Length}");
            }
            appliedFor = key;
            Last = string.Join("; ", notes);
            Kit.Info($"{Tag} Runic/Bushido weapons: {Last}");
            return true;
        }
    }
}
