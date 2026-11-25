using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Last.Battle;
using Last.Battle.Function;

namespace PrKit.Ff6
{
    // Umaro as the SNES has him, for any FF6 mod (moved here from Brave New World's UmaroRules,
    // 2026-11-20, for Worlds Collide: pr/SNES_PR_DISCREPANCIES.md).
    //
    // His attack odds (C2/5269): per relic set (none, Rage Ring, Blizzard Orb, both) a weight of
    // 255 for each of Fight, Tackle, Snowstorm and the ally toss ($FF: not offered). Vanilla:
    // 158/95, 94/63/-/95, 94/63/95/-, 62/63/63/63. The PR (decompiled tools/decomp/umaro,
    // ActSelectUmalo.SetActDataUmalo) has its own table per umaloType (0 none, 1 Rage Ring,
    // 2 Blizzard Orb, 3 both, 4 Magitek) of ability -> percent, 50/50, 25/37.5/37.5, 13/29/29/29,
    // inserted in the order below, then roll = CommonUtility.GetRand(100) and the first entry
    // whose running total passes the roll. The table is local to the method, so the kit picks
    // with the mod's weights and, during that one call, answers GetRand(100) with a roll that
    // lands on the pick in the PR's table. Magitek (type 4) keeps the PR's odds.
    //
    // The ally toss (ability 200): a thrown Mog is a sure critical (C2/16CF). The PR lost it: its
    // toss is a plain physical hit (function 2, BattleFunction2.HitCalc) and the ally is picked
    // only by the animation (ThrowAtActFunction.SelectThrowingTarget: a random targetable party
    // member, Umaro too, the same side in a side attack, Umaro himself if none; decompiled
    // tools/decomp/cover_hit). So one pick serves both: whichever of the two runs first makes it
    // (the hit with the PR's own rule, or the animation's own pick), the other uses it, and while a
    // toss of Mog is computed Umaro's critical rate reads 100 (PhysicalExecution: critical when
    // rand(99) < rate).
    public sealed class SnesUmaroMod
    {
        public string Name = "kit";
        // per relic set (none, Rage Ring, Blizzard Orb, both): the weight of each of Columns, $FF none
        public int[][] Rows = SnesUmaro.VanillaRows;
        public int[] Columns = SnesUmaro.VanillaColumns;
        public bool MogToss = true;
        public Func<bool> On = () => true;
    }

    public static class SnesUmaro
    {
        public static readonly int[][] VanillaRows =
        {
            new[] { 158, 95, 255, 255 }, new[] { 94, 63, 255, 95 }, new[] { 94, 63, 95, 255 }, new[] { 62, 63, 63, 63 },
        };
        // the PR's ability ids for Fight, Tackle ("Slam"), Snowstorm, the ally toss
        public static readonly int[] VanillaColumns = { 1, 201, 199, 200 };
        public const int TossAbility = 200;

        static SnesUmaroMod mod;
        static readonly Random Rng = new Random();
        static int pendingRoll = -1;
        static int mogStatusId = 11;   // Mog's character status id (the self-test points it at another member)

        sealed class Toss
        {
            public IntPtr Thrower;
            public BattleUnitData Thrown;
            public bool HitUsed, ViewUsed;
            public DateTime At;
        }
        static Toss toss;   // the toss in flight (one act runs at a time)
        [ThreadStatic] static IntPtr critParam, critParam2;   // during a Mog toss's hit: Umaro's parameters (unit's, owned)

        // the PR's tables (ability id, percent), in insertion order
        static readonly (int Ability, float Pct)[][] GameTables =
        {
            new[] { (1, 50f), (201, 50f) },
            new[] { (1, 25f), (201, 37.5f), (200, 37.5f) },
            new[] { (1, 25f), (201, 37.5f), (199, 37.5f) },
            new[] { (1, 13f), (201, 29f), (200, 29f), (199, 29f) },
        };

