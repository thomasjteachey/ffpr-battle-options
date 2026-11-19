using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Last.Management;
using PrKit;

namespace BattleOptions
{
    // The three choices, kept in BepInEx/config/prmod.battleoptions.cfg [Settings] and changed in
    // the game's Config menu (ConfigMenu.cs). A test run never writes the file.
    static class Settings
    {
        internal static ConfigFile File;
        internal static ConfigEntry<bool> Pause, CastBars, EnemyAtb;

        internal static void Bind(ConfigFile cfg)
        {
            File = cfg;
            Pause = cfg.Bind("Settings", "PauseMode", false,
                "Battle Mode: Pause. As Wait, and every ATB gauge, cast time and status timer (Regen, Poison, Stop...) also stops while a command is being chosen or an action plays out. " +
                "Chosen in the game's Config menu (Battle Mode: Wait / Active / Pause).");
            CastBars = cfg.Bind("Settings", "CastBars", true,
                "A character's ATB gauge drains through the wait between a chosen command and its action. Config menu: Cast Bars.");
            EnemyAtb = cfg.Bind("Settings", "EnemyAtb", true,
                "Each enemy's ATB gauge is shown under it. Config menu: Enemy ATB.");
        }

        internal static void Set(ConfigEntry<bool> entry, bool value)
        {
            if (entry == null || entry.Value == value) return;
            if (TestRun.Running) File.SaveOnConfigSet = false;   // a test run writes nothing of the player's
            entry.Value = value;
            Kit.Info($"[settings] {entry.Definition.Key} = {value}{(TestRun.Running ? " (test run: not saved)" : "")}");
        }
    }

    // The rows' words. The game's own On/Off and Battle Mode texts come from its message tables (every
    // language); the mod's own are English.
    static class Words
    {
        internal const string PauseName = "Pause";
        internal const string CastBarsName = "Cast Bars";
        internal const string EnemyAtbName = "Enemy ATB";
        internal const string PauseHelp = "Time stops while you choose a command and while actions play out: ATB gauges, cast times and status effects all wait.";
        internal const string CastBarsHelp = "Show the wait between a chosen command and its action on the character's ATB gauge.";
        internal const string EnemyAtbHelp = "Show each enemy's ATB gauge under it.";

        internal static string On => Msg("MENU_CONFIG_ITEMS_DETAIL_OPTION_1", "On");
        internal static string Off => Msg("MENU_CONFIG_ITEMS_DETAIL_OPTION_2", "Off");
        internal static string ModeHeader => Msg("MENU_CONFIG_ITEMS_DESCRIPTION_26", "Set how time flows in battle.");

        internal static string Msg(string key, string fallback)
        {
            try
            {
                var text = MessageManager.Instance?.GetMessageByMessageConclusion(key);
                return string.IsNullOrEmpty(text) ? fallback : text;
            }
            catch (Exception) { return fallback; }
        }
    }

    // Harmony with a priority: these hooks share game methods with other mods (Worlds Collide's and
    // Brave New World's ATB rates and wait times), and must see those mods' results (Last) or the
    // game's own value before them (First). Same shared-code refusal as Hooks.Patch.
    static class Patching
    {
        internal static bool Patch(Harmony h, MethodBase original, Type hooks, string prefix = null, string postfix = null, int priority = Priority.Normal)
        {
            var where = $"{original?.DeclaringType?.Name}.{original?.Name}";
            try
            {
                if (original == null) return false;
                var tiny = Hooks.TinyNativeCode(original);
                if (tiny != null)
                {
                    Kit.Warn($"[patch] {where} not patched: its native code is {tiny}, which other methods share");
                    return false;
                }
                h.Patch(original,
                    prefix: prefix == null ? null : new HarmonyMethod(hooks, prefix) { priority = priority },
                    postfix: postfix == null ? null : new HarmonyMethod(hooks, postfix) { priority = priority });
                return true;
            }
            catch (Exception e)
            {
                Kit.Warn($"[patch] {where}: {e.Message}");
                return false;
            }
        }

        // A method by name and parameter count, declared on the type or the nearest base that has it.
        internal static MethodInfo Find(Type type, string name, int args)
        {
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
                foreach (var m in AccessTools.GetDeclaredMethods(t))
                    if (m.Name == name && m.GetParameters().Length == args) return m;
            return null;
        }

        // The game's own class of a kind, by name (FF6: Serial.FF6...; FF4/FF5 theirs), else the shared base.
        internal static Type GameType(string format, Type fallback)
        {
            var t = Hooks.TypeByName(string.Format(format, (int)Game.Current));   // not AccessTools': see Hooks.TypeByName
            return t ?? fallback;
        }
    }
}
