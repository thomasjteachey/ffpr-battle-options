using System;
using System.Collections.Generic;
using HarmonyLib;
using PrKit;

namespace PrKit.Ff6
{
    // A randomizer's esper level-up bonuses (SNES esper byte 10: 0-16, 0xFF none; tools/seed_info.py
    // esper_bonus: vanilla's and the seed's by PR magicite content id). The PR builds its bonuses in code
    // (Serial.FF6.Management.MagicStoneData: type, value, text by magicite content id), so an esper whose
    // bonus changed answers as a vanilla esper with that bonus does (its "donor"). Four codes no vanilla
    // esper has: Speed +2 (Speed +1's, doubled), HP +100% (HP +10%'s, x10), and BC's LV -1
    // and LV +50% (half of the next level's EXP requirement). No bonus: an esper without one. Written for Worlds Collide
    // (WcEspers, -ebs/-ebr), in the kit
    // since 2026-11-20 for Beyond Chaos (its `e`).
    public static class SnesEsperBonus
    {
        public static Func<IReadOnlyDictionary<int, int>> Seed = () => null;      // the seed's codes where they differ
        public static Func<IReadOnlyDictionary<int, int>> Vanilla = () => null;   // vanilla's codes, every esper
        public static Action<string, string> SetText = (key, text) => GameText.Set(key, text);
        public static string Tag = "[kit]";
        public static string LastBonus = "";
        static bool busy;

        const int Hp10 = 0, Hp100 = 6, LevelMinus1 = 7, Level50 = 8, Speed1 = 11, Speed2 = 12, None = 0xFF;

        public static void Patch(Harmony h)
        {
            var t = typeof(Serial.FF6.Management.MagicStoneData);
            int n = 0;
            foreach (var (name, post) in new[] { ("GetParameterType", nameof(Type_Postfix)), ("GetParameterValue", nameof(Value_Postfix)),
                                                 ("GetBonusBaseValue", nameof(Base_Postfix)), ("GetMagicBonusMessageId", nameof(Message_Postfix)) })
            {
                var m = AccessTools.Method(t, name);
                if (m == null) continue;
                h.Patch(m, postfix: new HarmonyMethod(typeof(SnesEsperBonus), post));
                n++;
            }
            Kit.Info($"{Tag} esper bonuses: {n} of 4 lookups hooked");
            var level = AccessTools.Method(typeof(Last.Systems.StatusUpProvider), "Execution");
            if (level != null)
            {
                h.Patch(level, prefix: new HarmonyMethod(typeof(SnesEsperBonus), nameof(Level_Prefix)),
                               postfix: new HarmonyMethod(typeof(SnesEsperBonus), nameof(Level_Postfix)));
                Kit.Info($"{Tag} esper LV -1 / LV +50% hooked");
            }
        }

        // (donor content id, scale) for an esper whose bonus the seed changed; null = the PR's own
        public static (int Donor, int Scale, int Code)? Donor(int contentId)
        {
            var seed = Seed();
            var vanilla = Vanilla();
            if (seed == null || vanilla == null || seed.Count == 0 || !seed.TryGetValue(contentId, out int code)) return null;
            int scale = 1, want = code;
            bool HasCode(int c) { foreach (var v in vanilla.Values) if (v == c) return true; return false; }
            if (code == Speed2 && !HasCode(Speed2)) { want = Speed1; scale = 2; }
            else if (code == Hp100 && !HasCode(Hp100)) { want = Hp10; scale = 10; }
            else if (code == LevelMinus1 || code == Level50) want = None;
            else if (code > 16) want = None;
            foreach (var kv in vanilla)
                if (kv.Value == want) return (kv.Key, scale, code);
            return null;
        }

        public static void Type_Postfix(Serial.FF6.Management.MagicStoneData __instance, int contentId, ref Last.Defaine.ParameterType __result)
        {
            if (busy) return;
            var d = Donor(contentId);
            if (d == null) return;
            busy = true;
            try { __result = __instance.GetParameterType(d.Value.Donor); }
            catch (Exception e) { Hooks.WarnOnce("esper-type", e); }
            finally { busy = false; }
        }

        public static void Value_Postfix(Serial.FF6.Management.MagicStoneData __instance, int contentId, int value, ref int __result)
        {
            if (busy) return;
            var d = Donor(contentId);
            if (d == null) return;
            busy = true;
            try
            {
                __result = __instance.GetParameterValue(d.Value.Donor, value) * d.Value.Scale;
                LastBonus = $"esper {contentId}: bonus code {d.Value.Code} as esper {d.Value.Donor}'s x{d.Value.Scale} = {__result} (base {value})";
            }
            catch (Exception e) { Hooks.WarnOnce("esper-value", e); }
            finally { busy = false; }
        }

        public static void Base_Postfix(Serial.FF6.Management.MagicStoneData __instance, int contentId, ref int __result)
        {
            if (busy) return;
            var d = Donor(contentId);
            if (d == null) return;
            busy = true;
            try { __result = __instance.GetBonusBaseValue(d.Value.Donor) * d.Value.Scale; }
            catch (Exception e) { Hooks.WarnOnce("esper-base", e); }
            finally { busy = false; }
        }

