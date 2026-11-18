using System;
using System.Collections.Generic;
using HarmonyLib;
using Last.Management;
using Last.UI;
using PrKit;
using UnityEngine;
using Menu = Last.UI.KeyInput.ConfigActualDetailsControllerBase;
using Row = Last.UI.KeyInput.ConfigCommandController;

namespace BattleOptions
{
    // The game's Config menu (keyboard/gamepad UI, Last.UI.KeyInput; decompiled 2026-11-18,
    // tools/decomp/battle_options): its rows are ConfigCommandControllers in the menu's commandList,
    // laid out in the prefab; each row's ConfigCommandsData says which setting it is (ConfigCommandType).
    // InitializeCommandList sets every row up (SetCommandUseType: its name and kind; SetCommandNameList:
    // its choices and the current one, targetIndex). Left/Right: SwitchArrowSelectTypeProcess steps
    // targetIndex through the choices (wrapping) and the game's own class (FF6: Serial.FF6.UI.KeyInput.
    // ConfigActualDetailsController) applies it. A row's help line: SwitchDescriptionText.
    //  * Battle Mode (type 1): the game's choices Wait (0) and Active (1) are Config.BattleModeIndex and
    //    SystemConfig.ATBBattleType (Wait 0, Active 1). Pause is a third choice (2): the game keeps Wait
    //    (index 0, Wait) and Settings.Pause says the rest.
    //  * Cast Bars, Enemy ATB: two rows of our own after the battle rows, copies of an On/Off row
    //    (Memory cursor position or Continued auto mode) with types of our own; for a type it doesn't
    //    know, every one of the game's switches does nothing (decompiled), so the hooks below do it all.
    class ConfigMenu : Feature
    {
        internal const int BattleMode = 1, BattleMessage = 3;
        internal const int CastBarsType = 7101, EnemyAtbType = 7102;   // ours: not one of the game's
        internal const int Wait = 0, Active = 1, Pause = 2;            // Battle Mode's choices
        internal static int RowsAdded, MenusSeen;

        public override void Apply(Harmony h)
        {
            var game = Patching.GameType("Serial.FF{0}.UI.KeyInput.ConfigActualDetailsController", typeof(Menu));
            int n = 0;
            if (Patching.Patch(h, AccessTools.Method(typeof(Menu), "InitializeCommandList"), typeof(ConfigMenu), prefix: nameof(InitList_Prefix))) n++;
            if (Patching.Patch(h, Patching.Find(game, "SetCommandUseType", 1), typeof(ConfigMenu), postfix: nameof(UseType_Postfix))) n++;
            if (Patching.Patch(h, Patching.Find(game, "SetCommandNameList", 1), typeof(ConfigMenu), postfix: nameof(Names_Postfix))) n++;
            if (Patching.Patch(h, Patching.Find(game, "SwitchArrowSelectTypeProcess", 2), typeof(ConfigMenu), postfix: nameof(Arrow_Postfix))) n++;
            if (Patching.Patch(h, Patching.Find(game, "SetDescriptionText", 2), typeof(ConfigMenu), postfix: nameof(ModeText_Postfix))) n++;
            if (Patching.Patch(h, Patching.Find(game, "SwitchDescriptionText", 2), typeof(ConfigMenu), postfix: nameof(Describe_Postfix))) n++;
            Kit.Info($"[config] {n} of 6 Config menu hooks on {game.FullName}");
        }

        internal static int TypeOf(Row r)
        {
            try { var d = r == null ? null : r.commandsData; return d == null ? 0 : (int)d.configCommandType; }
            catch (Exception) { return 0; }
        }

        static bool Ours(int type) => type == CastBarsType || type == EnemyAtbType;

        // --- the rows -----------------------------------------------------------------------------

        public static void InitList_Prefix(Menu __instance)
        {
            if (!Hooks.IsA<Menu>(__instance)) return;
            try { AddRows(__instance); }
            catch (Exception e) { Hooks.WarnOnce("config.rows", e); }
        }