        static bool Odds => mod != null && mod.Rows != null && mod.Rows.Length == 4 && mod.On();
        static bool Mog => mod != null && mod.MogToss && mod.On();

        public static void Apply(Harmony h, SnesUmaroMod m)
        {
            mod = m;
            if (m == null) return;
            if (m.Rows != null && m.Rows.Length == 4)
            {
                Hooks.Patch(h, typeof(ActSelectUmalo), "SetActDataUmalo", typeof(SnesUmaro),
                    prefix: nameof(Pick_Prefix), postfix: nameof(Pick_Postfix));
                Hooks.Patch(h, typeof(global::CommonUtility), "GetRand", typeof(SnesUmaro),
                    prefix: nameof(GetRand_Prefix), args: new[] { typeof(int) });
            }
            if (m.MogToss)
            {
                Hooks.Patch(h, typeof(BattleFunction2), "HitCalc", typeof(SnesUmaro),
                    prefix: nameof(TossHit_Prefix), postfix: nameof(TossHit_Postfix));
                Hooks.Patch(h, typeof(ThrowAtActFunction), "SelectThrowingTarget", typeof(SnesUmaro),
                    prefix: nameof(SelectThrown_Prefix), postfix: nameof(SelectThrown_Postfix));
                Hooks.Patch(h, typeof(Last.Data.PlayerCharacterParameter), "ConfirmedCriticalRate", typeof(SnesUmaro),
                    postfix: nameof(Critical_Postfix));
                Hooks.Patch(h, typeof(Last.Data.PlayerCharacterParameter), "GetArmConfirmedCriticalRate", typeof(SnesUmaro),
                    postfix: nameof(Critical_Postfix));
            }
        }

        // --- the odds ---------------------------------------------------------------------------

        // the mod's pick for a relic set (0-3): an ability id
        public static int Pick(int[][] rows, int[] columns, int type, Func<int, int> rand)
        {
            var row = rows[type];
            var options = Enumerable.Range(0, Math.Min(row.Length, columns.Length))
                .Where(i => row[i] != 0xFF).Select(i => (Ability: columns[i], Weight: row[i])).ToList();
            int total = options.Sum(o => o.Weight);
            int x = rand(total);
            foreach (var o in options)
            {
                if (x < o.Weight) return o.Ability;
                x -= o.Weight;
            }
            return options.Last().Ability;
        }

        // the GetRand(100) answer that makes the PR's table pick this ability (-1: not in the table)
        public static int RollFor(int type, int ability)
        {
            float before = 0;
            foreach (var (a, pct) in GameTables[type])
            {
                if (a == ability) return Math.Min(99, (int)Math.Ceiling(before));
                before += pct;
            }
            return -1;
        }

        // what the PR's loop picks for a roll
        static int GamePick(int type, int roll)
        {
            float sum = 0;
            foreach (var (a, pct) in GameTables[type])
            {
                sum += pct;
                if (sum > roll) return a;
            }
            return 0;
        }

        public static void Pick_Prefix(ActSelectUmalo __instance)
        {
            pendingRoll = -1;
            if (!Odds || __instance == null) return;
            try
            {
                int type = (int)__instance.umaloType;
                if (type < 0 || type > 3) return;  // Magitek: the PR's own
                int pick = Pick(mod.Rows, mod.Columns, type, n => Rng.Next(n));
                pendingRoll = RollFor(type, pick);
                Hooks.NoteOnce("umaro-odds", () => $"[umaro] {mod.Name} odds: relic set {type} -> ability {pick} (roll {pendingRoll})");
            }
            catch (Exception e) { Hooks.WarnOnce("SnesUmaro.Pick", e); }
        }

        public static void Pick_Postfix() => pendingRoll = -1;

        public static bool GetRand_Prefix(int max, ref int __result)
        {
            if (pendingRoll < 0 || max != 100) return true;
            __result = pendingRoll;
            pendingRoll = -1;
            return false;
        }

