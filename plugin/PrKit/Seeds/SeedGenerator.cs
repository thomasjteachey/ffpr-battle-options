using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace PrKit.Seeds
{
    // Runs a mod's seed generator (a Python script: the randomizer, then the shared porter) in a
    // background process with no window, and reports its "[tag] ..." progress lines. The game keeps
    // running; the mod's seed library applies the finished seed on the main thread.
    //  Python: config [Generator] Python, else the bundled generator/python/python.exe in the mod's
    //  data folder (its bundle tool), else the project's pr/.venv (a development PC: ProjectDir).
    // Worlds Collide's WcGenerator (2026-11-05), in the kit since 2026-11-20 (Beyond Chaos uses it too).
    public sealed class SeedGenerator
    {
        public string Tag = "seed";                 // progress lines start "[tag] "
        public string Script = "seed.py";           // the generator script, in tools/
        public string SeedJson = "seed.json";       // a finished seed folder holds it
        public Func<string> BundleDir = () => "";   // the mod's data folder's generator/
        public string PythonSetting = "", ScriptSetting = "", ProjectDir = "";

        Process proc;
        readonly object gate = new object();
        readonly List<string> lines = new List<string>();
        string status = "", outDir;
        int exitCode = int.MinValue;
        DateTime started;

        public bool Running { get { lock (gate) return proc != null && exitCode == int.MinValue; } }
        public string Status { get { lock (gate) return status; } }
        public double Seconds => (DateTime.Now - started).TotalSeconds;
        public string OutDir => outDir;

        // finished since the last call: (ok, error message, output folder)
        public bool TakeResult(out bool ok, out string error, out string dir)
        {
            lock (gate)
            {
                ok = false; error = null; dir = outDir;
                if (proc == null || exitCode == int.MinValue) return false;
                ok = exitCode == 0 && File.Exists(Path.Combine(outDir, SeedJson));
                if (!ok)
                {
                    var prefix = $"[{Tag}] error";
                    var err = lines.LastOrDefault(l => l.StartsWith(prefix));
                    error = err != null ? err.Substring(Tag.Length + 3) : $"the generator stopped (exit {exitCode}): " + (lines.LastOrDefault() ?? "no output");
                }
                proc.Dispose();
                proc = null;
                exitCode = int.MinValue;
                return true;
            }
        }

        public string Log { get { lock (gate) return string.Join("\n", lines); } }

        // (python.exe, the script), or an error
        public (string Python, string Script, string Error) Resolve()
        {
            var py = PythonSetting;
            var script = ScriptSetting;
            var bundle = BundleDir?.Invoke() ?? "";
            if (string.IsNullOrEmpty(py) && bundle != "" && File.Exists(Path.Combine(bundle, "python", "python.exe")))
                py = Path.Combine(bundle, "python", "python.exe");
            if (string.IsNullOrEmpty(script) && bundle != "" && File.Exists(Path.Combine(bundle, "tools", Script)))
                script = Path.Combine(bundle, "tools", Script);
            if (!string.IsNullOrEmpty(ProjectDir))
            {
                if (string.IsNullOrEmpty(py)) py = Path.Combine(ProjectDir, "pr", ".venv", "Scripts", "python.exe");
                if (string.IsNullOrEmpty(script)) script = Path.Combine(ProjectDir, "pr", "tools", Script);
            }
            if (string.IsNullOrEmpty(py) || !File.Exists(py)) return (py, script, "the seed generator isn't installed (no Python found)");
            if (string.IsNullOrEmpty(script) || !File.Exists(script)) return (py, script, $"the seed generator isn't installed (no {Script})");
            return (py, script, null);
        }

        // Starts the script: `--seed SEED --flags=FLAGS --out DIR` and any extra (name, value) pairs
        public string Start(string seed, string flags, string dir, params (string Name, string Value)[] extra)
        {
            lock (gate)
            {
                if (proc != null) return "a seed is already being generated";
                var (py, script, error) = Resolve();
                if (error != null) return error;
                lines.Clear();
                status = "starting";
                outDir = dir;
                exitCode = int.MinValue;
                started = DateTime.Now;
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
                psi.ArgumentList.Add("--seed");
                psi.ArgumentList.Add(seed);
                psi.ArgumentList.Add("--flags=" + flags);  // "=" so a value starting with "-" isn't taken for an option
                foreach (var (name, value) in extra)
                    psi.ArgumentList.Add($"--{name}={value}");
                psi.ArgumentList.Add("--out");
                psi.ArgumentList.Add(dir);
                psi.Environment["PYTHONIOENCODING"] = "utf-8";
                psi.Environment["PYTHONNOUSERSITE"] = "1";
                psi.Environment.Remove("PYTHONHOME");
                psi.Environment.Remove("PYTHONPATH");
                try
                {
                    var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                    p.OutputDataReceived += (_, e) => OnLine(e.Data);
                    p.ErrorDataReceived += (_, e) => OnLine(e.Data);
                    p.Exited += (_, __) => { lock (gate) { try { exitCode = p.ExitCode; } catch { exitCode = -1; } } };
                    if (!p.Start()) return "couldn't start the generator";
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    proc = p;
                    Kit.Info($"[{Tag}] generating seed '{seed}' with {py} {script}");
                    return null;
                }
                catch (Exception e)
                {
                    return "couldn't start the generator: " + e.Message;
                }
            }
        }

        void OnLine(string line)
        {
            if (line == null) return;
            var prefix = $"[{Tag}] ";
            lock (gate)
            {
                lines.Add(line);
                if (line.StartsWith(prefix)) status = line.Substring(prefix.Length);
            }
            Kit.Info($"[{Tag} gen] " + line);
        }

        public void Cancel()
        {
            lock (gate)
            {
                try { if (proc != null && !proc.HasExited) proc.Kill(true); } catch { }
            }
        }
    }
}
