using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Last.Battle;
using Last.Defaine;

namespace PrKit.Ff6
{
    // The physical hit rate against a target's statuses, for any FF6 mod (2026-11-20,
    // pr/SNES_PR_DISCREPANCIES.md). Vanilla SNES (C2/235B-C2/2387): x1.25 against Blind, Zombie,
    // Muddle, Slow or Rerise; x0.75 against Poison, Near Fatal, Seizure or Haste. The PR
    // (decompiled PhysicalExecution, tools/decomp/physical) does both steps from two static arrays
    // read on every hit, CalcExecuteFF6.HitRatePlusConditions (x1.25, capped at 255) and
    // HitRateMinusConditions (x0.75): the same statuses, plus Charm (Temptation) in the first,
    // which the SNES list doesn't have. A mod takes statuses out of either list once at startup
    // (reading the static fields runs the class's static constructor first: Il2CppInterop).
    public static class SnesHitRates
    {
        public static readonly ConditionType[] NotOnSnes = { ConditionType.Temptation };
        static bool done;

        // a log line once the arrays are changed; null when already done
        public static string Fix(string tag, ICollection<ConditionType> dropPlus, ICollection<ConditionType> dropMinus)
        {
            if (done) return null;
            done = true;
            var plus = CalcExecuteFF6.HitRatePlusConditions;
            var minus = CalcExecuteFF6.HitRateMinusConditions;
            if (plus == null || minus == null) return $"[hit] {tag}: the game's hit-rate status lists weren't found: left as they are";
            string wasPlus = Names(plus), wasMinus = Names(minus);
            CalcExecuteFF6.HitRatePlusConditions = Without(plus, dropPlus);
            CalcExecuteFF6.HitRateMinusConditions = Without(minus, dropMinus);
            return $"[hit] {tag} hit-rate statuses: x1.25 against {wasPlus} -> {Names(CalcExecuteFF6.HitRatePlusConditions)}; " +
                   $"x0.75 against {wasMinus} -> {Names(CalcExecuteFF6.HitRateMinusConditions)}";
        }

        static Il2CppStructArray<ConditionType> Without(Il2CppStructArray<ConditionType> a, ICollection<ConditionType> drop)
        {
            var keep = new List<ConditionType>();
            foreach (var c in a)
                if (drop == null || !drop.Contains(c)) keep.Add(c);
            var r = new Il2CppStructArray<ConditionType>(keep.Count);
            for (int i = 0; i < keep.Count; i++) r[i] = keep[i];
            return r;
        }

        public static string Names(Il2CppStructArray<ConditionType> a)
        {
            if (a == null) return "(null)";
            if (a.Length == 0) return "(none)";
            var s = new List<string>();
            foreach (var c in a) s.Add(c.ToString());
            return string.Join(", ", s);
        }
    }
}