        // Startup check: the mod's odds per relic set over 25,600 picks, each pick's roll landing on
        // it in the PR's table, and the GetRand hook answering a pending roll.
        public static string Check()
        {
            if (!Odds) return "[umaro] odds: the game's own";
            var rng = new Random(1);
            var parts = new List<string>();
            for (int t = 0; t < 4; t++)
            {
                var counts = new Dictionary<int, int>();
                bool lands = true;
                for (int i = 0; i < 25600; i++)
                {
                    int a = Pick(mod.Rows, mod.Columns, t, n => rng.Next(n));
                    counts[a] = counts.TryGetValue(a, out var c) ? c + 1 : 1;
                    lands &= GamePick(t, RollFor(t, a)) == a;
                }
                parts.Add($"set {t}: " + string.Join(" ", counts.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value * 100.0 / 25600:F0}%")) +
                          (lands ? "" : " (a roll MISSES)"));
            }
            pendingRoll = 37;
            int got = global::CommonUtility.GetRand(100);
            pendingRoll = -1;
            int free = global::CommonUtility.GetRand(100);
            return $"[umaro] {mod.Name} odds {string.Join("; ", parts)} | GetRand hook {(got == 37 ? "ok" : $"NOT applied ({got})")}, otherwise random ({free})";
        }

        // --- the ally toss: Mog is a sure critical -------------------------------------------------

        // the other side's pick for this thrower, if it's waiting for this side
        static Toss Waiting(IntPtr thrower, bool forHit)
        {
            var t = toss;
            if (t == null || t.Thrower != thrower || (DateTime.UtcNow - t.At).TotalSeconds > 20) return null;
            return (forHit ? t.HitUsed : t.ViewUsed) ? null : t;
        }

        // the PR's own pick (SelectThrowingTarget and its two filters)
        static BattleUnitData PickThrown(BattleUnitData thrower)
        {
            var battle = BattlePlugManager.Instance();
            var players = battle?.GetPlayerUnits();
            var options = new List<BattleUnitData>();
            if (players != null)
            {
                bool side = battle.BattlePopPlug != null &&
                            battle.BattlePopPlug.fixedPreeMptiveState == BattlePopPlug.PreeMptiveState.SideAttack;
                foreach (BattleUnitData p in players)
                {
                    var param = p?.BattleUnitDataInfo?.Parameter;
                    if (param == null || BattleUtility.IsNotTargeting(param, (Last.Data.Master.Ability)null)) continue;
                    if (side && p.currentUnitPositionType != thrower.currentUnitPositionType) continue;
                    options.Add(p);
                }
            }
            return options.Count > 0 ? options[Rng.Next(options.Count)] : thrower;
        }

        static string Name(BattleUnitData u) => u?.TryCast<global::BattlePlayerData>()?.ownedCharacterData?.Name;

        static bool IsMog(BattleUnitData u) =>
            u?.TryCast<global::BattlePlayerData>()?.ownedCharacterData?.characterStatusId == mogStatusId;

        public static void TossHit_Prefix(BattleFunction2 __instance)
        {
            critParam = critParam2 = IntPtr.Zero;
            if (!Mog || __instance == null) return;
            try
            {
                var act = __instance.battleActData;
                var thrower = act?.AttackUnitData;
                var abilities = act?.abilityList;
                if (thrower == null || abilities == null || abilities.Count == 0 || abilities[0]?.Id != TossAbility) return;
                if (thrower.TryCast<global::BattlePlayerData>() == null) return;
                var t = Waiting(thrower.Pointer, true);
                string by = "the animation";
                if (t == null)
                {
                    t = new Toss { Thrower = thrower.Pointer, Thrown = PickThrown(thrower), At = DateTime.UtcNow };
                    toss = t;
                    by = "the hit";
                }
                t.HitUsed = true;
                bool mog = IsMog(t.Thrown);
                if (mog)
                {
                    critParam = thrower.BattleUnitDataInfo?.Parameter?.Pointer ?? IntPtr.Zero;
                    critParam2 = thrower.TryCast<global::BattlePlayerData>()?.ownedCharacterData?.Parameter?.Pointer ?? IntPtr.Zero;
                }
                string who = Name(t.Thrown);
                Hooks.NoteOnce("umaro-toss-" + by, () => $"[umaro] toss: {who} thrown (picked by {by}){(mog ? ": Mog, a sure critical" : "")}");
                if (mog) Hooks.NoteOnce("umaro-toss-mog", () => "[umaro] Mog thrown: a sure critical (SNES C2/16CF)");
            }
            catch (Exception e) { Hooks.WarnOnce("SnesUmaro.TossHit", e); }
        }

