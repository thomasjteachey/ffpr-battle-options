using System;
using System.Collections.Generic;
using HarmonyLib;
using Last.Battle;
using PrKit;
using UnityEngine;
using InfoController = Last.UI.KeyInput.BattlePlayerInfomationController;

namespace BattleOptions
{
    // Cast Bars: while a character waits between a chosen command and its action (the game "chants":
    // BattleProgressATB.chantingInfo holds the act and its time left, counted down by UpdateChantTime;
    // a spell's cast, an attack's wind-up, Jump's time in the air), their ATB gauge shows the wait left,
    // draining. Brave New World's Wait Gauge (BnwPlugin/WaitGauge.cs), as a setting for every game.
    //  * the wait's length: the chant time the game gives the act (GetChantingTime, seen last, after
    //    other mods change it: Worlds Collide's SNES waits, Brave New World's), or, where a game has no
    //    GetChantingTime (FF4), the longest time seen on the act's clock;
    //  * the bar: BattlePlayerInfomationController.UpdateATB(player, value) draws value; set last.
    //    With Cast Bars off, the value the game passed is put back, so another mod's bar (Brave New
    //    World's) goes too: the menu's setting decides.
    // Not in FINAL FANTASY V: every command there acts at once (no waits to show), so no row either.
    class CastBars : Feature
    {
        internal static bool Offered => Game.Current != PrGame.FF5;

        static readonly Dictionary<IntPtr, float> totals = new Dictionary<IntPtr, float>();               // act -> its wait
        static readonly Dictionary<IntPtr, (float Last, int Dir)> clocks = new Dictionary<IntPtr, (float, int)>();
        static readonly Dictionary<IntPtr, float> given = new Dictionary<IntPtr, float>();               // player -> the game's value
        internal static float FullValue = 1f;   // the gauge value of a full ATB bar (1 or 100, learnt)
        internal static int Drawn;              // bars drawn as a wait (test)
        internal static UnityEngine.UI.Image GaugeImage;   // a character's ATB gauge (EnemyAtb draws its look)

        public override void Apply(Harmony h)
        {
            int chants = 0;
            foreach (var t in new[] { Hooks.TypeByName("Last.Battle.BattleProgressATBFF6"),
                                      Hooks.TypeByName("Last.Battle.BattleProgressATBFF0"), typeof(BattleProgressATB) })
            {
                var m = t == null ? null : AccessTools.DeclaredMethod(t, "GetChantingTime");
                if (m != null && Patching.Patch(h, m, typeof(CastBars), postfix: nameof(Chant_Postfix), priority: Priority.Last)) chants++;
            }
            var bar = AccessTools.Method(typeof(InfoController), "UpdateATB");
            bool first = Patching.Patch(h, bar, typeof(CastBars), prefix: nameof(Given_Prefix), priority: Priority.First);
            bool last = Patching.Patch(h, bar, typeof(CastBars), prefix: nameof(Bar_Prefix), priority: Priority.Last);
            Kit.Info($"[cast] chant times hooked {chants}, ATB bar {(first && last ? "hooked" : "NOT hooked")}");
        }

        public static void Chant_Postfix(BattleActData __0, float __result)
        {
            try
            {
                if (__0 == null) return;
                lock (totals) totals[__0.Pointer] = __result;
            }
            catch (Exception e) { Hooks.WarnOnce("cast.chant", e); }
        }

        // first: the game's value (and a character's gauge, for EnemyAtb's look)
        public static void Given_Prefix(InfoController __instance, BattlePlayerData player, float value)
        {
            if (player == null || !Hooks.IsA<InfoController>(__instance)) return;
            try
            {
                given[player.Pointer] = value;
                if (value > FullValue) FullValue = value;
                if (GaugeImage == null)
                {
                    var contents = __instance.contentList;
                    if (contents != null && contents.Count > 0)
                        GaugeImage = contents[0]?.view?.atbGauge?.gaugeImage;
                }
            }
            catch (Exception e) { Hooks.WarnOnce("cast.given", e); }
        }

        // last: the wait left, or the game's own value with Cast Bars off
        public static void Bar_Prefix(InfoController __instance, BattlePlayerData player, ref float value)
        {
            if (!Offered || player == null || !Hooks.IsA<InfoController>(__instance)) return;
            try
            {
                if (!Settings.CastBars.Value)
                {
                    if (given.TryGetValue(player.Pointer, out var v)) value = v;
                    return;
                }
                float left = WaitLeft(player);
                if (left < 0f) return;
                value = left * FullValue;
                Drawn++;
            }
            catch (Exception e) { Hooks.WarnOnce("cast.bar", e); }
        }

        static BattleProgressATB Progress() => BattlePlugManager.Instance()?.BattleProgress?.TryCast<BattleProgressATB>();

        // The wait left for a character's chosen action, as a fraction (1 = just chosen), or -1.
        internal static float WaitLeft(BattleUnitData unit)
        {
            var list = Progress()?.chantingInfo;
            if (list == null) return -1f;
            for (int i = 0; i < list.Count; i++)
            {
                var info = list[i];
                var act = info.actData;
                if (act?.AttackUnitData == null || act.AttackUnitData.Pointer != unit.Pointer) continue;
                float time = info.time, total;
                lock (totals)
                {
                    totals.TryGetValue(act.Pointer, out total);
                    if (time > total) totals[act.Pointer] = total = time;   // no chant time seen (FF4): the clock's start
                }
                if (total <= 0f) return -1f;
                // the clock counts down (UpdateChantTime); learnt from two readings all the same
                int dir = 0;
                if (clocks.TryGetValue(act.Pointer, out var c))
                    dir = time < c.Last ? -1 : time > c.Last ? 1 : c.Dir;
                clocks[act.Pointer] = (time, dir);
                if (dir == 0) return 1f;
                return Mathf.Clamp01(dir < 0 ? time / total : 1f - time / total);
            }
            return -1f;
        }

        // A new battle: forget the last one's actions.
        static bool inBattle;

        public override void Update()
        {
            var battle = BattlePlugManager.Instance();
            bool now = battle != null && battle.IsBattle();
            if (now && !inBattle)
            {
                lock (totals) totals.Clear();
                clocks.Clear();
                given.Clear();
            }
            inBattle = now;
        }
    }
}
