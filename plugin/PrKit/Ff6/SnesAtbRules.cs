using System;
using System.Collections.Generic;

namespace PrKit.Ff6
{
    // The SNES's ATB and wait-timer math (vanilla FF6's; WC doesn't change it), with no game types: SnesAtb
    // is the game's side (its hooks read a fighter's speed, statuses and act, and ask this), and PrKit.Tests
    // runs it offline (architecture roadmap item 3, 2026-11-20: game logic as plain code, hooks thin).
    // The rules and their sources (C2/09D2, C2/111B, C2/1193, C2/0B7A, C2/067B): SnesAtb's notes.
    public static class SnesAtbRules
    {
        // battle time moves every 2 frames (NTSC 60.0988 Hz)
        public const double TicksPerSecond = 60.0988 / 2;

        // the ATB's multipliers (normal, Haste, Slow) and the monsters' battle-speed term: vanilla's by default
        // (WC, BC); a mod whose ROM changed them sets them (T-Edition: HasteK 144, MonsterSpeedTerm 4; C2/2482)
        public static int NormalK = 96, HasteK = 126, SlowK = 48, MonsterSpeedTerm = 24;

        // What's added to Speed before the multiply (vanilla: 20 for everyone), whether a monster's Speed + bias
        // goes through the battle-speed term at all, and whether a monster's multiplier is k + k/2 (8-bit LSR).
        // Vanilla by default; Brave New World's Speed Balance (its c2.asm C2/09D2 ATBMultipliers: characters k
        // 75/90/60 with bias 51, monsters (k + k/2) with bias 20 and no battle-speed term, the menu's Battle
        // Speed removed) sets NormalK 75, HasteK 90, SlowK 60, CharBias 51, MonsterBattleSpeed false,
        // MonsterHalfMore true.
        public static int CharBias = 20, MonsterBias = 20;
        public static bool MonsterBattleSpeed = true, MonsterHalfMore = false;

        // PR battle speed setting 0-4 (slower to faster) -> the SNES's 0-5 (the menu's Bat.Speed 1-6; 2 = default)
        static readonly int[] SnesBattleSpeedOf = { 5, 4, 2, 1, 0 };

        public static int BattleSpeedOf(int prConfig) =>
            prConfig >= 0 && prConfig < SnesBattleSpeedOf.Length ? SnesBattleSpeedOf[prConfig] : 2;

        // C2/09D2: Slow wins over Haste
        public static int K(bool haste, bool slow) => slow ? SlowK : haste ? HasteK : NormalK;

        // the ATB's add per move (every 2 frames), of a 65536 gauge: (Speed + 20) x k / 16; a monster's
        // Speed + 20 first becomes (Speed + 20) x (255 - term x battle speed) / 256 (the high byte)
        // (with the bias, battle-speed and k + k/2 settings above: BNW's (Speed + 51) x k / 16 for characters,
        // (Speed + 20) x (k + k/2) / 16 for monsters)
        public static int AtbAdd(int speed, bool monster, int battleSpeed, bool haste, bool slow)
        {
            int s = (Math.Max(0, speed) + (monster ? MonsterBias : CharBias)) & 0xFF;   // ADC #$14 (BNW: #$33), 8 bits
            int k = K(haste, slow);
            if (monster)
            {
                if (MonsterBattleSpeed) s = s * (255 - MonsterSpeedTerm * Math.Clamp(battleSpeed, 0, 5)) >> 8;
                if (MonsterHalfMore) k += k >> 1;                   // BNW: LSR, ADC (k x 1.5)
            }
            return s * k >> 4;
        }

        // the gauge's increase per second, in the PR's gauge (0-100)
        public static double Rate(int speed, bool monster, int battleSpeed, bool haste, bool slow) =>
            TicksPerSecond * AtbAdd(speed, monster, battleSpeed, haste, slow) / 65536.0 * 100.0;

        // the wait timer (C2/1193): `wait` x 256, gaining half the ATB's add a move
        public static double WaitSeconds(int wait, int speed, bool monster, int battleSpeed, bool haste, bool slow)
        {
            if (wait <= 0) return 0;
            int perTick = Math.Max(1, AtbAdd(speed, monster, battleSpeed, haste, slow) >> 1);   // LSR
            int moves = (wait * 256 + perTick - 1) / perTick;
            return moves / TicksPerSecond;
        }

