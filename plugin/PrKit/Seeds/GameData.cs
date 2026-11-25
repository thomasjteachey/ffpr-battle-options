using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PrKit.Seeds
{
    // A mod's copies of the game's own files (its generator's vanilla folder and cache, the baseline, the
    // Magicite folder) are made on the player's machine from their own game: a distributed mod carries none
    // of them (tools/gamedata.py, the mod's <mod>_gamedata.py; MODDING.md "Game files made on the player's
    // machine"). generator/gamedata.json lists them; the setup writes generator/vanilla/.complete (that
    // manifest's id) last. Call EnsureSetUp from the plugin's Load before anything reads them: a mod plugin
    // that loads before Magicite (BepInEx orders independent plugins by GUID) gets its Magicite folder made on
    // the same start, no restart. Worlds Collide's WcGameData (2026-11-12), in the kit since 2026-11-20 for
    // Beyond Chaos (WC keeps its own copy).
    public sealed class GameData
    {
        public string Tag = "[mod]";               // log prefix
        public string Title = "the mod";           // in the log: "making <Title>'s copies of the game's files"
        public string Script = "gamedata.py";      // <mod>_gamedata.py, in generator/tools
        public string MagiciteFolder = "";         // the mod's Magicite folder name
        public ModDataDir Data;                    // the mod's data folder (its generator/)
        public SeedGenerator Generator;            // whose Python runs the setup (bundled, config, project)
        public Action<string> Info = _ => { }, Fail = _ => { };
        public int TimeoutMs = 15 * 60 * 1000;
        public string Error;                       // the last setup's failure, for the mod's screen

        public bool Done
        {
            get
            {
                var gen = Data.Generator;
                var manifest = Path.Combine(gen, "gamedata.json");
                if (!File.Exists(manifest)) return true;       // a build without a manifest carries the files
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                    var id = doc.RootElement.GetProperty("id").GetString() ?? "";
                    var mark = Path.Combine(gen, "vanilla", ".complete");
                    return File.Exists(mark) && File.ReadAllText(mark).Trim() == id;
                }
                catch { return false; }
            }
        }

        public void EnsureSetUp()
        {
            var gen = Data.Generator;
            var manifest = Path.Combine(gen, "gamedata.json");
            if (!File.Exists(manifest)) return;
            try
            {
                using var _ = JsonDocument.Parse(File.ReadAllText(manifest));
            }
            catch (Exception e)
            {
                Error = $"{manifest}: {e.Message}";
                Fail($"{Tag} game data: {Error}");
                return;
            }
            if (Done) return;
            var (py, _, err) = Generator.Resolve();
            var script = Path.Combine(gen, "tools", Script);
            if (err != null || !File.Exists(script))
            {
                Error = err ?? $"no {script}";
                Fail($"{Tag} game data can't be made: {Error}");
                return;
            }
            Info($"{Tag} game data: making {Title}'s copies of the game's files from this game (once, about ten seconds)");
            var psi = new ProcessStartInfo
            {
                FileName = py,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(script),
            };
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("setup");
            psi.ArgumentList.Add("--game");
            psi.ArgumentList.Add(BepInEx.Paths.GameRootPath);
            psi.ArgumentList.Add("--magicite");
            psi.ArgumentList.Add(MagiciteDir.FolderPath(MagiciteFolder));
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONNOUSERSITE"] = "1";
            psi.Environment.Remove("PYTHONHOME");
            psi.Environment.Remove("PYTHONPATH");
            var output = new StringBuilder();
            var sw = Stopwatch.StartNew();
            try
            {
                using var p = new Process { StartInfo = psi };
                p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(TimeoutMs))
                {
                    try { p.Kill(true); } catch { }
                    Error = $"the setup took over {TimeoutMs / 60000} minutes";
                }
                else
                {
                    p.WaitForExit();    // the output's last lines
                    if (p.ExitCode != 0) Error = $"the setup failed (exit {p.ExitCode})";
                }
            }
            catch (Exception e)
            {
                Error = "couldn't start the setup: " + e.Message;
            }
            string text;
            lock (output) text = output.ToString().TrimEnd();
            if (Error == null && Done)
            {
                Info($"{Tag} game data made in {sw.Elapsed.TotalSeconds:0} s: {Last(text)}");
                return;
            }
            Error ??= "the setup didn't finish";
            Fail($"{Tag} game data: {Error}; seeds stay off until it works (it runs again next start):\n{Tail(text, 1500)}");
        }

        static string Last(string text)
        {
            var i = text.LastIndexOf('\n');
            return i < 0 ? text : text.Substring(i + 1);
        }

        static string Tail(string text, int n) => text.Length <= n ? text : text.Substring(text.Length - n);
    }
}