        static void AddRows(Menu menu)
        {
            var list = menu.commandList;
            if (list == null || list.Count == 0) return;
            MenusSeen++;
            Row anchor = null, source = null;
            int anchorAt = -1;
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                int t = TypeOf(r);
                if (Ours(t)) return;                                         // this menu has them already
                if (t >= BattleMode && t <= BattleMessage) { anchor = r; anchorAt = i; }   // after the last battle row
                if ((t == 5 || t == 4) && source == null) source = r;      // an On/Off row to copy
            }
            if (anchor == null)
            {
                Hooks.NoteOnce("config-no-battle-rows", () => "[config] a Config menu without battle rows: no rows added");
                return;
            }
            if (source == null) source = anchor;
            var parent = anchor.transform.parent;
            int sibling = anchor.transform.GetSiblingIndex(), at = anchorAt;
            var rows = new List<(int, string)>();
            if (CastBars.Offered) rows.Add((CastBarsType, "CastBars"));
            rows.Add((EnemyAtbType, "EnemyAtb"));
            foreach (var (type, key) in rows)
            {
                var go = UnityEngine.Object.Instantiate(source.gameObject, parent);
                go.name = "BattleOptions." + key;
                go.transform.SetSiblingIndex(++sibling);
                var row = go.GetComponent<Row>();
                var data = row == null ? null : row.commandsData;
                if (data == null || !data.transform.IsChildOf(go.transform))
                {
                    // its setting data isn't the copy's own: changing it would change the original row
                    UnityEngine.Object.Destroy(go);
                    Hooks.NoteOnce("config-shared-data", () => "[config] a row's setting data isn't part of the row: no rows added");
                    return;
                }
                data.configCommandType = (ConfigCommandType)type;
                list.Insert(++at, row);
                RowsAdded++;
            }
            var src = TypeOf(source);
            Hooks.NoteOnce("config-rows", () => $"[config] {(CastBars.Offered ? "Cast Bars and " : "")}Enemy ATB rows added after row {anchorAt} (a copy of a type {src} row) under '{parent?.name}'");
        }

        // a row of ours: its name, and the game's left/right choice look
        public static void UseType_Postfix(Menu __instance, Row __0)
        {
            try
            {
                int t = TypeOf(__0);
                if (!Ours(t)) return;
                __instance.SetCommandNameList(__0);          // the game's (which clears it), then Names_Postfix
                __0.SetNameText(t == CastBarsType ? Words.CastBarsName : Words.EnemyAtbName);
                __0.SetEnableArrowSelectType(true);
            }
            catch (Exception e) { Hooks.WarnOnce("config.usetype", e); }
        }

        // the choices and the current one
        public static void Names_Postfix(Menu __instance, Row __0)
        {
            try
            {
                int t = TypeOf(__0);
                if (t == BattleMode) ModeChoices(__instance, __0);
                else if (t == CastBarsType) OnOff(__0, Settings.CastBars.Value);
                else if (t == EnemyAtbType) OnOff(__0, Settings.EnemyAtb.Value);
            }
            catch (Exception e) { Hooks.WarnOnce("config.names", e); }
        }

        static void OnOff(Row r, bool on)
        {
            var names = r.NameList;
            if (names == null) return;
            names.Clear();
            names.Add(Words.Off);   // the game's order for On/Off rows: Off 0, On 1
            names.Add(Words.On);
            r.targetIndex = on ? 1 : 0;
            r.SetArrowChangeText(names[r.targetIndex]);
        }

        static void ModeChoices(Menu menu, Row r)
        {
            var names = r.NameList;
            if (names == null) return;
            if (names.Count == 2) names.Add(Words.PauseName);
            if (!Settings.Pause.Value) return;
            if (r.targetIndex == Wait)
            {
                r.targetIndex = Pause;
                r.SetArrowChangeText(Words.PauseName);
                menu.SetDescriptionText((ConfigCommandType)BattleMode, Pause);
            }
            else if (r.targetIndex == Active) Settings.Set(Settings.Pause, false);   // Active was chosen another way
        }

        // Left/Right on a row: the game has stepped the choice and applied its own
        public static void Arrow_Postfix(Menu __instance, Row __0)
        {
            try
            {
                int t = TypeOf(__0);
                if (t == BattleMode)
                {
                    bool pause = __0.targetIndex == Pause;
                    if (pause) KeepWait();
                    Settings.Set(Settings.Pause, pause);
                }
                else if (t == CastBarsType) Settings.Set(Settings.CastBars, __0.targetIndex == 1);
                else if (t == EnemyAtbType) Settings.Set(Settings.EnemyAtb, __0.targetIndex == 1);
            }
            catch (Exception e) { Hooks.WarnOnce("config.arrow", e); }
        }

