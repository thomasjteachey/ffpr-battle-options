using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Last.Battle;
using Last.Battle.Function;
using Last.Data.Master;
using Last.Data.User;
using Last.Defaine;
using Last.Systems;

namespace PrKit.Ff6
{
    // The Offering's and the Genji Glove's damage penalties as the SNES has them, for any FF6 mod
    // (2026-11-20, pr/SNES_PR_DISCREPANCIES.md). Vanilla (US ROM, kit disassembler): C2/2B9D, the damage
    // formula, whose only caller is the per-hit routine every command runs (C2/322B), applies them to every
    // hit with the physical flag ($11A2 bit 0) made by a character (C2/2C02-2C1C): the Offering halves the
    // damage (LSR $11B0), the Genji Glove takes a quarter off. Physical: Fight, Mug, Jump, Throw and the
    // physical tools (the item setup C2/2A37 keeps the flag for commands 2 and up: C2/18BB), the physical
    // Blitzes and Bushido (Pummel, Suplex, four Bushido), Rage/Sketch "Battle"/"Special" physicals; a
    // formula a special effect sets afterwards (GP Rain) isn't touched. The Genji Glove's flag: the
    // equipment check sets $11CF bit 4 only for weapon/weapon (C2/0F6F) and the battle setup strips it
    // from the relic flags (C2/28BB LDA $CF : TRB $D8), so the quarter comes off a wearer who is NOT
    // dual wielding, and two weapons lose nothing: vanilla's bug (BNW's Assassin fixed it).
    // The PR (decompiled PhysicalExecution): CalcExecuteCorrection.GetAccessoryPhysicalCorrectionValue type 0
    // (Offering x0.5) and type 1 (Genji x0.75 while IsSecondlyAttack, two weapons) on Attack (ability type 4)
    // only. A mod using this turns those two off where its own rule replaces them, and its rule applies to
    // every character's physical result (CalcControllerProvider.GetFightStatus: Attack, Jump, Mug through its
    // unique function, Blitz/Bushido physicals, monster physicals used by a character, tools; GetThrowStatus:
    // Throw), on the game's value.
    // SnesBugPlainFight (T-Edition, C2/2C06 -> C2/65E0): the SNES bug's -25% only on a plain physical hit
    // ($11A2 == 1 exactly): the PR's Attack (ability type 4), not Mug, Jump, Throw or physical skills
    public enum GenjiRule { Pr, SnesBug, None, SnesBugPlainFight }

    public sealed class SnesRelicPenaltiesMod
    {
        public string Name = "";
        public bool Offering = true;               // x0.5 on every physical hit by a character (else the PR's Attack-only)
        public GenjiRule Genji = GenjiRule.SnesBug;
        public Func<bool> Enabled = () => true;
    }

    public static class SnesRelicPenalties
    {
        static SnesRelicPenaltiesMod mod;
        static int[] offeringIds, genjiIds;        // the game's own accessory lists (PhysicalCorrectionAccessoryIds 0 / 1)
        public static int TestForce;               // tests: 1 = counts as wearing the Offering, 2 = the Genji Glove
        public static int Applied;                 // tests

