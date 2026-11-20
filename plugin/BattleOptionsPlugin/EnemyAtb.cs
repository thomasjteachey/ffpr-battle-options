using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Last.Battle;
using Last.Management;
using Last.UI;
using PrKit;
using UnityEngine;
using UiImage = UnityEngine.UI.Image;

namespace BattleOptions
{
    // Enemy ATB: each enemy's ATB gauge, under its sprite. The gauge is the game's own number
    // (BattleProgressATB.gaugeStatusDictionary, full at MaxATBGauge); the sprite is the enemy's entity
    // (BattlePlugManager.EventCommand.GetEnemyEntity(UniqueId).CharacterSR.MeshRenderer), projected by
    // the camera that draws it (with HD-2D too). Hidden while the battle is paused or its menus are
    // hidden, and for enemies that are gone (fled, removed, or dead once their death animation starts).
    //
    // Drawn as uGUI Images in the battle UI's own canvas, just before the battle menus
    // (BattleUIManager.keyMenuParent), so every menu window draws over them. Up to 0.2.1 they were
    // IMGUI (OnGUI), which Unity paints after every canvas, so the gauges showed through the menus
    // (the user, 2026-11-20: "the enemy atb gauge doesn't bleed through menus"). The look is
    // unchanged: a light frame, a dark back and the character gauge's own fill (its sprite and
    // colour, CastBars.GaugeImage), a plain yellow until a character's gauge has been seen.
    class EnemyAtb : Feature
    {
        internal static int Shown;               // gauges shown this frame (test)
        internal static string Art = "plain";    // the fill's look (test)
        internal static string Where = "none";   // where the gauges sit in the battle UI (test, log)

        static bool inBattle;

        public override void Update()
        {
            Shown = 0;
            var bm = BattlePlugManager.Instance();
            if (bm == null || !bm.IsBattle())
            {
                if (inBattle) NewBattle();
                inBattle = false;
                Hide();
                return;
            }
            if (!inBattle) NewBattle();
            inBattle = true;
            WatchActions(bm);
            if (!Settings.EnemyAtb.Value) { Hide(); return; }
            Place(Layout(out _));
        }

        static void NewBattle()
        {
            down.Clear();
            lastAmount.Clear();
            lastAction = -100f;
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
                if (p == null || Gone(enemy, conditions)) continue;
                int id = enemy.UniqueId;
                var entity = events.GetEnemyEntity(id);
                var body = entity?.CharacterSR?.MeshRenderer;
                if (body == null || !body.enabled || !body.gameObject.activeInHierarchy) continue;
                float amount;
                if (KnockedOut(enemy, p.CurrentHP, conditions))
                {
                    // the gauge as it was when the blow landed, until the death animation starts
                    if (Dying(id, entity) || !lastAmount.TryGetValue(id, out amount)) continue;
                }
                else
                {
                    down.Remove(id);   // alive (again: revived)
                    if (!gauges.ContainsKey(enemy)) continue;
                    amount = lastAmount[id] = Mathf.Clamp01(gauges[enemy] / max);
                }
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
                list.Add((id, new Rect(cx - w / 2f, y, w, h), amount));
            }
            if (list.Count == 0) why = "no enemy shown";
            return list;
        }

        // Out of the fight though still listed: fled or removed (BattleUtility.IsEscape, IsRemove), or
        // taken out of the battle (withdrawal: one status, SortId 628). The user, 2026-11-18: bars
        // stayed up after a monster fled or died.
        internal static bool Gone(BattleUnitData unit, Last.Battle.BattleConditionController conditions)
        {
            try
            {
                if (unit.IsRemove || BattleUtility.IsEscape(unit)) return true;
                if (conditions == null) return false;
                FindConditionChecks(conditions.GetType());
                return CallCondition(withdrawal, conditions, unit);
            }
            catch (Exception e)
            {
                Hooks.WarnOnce("enemy gauge gone check", e);
                return false;
            }
        }

