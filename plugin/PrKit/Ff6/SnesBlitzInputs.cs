using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Serial.FF6.Management;
using Last.Defaine;

namespace PrKit.Ff6
{
    // A mod's own Blitz inputs, for any FF6 mod (2026-11-20, for Beyond Chaos's `l`, random Blitz inputs).
    // SNES: C4/7A40, 12 bytes a Blitz (Pummel .. Bum Rush), up to 11 button codes then $01, the last byte
    // the length x 2; codes 3 X, 4 Y, 5 L, 6 R, 7 down-left, 8 down, 9 down-right, A right, B up-right,
    // C up, D up-left, E left. PR: the Blitz is picked from a list, then its input is asked for and shown;
    // both come from JobInfomationData.GetSpecialAbilityCommandList(ability id) (Serial.FF6.Management;
    // a dictionary filled in code, decompiled tools/decomp/commands), as SpecialAbilityCommandId (eight
    // directions, ActionKey, SwitchLeft, SwitchRight). The PR has no X and Y: which of its inputs stands for
    // each SNES button is learned from the game's own lists for vanilla's eight Blitzes (position by
    // position where a list is as long as the SNES's), so a mod's sequence asks for what the PR asks for the
    // same buttons. Opt-in: the mod sets Seed and calls Patch once.
    public static class SnesBlitzInputs
    {
        public static Func<IReadOnlyDictionary<int, int[]>> Seed = () => null;   // PR ability id -> SNES codes (no $01)
        public static string Tag = "[kit]";
        public static readonly int[] Abilities = { 132, 133, 134, 135, 136, 137, 138, 139 };   // Pummel .. Bum Rush
        public static readonly int[][] VanillaSnes =
        {
            new[] { 0xE, 0xA, 0xE }, new[] { 0x8, 0x7, 0xE }, new[] { 0x3, 0x4, 0x8, 0xC }, new[] { 0xE, 0x7, 0x8, 0x9, 0xA },
            new[] { 0x6, 0x5, 0x6, 0x5, 0x3, 0x4 }, new[] { 0xC, 0xB, 0xA, 0x9, 0x8, 0x7, 0xE },
            new[] { 0x6, 0x5, 0x3, 0x4, 0xA, 0xE }, new[] { 0xE, 0xD, 0xC, 0xB, 0xA, 0x9, 0x8, 0x7, 0xE },
        };
        // the directions as the PR names them (SpecialAbilityCommandId 0-7), and L/R: the fallback when the
        // game's lists don't say
        static readonly Dictionary<int, SpecialAbilityCommandId> Fallback = new Dictionary<int, SpecialAbilityCommandId>
        {
            [0x7] = SpecialAbilityCommandId.LeftDownArrow, [0x8] = SpecialAbilityCommandId.DownArrow, [0x9] = SpecialAbilityCommandId.RightDownArrow,
            [0xA] = SpecialAbilityCommandId.RightArrow, [0xB] = SpecialAbilityCommandId.RightUpArrow, [0xC] = SpecialAbilityCommandId.UpArrow,
            [0xD] = SpecialAbilityCommandId.LeftUpArrow, [0xE] = SpecialAbilityCommandId.LeftArrow,
            [0x5] = SpecialAbilityCommandId.SwitchLeft, [0x6] = SpecialAbilityCommandId.SwitchRight,
            [0x3] = SpecialAbilityCommandId.ActionKey, [0x4] = SpecialAbilityCommandId.ActionKey,
        };
        static Dictionary<int, SpecialAbilityCommandId> map;
        public static string Learned = "";
        static bool asking, patched;
        public static int Served;

        public static void Patch(Harmony h)
        {
            if (patched) return;
            patched = true;
            if (Hooks.Patch(h, typeof(JobInfomationData), nameof(JobInfomationData.GetSpecialAbilityCommandList), typeof(SnesBlitzInputs), postfix: nameof(List_Postfix)))
                Kit.Info($"{Tag} Blitz inputs: hooked");
        }

        // the PR's input for each SNES code, from the game's own vanilla lists
        static void Learn(JobInfomationData jobs)
        {
            var votes = new Dictionary<int, Dictionary<SpecialAbilityCommandId, int>>();
            int used = 0;
            asking = true;
            try
            {
                for (int i = 0; i < Abilities.Length; i++)
                {
                    var list = jobs.GetSpecialAbilityCommandList(Abilities[i]);
                    if (list == null || list.Count != VanillaSnes[i].Length) continue;
                    used++;
                    for (int k = 0; k < list.Count; k++)
                    {
                        if (!votes.TryGetValue(VanillaSnes[i][k], out var v)) votes[VanillaSnes[i][k]] = v = new Dictionary<SpecialAbilityCommandId, int>();
                        v[list[k]] = v.GetValueOrDefault(list[k]) + 1;
                    }
                }
            }
            finally { asking = false; }
            map = new Dictionary<int, SpecialAbilityCommandId>(Fallback);
            foreach (var (code, v) in votes) map[code] = v.OrderByDescending(kv => kv.Value).First().Key;
            Learned = $"from {used} of 8 vanilla lists: " + string.Join(", ", map.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key:X}={kv.Value}"));
            Kit.Info($"{Tag} Blitz inputs: SNES buttons as the PR asks for them, {Learned}");
        }

        public static void List_Postfix(JobInfomationData __instance, int abilityId, ref Il2CppSystem.Collections.Generic.List<SpecialAbilityCommandId> __result)
        {
            if (asking) return;
            try
            {
                var seed = Seed();
                if (seed == null || !seed.TryGetValue(abilityId, out var codes) || codes == null || codes.Length == 0) return;
                if (map == null) Learn(__instance);
                var list = new Il2CppSystem.Collections.Generic.List<SpecialAbilityCommandId>();
                foreach (var c in codes)
                    if (map.TryGetValue(c, out var id)) list.Add(id);
                if (list.Count == 0) return;
                __result = list;
                Served++;
            }
            catch (Exception e) { Hooks.WarnOnce("kit-blitz-inputs", e); }
        }

        // the game's list for a Blitz as it now answers (harness/self-tests)
        public static string Describe(JobInfomationData jobs, int abilityId)
        {
            var l = jobs?.GetSpecialAbilityCommandList(abilityId);
            return l == null ? "none" : string.Join(" ", Enumerable.Range(0, l.Count).Select(i => l[i].ToString()));
        }
    }
}