        // Jump's air time: the jumper lands when its wait timer reaches 224 x 256 (C2/0B7A: $322C = $E0)
        public const int JumpWait = 224 * 256;

        public static double JumpSeconds(int speed, bool monster, int battleSpeed, bool haste, bool slow) =>
            WaitSeconds(JumpWait >> 8, speed, monster, battleSpeed, haste, slow);

        // the wait between a character's command and its act: the C2/067B table's value ($322C) by PR
        // command (the SNES command tools/port.py SNES_CMD maps to it)
        public static readonly Dictionary<int, int> CommandWait = new Dictionary<int, int>
        {
            [1] = 16, [2] = 16, [3] = 16, [5] = 16, [7] = 32, [8] = 0, [9] = 0, [10] = 16, [11] = 16,
            [12] = 16, [13] = 16, [14] = 16, [15] = 16, [16] = 16, [17] = 32, [18] = 16, [19] = 16,
            [20] = 16, [21] = 16, [22] = 16, [23] = 16, [24] = 16, [25] = 16, [26] = 16, [27] = 32,
            [28] = 32, [29] = 16, [31] = 32,
        };

        // the PR's condition types and ability types the decision reads
        public const int HasteType = 15, SlowType = 16, BerserkType = 410, ConfusionType = 12, DanceType = 619;
        public const int LandingAbility = 16, UpAtOnceAbility = 15, DanceCommand = 22, MimicCommand = 25;

        public enum Chant
        {
            Game,      // the game's own chant stays (a monster's, a manipulated act's)
            Landing,   // Jump's landing: the SNES's air time (JumpSeconds)
            Now,       // up at once, no wait (Jump's going up, Berserk/Muddle/Rage turns, a Dance)
            Command,   // the SNES's wait for the command (`wait`: CommandWait's value; WaitSeconds)
            Unknown,   // a command the table doesn't have: the game's chant stays
        }

        // What an act's chant becomes, from what the hook reads off the act; `command` is the command whose
        // wait counts (Mimic's own for a copied act), `wait` its table value (Command only).
        public static Chant ChantOf(int abilityType, int command, bool monster, bool manipulated, bool wild,
                                    bool rampage, bool dancing, bool copied, out int wait)
        {
            wait = 0;
            if (abilityType == LandingAbility) return Chant.Landing;
            if (monster || manipulated) return Chant.Game;          // monsters act at once (their $322C: $FE)
            if (abilityType == UpAtOnceAbility || wild || rampage || (dancing && command == DanceCommand)) return Chant.Now;
            if (copied) command = MimicCommand;
            return CommandWait.TryGetValue(command, out wait) ? Chant.Command : Chant.Unknown;
        }

        // the command whose wait counts (Mimic's own for a copied act)
        public static int WaitCommand(int command, bool copied) => copied ? MimicCommand : command;

        // Brave New World's Quickfill (ef.asm EF/FBC8, called from C2/112C BattleFrameLoop): the battle loop
        // runs every frame, not every second one, while no character has a battle-menu slot ($4001-$4004:
        // taken when its ATB fills, given back when its command is chosen): x2 time then. The PR's
        // counterpart of those slots: the characters waiting for a command (battleATBOrderUiStack).
        public static int QuickfillFactor(int waitingForCommand) => waitingForCommand <= 0 ? 2 : 1;
    }

    // Time held while something lasts (an action playing out: BNW's nATB, Battle Options' Pause), with a safety:
    // held no longer than GiveUp seconds in a row (a state the game never left would otherwise stop the battle),
    // and once given up, not again until that state ends. Game-free (PrKit.Tests); SnesActionFreeze asks it.
    public sealed class TimeHold
    {
        public float GiveUp = 20f;
        public float Since { get; private set; } = -1f;    // when the current hold began (-1: none)
        public bool GaveUp { get; private set; }
        public int Holds { get; private set; }             // holds ended (counted when they end)
        public float LastHeld { get; private set; }         // how long the last one lasted

        // whether time stops now, given whether the thing is still going on at `now` (seconds)
        public bool Update(bool going, float now)
        {
            if (!going)
            {
                if (Since >= 0f)
                {
                    Holds++;
                    LastHeld = now - Since;
                }
                Since = -1f;
                GaveUp = false;
                return false;
            }
            if (Since < 0f) Since = now;
            if (!GaveUp && now - Since > GiveUp) GaveUp = true;
            return !GaveUp;
        }
    }
}