        // KO'd: no HP, or the game's "combat ineffective" check. Both are set when the blow is worked
        // out, at the start of the action that lands it, long before the monster dies on screen.
        static bool KnockedOut(BattleUnitData unit, int hp, Last.Battle.BattleConditionController conditions)
        {
            if (hp <= 0) return true;
            try
            {
                if (conditions == null) return false;
                FindConditionChecks(conditions.GetType());
                return CallCondition(ineffective, conditions, unit);
            }
            catch (Exception e)
            {
                Hooks.WarnOnce("enemy gauge KO check", e);
                return false;
            }
        }

        // A KO'd enemy's gauge goes when its death animation starts (the fizzle), not when the blow
        // is worked out (the user, 2026-11-20: "removed when the monster starts its dying/fizzle
        // animation, not at the start of the command that killed them"). The game starts it in
        // UnableFightConditionFunction.ExecuteUnableFight: AnimStateController.SetIsDown(true) puts
        // the enemy's animator in DownStart, then Down (tools/decomp/enemy_down). Once seen it stays
        // gone until the enemy lives again. If no death animation has begun a second after the last
        // action ended (a death the game shows another way), it goes then.
        static readonly HashSet<int> down = new HashSet<int>();
        static readonly Dictionary<int, float> lastAmount = new Dictionary<int, float>();
        static float lastAction = -100f;

        static bool Dying(int id, BattleSpriteEntity entity)
        {
            if (down.Contains(id)) return true;
            if (InDeathAnimation(entity)) { down.Add(id); return true; }
            return Time.unscaledTime - lastAction > 1f;
        }

        static void WatchActions(BattlePlugManager bm)
        {
            try
            {
                var ex = bm.BattleActExection;
                if (ex != null && ex.GetStatingAct()) lastAction = Time.unscaledTime;
            }
            catch (Exception e) { Hooks.WarnOnce("enemy gauge action check", e); }
        }

        static bool animatorFailed;

        static bool InDeathAnimation(BattleSpriteEntity entity)
        {
            if (animatorFailed || entity == null) return false;
            try { return AnimatorDown(entity); }
            catch (Exception e)
            {
                animatorFailed = true;
                Hooks.WarnOnce("enemy gauge death animation", e);
                return false;
            }
        }

        // The animator's states by name: the games number them each their own way.
        static bool statesKnown;
        static BattleAnimatorBase.SpriteEntityState downStart, downState;

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool AnimatorDown(BattleSpriteEntity entity)
        {
            if (!statesKnown)
            {
                var t = typeof(BattleAnimatorBase.SpriteEntityState);
                downStart = (BattleAnimatorBase.SpriteEntityState)Enum.Parse(t, "DownStart");
                downState = (BattleAnimatorBase.SpriteEntityState)Enum.Parse(t, "Down");
                statesKnown = true;
            }
            var anim = entity.GetAnimator();
            if (anim == null) return false;
            var state = anim.GetState();
            return state == downStart || state == downState;
        }

        // These helpers exist in current FF6 but not in the older FF4/FF5 builds. Resolve them at
        // runtime so the shared checks above still work there without a MissingMethodException.
        // HP, IsRemove, escape state and sprite visibility remain the fallback when neither exists.
        static Type conditionType;
        static MethodInfo ineffective, withdrawal;

        static void FindConditionChecks(Type type)
        {
            if (conditionType == type) return;
            conditionType = type;
            ineffective = Patching.Find(type, "IsCombatIneffectiveCondition", 1);
            withdrawal = Patching.Find(type, "IsWithdrawalCondition", 1);
        }

        static bool CallCondition(MethodInfo method, object target, BattleUnitData unit)
        {
            if (method == null) return false;
            try { return method.Invoke(target, new object[] { unit }) is bool value && value; }
            catch (Exception e)
            {
                Hooks.WarnOnce("enemy gauge " + method.Name, e);
                return false;
            }
        }