        public static void TossHit_Postfix() => critParam = critParam2 = IntPtr.Zero;

        // __state: the hit's pick was used (the PR's own pick skipped)
        public static bool SelectThrown_Prefix(ThrowAtActFunction __instance, ref bool __state)
        {
            __state = false;
            if (!Mog || __instance == null) return true;
            try
            {
                var thrower = __instance.throwerUnitData;
                if (thrower == null) return true;
                var t = Waiting(thrower.Pointer, false);
                if (t?.Thrown == null) return true;
                t.ViewUsed = true;
                __instance.throwingSideUnitData = t.Thrown;
                __state = true;
                return false;
            }
            catch (Exception e) { Hooks.WarnOnce("SnesUmaro.SelectThrown", e); return true; }
        }

        public static void SelectThrown_Postfix(ThrowAtActFunction __instance, bool __state)
        {
            if (!Mog || __state || __instance == null) return;
            try
            {
                var thrower = __instance.throwerUnitData;
                if (thrower == null) return;
                toss = new Toss { Thrower = thrower.Pointer, Thrown = __instance.throwingSideUnitData, ViewUsed = true, At = DateTime.UtcNow };
            }
            catch (Exception e) { Hooks.WarnOnce("SnesUmaro.SelectThrown", e); }
        }

        public static void Critical_Postfix(Last.Data.PlayerCharacterParameter __instance, ref int __result)
        {
            if (critParam == IntPtr.Zero && critParam2 == IntPtr.Zero || __instance == null) return;
            var p = __instance.Pointer;
            if (p == IntPtr.Zero || (p != critParam && p != critParam2)) return;
            __result = 100;
        }

        // --- tests ----------------------------------------------------------------------------------

        // An attack on `target` run the way the game runs an act's functions
        // (ParameterActFunctionManagment.GetFunction, Init, FixedTarget, HitCalc; no
        // UpdateParameter, so nothing is applied): who took it, and the hit type there.
        public static (BattleUnitData Took, Last.Systems.HitType Hit, string Error) RunAct(
            BattleUnitData attacker, BattleUnitData target, Last.Data.Master.Command command, Last.Data.Master.Ability ability)
        {
            try
            {
                var abilities = new Il2CppSystem.Collections.Generic.List<Last.Data.Master.Ability>();
                abilities.Add(ability);
                var targets = new Il2CppReferenceArray<BattleUnitData>(1);
                targets[0] = target;
                var act = new BattleActData(attacker, targets, command, abilities, BattleActData.ActType.Normal,
                    (BattleActData.TargetType)0, BattleActData.ArmType.Right, (BattleActData.ActAbilityType)0, 1, false);
                var fns = ParameterActFunctionManagment.GetFunction(ability);
                BattleFunction2 f = null;
                if (fns != null)
                    foreach (var x in fns)
                        if (x != null && (f = x.TryCast<BattleFunction2>()) != null) break;
                if (f == null) return (null, default, "no BattleFunction2 for the ability");
                var results = new Il2CppSystem.Collections.Generic.Dictionary<BattleUnitData, Last.Systems.ICalcResult>();
                var attacks = new Il2CppSystem.Collections.Generic.Dictionary<BattleUnitData, Last.Systems.ICalcResult>();
                f.Init(act, results, attacks);
                f.FixedTarget();
                f.HitCalc();
                var now = act.TargetUnitDatas;
                var took = now != null && now.Length > 0 ? now[0] : null;
                var dic = f.ICalcResultDic ?? results;
                if (took == null || !dic.ContainsKey(took)) return (took, default, "no result");
                return (took, dic[took].GetHitType(), null);
            }
            catch (Exception e) { return (null, default, e.GetType().Name + ": " + e.Message); }
        }

