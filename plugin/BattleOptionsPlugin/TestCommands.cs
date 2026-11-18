using System;
using System.Collections.Generic;
using Last.Battle;
using Last.Management;
using PrKit;

namespace BattleOptions
{
    // Harness commands for a headless test run (tools/battle_options_test.py; never saves):
    //   bo                     the settings, the battle mode, the queue, enemy gauges, counters
    //   bo set <pause|cast|enemy> <on|off>   as the Config menu would (not written to the file)
    //   bo menu                the Config menu's battle rows and ours, if it's open
    //   bo open                open the Config screen from the title (as its Config entry does)
    //   bo layout              where each enemy's gauge goes (a no-graphics run may never paint)
    static class TestCommands
    {
        internal static void Register() =>
            Harness.Register("bo", Run, "bo [set <pause|cast|enemy> <on|off> | menu | open | layout]: Battle Options' state");

        static string Run(string[] args)
        {
            try
            {
                if (args.Length >= 1 && args[0] == "menu") return ConfigMenu.Describe();
                if (args.Length >= 1 && args[0] == "open")
                {
                    var title = UnityEngine.Object.FindObjectOfType<Last.UI.KeyInput.TitleWindowController>();
                    if (title == null) return "no title screen";
                    title.SetCommand(Last.Defaine.TitleCommandId.Config, Last.UI.KeyInput.TitleWindowController.State.Select, false, true);
                    return "Config opened";
                }
                if (args.Length >= 1 && args[0] == "cfg")
                {
                    // the in-game menu's Config (the title's has only system settings)
                    Last.UI.KeyInput.MainMenuController menu = null;
                    foreach (var m in UnityEngine.Resources.FindObjectsOfTypeAll<Last.UI.KeyInput.MainMenuController>())
                        if (m != null && m.gameObject.scene.IsValid()) { menu = m; break; }   // hidden until opened
                    if (menu == null) return "no main menu";
                    menu.ShowBySpecifiedCommandId(Last.Defaine.MenuCommandId.Config, null);
                    return "main menu Config opened";
                }
                if (args.Length >= 1 && args[0] == "layout")
                {
                    var rects = EnemyAtb.Layout(out var why);
                    var parts = new List<string>();
                    foreach (var (id, r, amount) in rects) parts.Add($"enemy {id} at {r.x:0},{r.y:0} {r.width:0}x{r.height:0} {amount:P0}");
                    return $"screen {UnityEngine.Screen.width}x{UnityEngine.Screen.height}: " + (parts.Count > 0 ? string.Join("; ", parts) : why);
                }
                if (args.Length >= 3 && args[0] == "set")
                {
                    bool on = args[2] == "on";
                    if (args[1] == "pause") { if (on) ConfigMenu.KeepWait(); Settings.Set(Settings.Pause, on); }
                    else if (args[1] == "cast") Settings.Set(Settings.CastBars, on);
                    else if (args[1] == "enemy") Settings.Set(Settings.EnemyAtb, on);
                    else return "set what? pause, cast or enemy";
                }
                return State();
            }
            catch (Exception e) { return "error: " + e; }
        }

        static string State()
        {
            var sys = SystemConfig.Instance();
            var cfg = UserDataManager.Instance()?.Config;
            var parts = new List<string>
            {
                $"pause {Settings.Pause.Value} cast {Settings.CastBars.Value} enemy {Settings.EnemyAtb.Value}",
                $"atb type {sys?.ATBBattleType} mode index {cfg?.BattleModeIndex}",
                $"waiting {PauseAtb.Waiting()} turn up {PauseAtb.TurnUp()} frozen {PauseAtb.Frozen}",
                $"cast drawn {CastBars.Drawn} full {CastBars.FullValue} gauge art {(CastBars.GaugeImage?.sprite != null ? CastBars.GaugeImage.sprite.name : "none")}",
                $"enemy gauges shown {EnemyAtb.Shown}",
            };
            var bm = BattlePlugManager.Instance();
            var progress = bm?.BattleProgress?.TryCast<BattleProgressATB>();
            if (bm != null && bm.IsBattle() && progress?.gaugeStatusDictionary != null)
            {
                var g = progress.gaugeStatusDictionary;
                var units = new List<string>();
                foreach (var u in bm.GetEnemyUnits())
                    if (u != null && g.ContainsKey(u)) units.Add($"enemy {u.UniqueId} {g[u]:0.0}");
                foreach (var u in bm.GetPlayerUnits())
                    if (u != null && g.ContainsKey(u)) units.Add($"{u.GetOwnedCharacterData()?.Name} {g[u]:0.0} wait {CastBars.WaitLeft(u):0.00}");
                parts.Add($"gauges (max {BattleProgressATB.MaxATBGauge}): {string.Join(", ", units)}");
            }
            return string.Join(" | ", parts);
        }
    }
}
