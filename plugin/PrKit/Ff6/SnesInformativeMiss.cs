using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Last.Battle;
using Last.Battle.Function;
using Last.Data;
using Last.Data.Master;
using Last.Management;
using Last.Systems;
using UnityEngine;

namespace PrKit.Ff6
{
    // Informative misses for any FF6 mod (Bropedio's "Informative Miss", which Brave New World and Beyond Chaos's
    // `informativemiss` both carry; 2026-11-20, from BnwPlugin/BattleUi.cs, bnw-text-ui's). Beyond Chaos uses it;
    // Brave New World from 0.25 (branch bnw-v025-items, 2026-11-21: BattleUi.cs keeps only its Config gate and its
    // tests; its own copy stays in main's BattleUi.cs until that lands). A status attack that changes nothing shows
    //   "Null"  when the target is immune to every status it would set (the resistance list: 1 = resists),
    //   "Fail"  when the target was open to a status but a forced miss stopped it (the mod says which forced misses
    //           count: BNW's Spirit evasion; by default every forced miss but the PR's own special refusals),
    //   "Miss"  otherwise (hit roll, status already there, the PR's own special refusals).
    // The PR decides a status per target in AddConditionFunction.Calc / AddConditionMultiFunction.Calc (decompiled
    // tools/decomp/bnw_ui): IsAddable, then the serial provider's CheckForceMissCondition (forced miss), then the
    // target's resistance list; a miss of all of them makes the function's result HitType.Miss. Each such miss is
    // sorted as Bropedio's patch does (Fail before Null before Miss) and kept per target; when the game makes that
    // target's miss popup (BattleUtility.CreateDamageView(unit, .., isMiss) -> ViewDamageEntity.ViewMiss, which sets
    // the "miss_sprite" image and scales it to the sprite's size in pixels) the image gets FAIL or NULL drawn in
    // the Miss sprite's own style: 7-pixel white letters (240,240,240), a 1-pixel dark outline (24,24,40), 1 pixel
    // between letters. English only for now (the words are drawn, not translated); other languages keep Miss.
    // Off unless a mod calls Apply, and while its Enabled is false.
    public sealed class SnesInformativeMissMod
    {
        public string Name = "kit";
        public Func<bool> Enabled = () => true;
        // a forced miss (CheckForceMissCondition true) for this condition counts as "Fail"
        public Func<Condition, bool> ForcedIsFail = c => c != null && !SnesInformativeMiss.PrSpecialRefusals.Contains(c.ConditionType);
        public string Tag = "[informative miss]";
    }

    public static class SnesInformativeMiss
    {
        public enum Kind { Miss, Null, Fail }          // Bropedio's order: Fail wins over Null, Null over Miss
        public static readonly HashSet<int> PrSpecialRefusals = new HashSet<int> { 601, 515, 605 };   // Imp, Control, WeakMaker
        const int English = 2;
        const float KeepSeconds = 30f;                 // a sort waits this long for its popup (summons animate long)

        static SnesInformativeMissMod mod;
        // per target parameter: the act's sort of its status misses (Hit: a status function hit it)
        static readonly Dictionary<IntPtr, (Kind Kind, bool Hit, float At, int Frame)> kinds = new Dictionary<IntPtr, (Kind, bool, float, int)>();
        static IntPtr calcTarget;
        static bool calcActive, calcFail, calcNull;
        static Kind? pending;                          // the kind for the miss popup being made
        public static Kind? Forced;                    // tests: the kind for the next miss popup
        public static int Shown;                       // FAIL/NULL popups drawn (tests)

        static bool On => mod != null && mod.Enabled();

