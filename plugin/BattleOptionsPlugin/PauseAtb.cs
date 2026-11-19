using System;
using System.Collections.Generic;
using HarmonyLib;
using Last.Battle;
using Last.Management;
using PrKit;
using UnityEngine;
using ActState = Last.Battle.BattleActExection.ActState;

namespace BattleOptions
{
    // Battle Mode: Pause. The game's Wait stops all time in the battle's sub-menus (its ATB update,
    // BattleProgressATBFF6.Update, returns while BattleUIManager.IsWaiting and SystemConfig's
    // ATBBattleType is Wait) and while an action is staged (GetStatingAct / !IsStagingEnd). Pause is
    // Wait, and time also stops (the user, 2026-11-18: "all bars should pause whenever an animation
    // is occuring or i am choosing something"):
    //  * while a character's turn is up: the game's queue of characters waiting for a command
    //    (BattleProgressATB.battleATBOrderUiStack: added when a gauge fills, PrePlayerOrder; left when
    //    a command is chosen, Remove; units that can't act dropped each frame) isn't empty;
    //  * while an action plays out, its preparing and after phases included (BattleActExection's
    //    nowState InPrepare, InAction, InAfterAction, CounterAct, CounterAI: Brave New World's nATB).
    // Then no ATB gauge fills, the party's or the enemies' (ATBCalc, which UpdateGaugeIncrease adds
    // each frame, answers 0; its hook runs last, after other mods' rates: Worlds Collide's SNES rates,
    // Brave New World's formula), and no chosen action's wait counts down (UpdateChantTime, the Cast
    // Bars' clock, skipped). Status timers go on. An action state that lasts over 20 s stops holding
    // time (a state the game never left would otherwise stop the battle).
    class PauseAtb : Feature
    {
        internal static int Frozen;      // enemy gauge updates held (test)
        internal static int ChantsHeld;  // chant countdowns held (test)

        public override void Apply(Harmony h)
        {
            int n = 0;
            var seen = new HashSet<IntPtr>();
            // the game's ATB: FF6's own, FF0's (the one FF4/FF5 may use), the shared base
            foreach (var t in new[] { Hooks.TypeByName("Last.Battle.BattleProgressATBFF6"),
                                      Hooks.TypeByName("Last.Battle.BattleProgressATBFF0"), typeof(BattleProgressATB) })
            {
                var m = t == null ? null : AccessTools.DeclaredMethod(t, "ATBCalc");
                if (m == null) continue;
                if (Patching.Patch(h, m, typeof(PauseAtb), postfix: nameof(Calc_Postfix), priority: Priority.Last)) n++;
            }
            bool chants = Patching.Patch(h, AccessTools.Method(typeof(BattleProgressATB), "UpdateChantTime"), typeof(PauseAtb),
                                         prefix: nameof(Chant_Prefix));
            bool status = Patching.Patch(h, AccessTools.Method(typeof(Last.Battle.BattleConditionController), "Update"), typeof(PauseAtb),
                                         prefix: nameof(Conditions_Prefix), priority: Priority.First);
            Kit.Info($"[pause] {n} ATB gauge calculations hooked, cast countdown {(chants ? "hooked" : "NOT hooked")}, " +
                     $"status timers {(status ? "hooked" : "NOT hooked")}");
        }

        // Status timers (the user, 2026-11-18: "ticking effects (regen/etc) are still ticking with atb
        // pause mode on"): BattleConditionController.Update counts every status's time (Regen, Sap and
        // Poison ticks, Stop/Sleep/Reflect/Freeze running out, Doom's countdown) and is the part of the
        // ATB update the game's own Wait skips in its menus; skipped here while time is held.
        // Another mod's own status clock in a prefix of the same method still runs (HarmonyX runs every
        // prefix), so the frame is also written to AppDomain data "pr.battle.timeHeldFrame": a mod
        // that keeps its own battle clock can skip that frame (Worlds Collide's SNES status clock does).
        // No mod needs the other; without this one the key is never set.
        internal const string HeldKey = "pr.battle.timeHeldFrame";
        internal static int ConditionsHeld;   // status updates held (test)

        public static bool Conditions_Prefix()
        {
            try
            {
                if (!Hold()) return true;
                AppDomain.CurrentDomain.SetData(HeldKey, Time.frameCount);
                ConditionsHeld++;
                return false;
            }
            catch (Exception e)
            {
                Hooks.WarnOnce("pause.status", e);
                return true;
            }
        }

        // false = this frame's cast countdown is skipped (Pause: a command being chosen or an action playing)
        public static bool Chant_Prefix()
        {
            try
            {
                if (!Hold()) return true;
                ChantsHeld++;
                return false;
            }
            catch (Exception e)
            {
                Hooks.WarnOnce("pause.chant", e);
                return true;
            }
        }

        public static void Calc_Postfix(BattleUnitData __0, ref float __result)
        {
            try
            {
                if (__result <= 0f || __0 == null || !Hold()) return;
                __result = 0f;
                Frozen++;
            }
            catch (Exception e) { Hooks.WarnOnce("pause.atb", e); }
        }

        static int frame = -1;
        static bool up, held;
        static float animatingSince = -1f;
        static bool gaveUp;

        // time stops this frame (Pause chosen, the game in Wait, a turn up or an action playing); once a frame
        internal static bool Hold()
        {
            if (!Settings.Pause.Value) return false;
            int f = Time.frameCount;
            if (f == frame) return held;
            frame = f;
            up = false;
            held = false;
            var sys = SystemConfig.Instance();
            if (sys == null || sys.ATBBattleType != ATBBattleType.Wait) return false;
            up = Waiting() > 0;
            held = up || Animating();
            return held;
        }

        // a character's turn is up (test, and the menu's help)
        internal static bool TurnUp() { Hold(); return up; }

        static bool Animating()
        {
            var ex = BattlePlugManager.Instance()?.BattleActExection;
            if (ex == null) return false;
            var s = ex.nowState;
            bool acting = s == ActState.InPrepare || s == ActState.InAction || s == ActState.InAfterAction ||
                          s == ActState.CounterAct || s == ActState.CounterAI;
            float now = Time.unscaledTime;
            if (!acting) { animatingSince = -1f; gaveUp = false; return false; }
            if (animatingSince < 0f) animatingSince = now;
            if (!gaveUp && now - animatingSince > 20f)
            {
                gaveUp = true;
                Hooks.NoteOnce("pause-gave-up", () => $"[pause] action state {s} held time over 20 s: time runs on until it changes");
            }
            return !gaveUp;
        }

        // characters waiting for a command
        internal static int Waiting()
        {
            var p = BattlePlugManager.Instance()?.BattleProgress?.TryCast<BattleProgressATB>();
            var stack = p?.battleATBOrderUiStack?.orderUiStack;
            return stack == null ? 0 : stack.Count;
        }
    }
}
