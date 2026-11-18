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
    class EnemyAtb : Feature
    {
        internal static int Shown;   // gauges drawn in the last frame (test)
        static Texture2D solid;
        static int cameraFrame = -1;
        static Camera[] cameras = Array.Empty<Camera>();

        public override void OnGUI()
        {
            var e = Event.current;
            if (e == null || e.type != EventType.Repaint) return;
            Shown = 0;
            if (!Settings.EnemyAtb.Value) return;
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
            foreach (var enemy in enemies)
            {
                if (enemy == null) continue;
                var p = enemy.BattleUnitDataInfo?.Parameter;
                if (p == null || p.CurrentHP <= 0 || !gauges.ContainsKey(enemy)) continue;
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

        static void Draw(Rect r, float amount, float s)
        {
            if (solid == null)
            {
                solid = new Texture2D(1, 1);
                solid.SetPixel(0, 0, Color.white);
                solid.Apply();
                solid.hideFlags = HideFlags.HideAndDontSave;
            }
            var keep = GUI.color;
            float o = Mathf.Max(1f, Mathf.Round(s));
            GUI.color = Edge;
            GUI.DrawTexture(new Rect(r.x - o, r.y - o, r.width + 2 * o, r.height + 2 * o), solid);
            GUI.color = Back;
            GUI.DrawTexture(r, solid);
            if (amount > 0f)
            {
                var fill = new Rect(r.x, r.y, r.width * amount, r.height);
                var img = CastBars.GaugeImage;
                var sprite = img != null ? img.sprite : null;
                if (sprite != null && sprite.texture != null)
                {
                    // the character gauge's own art (its fill, tinted as the game tints it)
                    var tex = sprite.texture;
                    var tr = sprite.textureRect;
                    var uv = new Rect(tr.x / tex.width, tr.y / tex.height, tr.width / tex.width * amount, tr.height / tex.height);
                    GUI.color = img.color;
                    GUI.DrawTextureWithTexCoords(fill, tex, uv);
                }
                else
                {
                    GUI.color = Fill;
                    GUI.DrawTexture(fill, solid);
                }
            }
            GUI.color = keep;
        }
    }
}