        // Each enemy: HP, KO'd, its animator's state, death animation seen, gauge listed; then whether
        // an action is playing and where the gauges sit (test: bo enemies).
        internal static string Describe()
        {
            var bm = BattlePlugManager.Instance();
            if (bm == null || !bm.IsBattle()) return "no battle";
            var listed = new HashSet<int>();
            var layout = Layout(out var why);
            foreach (var (id, _, _) in layout) listed.Add(id);
            var conditions = bm.BattleConditionController;
            var parts = new List<string>();
            foreach (var e in bm.GetEnemyUnits())
            {
                if (e == null) continue;
                var p = e.BattleUnitDataInfo?.Parameter;
                var entity = bm.EventCommand?.GetEnemyEntity(e.UniqueId);
                string anim;
                try { anim = entity?.GetAnimator()?.GetState().ToString() ?? "none"; }
                catch (Exception ex) { anim = ex.GetType().Name; }
                bool ko = p != null && KnockedOut(e, p.CurrentHP, conditions);
                parts.Add($"enemy {e.UniqueId} hp {p?.CurrentHP} ko {ko} gone {Gone(e, conditions)} anim {anim} " +
                          $"down {down.Contains(e.UniqueId)} gauge {listed.Contains(e.UniqueId)}");
            }
            parts.Add($"acting {Time.unscaledTime - lastAction < 0.1f} shown {Shown}" + (why != null ? $" ({why})" : "") +
                      $" root {(root != null ? $"{root.activeSelf} [{rootRect.GetSiblingIndex()}]" : "none")} in {Where}");
            return string.Join(" | ", parts);
        }

        static int cameraFrame = -1;
        static Camera[] cameras = Array.Empty<Camera>();

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

        // ---- the gauges, in the battle UI ----

        static readonly Color Back = new Color(0f, 0f, 0f, 0.6f);
        static readonly Color Edge = new Color(0.86f, 0.89f, 0.97f, 0.85f);
        static readonly Color Fill = new Color(0.97f, 0.9f, 0.45f, 1f);

        sealed class Gauge
        {
            internal GameObject Go;
            internal RectTransform Rect, Edge, Fill;
            internal UiImage FillImage;
        }

        static GameObject root;            // full size, just before the menus; holds the gauges
        static RectTransform rootRect;
        static readonly List<Gauge> pool = new List<Gauge>();

        static void Hide()
        {
            if (root != null && root.activeSelf) root.SetActive(false);   // Unity's null: gone with the battle UI
        }

        static void Place(List<(int Id, Rect Rect, float Amount)> list)
        {
            var ui = BattleUIManager.Instance;
            if (list.Count == 0 || ui == null || !Ready(ui)) { Hide(); return; }
            var canvas = ui.canvas != null ? ui.canvas.rootCanvas : null;
            var cam = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            if (!root.activeSelf) root.SetActive(true);
            TakeArt();
            float s = Screen.height / 720f;
            int used = 0;
            foreach (var (_, r, amount) in list)
            {
                // the screen rect (top-left origin) in the root's own space (y up)
                if (!Local(new Vector2(r.xMin, Screen.height - r.yMin), cam, out var topLeft) ||
                    !Local(new Vector2(r.xMax, Screen.height - r.yMax), cam, out var bottomRight)) continue;
                float w = bottomRight.x - topLeft.x, h = topLeft.y - bottomRight.y;
                if (w <= 0f || h <= 0f) continue;
                float o = Mathf.Max(1f, Mathf.Round(s)) * w / r.width;   // the frame: a screen pixel or so
                var g = used < pool.Count ? pool[used] : NewGauge();
                used++;
                g.Rect.anchoredPosition = topLeft;
                g.Rect.sizeDelta = new Vector2(w, h);
                g.Edge.offsetMin = new Vector2(-o, -o);
                g.Edge.offsetMax = new Vector2(o, o);
                g.Fill.anchorMax = new Vector2(amount, 1f);
                if (g.FillImage.enabled != amount > 0f) g.FillImage.enabled = amount > 0f;
                if (!g.Go.activeSelf) g.Go.SetActive(true);
            }
            for (int i = used; i < pool.Count; i++)
                if (pool[i].Go.activeSelf) pool[i].Go.SetActive(false);
            Shown = used;
        }

        static bool Local(Vector2 screen, Camera cam, out Vector2 local) =>
            RectTransformUtility.ScreenPointToLocalPointInRectangle(rootRect, screen, cam, out local);

