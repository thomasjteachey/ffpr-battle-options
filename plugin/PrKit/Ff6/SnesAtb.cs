using System;
using PrKit;
using System.Collections.Generic;
using HarmonyLib;
using Last.Battle;
using Last.Management;
using UnityEngine;

namespace PrKit.Ff6
{
    // WC's (the SNES's) ATB fill in place of the PR's (the user, 2026-11-11: "is ATB/etc on parity
    // with WC too?"). WC doesn't change it, so WC's is vanilla FF6's.
    // SNES (C2/09D2 CalcSpeed, C2/111B UpdateBattleTime): every 2 frames (NTSC 60.0988 Hz) a gauge of
    // 65536 gains (Speed + 20) x k / 16, k = 96, Haste 126, Slow 48 (Slow wins); a monster's
    // Speed + 20 first becomes (Speed + 20) x (255 - 24 x battle speed) / 256 (high byte), battle
    // speed 0-5 = the menu's Bat.Speed 1-6 (default 3); characters aren't affected by it.
    // PR (measured headless 2026-11-11, WcTest "atb"; gauge 0-100 per second): party
    // 0.260 x (Speed + 13), monsters 0.216 x (Speed + 21) at its middle battle speed, Haste x1.5, and
    // its battle speed (BattleUtility.GetBattleSpeedByConfigValue: 0-4 = x0.5, 0.75, 1, 1.5, 2) scales
    // both sides. The SNES at Bat.Speed 3: party 0.275 x (Speed + 20), monsters x0.81 of that: the
    // party's turns came 11-37% slower than WC's (more at low Speed) while the monsters' matched.
    // BattleProgressATBFF6.ATBCalc (a unit's gauge increase this frame) answers the SNES's rate x
    // Time.deltaTime wherever the game gives the unit any increase (a stopped or fallen unit's 0 stays
    // 0). The PR's five battle speeds pick the SNES's monster speeds Bat.Speed 6, 5, 3, 2, 1 (slower to
    // faster, as the PR's setting reads; its middle one is the SNES's default).
    // Time runs while an action plays, in both: the SNES's battle-time counter ($3A3E) went on through
    // a Fight's attack (37 moves over its 86 frames: 1.43 s real, harness 2026-11-12), and the PR's FF6 update
    // (BattleProgressATBFF6.Update) stops only for BattleUIManager.IsWaiting / IsForceWaiting (Wait
    // mode's menus, forced pauses): measured headless (WcTest turn_cycle), a turn had no stopped time,
    // its attack 1.40 s real (the SNES's 1.43). An earlier note here said the PR stops; it doesn't.
    // (Auto-battle runs the PR's whole battle at 1.5x: BattleController.UpdateBattle, IsAuto.)
    // Left as the PR has it: the gauges' start (the PR: 50-80% at random; the SNES: Speed +
    // random(Speed) + a random 0-72 + 16 per empty slot, of 256).
    // The math is game-free in SnesAtbRules (offline tests: PrKit.Tests); these hooks read the game and ask it.
    public static class SnesAtb
    {
        // what the mod sets (Worlds Collide: WcParity.cs; Beyond Chaos: its Plugin.cs)
        public static Func<bool> On = () => false;                  // the mod's rules are on (a seed of its own is active)
        public static Func<bool> Testing = () => TestRun.Running;   // a headless self-test (extra logs)
        public static string Tag = "[kit]";
        public static Func<long> PerfNow = () => 0;                 // per-frame timing (WC's WcPerf)
        public static Action<int, string, long> PerfLap = (slot, name, since) => { };

        public static int Calls;

        public static void Patch(Harmony h) => Patch(h, true);

        // chants: the commands' waits and Jump's air time too (vanilla's table, SnesAtbRules.CommandWait); false
        // for a mod with a wait table of its own (Brave New World: the fill rates only)
        public static void Patch(Harmony h, bool chants)
        {
            var m = AccessTools.Method(typeof(BattleProgressATBFF6), "ATBCalc");
            if (m == null) throw new MissingMethodException("BattleProgressATBFF6.ATBCalc");
            h.Patch(m, postfix: new HarmonyMethod(typeof(SnesAtb), nameof(ATBCalc_Postfix)));
            if (!chants)
            {
                Kit.Info(Tag + " ATB: the SNES's fill rates hooked (the game's chants left alone)");
                return;
            }
            var chant = AccessTools.Method(typeof(BattleProgressATBFF6), "GetChantingTime");
            if (chant == null) throw new MissingMethodException("BattleProgressATBFF6.GetChantingTime");
            h.Patch(chant, postfix: new HarmonyMethod(typeof(SnesAtb), nameof(Chanting_Postfix)));
            Kit.Info(Tag + " ATB: the SNES's fill rates and Jump's air time hooked");
        }

