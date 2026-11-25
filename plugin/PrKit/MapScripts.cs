using System;
using Last.Interpreter;
using Last.Management;
using Object = UnityEngine.Object;

namespace PrKit
{
    // The field's event scripts from a plugin (any PR): what runs now, and starting one of the current
    // map's scripts. A map runs only the scripts its map group's package.json lists (tools/pr_script.py
    // list_in_map adds a mod's own).
    public static class MapScripts
    {
        // the script the interpreter runs or last ran (a script's name stays after it ends)
        public static string Current
        {
            get { try { return Integrator.Instance?.scriptName; } catch (Exception) { return null; } }
        }

        // a script runs, or one waits to (reserved for the next map)
        public static bool Busy
        {
            get
            {
                try
                {
                    var i = Integrator.Instance;
                    return i != null && (i.IsMainCoreAlive() || i.ExistsReserveScript());
                }
                catch (Exception) { return true; }
            }
        }

        public static int MapId => UserDataManager.Instance()?.CurrentMapId ?? -1;

        // Starts one of the current map's scripts as a talk event does. With the battle events' callbacks
        // (a no-op set) the interpreter clears its busy state when the script ends, so the game's own scripts
        // that follow aren't refused ("Interpreter is busy"). cancelReserved: drop a script reserved for the
        // next map first (the harness does; the game's own flow keeps it). Returns null or why it didn't start.
        public static string Start(string name, bool cancelReserved = false)
        {
            var ep = Object.FindObjectOfType<MainGame>()?.eventProcedure;
            if (ep == null) return "no event procedure (not on the field?)";
            var integ = Integrator.Instance;
            if (cancelReserved && integ != null && integ.ExistsReserveScript()) integ.CancelReservedScript();
            ICallbacks cb = null;
            try { cb = new Last.Battle.EventCallBacks().Cast<ICallbacks>(); }
            catch (Exception) { }
            return ep.EventRunScript(name, cb) ? null : $"{name} didn't start (not in map {MapId}'s list, or a script runs)";
        }
    }
}