        // The root: a full-size child of the battle UI, right before its menus (made again with each
        // new battle UI). Checked each frame: the game may reorder its windows.
        static bool Ready(BattleUIManager ui)
        {
            var menuObject = ui.keyMenuParent;
            var menu = menuObject != null ? menuObject.transform : null;
            Transform parent = menu != null ? menu.parent : null;
            if (parent == null && ui.mainCanvasRect != null) parent = ui.mainCanvasRect;
            if (parent == null && ui.canvas != null) parent = ui.canvas.transform;
            if (parent == null) return false;
            if (root == null || rootRect.parent == null || rootRect.parent.Pointer != parent.Pointer)
            {
                if (root != null) UnityEngine.Object.Destroy(root);
                pool.Clear();
                root = new GameObject("BattleOptions Enemy ATB");
                root.layer = parent.gameObject.layer;
                rootRect = root.AddComponent<RectTransform>();
                rootRect.SetParent(parent, false);
                rootRect.anchorMin = Vector2.zero;
                rootRect.anchorMax = Vector2.one;
                rootRect.pivot = new Vector2(0.5f, 0.5f);
                rootRect.offsetMin = rootRect.offsetMax = Vector2.zero;
                root.AddComponent<UnityEngine.UI.LayoutElement>().ignoreLayout = true;   // a layout group there leaves it be
                placed = false;
            }
            bool beside = menu != null && menu.parent != null && menu.parent.Pointer == parent.Pointer;
            if (beside)
            {
                int at = menu.GetSiblingIndex(), mine = rootRect.GetSiblingIndex();
                if (mine != at - 1) rootRect.SetSiblingIndex(mine < at ? at - 1 : at);
            }
            else if (rootRect.GetSiblingIndex() != 0) rootRect.SetAsFirstSibling();
            if (!placed)
            {
                placed = true;
                var c = ui.canvas != null ? ui.canvas.rootCanvas : null;
                Where = $"{Path(parent)} [{rootRect.GetSiblingIndex()} of {parent.childCount}], " +
                        (beside ? $"before {menu.name} [{menu.GetSiblingIndex()}]" : "first (no menu parent found)") +
                        $", canvas {(c != null ? c.renderMode.ToString() : "none")}";
                Kit.Info($"[enemy atb] gauges in the battle UI: {Where}");
            }
            return true;
        }

        static bool placed;

        static string Path(Transform t)
        {
            var names = new List<string>();
            for (int i = 0; t != null && i < 8; i++, t = t.parent) names.Add(t.name);
            names.Reverse();
            return string.Join("/", names);
        }

        static Gauge NewGauge()
        {
            var go = new GameObject("Enemy gauge");
            go.layer = root.layer;
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(rootRect, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);   // the root's pivot: its local space
            rt.pivot = new Vector2(0f, 1f);                          // placed by its top left
            var g = new Gauge { Go = go, Rect = rt };
            g.Edge = Part(rt, "Edge", Edge, out _);
            Part(rt, "Back", Back, out _);
            g.Fill = Part(rt, "Fill", Fill, out g.FillImage);
            g.Fill.anchorMax = new Vector2(0f, 1f);
            Paint(g.FillImage);
            pool.Add(g);
            return g;
        }

        static RectTransform Part(RectTransform parent, string name, Color color, out UiImage image)
        {
            var go = new GameObject(name);
            go.layer = parent.gameObject.layer;
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            image = go.AddComponent<UiImage>();
            image.color = color;
            image.raycastTarget = false;
            return rt;
        }

        // The fill's look: the first character gauge sprite seen (and its colour), kept. Its sprite
        // can change (a full gauge's), and the enemies' fill shouldn't flicker with it.
        static Sprite art;
        static Color artColor = Fill;

        static void TakeArt()
        {
            if (art != null) return;
            var img = CastBars.GaugeImage;
            var sprite = img != null ? img.sprite : null;
            if (sprite == null) return;
            art = sprite;
            artColor = img.color;
            Art = $"{sprite.name} {sprite.rect.width:0}x{sprite.rect.height:0}";
            foreach (var g in pool) Paint(g.FillImage);
            Kit.Info($"[enemy atb] gauge fill: {Art}");
        }

        static void Paint(UiImage image)
        {
            image.sprite = art;   // null: the plain fill
            image.type = UiImage.Type.Simple;
            image.preserveAspect = false;
            image.color = art != null ? artColor : Fill;
        }
    }
}