        // the ATB's multipliers (normal, Haste, Slow) and the monsters' battle-speed term: vanilla's by default
        // (WC, BC); a mod whose ROM changed them sets them (T-Edition: HasteK 144, MonsterSpeedTerm 4; C2/2482)
        // They live in SnesAtbRules; these names stay for the mods that set them.
        public static int NormalK { get => SnesAtbRules.NormalK; set => SnesAtbRules.NormalK = value; }
        public static int HasteK { get => SnesAtbRules.HasteK; set => SnesAtbRules.HasteK = value; }
        public static int SlowK { get => SnesAtbRules.SlowK; set => SnesAtbRules.SlowK = value; }
        public static int MonsterSpeedTerm { get => SnesAtbRules.MonsterSpeedTerm; set => SnesAtbRules.MonsterSpeedTerm = value; }

        // the SNES's gauge increase per second, in the PR's gauge (0-100)
        public static double SnesRate(int speed, bool monster, int battleSpeed, bool haste, bool slow) =>
            SnesAtbRules.Rate(speed, monster, battleSpeed, haste, slow);

        // Jump's air time (the user, 2026-11-12, with the status timers): the SNES's jumper lands when its
        // wait timer ($3AB4, gaining $3AC8 / 2 a move, C2/1193) reaches 224 x 256 (C2/0B7A: $322C = $E0):
        // 1.75 gauges; measured in the harness (Speed 33, $3AC8 318): 729-743 frames up, 722 by the rule.
        // The PR's lands when its landing act (ability 22, type 16 "ContinuousJumpEnd", ability_wait 30)
        // has chanted: BattleProgressATBFF6.GetChantingTime = 20 x wait / ((Agility / 2 + 20) x (Haste 2,
        // Slow 0.5) x the battle speed), counted down by frame time x the battle speed (UpdateChantTime;
        // decompiled 2026-11-12): 600 / (Agility / 2 + 20) s at the default speed, 14.46 s at Speed 43
        // (measured headless 16.05 s; SNES 10.10). Now the landing's chant is the SNES's air time.
        public const int JumpWait = SnesAtbRules.JumpWait;

        public static double SnesJumpSeconds(int speed, bool monster, int battleSpeed, bool haste, bool slow) =>
            SnesAtbRules.JumpSeconds(speed, monster, battleSpeed, haste, slow);

        // the SNES's wait timer: `wait` x 256, gaining half the ATB's add a move (C2/1193)
        public static double SnesWaitSeconds(int wait, int speed, bool monster, int battleSpeed, bool haste, bool slow) =>
            SnesAtbRules.WaitSeconds(wait, speed, monster, battleSpeed, haste, slow);

        // The wait between a character's command and its act (the user, 2026-11-12: "investigate and if
        // needed fix this"). The SNES's: the command's entry in the C2/067B table ($322C), then the wait
        // timer above; measured in the harness (Speed 33): Fight and Steal $10, ready 51-57 frames after
        // the input (52 by the rule), Magic $20 (136 frames); Jump goes up at once (its $E0 is the
        // landing); monsters act at once (their $322C: $FE, no wait), and so do Berserk, Muddle and Rage
        // turns. The PR's (BattleProgressATBFF6.GetChantingTime, decompiled 2026-11-12): for a
        // character's act, 20 x the ability's ability_wait / ((Agility / 2 + 20) x (Haste 2, Slow 0.5)
        // x the battle speed): Attack (wait 3) 1.64 s at Agility 33 (SNES 0.87), spells (5) 2.74 s
        // (SNES 1.73), items (0) none (SNES 0.87); monsters none. PR command -> the SNES table's value
        // (by the SNES command tools/port.py SNES_CMD maps to it): SnesAtbRules.CommandWait; which acts wait
        // at all: SnesAtbRules.ChantOf.
        public static Dictionary<int, int> SnesCommandWait => SnesAtbRules.CommandWait;
        static readonly HashSet<int> unknownCommands = new HashSet<int>();

