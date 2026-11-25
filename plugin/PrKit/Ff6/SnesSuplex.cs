using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Last.Battle;
using Last.Battle.Function;
using Last.Data.Master;
using Last.Systems;

namespace PrKit.Ff6
{
    // Suplex misses a monster that can't be suplexed, as on the SNES, for any FF6 mod (2026-11-20,
    // pr/SNES_PR_DISCREPANCIES.md; found by bc-battle). SNES: monster byte $13 bit 2 makes Suplex miss. The PR's
    // Suplex (ability 134) is function 2, a plain physical hit, and the PR's monster flags have no such bit, so it
    // never misses for that reason. BattleFunction2.HitCalc's postfix turns a hit of the mod's Suplex on a monster
    // of the mod's list (SnesSuplexRules) into a miss. Off unless a mod calls Apply, and while its Enabled is false.
    // The list includes the PR's Coliseum copies of those monsters (ids 500+). WC: vanilla's list with copies; Beyond
    // Chaos: the same, off for a seed with its suplexwrecks code (which clears the bit on every monster); BNW: its own.
    public sealed class SnesSuplexMod
    {
        public string Name = "";
        public ICollection<int> NoSuplex = SnesSuplexRules.VanillaWithCopies;   // PR monster ids (regulars + Coliseum copies)
        public int Ability = SnesSuplexRules.SuplexAbility;
        public Func<bool> Enabled = () => true;
        // the mod's Suplex runs a function without its own HitCalc (BattleBasicFunction's, e.g. BNW's Suplex is function
        // 3, function group 469; QA bnw-atb 2026-11-21: 0 of 30 hits turned): hook that HitCalc too. Off for WC and BC
        // (vanilla's Suplex, function 2, which has its own).
        public bool BasicFunctions;
    }

    public static class SnesSuplex
    {
        public const int BlitzCommand = 15;   // the PR's Blitz command (its ability group 4 holds Suplex)
        static SnesSuplexMod mod;
        static HashSet<int> ids = new HashSet<int>();
        public static int Misses;             // tests

        static bool On => mod != null && mod.Enabled();

        public static void Apply(Harmony h, SnesSuplexMod m)
        {
            mod = m;
            ids = new HashSet<int>(m.NoSuplex ?? Array.Empty<int>());
            Hooks.Patch(h, typeof(BattleFunction2), "HitCalc", typeof(SnesSuplex), postfix: nameof(HitCalc_Postfix));
            if (m.BasicFunctions)
                Hooks.Patch(h, typeof(BattleBasicFunction), "HitCalc", typeof(SnesSuplex), postfix: nameof(BasicHitCalc_Postfix));
            Kit.Info($"[suplex] {m.Name}: Suplex (ability {m.Ability}) misses the {ids.Count} monsters that can't be suplexed (the SNES's monster flag)" +
                     (m.BasicFunctions ? "; hooked in function 2 and the basic functions" : ""));
        }

        public static void HitCalc_Postfix(BattleFunction2 __instance) => Turn(__instance);

        public static void BasicHitCalc_Postfix(BattleBasicFunction __instance) => Turn(__instance);

        // a hit of the mod's Suplex on a listed monster becomes a miss (a miss already stays: running twice is harmless)
        static void Turn(BattleBaseFunction f)
        {
            if (!On || f == null) return;
            try
            {
                var abilities = f.battleActData?.abilityList;
                if (abilities == null || abilities.Count == 0 || abilities[0] == null || abilities[0].Id != mod.Ability) return;
                var dic = f.ICalcResultDic;
                if (dic == null) return;
                var keys = new List<BattleUnitData>();
                foreach (var k in dic.Keys) keys.Add(k);
                foreach (var unit in keys)
                {
                    int monster = unit?.TryCast<BattleEnemyData>()?.GetMonster()?.Id ?? 0;
                    if (!SnesSuplexRules.Misses(abilities[0].Id, monster, ids, mod.Ability)) continue;
                    var r = dic[unit];
                    var hit = r.GetHitType();
                    if (hit != HitType.Hit && hit != HitType.Critical) continue;
                    r.SetStatus(0, HitType.Miss, r.GetHitCount(), new Il2CppSystem.Collections.Generic.List<Condition>(), false);
                    Misses++;
                    Hooks.NoteOnce("snes-suplex-" + monster, () => $"[suplex] {mod.Name}: Suplex on monster {monster} missed (it can't be suplexed, the SNES's flag)");
                }
            }
            catch (Exception e) { Hooks.WarnOnce("SnesSuplex", e); }
        }