        // In a throwaway battle (the mod's probe or test run decides when): the toss (ability 200) by
        // a party member on the first monster through the game's act flow. With Mog's toss already
        // picked by "the animation" every hit is a critical; with another ally's, the game's own
        // rate; with none picked, the hit makes the pick (and leaves it for the animation). Null
        // until a battle with a party and a monster.
        public static string TossSelfTest()
        {
            if (!Mog) return "[umaro] toss self-test: off";
            var battle = BattlePlugManager.Instance();
            if (battle == null || !battle.IsBattle()) return null;
            var players = battle.GetPlayerUnits();
            var enemies = battle.GetEnemyUnits();
            var mm = Last.Data.Master.MasterManager.Instance;
            var fight = mm?.GetData<Last.Data.Master.Command>(1);
            var tossAbility = mm?.GetData<Last.Data.Master.Ability>(TossAbility);
            if (players == null || players.Count == 0 || enemies == null || enemies.Count == 0 || fight == null || tossAbility == null) return null;
            BattleUnitData thrower = players[0], other = players.Count > 1 ? players[1] : players[0], enemy = enemies[0];
            int otherId = other.TryCast<global::BattlePlayerData>()?.ownedCharacterData?.characterStatusId ?? -1;
            const int N = 40;
            int mogWas = mogStatusId;
            string firstError = null;
            (int Hits, int Crits, int Errors) Run(bool pending)
            {
                int hits = 0, crits = 0, errors = 0;
                for (int i = 0; i < N; i++)
                {
                    toss = pending ? new Toss { Thrower = thrower.Pointer, Thrown = other, ViewUsed = true, At = DateTime.UtcNow } : null;
                    var r = RunAct(thrower, enemy, fight, tossAbility);
                    if (r.Error != null) { errors++; firstError ??= r.Error; continue; }
                    if (r.Hit == Last.Systems.HitType.Critical) { hits++; crits++; }
                    else if (r.Hit == Last.Systems.HitType.Hit) hits++;
                }
                return (hits, crits, errors);
            }
            try
            {
                mogStatusId = otherId;   // the waiting ally counts as Mog
                var mog = Run(true);
                mogStatusId = -1;        // nobody is Mog
                var plain = Run(true);
                toss = null;
                var picked = Run(false);
                var left = toss;
                bool pickOk = left != null && left.HitUsed && !left.ViewUsed && left.Thrown != null && left.Thrower == thrower.Pointer;
                bool ok = mog.Errors + plain.Errors + picked.Errors == 0 && mog.Hits > 0 && mog.Crits == mog.Hits && plain.Crits < plain.Hits && pickOk;
                string name = Name(other);
                return $"[umaro] {mod.Name} toss self-test {(ok ? "ok" : "FAILED")}: {N} tosses by {Name(thrower)} " +
                       $"on monster {enemy.TryCast<BattleEnemyData>()?.GetMonster()?.Id}: {name} as Mog: {mog.Crits} criticals of {mog.Hits} hits; " +
                       $"{name} not Mog: {plain.Crits} of {plain.Hits}; no pick waiting: {picked.Crits} of {picked.Hits}, the hit picked " +
                       $"{(Name(left?.Thrown) ?? "nobody")} for the animation; " +
                       $"errors {mog.Errors + plain.Errors + picked.Errors}{(firstError != null ? $" (first: {firstError})" : "")}";
            }
            finally
            {
                mogStatusId = mogWas;
                toss = null;
            }
        }
    }
}
