using System;
using System.Collections.Generic;
using HarmonyLib;
using Last.Battle;
using Last.Management;
using PrKit;
using UnityEngine;

namespace BattleOptions
{
    // Battle Mode: Pause. The game's Wait stops all time in the battle's sub-menus (its ATB update,
    // BattleProgressATBFF6.Update, returns while BattleUIManager.IsWaiting and SystemConfig's
    // ATBBattleType is Wait). Pause is Wait, and while a character's turn is up the enemies' ATB
    // gauges don't fill at all: the game's queue of characters waiting for a command
    // (BattleProgressATB.battleATBOrderUiStack: added when a gauge fills, PrePlayerOrder; left when a
    // command is chosen, Remove; units that can't act dropped each frame, BattleATBOrderUiStack.Update)
    // isn't empty. A gauge's increase is ATBCalc (UpdateGaugeIncrease adds it each frame); its hook
    // runs last, after other mods' rates (Worlds Collide's SNES rates, Brave New World's formula).
    // The other characters' gauges, actions already under way and status timers go on.
    class PauseAtb : Feature
    {
        internal static int Frozen;   // enemy gauge updates held (test)

        public override void Apply(Harmony h)
        {
            int n = 0;
            var seen = new HashSet<IntPtr>();
            // the game's ATB: FF6's own, FF0's (the one FF4/FF5 may use), the shared base
            foreach (var t in new[] { AccessTools.TypeByName("Last.Battle.BattleProgressATBFF6"),
                                      AccessTools.TypeByName("Last.Battle.BattleProgressATBFF0"), typeof(BattleProgressATB) })
            {
                var m = t == null ? null : AccessTools.DeclaredMethod(t, "ATBCalc");
                if (m == null) continue;
                if (Patching.Patch(h, m, typeof(PauseAtb), postfix: nameof(Calc_Postfix), priority: Priority.Last)) n++;
            }
            Kit.Info($"[pause] {n} ATB gauge calculations hooked");
        }

        public static void Calc_Postfix(BattleUnitData __0, ref float __result)
        {
            try
            {
                if (__result <= 0f || !Settings.Pause.Value || __0 == null) return;
                if (__0.TryCast<BattleEnemyData>() == null) return;
                if (!TurnUp()) return;
                __result = 0f;
                Frozen++;
            }
            catch (Exception e) { Hooks.WarnOnce("pause.atb", e); }
        }

        static int frame = -1;
        static bool up;

        // a character's turn is up (Pause chosen, the game in Wait); asked once a frame
        internal static bool TurnUp()
        {
            int f = Time.frameCount;
            if (f == frame) return up;
            frame = f;
            up = false;
            var sys = SystemConfig.Instance();
            if (sys == null || sys.ATBBattleType != ATBBattleType.Wait) return false;
            up = Waiting() > 0;
            return up;
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
