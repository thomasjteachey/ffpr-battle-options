using System;
using System.Collections.Generic;
using Last.Battle;
using Last.UI;
using PrKit;
using UnityEngine;

namespace BattleOptions
{
    // Enemy ATB: each enemy's ATB gauge, under its sprite. The gauge is the game's own number
    // (BattleProgressATB.gaugeStatusDictionary, full at MaxATBGauge); the sprite is the enemy's entity
    // (BattlePlugManager.EventCommand.GetEnemyEntity(UniqueId).CharacterSR.MeshRenderer), projected by
    // the camera that draws it (with HD-2D too). Drawn over the battle (IMGUI) with the look of a
    // character's ATB gauge (its sprite and colour, CastBars.GaugeImage), hidden while the battle is
    // paused or its menus are hidden, and for enemies that are gone (dead, not shown).
    // The game's build strips GUI.DrawTexture and GUI.DrawTextureWithTexCoords (they throw "Method
    // unstripping failed"), so every piece is a GUI.Box with a style whose background is the art.
    class EnemyAtb : Feature
    {
        internal static int Shown;   // gauges drawn in the last frame (test)
        internal static string Art = "none";   // where the fill's look came from (test)
        static int cameraFrame = -1;
        static Camera[] cameras = Array.Empty<Camera>();

        public override void Update()
        {
            if (!Settings.EnemyAtb.Value) return;
            var bm = BattlePlugManager.Instance();
            if (bm != null && bm.IsBattle()) BuildStyles();   // a Blit, so here and not mid-OnGUI
        }

        public override void OnGUI()
        {
            var e = Event.current;
            if (e == null || e.type != EventType.Repaint) return;
            Shown = 0;
            if (!Settings.EnemyAtb.Value || fillStyle == null) return;
            float s = Screen.height / 720f;
            foreach (var (_, rect, amount) in Layout(out var why))
            {
                Draw(rect, amount, s);
                Shown++;
            }
        }

        // Where each enemy's gauge goes this frame (screen rect, top-left origin) and how full it is;
        // why: what hid them all, if nothing is listed.
        internal static List<(int Id, Rect Rect, float Amount)> Layout(out string why)
        {
            var list = new List<(int, Rect, float)>();
            why = null;
            var bm = BattlePlugManager.Instance();
            if (bm == null || !bm.IsBattle()) { why = "no battle"; return list; }
            var ui = BattleUIManager.Instance;
            if (ui == null || ui.IsPause() || ui.isHideMenu) { why = "battle paused or its menus hidden"; return list; }
            if (ui.canvasGroup != null && ui.canvasGroup.alpha < 0.05f) { why = "battle UI faded out"; return list; }
            var progress = bm.BattleProgress?.TryCast<BattleProgressATB>();
            var gauges = progress?.gaugeStatusDictionary;
            var enemies = bm.GetEnemyUnits();
            var events = bm.EventCommand;
            if (gauges == null || enemies == null || events == null) { why = "no gauges"; return list; }
            float max = BattleProgressATB.MaxATBGauge > 0f ? BattleProgressATB.MaxATBGauge : 100f;
            float s = Screen.height / 720f;
            var conditions = bm.BattleConditionController;
            foreach (var enemy in enemies)
            {
                if (enemy == null) continue;
                var p = enemy.BattleUnitDataInfo?.Parameter;
                if (p == null || p.CurrentHP <= 0 || !gauges.ContainsKey(enemy) || Gone(enemy, conditions)) continue;
                var body = events.GetEnemyEntity(enemy.UniqueId)?.CharacterSR?.MeshRenderer;
                if (body == null || !body.enabled || !body.gameObject.activeInHierarchy) continue;
                var cam = CameraFor(body.gameObject.layer);
                if (cam == null) continue;
                var b = body.bounds;
                Vector3 left = cam.WorldToScreenPoint(new Vector3(b.min.x, b.min.y, b.center.z));
                Vector3 right = cam.WorldToScreenPoint(new Vector3(b.max.x, b.min.y, b.center.z));
                if (left.z < 0f) continue;
                float k = cam.targetTexture == null ? 1f : Screen.width / (float)Math.Max(1, cam.pixelWidth);
                float cx = (left.x + right.x) / 2f * k;
                float w = Mathf.Clamp(Mathf.Abs(right.x - left.x) * k * 0.6f, 44f * s, 110f * s);
                float h = Mathf.Max(4f, 7f * s);
                float y = Screen.height - left.y * k + 3f * s;
                list.Add((enemy.UniqueId, new Rect(cx - w / 2f, y, w, h), Mathf.Clamp01(gauges[enemy] / max)));
            }
            if (list.Count == 0) why = "no enemy shown";
            return list;
        }

