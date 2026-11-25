using System;
using System.Reflection;
using HarmonyLib;
using Last.Battle;
using UnityEngine;

namespace PrKit.Ff6
{
    // Brave New World's Quickfill (ef.asm EF/FBC8 from C2/112C BattleFrameLoop; readme: "the ATB fills twice as
    // fast while no one's turn is up"): the SNES's battle loop runs every frame instead of every second one while
    // no character holds a battle-menu slot, so everything on the battle clock goes x2: ATB gauges (monsters'
    // too), the chosen acts' waits, the status timers, running away's charge. Off unless a mod turns it on.
    // The PR's counterpart of the menu slots ($4001-$4004: taken when a gauge fills, given back when the command
    // is chosen) is its queue of characters waiting for a command (BattleProgressATB.battleATBOrderUiStack:
    // PrePlayerOrder adds, a chosen command removes). While it's empty (SnesAtbRules.QuickfillFactor):
    //  * the gauge: BattleProgressATBFF6.ATBCalc's answer x2 (a postfix after the mods' rates, before
    //    SnesActionFreeze's, which runs last);
    //  * the chants and the status timers: BattleProgressATB.UpdateChantTime and BattleConditionController.Update
    //    run a second time that frame (each counts its own Time.deltaTime: a second run is a second frame's worth,
    //    as the SNES's second pass through its loop). SnesActionFreeze's prefixes hold those runs too.
    // The game's Wait (a sub-menu open) stops the whole FF6 update, so none of this runs then. Not doubled
    // (unknown, logged in tests): BattlePlugManager's elapsed battle time, the dying-special timer.
    public static class SnesQuickfill
    {
        public static Func<bool> On = () => false;              // the mod's rule is on
        public static string Tag = "[kit]";
        public static int GaugesDoubled, ChantsDoubled, ConditionsDoubled, EscapesDoubled;
        static bool patched, inChant, inConditions, inEscape;
        static MethodInfo chantRun, conditionsRun, escapeRun;

        public static void Patch(Harmony h)
        {
            if (patched) return;
            patched = true;
            chantRun = AccessTools.Method(typeof(BattleProgressATB), "UpdateChantTime");
            conditionsRun = AccessTools.Method(typeof(BattleConditionController), "Update");
            bool gauge = Hooks.Patch(h, AccessTools.DeclaredMethod(typeof(BattleProgressATBFF6), "ATBCalc"), typeof(SnesQuickfill),
                                     postfix: nameof(Calc_Postfix), priority: Priority.Low);
            bool chants = Hooks.Patch(h, chantRun, typeof(SnesQuickfill), postfix: nameof(Chant_Postfix));
            bool status = Hooks.Patch(h, conditionsRun, typeof(SnesQuickfill), postfix: nameof(Conditions_Postfix));
            escapeRun = AccessTools.Method(typeof(BattleProgressATB), "UpdateEscape");   // running away's charge
            bool escape = Hooks.Patch(h, escapeRun, typeof(SnesQuickfill), postfix: nameof(Escape_Postfix));
            Kit.Info($"{Tag} quickfill: gauge {(gauge ? "hooked" : "NOT hooked")}, chants {(chants ? "hooked" : "NOT hooked")}, " +
                     $"status timers {(status ? "hooked" : "NOT hooked")}, running away {(escape ? "hooked" : "NOT hooked")}");
        }

        public static void Escape_Postfix(BattleProgressATB __instance)
        {
            if (inEscape || __instance == null) return;
            try
            {
                if (Factor() == 1) return;
                inEscape = true;
                escapeRun.Invoke(__instance, null);
                EscapesDoubled++;
            }
            catch (Exception e) { Hooks.WarnOnce("quickfill.escape", e); }
            finally { inEscape = false; }
        }

        // characters waiting for a command (the game's queue of turns up)
        public static int Waiting()
        {
            var p = BattlePlugManager.Instance()?.BattleProgress?.TryCast<BattleProgressATB>();
            var stack = p?.battleATBOrderUiStack?.orderUiStack;
            return stack == null ? 0 : stack.Count;
        }

        static int frame = -1, factor = 1;

        // this frame's time factor (once a frame): 2 while no one waits for a command, else 1
        public static int Factor()
        {
            int f = Time.frameCount;
            if (f == frame) return factor;
            frame = f;
            factor = On() ? SnesAtbRules.QuickfillFactor(Waiting()) : 1;
            return factor;
        }

        public static void Calc_Postfix(BattleUnitData __0, ref float __result)
        {
            try
            {
                if (__result <= 0f || __0 == null || Factor() == 1) return;
                __result *= 2f;
                GaugesDoubled++;
            }
            catch (Exception e) { Hooks.WarnOnce("quickfill.atb", e); }
        }

        public static void Chant_Postfix(BattleProgressATB __instance)
        {
            if (inChant || __instance == null) return;
            try
            {
                if (Factor() == 1) return;
                inChant = true;
                chantRun.Invoke(__instance, null);
                ChantsDoubled++;
            }
            catch (Exception e) { Hooks.WarnOnce("quickfill.chant", e); }
            finally { inChant = false; }
        }

        public static void Conditions_Postfix(BattleConditionController __instance)
        {
            if (inConditions || __instance == null) return;
            try
            {
                if (Factor() == 1) return;
                inConditions = true;
                conditionsRun.Invoke(__instance, null);
                ConditionsDoubled++;
            }
            catch (Exception e) { Hooks.WarnOnce("quickfill.status", e); }
            finally { inConditions = false; }
        }
    }
}
