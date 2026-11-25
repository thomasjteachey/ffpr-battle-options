using System;
using Last.Management;
using Last.UI;
using UnityEngine;

namespace PrKit.Ff6
{
    // The battle clock's seconds this frame, for what runs on the SNES's battle time outside the PR's own ATB update:
    // the AI scripts' battle timer and monster timers (C2/5BFC: every 16 frames of battle time; SnesAiHost asks its
    // mod, SnesAiMod.BattleClockDt). On the SNES those stop and go with everything else on the clock (C2/111B's
    // frame loop): not in Wait's menus, not while Brave New World's nATB holds time for an animation (C2/1124),
    // twice as fast under its Quickfill (EF/FBC8). The PR's counterparts:
    //  * the game's own pause: FF6's battle update (BattleProgressATBFF6.Update, tools/decomp/battle) returns while
    //    BattleUIManager.IsForceWaiting, and in Wait (SystemConfig.ATBBattleType) while IsWaiting (a sub-menu);
    //  * SnesActionFreeze.Held() (0) and SnesQuickfill.Factor() (x2): each answers "no change" unless its mod turned
    //    it on, so a mod that only wants the pause gets just that.
    // Off unless a mod asks (Brave New World's SnesAiMod); a mod that doesn't keeps the frame's time.
    public static class SnesBattleClock
    {
        public static float Dt()
        {
            float dt = Time.deltaTime;
            try
            {
                if (Paused() || SnesActionFreeze.Held()) return 0f;
                return dt * SnesQuickfill.Factor();
            }
            catch (Exception e)
            {
                Hooks.WarnOnce("battleclock", e);
                return dt;
            }
        }

        // the game's own pause this frame (FF6's battle update doesn't run)
        public static bool Paused()
        {
            var ui = BattleUIManager.Instance;
            if (ui == null) return false;
            if (ui.IsForceWaiting()) return true;
            var sys = SystemConfig.Instance();
            return sys != null && sys.ATBBattleType == ATBBattleType.Wait && ui.IsWaiting();
        }
    }
}