        // Pause is Wait in the game's own config (saved as Wait, and Wait's menus stop time)
        internal static void KeepWait()
        {
            ConfigClient.Instance()?.SetBattleMode(Wait);
            var sys = SystemConfig.Instance();
            if (sys != null) sys._ATBBattleType_k__BackingField = ATBBattleType.Wait;
        }

        // --- help lines ---------------------------------------------------------------------------

        static string PauseText => Words.ModeHeader + Environment.NewLine + Words.PauseHelp;

        // Battle Mode's help for a choice (the game's has Wait's and Active's)
        public static void ModeText_Postfix(Menu __instance, ConfigCommandType __0, int __1, ref string __result)
        {
            try
            {
                if ((int)__0 != BattleMode || __1 != Pause) return;
                __result = PauseText;
                __instance.SetDescriptionText(__result);
            }
            catch (Exception e) { Hooks.WarnOnce("config.modetext", e); }
        }

        // the help line of the row under the cursor
        public static void Describe_Postfix(Menu __instance, ConfigCommandType __0, ref string __result)
        {
            try
            {
                int t = (int)__0;
                if (t == CastBarsType) __result = Words.CastBarsHelp;
                else if (t == EnemyAtbType) __result = Words.EnemyAtbHelp;
                else if (t == BattleMode && ModeRow(__instance)?.targetIndex == Pause) __result = PauseText;
            }
            catch (Exception e) { Hooks.WarnOnce("config.describe", e); }
        }

        static Row ModeRow(Menu menu)
        {
            var list = menu.commandList;
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
                if (TypeOf(list[i]) == BattleMode) return list[i];
            return null;
        }

        public override string Check() => "";

        // --- test (TestCommands: "bo menu") --------------------------------------------------------

        internal static string Describe()
        {
            // every Config page, hidden ones too (the screen opens on its page list)
            var menus = Resources.FindObjectsOfTypeAll<Menu>();
            if (menus == null || menus.Length == 0) return $"no Config menu (menus set up {MenusSeen}, rows added {RowsAdded})";
            var lines = new List<string>();
            foreach (var m in menus)
            {
                if (m == null || !m.gameObject.scene.IsValid()) continue;   // prefabs, not in a scene
                var list = m.commandList;
                var types = new List<string>();
                if (list != null) for (int i = 0; i < list.Count; i++) types.Add(TypeOf(list[i]).ToString());
                lines.Add($"menu '{m.name}' active {m.gameObject.activeInHierarchy}: {list?.Count ?? 0} rows, types [{string.Join(" ", types)}], " +
                          $"selected {TypeOf(m.SelectedCommand)}");
                if (list == null || !types.Contains(BattleMode.ToString())) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    var r = list[i];
                    int t = TypeOf(r);
                    if (t > BattleMessage + 2 && !Ours(t)) continue;   // the battle rows, their neighbours, ours
                    var rt = r.GetComponent<RectTransform>();
                    var names = r.NameList;
                    lines.Add($"  [{i}] type {t} '{r.NameText?.text}' = '{r.GetArrowChangeText()?.text}' index {r.targetIndex} of " +
                              $"{names?.Count ?? 0} ({Join(names)}) parent '{r.transform.parent?.name}' " +
                              $"sibling {r.transform.GetSiblingIndex()} active {r.gameObject.activeSelf} size {rt?.rect.width:0}x{rt?.rect.height:0} y {rt?.anchoredPosition.y:0}");
                }
                var p = list.Count > 0 ? list[0].transform.parent : null;
                if (p != null)
                {
                    var comps = new List<string>();
                    foreach (var c in p.GetComponents<Component>()) comps.Add(c.GetIl2CppType().Name);
                    var prt = p.GetComponent<RectTransform>();
                    lines.Add($"  parent '{p.name}' [{string.Join(", ", comps)}] size {prt?.rect.width:0}x{prt?.rect.height:0} children {p.childCount}");
                }
                var d = m.descriptionText;
                lines.Add($"  help: '{d?.text?.Replace("\n", " | ").Replace("\r", "")}'");
            }
            return string.Join("\n", lines);
        }

        static string Join(Il2CppSystem.Collections.Generic.List<string> names)
        {
            if (names == null) return "";
            var parts = new List<string>();
            for (int i = 0; i < names.Count; i++) parts.Add(names[i]);
            return string.Join("/", parts);
        }
    }
}
