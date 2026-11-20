using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Last.UI;
using Menu = Last.UI.KeyInput.ConfigActualDetailsControllerBase;
using Row = Last.UI.KeyInput.ConfigCommandController;

namespace PrKit
{
    // An On/Off row of a mod's own in the game's Config menu (the keyboard/gamepad Config screen).
    public sealed class ConfigRow
    {
        public int Type;                 // a command type of the mod's own (not one of the game's: 7000 and up)
        public string Key = "", Name = "", Help = "";
        public Func<bool> Get = () => false;
        public Action<bool> Set = _ => { };
        public Func<bool> Offered = () => true;
    }

    // A mod's On/Off rows in the game's Config menu: a copy of one of the game's On/Off rows with a command
    // type of the mod's own, after the battle rows (and after rows other mods put there). The way Battle
    // Options adds its Cast Bars and Enemy ATB rows (BattleOptionsPlugin/ConfigMenu.cs, decompiled
    // 2026-11-18, tools/decomp/battle_options): the menu's rows are ConfigCommandControllers in its
    // commandList; InitializeCommandList sets each up (SetCommandUseType: its name and kind;
    // SetCommandNameList: its choices and the current one, targetIndex); Left/Right
    // (SwitchArrowSelectTypeProcess) steps targetIndex and the game's own class applies a type it knows
    // (one it doesn't: nothing, so these hooks do it all); SwitchDescriptionText is the help line.
    // First used for Brave New World's Encounters On/Off (madsiur's option for BNW's config menu).
    public static class ConfigRows
    {
        const int BattleMode = 1, BattleMessage = 3, OwnTypes = 7000;
        static readonly List<ConfigRow> rows = new List<ConfigRow>();
        static bool patched;
        public static int RowsAdded;

        public static void Add(Harmony h, ConfigRow row)
        {
            rows.Add(row);
            if (patched) return;
            patched = true;
            int serial = Game.Current == PrGame.FF4 ? 0 : (int)Game.Current;   // FF4's own class is Serial.FF0's
            var game = Hooks.TypeByName($"Serial.FF{serial}.UI.KeyInput.ConfigActualDetailsController") ?? typeof(Menu);
            int n = 0;
            if (Patch(h, AccessTools.Method(typeof(Menu), "InitializeCommandList"), prefix: nameof(InitList_Prefix))) n++;
            if (Patch(h, Find(game, "SetCommandUseType", 1), postfix: nameof(UseType_Postfix))) n++;
            if (Patch(h, Find(game, "SetCommandNameList", 1), postfix: nameof(Names_Postfix))) n++;
            if (Patch(h, Find(game, "SwitchArrowSelectTypeProcess", 2), postfix: nameof(Arrow_Postfix))) n++;
            if (Patch(h, Find(game, "SwitchDescriptionText", 2), postfix: nameof(Describe_Postfix))) n++;
            Kit.Info($"[config] {n} of 5 Config menu hooks on {game.FullName} for {Kit.ModName}'s rows");
        }

        static bool Patch(Harmony h, MethodBase m, string prefix = null, string postfix = null)
        {
            if (m == null) return false;
            return Hooks.Patch(h, m, typeof(ConfigRows), prefix, postfix);
        }

        static MethodInfo Find(Type type, string name, int args)
        {
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
                foreach (var m in AccessTools.GetDeclaredMethods(t))
                    if (m.Name == name && m.GetParameters().Length == args) return m;
            return null;
        }

        static int TypeOf(Row r)
        {
            try { var d = r == null ? null : r.commandsData; return d == null ? 0 : (int)d.configCommandType; }
            catch (Exception) { return 0; }
        }

        static ConfigRow Ours(int type)
        {
            foreach (var r in rows) if (r.Type == type) return r;
            return null;
        }

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
            Row anchor = null, source = null;
            int anchorAt = -1;
            var have = new HashSet<int>();
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                int t = TypeOf(r);
                have.Add(t);
                // after the last battle row, and after other mods' rows right behind it
                if ((t >= BattleMode && t <= BattleMessage) || (t >= OwnTypes && anchorAt == i - 1 && anchor != null)) { anchor = r; anchorAt = i; }
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
            var added = new List<string>();
            foreach (var cr in rows)
            {
                if (have.Contains(cr.Type) || !cr.Offered()) continue;   // this menu has it already / not offered
                var go = UnityEngine.Object.Instantiate(source.gameObject, parent);
                go.name = Kit.ModName + "." + cr.Key;
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
                data.configCommandType = (ConfigCommandType)cr.Type;
                list.Insert(++at, row);
                RowsAdded++;
                added.Add(cr.Key);
            }
            if (added.Count > 0)
            {
                int src = TypeOf(source), after = anchorAt;
                Hooks.NoteOnce("config-rows-" + Kit.ModName, () => $"[config] {string.Join(", ", added)} added after row {after} (a copy of a type {src} row) under '{parent?.name}'");
            }
        }

        public static void UseType_Postfix(Menu __instance, Row __0)
        {
            try
            {
                var cr = Ours(TypeOf(__0));
                if (cr == null) return;
                __instance.SetCommandNameList(__0);          // the game's (which clears it), then Names_Postfix
                __0.SetNameText(cr.Name);
                __0.SetEnableArrowSelectType(true);
            }
            catch (Exception e) { Hooks.WarnOnce("config.usetype", e); }
        }

        public static void Names_Postfix(Menu __instance, Row __0)
        {
            try
            {
                var cr = Ours(TypeOf(__0));
                var names = cr == null ? null : __0.NameList;
                if (names == null) return;
                names.Clear();
                names.Add(Msg("MENU_CONFIG_ITEMS_DETAIL_OPTION_2", "Off"));   // the game's order for On/Off rows: Off 0, On 1
                names.Add(Msg("MENU_CONFIG_ITEMS_DETAIL_OPTION_1", "On"));
                __0.targetIndex = cr.Get() ? 1 : 0;
                __0.SetArrowChangeText(names[__0.targetIndex]);
            }
            catch (Exception e) { Hooks.WarnOnce("config.names", e); }
        }

        public static void Arrow_Postfix(Menu __instance, Row __0)
        {
            try
            {
                var cr = Ours(TypeOf(__0));
                if (cr != null) cr.Set(__0.targetIndex == 1);
            }
            catch (Exception e) { Hooks.WarnOnce("config.arrow", e); }
        }

        public static void Describe_Postfix(Menu __instance, ConfigCommandType __0, ref string __result)
        {
            try
            {
                var cr = Ours((int)__0);
                if (cr != null) __result = cr.Help;
            }
            catch (Exception e) { Hooks.WarnOnce("config.describe", e); }
        }

        static string Msg(string key, string fallback)
        {
            try
            {
                var s = Last.Management.MessageManager.Instance?.GetMessageByMessageConclusion(key);
                return string.IsNullOrEmpty(s) ? fallback : s;
            }
            catch (Exception) { return fallback; }
        }
    }
}