        public static void Message_Postfix(Serial.FF6.Management.MagicStoneData __instance, int contentId, ref string __result)
        {
            if (busy) return;
            var d = Donor(contentId);
            if (d == null) return;
            busy = true;
            try
            {
                var key = __instance.GetMagicBonusMessageId(d.Value.Donor);
                if (d.Value.Code == Speed2 && d.Value.Scale == 2)
                    key = key == "MSG_MAG_INF_146" ? "MSG_MAG_INF_166" : key == "MSG_MAG_INF_161" ? "MSG_MAG_INF_167" : key;
                else if (d.Value.Code == Hp100 && d.Value.Scale == 10)
                {
                    bool longText = key == "MSG_MAG_INF_138";
                    key = longText ? "WC_ESPER_HP100" : "WC_ESPER_HP100_SHORT";
                    SetText(key, longText ? "+100% bonus to HP increase at next level-up" : "HP +100%");
                }
                else if (d.Value.Code == LevelMinus1)
                {
                    key = "BC_ESPER_LEVEL_MINUS1";
                    SetText(key, "LV -1");
                }
                else if (d.Value.Code == Level50)
                {
                    key = "BC_ESPER_LEVEL50";
                    SetText(key, "LV +50% (EXP toward the next level)");
                }
                __result = key;
            }
            catch (Exception e) { Hooks.WarnOnce("esper-text", e); }
            finally { busy = false; }
        }

        // BC's code 8 (randomizer.py: manage_esper_boosts) adds half of the EXP needed
        // from the new level to the following one. Apply it before the PR's level-up pass
        // so that the provider persists the extra EXP with its resulting character data.
        public static void Level_Prefix(Last.Data.User.OwnedCharacterData target, out int __state)
        {
            __state = 0;
            try
            {
                if (target?.Parameter == null || target.MagicStoneId <= 0) return;
                __state = target.Parameter.ConfirmedLevel();
                var seed = Seed();
                if (seed == null || !seed.TryGetValue(target.MagicStoneId, out int code) || code != Level50) return;
                int current = __state;
                if (current >= 99 || target.CurrentExp < 0) return;
                var master = Last.Data.Master.MasterManager.Instance;
                long exp = target.CurrentExp, extra = 0;
                int boosts = 0;
                for (int level = current + 1; level < 99; level++)
                {
                    var reached = master?.GetData<Last.Data.Master.ExpTable>(level);
                    var following = master?.GetData<Last.Data.Master.ExpTable>(level + 1);
                    if (reached == null || following == null || exp + extra < reached.TotalExp) break;
                    int half = (following.TotalExp - reached.TotalExp) / 2;
                    if (half <= 0) break;
                    extra += half;
                    boosts++;
                }
                if (extra == 0) return;
                target.AdditionalExp = (int)Math.Min(16777215L, (long)target.AdditionalExp + extra);
                LastBonus = $"esper {target.MagicStoneId}: LV +50% gave {extra} EXP across {boosts} level-up(s)";
            }
            catch (Exception e) { Hooks.WarnOnce("esper-level50", e); }
        }

        // BC's code 7 (manage_esper_boosts) cancels the gained level and takes one more,
        // resets EXP to the lower level's threshold, and lowers two random stats by one.
        // Its SNES routine leaves a gain to level 2 alone.
        public static void Level_Postfix(Last.Data.User.OwnedCharacterData target,
                                         ref Last.Data.User.OwnedCharacterData __result, int __state)
        {
            try
            {
                if (__state < 1 || target == null || __result?.Parameter == null) return;
                var seed = Seed();
                if (seed == null || !seed.TryGetValue(target.MagicStoneId, out int code) || code != LevelMinus1) return;
                if (__result.Parameter.ConfirmedLevel() <= __state || __result.Parameter.ConfirmedLevel() == 2) return;
                int lowered = Math.Max(1, __state - 1);
                var row = Last.Data.Master.MasterManager.Instance?.GetData<Last.Data.Master.ExpTable>(lowered);
                if (row == null || __result.saveData == null) return;
                __result.Parameter.AddtionalLevel = lowered;
                // CurrentExp includes the character status's starting EXP. AdditionalExp
                // is the saved offset, so zeroing it would leave a level-3 starter's EXP
                // above the level-2 threshold even though the level itself fell.
                __result.AdditionalExp = row.TotalExp - __result.CharacterStatusExp;
                for (int n = 0; n < 2; n++) LowerRandomStat(__result.Parameter);
                LastBonus = $"esper {target.MagicStoneId}: LV -1 changed level {__state} to {lowered}";
            }
            catch (Exception e) { Hooks.WarnOnce("esper-level-minus1", e); }
        }

        static void LowerRandomStat(Last.Data.PlayerCharacterParameter p)
        {
            switch (global::CommonUtility.GetRand(4))
            {
                case 0: if (p.BasePower + p.AddtionalPower > 1) p.AddtionalPower--; break;
                case 1: if (p.BaseAgility + p.AddtionalAgility > 1) p.AddtionalAgility--; break;
                case 2: if (p.BaseVitality + p.AddtionalVitality > 1) p.AddtionalVitality--; break;
                default: if (p.BaseMagic + p.AddtionalMagic > 1) p.AddtionalMagic--; break;
            }
        }
    }
}
