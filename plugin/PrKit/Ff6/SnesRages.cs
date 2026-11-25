using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Last.Battle;
using Last.Data.Master;

namespace PrKit.Ff6
{
    // Rages and lores learned on the party's behalf after a won battle (FF6's SNES learns a formation's rages with its
    // C2/4A07 routine; randomizers call it after every victory: Worlds Collide's -rnl, Beyond Chaos's `o`/`w`), and Leap
    // taken away (WC's -rnl). In the SNES rages and lores are the party's; here they go to whoever holds the command
    // (StartSkills.Teach: with no holder they wait, StartSkills.TeachPending). Which rages: SnesRagesRules.
    // A mod sets the switches (all off by default), then either calls BattleStart / SeenLore / BattleWon from its own
    // battle triggers (WC) or lets Apply hook the battle's end itself (ownTriggers: the formation's monsters, read from
    // the battle's MonsterParty when the game counts the won battle's EXP). From Worlds Collide's WcRages (2026-11-21).
    public static class SnesRages
    {
        public const int Leap = 24;
        public static Func<bool> NoLeap = () => false;            // Leap unavailable
        public static Func<bool> LearnRagesOnWin = () => false;   // a won battle teaches its monsters' rages
        public static Func<bool> LearnLoresOnWin = () => false;   // ... and the lores enemies used in it (SeenLore)
        public static string Tag = "[kit]";
        public static Action<string, Exception> Warn = Hooks.WarnOnce;   // a mod's own warning (WC: WcRunner.WarnOnce)
        public static string Last = "";
        public static int Wins;                                    // battles won seen (ownTriggers)

        static readonly List<int> battleMonsters = new List<int>();
        static readonly HashSet<int> battleLores = new HashSet<int>();

        public static void Apply(Harmony h, bool ownTriggers)
        {
            Hooks.Patch(h, typeof(Serial.FF6.Management.CommandAvailableData), "IsAvailable", typeof(SnesRages),
                        postfix: nameof(Available_Postfix));
            if (ownTriggers)
                Hooks.Patch(h, typeof(BattleAddExpPlug), "GetExp", typeof(SnesRages),
                            prefix: nameof(Exp_Prefix), postfix: nameof(Exp_Postfix));
        }

        public static void Available_Postfix(Command command, ref bool __result)
        {
            try { if (__result && command != null && command.Id == Leap && NoLeap()) __result = false; }
            catch (Exception e) { Warn("no-leap", e); }
        }

        public static void BattleStart(IEnumerable<int> monsters)
        {
            battleMonsters.Clear();
            battleMonsters.AddRange(monsters.Distinct());
            battleLores.Clear();
        }

        public static void SeenLore(int ability) => battleLores.Add(ability);

        // Capture before the reward calculation, which can clear MonsterParty by its end.
        public static void Exp_Prefix()
        {
            try { ReadFormation(); }
            catch (Exception e) { Warn("rages-formation", e); }
        }

        static void ReadFormation()
        {
            var battle = BattlePlugManager.Instance();
            var p = battle?.MonsterParty;
            battleMonsters.Clear();
            if (p != null)
                battleMonsters.AddRange(new[] { p.Monster1, p.Monster2, p.Monster3, p.Monster4, p.Monster5, p.Monster6 }
                                        .Where(m => m > 0).Distinct());
            if (battleMonsters.Count > 0) return;
            var defeated = battle?.BattleAddExpPlug?.expeditionMonsters;
            if (defeated != null)
                for (int i = 0; i < defeated.Count; i++)
                    if (defeated[i] != null && defeated[i].Id > 0 && !battleMonsters.Contains(defeated[i].Id))
                        battleMonsters.Add(defeated[i].Id);
        }

        // the won battle's formation (ownTriggers)
        public static void Exp_Postfix()
        {
            Wins++;
            try
            {
                if (!LearnRagesOnWin()) return;
                if (battleMonsters.Count == 0) ReadFormation();
                BattleWon();
            }
            catch (Exception e) { Warn("rages-won", e); }
        }

        // a battle was won: its lores (LearnLoresOnWin), then its monsters' rages (LearnRagesOnWin)
        public static void BattleWon()
        {
            try
            {
                var lores = battleLores.ToList();
                battleLores.Clear();
                if (lores.Count > 0 && LearnLoresOnWin()) Note(StartSkills.Lore, "lores", lores, StartSkills.Teach(StartSkills.Lore, lores));
            }
            catch (Exception e) { Warn("lores-won", e); }
            if (!LearnRagesOnWin() || battleMonsters.Count == 0) return;
            try
            {
                var mm = MasterManager.Instance;
                var rages = SnesRagesRules.Rages(battleMonsters).Where(id => mm.GetData<Ability>(id) != null).ToList();
                battleMonsters.Clear();
                if (rages.Count > 0) Note(StartSkills.Rage, "rages", rages, StartSkills.Teach(StartSkills.Rage, rages));
            }
            catch (Exception e) { Warn("rages-won", e); }
        }

        static void Note(int command, string what, List<int> ids, int taught)
        {
            if (taught > 0) Last = $"{what} {string.Join(",", ids)} ({taught} taught)";
            else if (StartSkills.Pending(command) > 0) Last = $"{what} {string.Join(",", ids)} (nobody holds the command: {StartSkills.Pending(command)} waiting)";
            else { Last = $"{what} {string.Join(",", ids)} (all known already)"; return; }   // for tests (no hook vs known); no log line
            Kit.Info($"{Tag} won battle: {Last}");
        }
    }
}