        public static void Apply(Harmony h, SnesInformativeMissMod m)
        {
            mod = m;
            int n = 0;
            if (Hooks.Patch(h, typeof(AddConditionFunction), "Calc", typeof(SnesInformativeMiss), prefix: nameof(Calc_Prefix), postfix: nameof(Calc_Postfix))) n++;
            if (Hooks.Patch(h, typeof(AddConditionMultiFunction), "Calc", typeof(SnesInformativeMiss), prefix: nameof(Calc_Prefix), postfix: nameof(Calc_Postfix))) n++;
            // last among the postfixes on CheckForceMissCondition: a mod's own forced misses (BNW's Spirit evasion) first
            var force = AccessTools.Method(typeof(Serial.FF6.Battle.AddConditionFunctionSerial), "CheckForceMissCondition");
            if (force != null && Hooks.Patch(h, force, typeof(SnesInformativeMiss), postfix: nameof(ForceMiss_Postfix), priority: Priority.Last)) n++;
            if (Hooks.Patch(h, typeof(BattleUtility), "CreateDamageView", typeof(SnesInformativeMiss), prefix: nameof(View_Prefix), postfix: nameof(View_Postfix),
                            args: new[] { typeof(BattleUnitData), typeof(int), typeof(bool), typeof(bool) })) n++;
            if (Hooks.Patch(h, typeof(ViewDamageEntity), "ViewMiss", typeof(SnesInformativeMiss), postfix: nameof(ViewMiss_Postfix))) n++;
            // a new act: the last one's sorts go (its popups are made during its own animation)
            if (Hooks.Patch(h, typeof(Serial.FF6.Calc.FunctionProvider), "CreateActFunction", typeof(SnesInformativeMiss), prefix: nameof(Act_Prefix))) n++;
            Kit.Info($"{m.Tag} {m.Name}: {n} of 6 hooks (Null / Fail in place of Miss, English)");
        }

        // --- the status calc ------------------------------------------------------------------------------

        public static void Act_Prefix() => kinds.Clear();

        public static void Calc_Prefix(CharacterParameterBase targetParameter)
        {
            calcActive = On && targetParameter != null;
            calcTarget = targetParameter?.Pointer ?? IntPtr.Zero;
            calcFail = calcNull = false;
        }

        public static void ForceMiss_Postfix(CharacterParameterBase targetParameter, Condition condition, bool __result)
        {
            if (!calcActive || targetParameter == null || condition == null || targetParameter.Pointer != calcTarget) return;
            try
            {
                if (__result)
                {
                    if (mod.ForcedIsFail(condition)) calcFail = true;
                    return;
                }
                // not forced: the resistance list decides next (AddConditionFunction.Calc); 1 = resists
                var res = targetParameter.ConfirmedResistanceCondtionList();
                if (res != null && res.ContainsKey(condition.Id) && res[condition.Id] == 1) calcNull = true;
            }
            catch (Exception e) { Hooks.WarnOnce("kit-informative-miss-force", e); }
        }

        public static void Calc_Postfix(FunctionBase __instance, CharacterParameterBase targetParameter)
        {
            if (!calcActive) return;
            calcActive = false;
            try
            {
                if (targetParameter == null) return;
                var key = targetParameter.Pointer;
                bool hit = __instance.calcResult?.GetHitType() != HitType.Miss;
                var kind = hit ? Kind.Miss : calcFail ? Kind.Fail : calcNull ? Kind.Null : Kind.Miss;
                // several status functions of one act (one frame): sorted together, and a status that landed
                // means the act didn't miss there
                if (kinds.TryGetValue(key, out var was) && was.Frame == Time.frameCount)
                {
                    hit |= was.Hit;
                    if (was.Kind > kind) kind = was.Kind;
                }
                kinds[key] = (kind, hit, Time.realtimeSinceStartup, Time.frameCount);
                if (hit || kind == Kind.Miss) return;
                Hooks.NoteOnce("kit-miss-" + kind, () => $"{mod.Tag} a status miss sorted as {kind.ToString().ToUpper()}");
            }
            catch (Exception e) { Hooks.WarnOnce("kit-informative-miss-calc", e); }
        }

        // the sort kept for a unit's next miss popup (null: none, or a status landed), taken once
        public static Kind? KindFor(BattleUnitData unit) => Find(unit, true);

        // the same without taking it (a test or a mod's own display reads it; the popup still gets it)
        public static Kind? PeekKindFor(BattleUnitData unit) => Find(unit, false);

        static Kind? Find(BattleUnitData unit, bool take)
        {
            if (unit == null) return null;
            var keys = new List<IntPtr>();
            var p = unit.BattleUnitDataInfo?.Parameter;
            if (p != null) keys.Add(p.Pointer);
            var owned = unit.TryCast<BattlePlayerData>()?.ownedCharacterData?.Parameter;
            if (owned != null) keys.Add(owned.Pointer);
            foreach (var k in keys)
                if (kinds.TryGetValue(k, out var v) && Time.realtimeSinceStartup - v.At <= KeepSeconds)
                {
                    if (take) kinds.Remove(k);
                    return v.Hit ? null : v.Kind;
                }
            return null;
        }

