using System;
using System.Collections.Generic;
using HarmonyLib;
using Last.Battle;
using UnityEngine;

namespace PrKit.Ff6
{
    // Time stops while an action plays out: no ATB gauge fills, no chosen act's wait counts down, no status timer
    // runs, running away doesn't charge (UpdateEscape). Brave New World's nATB (c2.asm C2/1124 ORA $3A8F: the battle loop skips while an animation script
    // runs; readme "time pauses during animations"); off unless a mod turns it on (never for Worlds Collide,
    // whose SNES rules keep time running through actions, as vanilla FF6 does).
    // The PR's FF6 update (BattleProgressATBFF6.Update) runs through actions (the engine's shared
    // BattleProgressATB.Update, which FF6 overrides, would stop for them), so the hooks are on what it calls,
    // as Battle Options' Pause (BattleOptionsPlugin/PauseAtb.cs, released, the same hooks) found them:
    //  * the gauge: BattleProgressATBFF6.ATBCalc answers 0 (a postfix that runs last, after every mod's rate);
    //  * the chant: BattleProgressATB.UpdateChantTime skipped;
    //  * the status timers: BattleConditionController.Update skipped (Regen/Sap/Poison ticks, Stop, Sleep,
    //    Reflect, Freeze running out, Doom); the frame goes to AppDomain data "pr.battle.timeHeldFrame" for a
    //    mod's own status clock (SnesStatusTicks skips it).
    // "An action plays out": BattleActExection.GetStatingAct() (its state machine not in None; nowState is never
    // written by the game, so it can't tell: Battle Options 0.2.1's finding). WhileTurnUp: also while any character
    // waits for a command (Battle Options' Pause; BNW's menus stop time through the game's own Wait instead,
    // BattleSettingsLock). Held no longer than TimeHold.GiveUp seconds in a row.
    // Not held (unknown, logged in tests): BattlePlugManager's elapsed battle time and the dying-special timer,
    // which the FF6 update also advances.
    public static class SnesActionFreeze
    {
        public static Func<bool> On = () => false;              // the mod's rule is on
        public static Func<bool> WhileTurnUp = () => false;     // also while a character waits for a command
        public static readonly TimeHold Hold = new TimeHold();
        public static string Tag = "[kit]";
        public const string HeldKey = "pr.battle.timeHeldFrame";
        public static int GaugesHeld, ChantsHeld, ConditionsHeld;
        static bool patched;

        public static void Patch(Harmony h)
        {
            if (patched) return;
            patched = true;
            bool gauge = Hooks.Patch(h, AccessTools.DeclaredMethod(typeof(BattleProgressATBFF6), "ATBCalc"), typeof(SnesActionFreeze),
                                     postfix: nameof(Calc_Postfix), priority: Priority.Last);
            bool chants = Hooks.Patch(h, AccessTools.Method(typeof(BattleProgressATB), "UpdateChantTime"), typeof(SnesActionFreeze),
                                      prefix: nameof(Chant_Prefix), priority: Priority.First);
            bool status = Hooks.Patch(h, AccessTools.Method(typeof(BattleConditionController), "Update"), typeof(SnesActionFreeze),
                                      prefix: nameof(Conditions_Prefix), priority: Priority.First);
            // running away charges on the battle clock too (BattleProgressATB.UpdateEscape counts the frame's time)
            bool escape = Hooks.Patch(h, AccessTools.Method(typeof(BattleProgressATB), "UpdateEscape"), typeof(SnesActionFreeze),
                                      prefix: nameof(Escape_Prefix), priority: Priority.First);
            Kit.Info($"{Tag} action freeze: gauge {(gauge ? "hooked" : "NOT hooked")}, chants {(chants ? "hooked" : "NOT hooked")}, " +
                     $"status timers {(status ? "hooked" : "NOT hooked")}, running away {(escape ? "hooked" : "NOT hooked")}");
        }

        public static int EscapesHeld;

        // false = this frame's running-away charge skipped
        public static bool Escape_Prefix()
        {
            try
            {
                if (!Held()) return true;
                EscapesHeld++;
                return false;
            }
            catch (Exception e)
            {
                Hooks.WarnOnce("freeze.escape", e);
                return true;
            }
        }

        static int frame = -1;
        static bool held, acting, up;
        static int logged;

        // whether time stops this frame (once a frame)
        public static bool Held()
        {
            int f = Time.frameCount;
            if (f == frame) return held;
            frame = f;
            held = acting = up = false;
            if (!On()) return false;
            var ex = BattlePlugManager.Instance()?.BattleActExection;
            acting = ex != null && ex.GetStatingAct();
            int before = Hold.Holds;
            bool hold = Hold.Update(acting, Time.unscaledTime);
            if (Hold.Holds != before && logged < 3)
            {
                logged++;
                Kit.Info($"{Tag} action freeze: time held {Hold.LastHeld:0.00} s for an action");
            }
            if (Hold.GaveUp)
                Hooks.NoteOnce("freeze-gave-up", () => $"{Tag} action freeze: an action held time over {Hold.GiveUp:0} s: time runs on until it ends");
            up = WhileTurnUp() && SnesQuickfill.Waiting() > 0;
            held = hold || up;
            return held;
        }

        public static bool Acting { get { Held(); return acting; } }

        public static void Calc_Postfix(BattleUnitData __0, ref float __result)
        {
            try
            {
                if (__result <= 0f || __0 == null || !Held()) return;
                __result = 0f;
                GaugesHeld++;
            }
            catch (Exception e) { Hooks.WarnOnce("freeze.atb", e); }
        }

        // false = this frame's chant countdown skipped
        public static bool Chant_Prefix()
        {
            try
            {
                if (!Held()) return true;
                ChantsHeld++;
                return false;
            }
            catch (Exception e)
            {
                Hooks.WarnOnce("freeze.chant", e);
                return true;
            }
        }

        // false = this frame's status timers skipped
        public static bool Conditions_Prefix()
        {
            try
            {
                if (!Held()) return true;
                AppDomain.CurrentDomain.SetData(HeldKey, Time.frameCount);
                ConditionsHeld++;
                return false;
            }
            catch (Exception e)
            {
                Hooks.WarnOnce("freeze.status", e);
                return true;
            }
        }
    }
}