        // Out of the fight though still listed: KO'd (the game's "combat ineffective" check, which a
        // monster's death sets before its HP or sprite go), fled or removed (BattleUtility.IsEscape,
        // IsRemove), or taken out of the battle (withdrawal). The user, 2026-11-18: bars stayed up
        // after a monster fled or died.
        internal static bool Gone(BattleUnitData unit, Last.Battle.BattleConditionController conditions)
        {
            try
            {
                if (unit.IsRemove || BattleUtility.IsEscape(unit)) return true;
                return conditions != null &&
                       (conditions.IsCombatIneffectiveCondition(unit) || conditions.IsWithdrawalCondition(unit));
            }
            catch (Exception e)
            {
                Hooks.WarnOnce("enemy gauge gone check", e);
                return false;
            }
        }

        static Camera CameraFor(int layer)
        {
            if (Time.frameCount != cameraFrame)
            {
                cameraFrame = Time.frameCount;
                cameras = Camera.allCameras;
            }
            Camera best = null;
            foreach (var c in cameras)
            {
                if (c == null || !c.isActiveAndEnabled || (c.cullingMask & (1 << layer)) == 0) continue;
                // the screen's own cameras before ones that draw into a texture, then the one drawn last
                if (best == null || (best.targetTexture != null && c.targetTexture == null) ||
                    ((best.targetTexture == null) == (c.targetTexture == null) && c.depth > best.depth)) best = c;
            }
            return best;
        }

        static readonly Color Back = new Color(0f, 0f, 0f, 0.6f);
        static readonly Color Edge = new Color(0.86f, 0.89f, 0.97f, 0.85f);
        static readonly Color Fill = new Color(0.97f, 0.9f, 0.45f, 1f);

        static GUIStyle edgeStyle, backStyle, fillStyle;
        static Texture2D fillArt;
        static GUIContent none;
        static IntPtr artFrom = (IntPtr)(-1);   // the gauge sprite the fill was made from (0: none yet)

        static void Draw(Rect r, float amount, float s)
        {
            float o = Mathf.Max(1f, Mathf.Round(s));
            GUI.Box(new Rect(r.x - o, r.y - o, r.width + 2 * o, r.height + 2 * o), none, edgeStyle);
            GUI.Box(r, none, backStyle);
            if (amount > 0f) GUI.Box(new Rect(r.x, r.y, r.width * amount, r.height), none, fillStyle);
        }

        // The frame, the back and the fill: the fill is the character gauge's own art (its sprite,
        // tinted as the game tints it) once a character's gauge has been seen, a plain yellow before.
        // The art is copied once (at most 3 tries): the copy reads the GPU back, a stall, and 0.1.1
        // copied again whenever the watched gauge's sprite changed, every battle frame it was
        // checked (suspected in the user's "why is my game hitching so hard", 2026-11-18).
        static int artCopies;

        static void BuildStyles()
        {
            if (edgeStyle == null)
            {
                none = new GUIContent("");
                edgeStyle = Style(Solid(Edge));
                backStyle = Style(Solid(Back));
            }
            if (fillStyle != null && fillArt != null && (artFrom != IntPtr.Zero || artCopies >= 3)) return;   // done (or the art never copies)
            var img = CastBars.GaugeImage;
            var sprite = img != null ? img.sprite : null;
            var from = sprite != null ? sprite.Pointer : IntPtr.Zero;
            if (fillStyle != null && fillArt != null && from == IntPtr.Zero) return;   // still no character gauge seen: keep the plain fill
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Texture2D art = null;
            if (sprite != null)
            {
                artCopies++;
                try { art = CopyArt(sprite, img.color); }
                catch (Exception e) { Hooks.WarnOnce("enemy gauge art", e); }
            }
            artFrom = art != null ? from : IntPtr.Zero;
            if (fillArt != null) UnityEngine.Object.Destroy(fillArt);
            fillArt = art ?? Solid(Fill);
            fillStyle = Style(fillArt);
            Art = art != null ? $"{sprite.name} {art.width}x{art.height}" : "plain";
            if (art != null || sprite != null)
                Kit.Info($"[enemy atb] gauge art: {Art} ({clock.ElapsedMilliseconds} ms, copy {artCopies})");
        }

        static GUIStyle Style(Texture2D background)
        {
            var st = new GUIStyle();
            st.normal.background = background;
            return st;
        }

        static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        // A readable copy of a sprite's part of its (unreadable) atlas, times the Image's colour.
        static Texture2D CopyArt(Sprite sprite, Color tint)
        {
            var src = sprite.texture;
            if (src == null || src.width <= 0 || src.height <= 0) return null;
            var r = sprite.textureRect;
            int w = Math.Max(1, Mathf.RoundToInt(r.width)), h = Math.Max(1, Mathf.RoundToInt(r.height));
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            var was = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt, new Vector2(r.width / src.width, r.height / src.height),
                                       new Vector2(r.x / src.width, r.y / src.height));
                RenderTexture.active = rt;
                var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
                t.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                var px = t.GetPixels();
                for (int i = 0; i < px.Length; i++) px[i] = px[i] * tint;
                t.SetPixels(px);
                t.Apply();
                t.wrapMode = TextureWrapMode.Clamp;
                t.filterMode = FilterMode.Bilinear;
                t.hideFlags = HideFlags.HideAndDontSave;
                return t;
            }
            finally
            {
                RenderTexture.active = was;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
