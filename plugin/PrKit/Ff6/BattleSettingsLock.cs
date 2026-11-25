using System;
using HarmonyLib;
using Last.Battle;
using Last.Management;

namespace PrKit.Ff6
{
    // A mod whose SNES ROM took the Active/Wait and Battle Speed settings out (Brave New World: c3.asm's Bat.Mode
    // and Bat.Speed removed; its nATB is always "Wait", its Speed Balance has no battle-speed term): those two
    // Config rows hidden and battles in Wait, without writing the player's saved config (one config for every mod
    // installed: the one synced save folder).
    //  * Wait: the FF6 battle update reads SystemConfig.ATBBattleType (in memory, set from the saved config);
    //    it's set to Wait on each battle frame while On (and put back once On is off).
    //  * the rows: ConfigRows.Hide removes battle speed, and battle mode unless Battle Options
    //    supplies its Wait/Pause choice.
    // Not hooked (2026-11-21): BattleUtility.GetBattleModeByConfigValue and GetBattleSpeedByConfigValue. Each has a
    // short conditional jump in its first 14 bytes, which a detour moves and breaks: the (int) mode one crashed BNW's
    // battles as they started (GameAssembly.dll+0x91FD24, an int3 past its end), and the speed one (movss, test,
    // je at byte 10) has the same shape. So the battle speed isn't pinned: the player's saved setting (hidden, the
    // middle x1 unless changed before) still paces what reads it; a mod's own waits read the same value
    // (SnesAtb's chants multiply by it), so they come out right at any setting.
    // Unknown until checked in game: whether anything copies SystemConfig back into the saved config.
    public static class BattleSettingsLock
    {
        public static Func<bool> On = () => false;
        public static string Tag = "[kit]";
        public const int MiddleSpeed = 2;                    // the PR's battle speed settings 0-4; 2 = x1 (not enforced)
        static bool patched, forced;
        static ATBBattleType before;
        public static int Forced;

        public static void Patch(Harmony h)
        {
            if (patched) return;
            patched = true;
            bool frame = Hooks.Patch(h, AccessTools.DeclaredMethod(typeof(BattleProgressATBFF6), "Update"), typeof(BattleSettingsLock),
                                     prefix: nameof(Frame_Prefix), priority: Priority.First);
            // Battle Options supplies a Wait/Pause choice. Keep that row for its own menu hook;
            // nATB still keeps the underlying game mode in Wait for either choice.
            ConfigRows.Hide(h, 1, () => On() && !BattleOptionsPresent());
            ConfigRows.Hide(h, 2, On);   // ConfigCommandType.BattleSpeed
            Kit.Info($"{Tag} battle settings lock: battle frame {(frame ? "hooked (Wait)" : "NOT hooked")}; " +
                     "Battle Speed hidden; Battle Mode hidden without Battle Options");
        }

        public static bool BattleOptionsPresent() =>
            AppDomain.CurrentDomain.GetData("pr.battleoptions.present") is bool present && present;

        public static void Frame_Prefix()
        {
            try
            {
                var sys = SystemConfig.Instance();
                if (sys == null) return;
                if (On())
                {
                    if (sys.ATBBattleType == ATBBattleType.Wait) return;
                    if (!forced) { before = sys.ATBBattleType; forced = true; }
                    sys.ATBBattleType = ATBBattleType.Wait;
                    Forced++;
                    Hooks.NoteOnce("lock-wait", () => $"{Tag} battle settings lock: battles in Wait (the saved setting was {before})");
                }
                else if (forced)
                {
                    sys.ATBBattleType = before;
                    forced = false;
                }
            }
            catch (Exception e) { Hooks.WarnOnce("lock.frame", e); }
        }
    }
}
