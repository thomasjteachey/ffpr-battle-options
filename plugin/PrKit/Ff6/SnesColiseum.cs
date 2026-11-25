using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Last.Data.Master;

namespace PrKit.Ff6
{
    // A mod's Coliseum (the SNES's 256 matches at DF/B600, 4 bytes per wagered item: opponent, ?,
    // reward, reward hidden) for any FF6 mod. The PR builds its table in code
    // (Serial.FF6.Management.ColosseumInfomationData, by wager content id); its GetMonsterId /
    // GetRewardItemId / IsRewardNameUnknown answer from the mod's rows instead. The rows come from
    // the kit's tools/seed_info.coliseum: [wager content, vanilla party, vanilla reward, party (0:
    // none), reward, hidden, monster] (PR monster = SNES enemy + 1), keyed here by wager content.
    // A match whose monster has no Coliseum party (party 0) fights the game's own party for that
    // wager with its monster swapped for the mod's (restored on the next lookup).
    // Generalized from Worlds Collide's WcColiseum (2026-11-20, for Brave New World); WC keeps its
    // own until it moves here (the coordinator's call). Off unless a mod applies it.
    public sealed class SnesColiseumMod
    {
        public string Name = "kit";
        public Func<bool> On = () => true;
        // wager content id -> [vanilla party, vanilla reward, party, reward, hidden, monster]
        public Func<int, int[]> Match = _ => null;
    }

    public static class SnesColiseum
    {
        static SnesColiseumMod mod;
        public static int MatchParty;          // the last match's party ("Win A Coliseum Match")
        public static string Last = "";        // tests: the game's own answer and the mod's
        public static int GameMonster, GameReward;
        public static int Hooked;

        // rows as seed_info.coliseum writes them -> wager content id -> the rest
        public static Dictionary<int, int[]> Table(IEnumerable<int[]> rows)
        {
            var d = new Dictionary<int, int[]>();
            foreach (var r in rows ?? Enumerable.Empty<int[]>())
                if (r != null && r.Length >= 7) d[r[0]] = r.Skip(1).ToArray();
            return d;
        }

        public static void Apply(Harmony h, SnesColiseumMod m)
        {
            mod = m;
            if (m == null) return;
            var t = typeof(Serial.FF6.Management.ColosseumInfomationData);
            Hooked = 0;
            foreach (var (name, post) in new[] { ("GetMonsterId", nameof(Monster_Postfix)), ("GetRewardItemId", nameof(Reward_Postfix)),
                                                 ("IsRewardNameUnknown", nameof(Hidden_Postfix)) })
                if (Hooks.Patch(h, t, name, typeof(SnesColiseum), postfix: post)) Hooked++;
            Kit.Info($"[{m.Name}] coliseum: {Hooked} of 3 lookups hooked");
        }

        static int[] Match(int wager)
        {
            if (mod == null || !mod.On()) return null;
            return mod.Match(wager);
        }

        static int Get(MonsterParty p, int slot) => slot switch
        {
            1 => p.Monster1, 2 => p.Monster2, 3 => p.Monster3, 4 => p.Monster4, 5 => p.Monster5, 6 => p.Monster6, _ => 0,
        };

        static void Set(MonsterParty p, int slot, int monster)
        {
            switch (slot)
            {
                case 1: p.Monster1 = monster; break;
                case 2: p.Monster2 = monster; break;
                case 3: p.Monster3 = monster; break;
                case 4: p.Monster4 = monster; break;
                case 5: p.Monster5 = monster; break;
                case 6: p.Monster6 = monster; break;
            }
        }

        // Coliseum parties retargeted to another monster: party -> (slot, original monster)
        static readonly Dictionary<int, (int Slot, int Monster)> retargeted = new Dictionary<int, (int, int)>();

        static void Retarget(int partyId, int monster)
        {
            var p = MasterManager.Instance.GetData<MonsterParty>(partyId);
            if (p == null) return;
            if (!retargeted.TryGetValue(partyId, out var orig))
            {
                int slot = Enumerable.Range(1, 6).FirstOrDefault(i => Get(p, i) > 0);
                if (slot == 0) return;
                orig = (slot, Get(p, slot));
                retargeted[partyId] = orig;
            }
            Set(p, orig.Slot, monster);
        }

        static void Restore(int partyId)
        {
            if (!retargeted.TryGetValue(partyId, out var orig)) return;
            var p = MasterManager.Instance.GetData<MonsterParty>(partyId);
            if (p != null) Set(p, orig.Slot, orig.Monster);
            retargeted.Remove(partyId);
        }

        public static void Monster_Postfix(int selectItemContentId, ref int __result)
        {
            try
            {
                var m = Match(selectItemContentId);
                if (m == null) return;
                GameMonster = __result;
                if (m[2] > 0)
                {
                    Restore(m[2]);
                    Last = $"wager {selectItemContentId}: party game {__result} (vanilla table {m[0]}) -> {m[2]}";
                    __result = m[2];
                }
                else if (m[5] > 0)
                {
                    // no Coliseum copy of the mod's opponent: the game's party for this wager fights it
                    Retarget(__result, m[5]);
                    Last = $"wager {selectItemContentId}: party game {__result} (vanilla table {m[0]}) keeps, monster -> {m[5]}";
                }
            }
            catch (Exception e) { Hooks.WarnOnce("coliseum-monster", e); }
            finally { MatchParty = __result; }
        }

        public static void Reward_Postfix(int selectItemContentId, ref int __result)
        {
            try
            {
                var m = Match(selectItemContentId);
                if (m == null) return;
                GameReward = __result;
                Last += $"; reward game {__result} (vanilla table {m[1]}) -> {m[3]}";
                __result = m[3];
            }
            catch (Exception e) { Hooks.WarnOnce("coliseum-reward", e); }
        }

        public static void Hidden_Postfix(int selectItemContentId, ref bool __result)
        {
            try
            {
                var m = Match(selectItemContentId);
                if (m != null) __result = m[4] != 0;
            }
            catch (Exception e) { Hooks.WarnOnce("coliseum-hidden", e); }
        }

        // Self-test (a harness or probe run): the game's own lookups for up to n wagers against the
        // mod's rows. Returns null when all agree, else what differs.
        public static string Check(IEnumerable<int> wagers, int n = 30)
        {
            if (mod == null) return "not applied";
            var data = new Serial.FF6.Management.ColosseumInfomationData();
            int ours = 0, seen = 0, same = 0;
            var bad = new List<string>();
            foreach (var w in wagers.Take(n))
            {
                var m = mod.Match(w);
                if (m == null) continue;
                seen++;
                int mon = data.GetMonsterId(w), rew = data.GetRewardItemId(w);
                bool hid = data.IsRewardNameUnknown(w);
                bool fights = true;
                if (m[2] == 0 && m[5] > 0)
                {
                    var p = MasterManager.Instance.GetData<MonsterParty>(mon);
                    fights = p != null && Enumerable.Range(1, 6).Any(i => Get(p, i) == m[5]);
                }
                if ((m[2] > 0 ? mon == m[2] : fights) && rew == m[3] && hid == (m[4] != 0)) ours++;
                else bad.Add($"{w}: party {mon} reward {rew} hidden {hid} (want {m[2]}/{m[5]}, {m[3]}, {m[4]})");
                if (GameMonster == m[0] && GameReward == m[1]) same++;
            }
            Kit.Info($"[{mod.Name}] coliseum self-test: {ours} of {seen} wagers answered from the mod's table; the game's own matched the vanilla table {same} of {seen}");
            return seen > 0 && ours == seen ? null : string.Join("; ", bad.Take(5));
        }
    }
}