        public static void Chanting_Postfix(BattleActData battleActData, ref float __result)
        {
            try
            {
                if (!On() || battleActData == null) return;
                var abilities = battleActData.abilityList;
                var ab = abilities != null && abilities.Count > 0 ? abilities[0] : null;
                var u = battleActData.AttackUnitData;
                var p = u?.BattleUnitDataInfo?.Parameter;
                if (ab == null || p == null) return;
                bool haste = false, slow = false, wild = false, dancing = false;
                var list = p.CurrentConditionList;
                if (list != null)
                    foreach (var c in list)
                    {
                        if (c == null) continue;
                        int t = c.ConditionType;
                        if (t == SnesAtbRules.HasteType) haste = true;
                        else if (t == SnesAtbRules.SlowType) slow = true;
                        else if (t == SnesAtbRules.BerserkType || t == SnesAtbRules.ConfusionType) wild = true;  // Berserk ("Bersak"), Confusion
                        else if (t == SnesAtbRules.DanceType) dancing = true;
                    }
                bool monster = u.TryCast<BattleEnemyData>() != null;
                int cfg = UserDataManager.Instance()?.Config?.BattleSpeed ?? 2;
                float speed = BattleUtility.GetBattleSpeedByConfigValue(cfg);   // the chant counts down at this rate
                float was = __result;
                int cmd = battleActData.Command?.Id ?? 0;
                var chant = SnesAtbRules.ChantOf(ab.TypeId, cmd, monster, battleActData.isManipulateAttack, wild,
                                                 battleActData.isRampageAct, dancing, battleActData.isCopiedAct, out int wait);
                if (chant == SnesAtbRules.Chant.Landing)                          // the landing (ContinuousJumpEnd)
                {
                    double air = SnesAtbRules.JumpSeconds(p.ConfirmedAgility(), monster, SnesBattleSpeed(), haste, slow);
                    __result = (float)(air * speed);
                    JumpChants++;
                    if (JumpChants == 1)
                        Kit.Info($"{Tag} ATB: a landing's chant {was:0.00} -> {__result:0.00} (Speed {p.ConfirmedAgility()}: the SNES's {air:0.00} s in the air)");
                    return;
                }
                if (chant == SnesAtbRules.Chant.Game) return;                   // monsters: none (the PR's 0)
                if (chant == SnesAtbRules.Chant.Now)
                {
                    __result = 0f;                                             // up at once / no wait
                    return;
                }
                cmd = SnesAtbRules.WaitCommand(cmd, battleActData.isCopiedAct);  // Mimic's own wait
                if (!CommandWaits)                                               // test only: the game's own
                {
                    if (Testing() && cmd == 1) ChantLog.Add((SnesStatusTicks.BattleTime, u.Pointer, cmd, was));
                    return;
                }
                if (chant == SnesAtbRules.Chant.Unknown)
                {
                    if (unknownCommands.Add(cmd))
                        Kit.Info($"{Tag} ATB: no SNES wait for command {cmd} (ability {ab.Id}, type {ab.TypeId}): the game's {was:0.00} stays");
                    return;
                }
                double secs = SnesAtbRules.WaitSeconds(wait, p.ConfirmedAgility(), false, 0, haste, slow);
                __result = (float)(secs * speed);
                CommandChants++;
                if (Testing()) ChantLog.Add((SnesStatusTicks.BattleTime, u.Pointer, cmd, (float)secs));
                if (CommandChants <= 3)
                    Kit.Info($"{Tag} ATB: command {cmd} (ability {ab.Id}) waits {secs:0.00} s, the SNES's {wait} (the game's chant {was:0.00})");
            }
            catch (Exception e) { Hooks.WarnOnce("atb.jump", e); }
        }
        public static int JumpChants, CommandChants;
        public static bool CommandWaits = true;      // the headless check turns it off to compare
        // test only (WcTest status_timers): battle time, unit, command, the SNES wait given
        public static readonly List<(float Time, IntPtr Unit, int Command, float Wait)> ChantLog = new List<(float, IntPtr, int, float)>();

        public static int SnesBattleSpeed() => SnesAtbRules.BattleSpeedOf(UserDataManager.Instance()?.Config?.BattleSpeed ?? 2);

        public static void ATBCalc_Postfix(BattleUnitData __0, ref float __result)
        {
            long t0 = 0;
            try
            {
                if (!On() || __0 == null || __result <= 0) return;
                t0 = PerfNow();
                var p = __0.BattleUnitDataInfo?.Parameter;
                if (p == null) return;
                bool haste = false, slow = false;
                var list = p.CurrentConditionList;
                if (list != null)
                    foreach (var c in list)
                    {
                        if (c == null) continue;
                        if (c.ConditionType == SnesAtbRules.HasteType) haste = true;    // ConditionType.Heist
                        else if (c.ConditionType == SnesAtbRules.SlowType) slow = true; // ConditionType.Slow
                    }
                bool monster = __0.TryCast<BattleEnemyData>() != null;
                __result = (float)(SnesAtbRules.Rate(p.ConfirmedAgility(), monster, SnesBattleSpeed(), haste, slow) * Time.deltaTime);
                Calls++;
            }
            catch (Exception e) { Hooks.WarnOnce("atb", e); }
            finally { if (t0 != 0) PerfLap(10, "atb", t0); }
        }
    }
}
