using System;
using System.Collections.Generic;
using HarmonyLib;
using Last.Battle;

namespace PrKit.Ff6
{
    // MP damage and MP healing in colours of their own (SNES hacks: SilentEnigma's "Decoupled Color-Coded MP Digits",
    // C2/63A8, in Beyond Chaos's `magicnumbers`; T-Edition's MP digits). The PR shows MP like HP: BattleBasicFunction.
    // CreateDamageView(unit, value, hitType, isRecovery) passes hit type 5 (MPHit) as damage and 6 (MPRecovery) as
    // healing to BattleUtility.CreateDamageView(unit, value, isRecovery, isMiss), whose ViewDamageEntity has no MP
    // style. The hit type is caught on the first, the popup the second makes is remembered, and Update() tints its
    // images every frame for Seconds (the digits are made over the popup's first frames; alpha stays the popup's).
    // Off unless a mod calls Apply; one mod's colours at a time (each plugin compiles its own kit copy).
    //
    //   SnesMpNumbers.Apply(harmony, new SnesMpNumbersMod { Name = "T-Edition", MpDamage = pink, MpHeal = blue,
    //                                                       Enabled = () => TedMode });
    //   SnesMpNumbers.Update();   // every frame (the plugin's runner)
    public sealed class SnesMpNumbersMod
    {
        public string Name = "kit";
        public UnityEngine.Color MpDamage = new UnityEngine.Color(0.78f, 0.6f, 1f);
        public UnityEngine.Color MpHeal = new UnityEngine.Color(0.5f, 0.85f, 1f);
        public float Seconds = 2.5f;
        public Func<bool> Enabled = () => true;
    }

    public static class SnesMpNumbers
    {
        static SnesMpNumbersMod mod;
        static int hitType = -1;
        public static int Tinted { get; private set; }
        static readonly List<(ViewDamageEntity View, UnityEngine.Color Color, float Until)> views =
            new List<(ViewDamageEntity, UnityEngine.Color, float)>();

        // the hooks once (a second call only changes the colours); returns how many of the 2 hooks are in
        public static int Apply(Harmony h, SnesMpNumbersMod m)
        {
            bool first = mod == null;
            mod = m ?? new SnesMpNumbersMod();
            if (!first) return 2;
            int n = 0;
            if (Hooks.Patch(h, typeof(Last.Battle.Function.BattleBasicFunction), "CreateDamageView", typeof(SnesMpNumbers),
                            prefix: nameof(Basic_Prefix), postfix: nameof(Basic_Postfix))) n++;
            var view = AccessTools.Method(typeof(BattleUtility), "CreateDamageView",
                new[] { typeof(BattleUnitData), typeof(int), typeof(bool), typeof(bool) });
            if (view != null && Hooks.Patch(h, view, typeof(SnesMpNumbers), postfix: nameof(View_Postfix))) n++;
            Kit.Info($"[mp numbers] {mod.Name}: {n} of 2 hooks");
            return n;
        }

        static bool On => mod != null && mod.Enabled();

        // HitType 5 = MPHit (MP damage), 6 = MPRecovery (MP healing)
        public static void Basic_Prefix(Last.Systems.HitType hitType) => SnesMpNumbers.hitType = (int)hitType;
        public static void Basic_Postfix() => hitType = -1;

        public static void View_Postfix(ViewDamageEntity __result)
        {
            try
            {
                if (__result == null || (hitType != 5 && hitType != 6) || !On) return;
                views.Add((__result, hitType == 5 ? mod.MpDamage : mod.MpHeal, UnityEngine.Time.realtimeSinceStartup + mod.Seconds));
                Tinted++;
                Hooks.NoteOnce("mp-numbers-" + mod.Name, () => $"[mp numbers] {mod.Name}: MP {(hitType == 5 ? "damage" : "healing")} tinted");
            }
            catch (Exception e) { Hooks.WarnOnce("SnesMpNumbers.View", e); }
        }

        // every frame: the remembered popups' images take their colour
        public static void Update()
        {
            if (views.Count == 0) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            for (int i = views.Count - 1; i >= 0; i--)
            {
                var (v, col, until) = views[i];
                try
                {
                    if (v == null || v.WasCollected || now > until) { views.RemoveAt(i); continue; }
                    foreach (var g in v.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
                        if (g != null) g.color = new UnityEngine.Color(col.r, col.g, col.b, g.color.a);
                    foreach (var r in v.GetComponentsInChildren<UnityEngine.SpriteRenderer>(true))
                        if (r != null) r.color = new UnityEngine.Color(col.r, col.g, col.b, r.color.a);
                }
                catch (Exception e) { views.RemoveAt(i); Hooks.WarnOnce("SnesMpNumbers.Tint", e); }
            }
        }

        public static string State() => mod == null ? "[mp numbers] off" : $"[mp numbers] {mod.Name}: {(On ? "on" : "off")}, {Tinted} popups tinted";
    }
}