        public static void Apply(Harmony h, SnesRelicPenaltiesMod m)
        {
            mod = m;
            Hooks.Patch(h, typeof(CalcExecuteCorrection), "GetAccessoryPhysicalCorrectionValue", typeof(SnesRelicPenalties), postfix: nameof(Correction_Postfix));
            Hooks.Patch(h, typeof(CalcControllerProvider), "GetFightStatus", typeof(SnesRelicPenalties), postfix: nameof(Result_Postfix));
            Hooks.Patch(h, typeof(CalcControllerProvider), "GetThrowStatus", typeof(SnesRelicPenalties), postfix: nameof(Result_Postfix));
            Kit.Info($"[relics] {m.Name}: Offering {(m.Offering ? "x0.5 on every physical hit by a character (SNES)" : "the PR's (Attack only)")}; " +
                     $"Genji Glove {(m.Genji == GenjiRule.SnesBug ? "-25% unless dual wielding (the SNES's own bug)" : m.Genji == GenjiRule.SnesBugPlainFight ? "-25% unless dual wielding, on Attack only (T-Edition)" : m.Genji == GenjiRule.Pr ? "the PR's (-25% on Attack while dual wielding)" : "no penalty")}");
        }

        static bool On => mod != null && mod.Enabled();

        // the PR's own Attack-only steps, off where the mod's rule takes over
        public static void Correction_Postfix(CalcExecuteCorrection.PhysicalType physicalType, ref float __result)
        {
            if (!On) return;
            if (physicalType == CalcExecuteCorrection.PhysicalType.PilgrimsPassport && mod.Offering) __result = 0f;
            else if (physicalType == CalcExecuteCorrection.PhysicalType.GenchisBasho && mod.Genji != GenjiRule.Pr) __result = 0f;
        }

        static int[] Ids(int key)
        {
            try
            {
                var prop = typeof(CalcExecuteCorrection).GetProperty("PhysicalCorrectionAccessoryIds",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var dict = prop?.GetValue(null) as Il2CppSystem.Collections.Generic.Dictionary<int, Il2CppStructArray<int>>;
                if (dict == null || !dict.ContainsKey(key)) return Array.Empty<int>();
                var a = dict[key];
                var r = new int[a.Length];
                for (int i = 0; i < r.Length; i++) r[i] = a[i];
                return r;
            }
            catch (Exception) { return Array.Empty<int>(); }
        }

        static bool Wears(OwnedCharacterData c, int[] ids)
        {
            var eq = c?.EquipmentData;
            if (eq == null || ids.Length == 0) return false;
            foreach (var it in eq.Values)
            {
                var armor = it?.Armor;
                if (armor != null && Array.IndexOf(ids, armor.Id) >= 0) return true;
            }
            return false;
        }

        // two real weapons in hand (the SNES's weapon/weapon case)
        static bool TwoWeapons(OwnedCharacterData c)
        {
            var eq = c?.EquipmentData;
            if (eq == null || !eq.ContainsKey(EquipSlotType.Slot1) || !eq.ContainsKey(EquipSlotType.Slot2)) return false;
            var r = eq[EquipSlotType.Slot1]?.Weapon;
            var l = eq[EquipSlotType.Slot2]?.Weapon;
            return r != null && l != null && r.TypeId != 2 && l.TypeId != 2;
        }

        // the SNES's factor on a character's physical hit (1: none); plainFight: the hit is the PR's Attack
        // (ability type 4), for GenjiRule.SnesBugPlainFight
        public static double Factor(OwnedCharacterData c, out string why, bool plainFight = true)
        {
            why = "";
            if (!On || c == null) return 1.0;
            offeringIds ??= Ids(0);
            genjiIds ??= Ids(1);
            double f = 1.0;
            if (mod.Offering && ((TestForce & 1) != 0 || Wears(c, offeringIds))) { f *= 0.5; why += "Offering x0.5 "; }
            bool genji = mod.Genji == GenjiRule.SnesBug || (mod.Genji == GenjiRule.SnesBugPlainFight && plainFight);
            if (genji && ((TestForce & 2) != 0 || Wears(c, genjiIds)) && !TwoWeapons(c)) { f *= 0.75; why += "Genji Glove -25% "; }
            return f;
        }

        public static void Result_Postfix(CalcControllerProvider __instance, ICalcResult __result)
        {
            if (!On || __result == null || __instance == null) return;
            try
            {
                var c = __instance.userCharaData;
                if (c == null) return;   // monsters: none
                var hit = __result.GetHitType();
                if (hit != HitType.Hit && hit != HitType.Critical) return;
                int value = __result.GetValue(false);
                if (value <= 0) return;
                double f = Factor(c, out var why, __instance.ability != null && __instance.ability.TypeId == 4);
                if (f >= 0.999) return;
                // the SNES's integer steps: the Offering's LSR, then damage - damage / 4
                int v = value;
                if (why.Contains("Offering")) v >>= 1;
                if (why.Contains("Genji")) v -= v >> 2;
                v = Math.Max(1, v);
                __result.SetValue(v);
                Applied++;
                string who = c.Name, w = why.Trim();
                Hooks.NoteOnce("snes-relic-" + w, () => $"[relics] {mod.Name}: {who}'s physical hit {value} -> {v} ({w}, the SNES's)");
            }
            catch (Exception e) { Hooks.WarnOnce("SnesRelicPenalties", e); }
        }

        // In a battle (a mod's test run decides when): the first party member's Fight and Mug through the game's
        // own functions (nothing applied), 30 each with the Offering, the Genji Glove (one weapon) and neither
        // counted as worn, the Genji Glove with the same weapon in both hands. Null until a battle.
        public static string SelfTest()
        {
            if (!On) return $"[relics] SNES relic penalties self-test: off";
            var battle = BattlePlugManager.Instance();
            if (battle == null || !battle.IsBattle()) return null;
            var players = battle.GetPlayerUnits();
            var enemies = battle.GetEnemyUnits();
            var mm = MasterManager.Instance;
            var fight = mm?.GetData<Command>(1);
            var attack = mm?.GetData<Ability>(1);
            var mugCmd = mm?.GetData<Command>(11);
            var mug = mm?.GetData<Ability>(9);
            if (players == null || players.Count == 0 || enemies == null || enemies.Count == 0 || fight == null || attack == null) return null;
            BattleUnitData p = players[0], e = enemies[0];
            var oc = p.TryCast<BattlePlayerData>()?.ownedCharacterData;
            if (oc == null) return null;
            double Mean(Command cmd, Ability ab)
            {
                double sum = 0;
                int n = 0;
                for (int i = 0; i < 30; i++)
                {
                    var v = Hit(p, e, cmd, ab);
                    if (v > 0) { sum += v; n++; }
                }
                return n == 0 ? 0 : sum / n;
            }
            int was = TestForce;
            var eq = oc.EquipmentData;
            OwnedItemData left = null;
            bool hasLeft = eq != null && eq.ContainsKey(EquipSlotType.Slot2);
            if (hasLeft) left = eq[EquipSlotType.Slot2];
            try
            {
                TestForce = 0;
                double plain = Mean(fight, attack);
                double mugPlain = mug != null ? Mean(mugCmd, mug) : 0;
                TestForce = 1;
                double offering = Mean(fight, attack);
                double mugOffering = mug != null ? Mean(mugCmd, mug) : 0;
                TestForce = 2;
                bool two = TwoWeapons(oc);
                double genji = Mean(fight, attack);
                double genjiDual = 0;
                var right = eq != null && eq.ContainsKey(EquipSlotType.Slot1) ? eq[EquipSlotType.Slot1] : null;
                if (!two && right?.Weapon != null && right.Weapon.TypeId != 2 && hasLeft)
                {
                    eq[EquipSlotType.Slot2] = right;
                    genjiDual = Mean(fight, attack);
                }
                double rO = plain > 0 ? offering / plain : 0, rM = mugPlain > 0 ? mugOffering / mugPlain : 0;
                double rG = plain > 0 ? genji / plain : 0, rD = plain > 0 && genjiDual > 0 ? genjiDual / plain : 0;
                bool ok = plain > 0 && Math.Abs(rO - 0.5) < 0.08 && (mug == null || mugPlain == 0 || Math.Abs(rM - 0.5) < 0.1)
                          && (mod.Genji != GenjiRule.SnesBug || (two ? Math.Abs(rG - 1) < 0.1 : Math.Abs(rG - 0.75) < 0.08))
                          && (mod.Genji != GenjiRule.SnesBug || genjiDual == 0 || Math.Abs(rD - 1) < 0.15);
                return $"[relics] {mod.Name} SNES relic penalties self-test {(ok ? "ok" : "FAILED")}: Fight by {oc.Name}: with the Offering " +
                       $"x{rO:0.00} (SNES x0.5), Mug with it x{rM:0.00} (x0.5; the PR's own: x1); the Genji Glove with " +
                       $"{(two ? "two weapons" : "one weapon")} x{rG:0.00} (SNES: {(two ? "x1" : "x0.75")}), with the same weapon in both hands " +
                       $"{(genjiDual > 0 ? $"x{rD:0.00} (SNES x1)" : "not tried")}";
            }
            finally
            {
                TestForce = was;
                if (hasLeft) eq[EquipSlotType.Slot2] = left;
            }
        }

        // one act through every function of the ability (nothing applied): the value on the target, 0 if no hit
        static int Hit(BattleUnitData attacker, BattleUnitData target, Command cmd, Ability ab)
        {
            var abilities = new Il2CppSystem.Collections.Generic.List<Ability>();
            abilities.Add(ab);
            var targets = new Il2CppReferenceArray<BattleUnitData>(1);
            targets[0] = target;
            var act = new BattleActData(attacker, targets, cmd, abilities, BattleActData.ActType.Normal,
                (BattleActData.TargetType)0, BattleActData.ArmType.Right, (BattleActData.ActAbilityType)0, 1, false);
            var fns = ParameterActFunctionManagment.GetFunction(ab);
            if (fns == null) return 0;
            var results = new Il2CppSystem.Collections.Generic.Dictionary<BattleUnitData, ICalcResult>();
            var attacks = new Il2CppSystem.Collections.Generic.Dictionary<BattleUnitData, ICalcResult>();
            int value = 0;
            foreach (var f in fns)
            {
                if (f == null) continue;
                f.Init(act, results, attacks);
                f.FixedTarget();
                f.HitCalc();
                var dic = f.ICalcResultDic ?? results;
                if (dic != null && dic.ContainsKey(target))
                {
                    var r = dic[target];
                    var h = r.GetHitType();
                    value = h == HitType.Hit ? r.GetValue(false) : 0;   // criticals left out: they double
                }
            }
            return value;
        }
    }
}