        // --- the popup ------------------------------------------------------------------------------------

        public static void View_Prefix(BattleUnitData targetUnitData, bool isMiss)
        {
            pending = null;
            if (!isMiss) return;
            // a forced sort is for the next miss popup, shown or not: cleared before the off / language checks, so
            // it never waits for a later popup (bnw-atb, 2026-11-21)
            var forced = Forced;
            Forced = null;
            if (!On) return;
            try
            {
                if ((int)(MessageManager.Instance?.currentLanguage ?? 0) != English) return;
                var k = forced ?? KindFor(targetUnitData);
                if (k == Kind.Fail || k == Kind.Null) pending = k;
            }
            catch (Exception e) { Hooks.WarnOnce("kit-informative-miss-view", e); }
        }

        public static void View_Postfix() => pending = null;

        public static void ViewMiss_Postfix(ViewDamageEntity __instance)
        {
            if (pending == null) return;
            var kind = pending.Value;
            pending = null;
            try
            {
                var image = __instance.prefab?.transform?.GetChild(0)?.GetComponent<UnityEngine.UI.Image>();
                var sprite = SpriteFor(kind);
                if (image == null || sprite == null) return;
                image.sprite = sprite;
                var rt = image.rectTransform;
                var z = rt.localScale.z;
                rt.localScale = new Vector3(sprite.rect.width, sprite.rect.height, z);   // as ViewMiss: the sprite's size in pixels
                Shown++;
                Hooks.NoteOnce("kit-popup-" + kind, () => $"{mod?.Tag ?? "[informative miss]"} {kind.ToString().ToUpper()} shown in place of Miss ({sprite.rect.width}x{sprite.rect.height})");
            }
            catch (Exception e) { Hooks.WarnOnce("kit-informative-miss-popup", e); }
        }

        // 7 rows per letter, '#' = a letter pixel
        static readonly Dictionary<char, string[]> Glyphs = new Dictionary<char, string[]>
        {
            ['F'] = new[] { "#####", "#....", "#....", "####.", "#....", "#....", "#...." },
            ['A'] = new[] { ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" },
            ['I'] = new[] { "#", "#", "#", "#", "#", "#", "#" },
            ['L'] = new[] { "#...", "#...", "#...", "#...", "#...", "#...", "####" },
            ['N'] = new[] { "#...#", "##..#", "#.#.#", "#.#.#", "#..##", "#...#", "#...#" },
            ['U'] = new[] { "#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." },
        };
        static readonly Dictionary<Kind, Sprite> sprites = new Dictionary<Kind, Sprite>();

        public static Sprite SpriteFor(Kind kind)
        {
            if (sprites.TryGetValue(kind, out var s) && s != null) return s;
            string word = kind == Kind.Fail ? "FAIL" : "NULL";
            var mask = Mask(word, out int w, out int h);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var fill = new Color32(240, 240, 240, 255);
            var edge = new Color32(24, 24, 40, 255);
            var clear = new Color32(0, 0, 0, 0);
            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool on = mask[y, x], near = false;
                    for (int dy = -1; dy <= 1 && !near; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int yy = y + dy, xx = x + dx;
                            if (yy >= 0 && yy < h && xx >= 0 && xx < w && mask[yy, xx]) { near = true; break; }
                        }
                    // the texture's rows go bottom-up
                    pixels[(h - 1 - y) * w + x] = on ? fill : near ? edge : clear;
                }
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            tex.hideFlags = HideFlags.HideAndDontSave;   // kept across battles (unused-asset sweeps)
            s = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            s.name = "KIT_" + word;
            s.hideFlags = HideFlags.HideAndDontSave;
            sprites[kind] = s;
            return s;
        }

        // the word's letter pixels with a 1-pixel border for the outline: [row, column]
        public static bool[,] Mask(string word, out int w, out int h)
        {
            h = 9;
            w = 2 + word.Sum(c => Glyphs[c][0].Length) + word.Length - 1;
            var m = new bool[h, w];
            int x0 = 1;
            foreach (var c in word)
            {
                var g = Glyphs[c];
                for (int y = 0; y < 7; y++)
                    for (int x = 0; x < g[y].Length; x++)
                        if (g[y][x] == '#') m[y + 1, x0 + x] = true;
                x0 += g[0].Length + 1;
            }
            return m;
        }
    }
}