        // In a battle (a mod's test run decides when): the first party member's Suplex on the first monster through
        // the game's own functions (nothing applied), 30 times with that monster on the list and 30 times off it.
        // On the list every hit turns into a miss; off it, Suplex hits. Null until a battle.
        public static string SelfTest()
        {
            if (!On) return "[suplex] SNES Suplex self-test: off";
            var battle = BattlePlugManager.Instance();
            if (battle == null || !battle.IsBattle()) return null;
            var players = battle.GetPlayerUnits();
            var enemies = battle.GetEnemyUnits();
            var mm = MasterManager.Instance;
            var suplex = mm?.GetData<Ability>(mod.Ability);
            var blitz = mm?.GetData<Command>(BlitzCommand);
            if (players == null || players.Count == 0 || enemies == null || enemies.Count == 0 || suplex == null || blitz == null) return null;
            BattleUnitData p = players[0], e = enemies[0];
            int monster = e.TryCast<BattleEnemyData>()?.GetMonster()?.Id ?? 0;
            if (monster == 0) return null;
            string who = p.TryCast<BattlePlayerData>()?.ownedCharacterData?.Name ?? "the first party member";
            bool listed = ids.Contains(monster);
            try
            {
                ids.Add(monster);
                int before = Misses;
                int hitsOn = Hits(p, e, blitz, suplex, 30);
                int turned = Misses - before;
                ids.Remove(monster);
                int hitsOff = Hits(p, e, blitz, suplex, 30);
                if (listed) ids.Add(monster);
                string copy = CopyCheck(out bool copyOk);
                bool ok = hitsOn == 0 && turned > 0 && hitsOff > 0 && copyOk;
                return $"[suplex] {mod.Name} SNES Suplex self-test {(ok ? "ok" : "FAILED")}: {who}'s Suplex on monster {monster} " +
                       $"({(listed ? "on" : "not on")} the mod's list of {ids.Count}): counted as can't-suplex {hitsOn}/30 hits " +
                       $"({turned} hits turned to misses), counted as suplexable {hitsOff}/30 hits; {copy}";
            }
            finally
            {
                if (listed) ids.Add(monster); else ids.Remove(monster);
            }
        }

        // a Coliseum copy on the list (ids 500+) names a listed regular in the game's own monster table (by name key:
        // a seed may change a regular's stats while its copy keeps vanilla's)
        static string CopyCheck(out bool ok)
        {
            ok = true;
            int copy = 0, copies = 0;
            foreach (var id in ids) if (id >= 500) { copies++; if (copy == 0 || id < copy) copy = id; }
            if (copy == 0) return "no Coliseum copies on the list";
            var mm = MasterManager.Instance;
            var c = mm?.GetData<Monster>(copy);
            if (c == null) { ok = false; return $"Coliseum copy {copy} isn't in the game's monster table"; }
            foreach (var r in ids)
            {
                if (r >= 500) continue;
                var m = mm.GetData<Monster>(r);
                if (m != null && m.MesIdName == c.MesIdName)
                    return $"{copies} Coliseum copies on the list; copy {copy} names listed monster {r} ({c.MesIdName})";
            }
            ok = false;
            return $"Coliseum copy {copy} is on the list but names no listed monster ({c.MesIdName})";
        }

        // n acts through every function of the ability (nothing applied): how many hit the target
        static int Hits(BattleUnitData attacker, BattleUnitData target, Command cmd, Ability ab, int n)
        {
            var fns = ParameterActFunctionManagment.GetFunction(ab);
            if (fns == null) return 0;
            int hits = 0;
            for (int i = 0; i < n; i++)
            {
                var abilities = new Il2CppSystem.Collections.Generic.List<Ability>();
                abilities.Add(ab);
                var targets = new Il2CppReferenceArray<BattleUnitData>(1);
                targets[0] = target;
                var act = new BattleActData(attacker, targets, cmd, abilities, BattleActData.ActType.Normal,
                    (BattleActData.TargetType)0, BattleActData.ArmType.Right, (BattleActData.ActAbilityType)0, 1, false);
                var results = new Il2CppSystem.Collections.Generic.Dictionary<BattleUnitData, ICalcResult>();
                var attacks = new Il2CppSystem.Collections.Generic.Dictionary<BattleUnitData, ICalcResult>();
                HitType last = HitType.Non;
                foreach (var f in fns)
                {
                    if (f == null) continue;
                    f.Init(act, results, attacks);
                    f.FixedTarget();
                    f.HitCalc();
                    var dic = f.ICalcResultDic ?? results;
                    if (dic != null && dic.ContainsKey(target)) last = dic[target].GetHitType();
                }
                if (last == HitType.Hit || last == HitType.Critical) hits++;
            }
            return hits;
        }
    }
}
