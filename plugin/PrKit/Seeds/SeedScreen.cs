using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppSystem.Input;
using UnityEngine;
using Device = Il2CppSystem.Input.KeyConfig.KeyValue.InputDeviceType;
using Object = UnityEngine.Object;

namespace PrKit.Seeds
{
    // A randomizer's options screen in the game, as on its website or app: every flag (FlagSet),
    // presets, modes, the seed; "Generate" makes the seed in the background and applies it to the
    // running game, no restart (SeedGenerator, SeedLibrary). Opens from the mod's entry in the title
    // screen's menu (TitleMenuEntry), or F7 anywhere.
    //  D-pad/arrows: move, change   A/Enter: toggle, edit, run   B/Esc: to the page list (left),
    //  and from there close   Left on a row without a value: the page list   L1/R1 (PageUp/
    //  PageDown): previous/next page   text: type, Ctrl+V pastes (Deck: Steam+X)
    // Drawn with IMGUI: the game's build keeps GUI.Box/Label/Button, not GUILayout's fields, and
    // strips GUI.DrawTexture, so the rows, cursor and text editing are our own. Worlds Collide's
    // WcScreen (2026-11-05..19), in the kit since 2026-11-20; a mod subclasses it for its own rows,
    // seed rules and words (Beyond Chaos: BcScreen).
    public class SeedScreen
    {
        public bool Open;
        public bool Active;              // set up (the first open or a toast): runners call Update/Draw only then
        public FlagSet Defs;
        public SeedLibrary Lib;
        public SeedGenerator Gen;
        public string Tag = "[seed screen]";
        public string Name = "Mod";      // the randomizer's name, in titles and help
        public Func<bool> ModOn = () => true;
        public Func<bool> OnTitle = () => TitleScene();

        protected FlagValues values;
        protected string seedText = "", presetName = "", modeName = "";
        protected int page, cursor, scroll, slot;
        protected bool onPages;            // the cursor is on the page list (left) instead of the rows
        protected string editing;          // what the text editor is editing ("seed", "flags", a flag)
        string editBuffer = "";
        string note = "";
        float noteUntil;
        bool onTitle;
        int titleCheck;
        bool releaseWait;        // after closing, keep the game's input off until keys are up
        bool openWait;           // after opening, ignore the press that opened the screen
        string deleteId;         // the earlier seed whose row offers Delete (Left/Right)
        bool deleteSure;         // ... and A was pressed once: the next A deletes
        bool initialized;

        public sealed class Row
        {
            public string Label, Value = "", Help = "", Key;   // Key: an earlier seed's id
            public bool Header, Disabled;
            public int Indent;
            public Action Activate;
            public Action<int, int> Step;  // dir, amount
        }

        // --- what a mod sets ----------------------------------------------------------------

        public virtual string Title => $"{Name} {Defs?.Version}";
        public virtual string NotInstalled => $"{Name}'s data isn't installed (its Magicite folder): reinstall the mod.";
        public virtual string SeedHelp => "The same seed and flags make the same game. A: type a seed. Left/right: a new random seed.";
        public virtual string PresetHelp => "A preset sets every flag. Change any flag on the pages after this one (L/R).";
        public virtual string FlagsHelp => "A: edit as text (paste a flag string with Ctrl+V).";
        public virtual string GenerateHelp => $"Makes the seed ({Name}, then the port to the Pixel Remaster: a few seconds) and applies it at once. " +
                                              "Then start a New Game. Saves remember their seed.";
        public virtual string PortNote => $"Pages marked * work partly in the Pixel Remaster port; dimmed ones not yet.";
        public virtual string PortHelp(string port) => port switch
        {
            "no" => $"Not in the Pixel Remaster port yet: kept in the seed's flags, no effect in this game. ",
            "partly" => $"Partly in the Pixel Remaster port (data yes, the rest not yet). ",
            "na" => "Has no bearing on the Pixel Remaster. ",
            _ => "",
        };
        public virtual string RandomSeed()
        {
            const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
            var rng = new System.Random();
            return new string(Enumerable.Range(0, 12).Select(_ => chars[rng.Next(chars.Length)]).ToArray());
        }
        public virtual bool SeedChar(char c) => char.IsLetterOrDigit(c) || c == '-' || c == '_';
        public virtual string SeedRule => "Seeds use letters, digits, - and _.";
        public virtual bool LooksLikeFlags(string text) => text.Contains("-");
        // the generator's extra arguments (Beyond Chaos: its mode)
        public virtual (string Name, string Value)[] ExtraArgs() => Array.Empty<(string, string)>();
        // rows a mod adds to the first page after Seed (Beyond Chaos: its mode)
        protected virtual IEnumerable<Row> MoreMainRows() => Enumerable.Empty<Row>();
        // what a seed shows in the lists: its seed text, and beside it (WC: its hash)
        protected virtual string SeedLabel(SeedInfo s) => s.SeedText;
        protected virtual string SeedValue(SeedInfo s) => s.Hash;
        protected virtual string SeedDetails(SeedInfo s) => $"Flags: {s.Flags}";
        // the flags the mode forces on, or forbids (a dimmed row says why)
        protected virtual string Blocked(FlagDef d) => null;
        // more pages than the flags' (WC's Track: a page set of its own)
        protected virtual int PageCount => ModOn() ? Defs.Sections.Count + 1 : 1;
        protected virtual string PageTitle(int i) => i == 0 ? "Seed" : Defs.Sections[i - 1].Title + (Defs.Sections[i - 1].Port == "partly" ? " *" : "");
        protected virtual string PagePort(int i) => i == 0 ? "yes" : Defs.Sections[i - 1].Port;
        protected virtual List<Row> PageRows(int i) => i == 0 ? MainRows() : FlagRows(Defs.Sections[i - 1]);
        // the flag section a page shows (its count of flags on, in the page list), or null
        protected virtual Section PageSection(int i) => i > 0 && i <= Defs.Sections.Count ? Defs.Sections[i - 1] : null;

        // --- setting up -----------------------------------------------------------------------

        public void Init()
        {
            if (initialized) return;
            initialized = true;
            values = new FlagValues(Defs);
            var active = Lib.Active;
            if (active != null)
            {
                values.Parse(active.Flags);
                seedText = active.SeedText;
                presetName = active.Preset ?? "";
                OnActiveSeed(active);
            }
            else if (Defs.Presets.Count > 0)
            {
                presetName = Defs.Presets.Keys.First();
                values.Parse(Defs.Presets[presetName]);
                seedText = RandomSeed();
            }
            if (modeName == "" && Defs.Modes.Count > 0) modeName = Defs.Modes[0].Name;
        }

        // the current seed's own settings into the fields (a mod reads more of it: BC's mode)
        protected virtual void OnActiveSeed(SeedInfo s) { }

        protected void Say(string text, float seconds = 6)
        {
            note = text;
            noteUntil = Time.unscaledTime + seconds;
            Kit.Info($"{Tag} " + text);
        }

        public static bool TitleScene()
        {
            try
            {
                var m = global::Last.Management.SceneManager.Instance?.GetCurrentSubSceneManager();
                return m != null && m.TryCast<global::Last.Management.SubSceneManagerTitle>() != null;
            }
            catch (Exception) { return false; }
        }

        // the title scene is the current sub-scene: a field read, not a search of every object in the
        // scene (that ran 3 times a second, a hitch on the Deck)
        bool IsTitle()
        {
            if (++titleCheck % 10 == 1) onTitle = OnTitle();
            return onTitle;
        }

        public void Show(bool show)
        {
            if (show)
            {
                Active = true;
                Init();
            }
            if (show == Open) return;
            Open = show;
            editing = null;
            if (show) { PadInput.Blocking = true; releaseWait = false; openWait = true; onPages = false; }
            else releaseWait = true;
            PauseClicks(show);
        }

        // The title menu under the screen mustn't take mouse/touch clicks: its canvases' raycasters
        // are paused while the screen is open. (Switching the whole EventSystem off can also stop the
        // Input System's gamepad actions the game reads.)
        readonly List<UnityEngine.UI.GraphicRaycaster> paused = new List<UnityEngine.UI.GraphicRaycaster>();

        void PauseClicks(bool pause)
        {
            try
            {
                if (pause)
                {
                    paused.Clear();
                    foreach (var r in Object.FindObjectsOfType<UnityEngine.UI.GraphicRaycaster>())
                        if (r != null && r.enabled) { r.enabled = false; paused.Add(r); }
                }
                else
                {
                    foreach (var r in paused) if (r != null) r.enabled = true;
                    paused.Clear();
                }
            }
            catch (Exception e) { Hooks.WarnOnce("screen-clicks", e); }
        }

        // --- per frame: gamepad/keyboard through the game's input -------------------------

        public void Update()
        {
            IsTitle();
            if (releaseWait)
            {
                bool held = PadInput.Held(Key.Cancel) || PadInput.Held(Key.Action) || PadInput.Held(Key.Select);
                if (!held) { releaseWait = false; PadInput.Blocking = false; }
            }
            if (!Open) return;
            if (openWait)
            {
                // the Confirm press that picked the title menu entry must not also pick a row here
                if (PadInput.Held(Key.Action) || PadInput.Held(Key.Cancel)) return;
                openWait = false;
            }
            if (editing != null)
            {
                // typing comes through OnGUI; the pad can still confirm or cancel
                if (PadInput.Down(Key.Action, Device.GamePad)) CommitEdit();
                else if (PadInput.Down(Key.Cancel, Device.GamePad)) editing = null;
                return;
            }
            int up = PadInput.Repeat(0, Key.Up), down = PadInput.Repeat(1, Key.Down);
            int left = PadInput.Repeat(2, Key.Left), right = PadInput.Repeat(3, Key.Right);
            Nav(up, down, left, right, PadInput.Down(Key.Left), PadInput.Down(Key.Action), PadInput.Down(Key.Cancel),
                PadInput.Down(Key.PageUp) || PadInput.Down(Key.SwitchLeft), PadInput.Down(Key.PageDown) || PadInput.Down(Key.SwitchRight));
        }

        // one frame of buttons (counts from key repeat, presses as bools)
        void Nav(int up, int down, int left, int right, bool leftPress, bool a, bool b, bool prev, bool next)
        {
            var rows = Rows();
            if (prev) Page(-1);
            if (next) Page(+1);
            if (onPages)
            {
                // the page list: Up/Down pick a page (its rows show at once), A/Right go into it
                if (up > 0) Page(-1);
                if (down > 0) Page(+1);
                if (right > 0 || a) { onPages = false; rows = Rows(); if (rows.Count > 0 && rows[cursor].Header) Move(rows, +1); }
                else if (b) Show(false);
                return;
            }
            if (up > 0) Move(rows, -1);
            if (down > 0) Move(rows, +1);
            if (left > 0)
            {
                // Left changes a value; on a row without one it goes to the page list
                var r = cursor < rows.Count ? rows[cursor] : null;
                if (r != null && r.Step != null && !r.Disabled) StepRow(rows, -1, left);
                else if (leftPress) onPages = true;
            }
            if (right > 0) StepRow(rows, +1, right);
            if (a) ActivateRow(rows);
            if (b) onPages = true;  // B: back to the page list (B there closes)
        }

        // headless check of the controller flow: rows -B-> page list -Down-> next page -A-> its
        // rows -B-> page list -B-> closed; LB/RB step pages from the rows
        public string NavTest()
        {
            bool wasOpen = Open;
            int keepPage = page, keepCursor = cursor;
            var steps = new List<string>();
            try
            {
                Open = true; onPages = false; page = 0; cursor = 0;
                string State() => (Open ? "" : "closed ") + (onPages ? "list" : "rows") + $" p{page}";
                Nav(0, 0, 0, 0, false, false, true, false, false); steps.Add("B:" + State());
                Nav(0, 1, 0, 0, false, false, false, false, false); steps.Add("Down:" + State());
                Nav(0, 0, 0, 0, false, true, false, false, false); steps.Add("A:" + State());
                Nav(0, 0, 0, 0, false, false, false, false, true); steps.Add("RB:" + State());
                Nav(0, 0, 0, 0, false, false, true, false, false); steps.Add("B:" + State());
                Nav(0, 0, 0, 0, false, false, true, false, false); steps.Add("B:" + State());
                return string.Join(" ", steps);
            }
            finally
            {
                Open = wasOpen; onPages = false; page = keepPage; cursor = keepCursor; scroll = 0;
                PadInput.Blocking = Open; releaseWait = false;
            }
        }

        void Move(List<Row> rows, int dir)
        {
            if (rows.Count == 0) return;
            int c = cursor;
            for (int i = 0; i < rows.Count; i++)
            {
                c = (c + dir + rows.Count) % rows.Count;
                if (!rows[c].Header) break;
            }
            cursor = c;
            slot = 0;
        }

        void Page(int dir)
        {
            int pages = PageCount;
            page = (page + dir + pages) % pages;
            cursor = 0;
            scroll = 0;
            slot = 0;
            var rows = Rows();
            if (rows.Count > 0 && rows[0].Header) Move(rows, +1);
        }

        void StepRow(List<Row> rows, int dir, int amount)
        {
            if (cursor >= rows.Count) return;
            var r = rows[cursor];
            if (r.Disabled || r.Step == null) return;
            r.Step(dir, amount);
        }

        void ActivateRow(List<Row> rows)
        {
            if (cursor >= rows.Count) return;
            var r = rows[cursor];
            if (r.Disabled) { if (!string.IsNullOrEmpty(r.Help)) Say(r.Help); return; }
            r.Activate?.Invoke();
        }

        // --- rows -------------------------------------------------------------------------

        List<Row> Rows() => PageRows(Math.Min(page, PageCount - 1));

        protected List<Row> MainRows()
        {
            var rows = new List<Row>();
            bool title = onTitle;
            if (!ModOn())
            {
                rows.Add(new Row { Header = true, Label = NotInstalled });
                rows.Add(new Row { Label = "Close", Activate = () => Show(false) });
                return rows;
            }
            var presets = Defs.Presets.Keys.ToList();
            rows.Add(new Row
            {
                Label = "Preset",
                Value = presetName == "" ? "Custom" : presetName,
                Help = PresetHelp,
                Step = (d, _) =>
                {
                    int i = presets.IndexOf(presetName) + d;
                    if (i < -1) i = presets.Count - 1;
                    if (i >= presets.Count) i = -1;
                    presetName = i < 0 ? "" : presets[i];
                    if (presetName != "") values.Parse(Defs.Presets[presetName]);
                },
                Activate = () => { if (presetName != "") { values.Parse(Defs.Presets[presetName]); Say($"Flags set from {presetName}."); } },
            });
            rows.AddRange(MoreMainRows());
            rows.Add(new Row
            {
                Label = "Seed",
                Value = seedText == "" ? "(random)" : seedText,
                Help = SeedHelp,
                Step = (d, _) => seedText = RandomSeed(),
                Activate = () => StartEdit("seed", seedText),
            });
            var flags = values.Serialize();
            rows.Add(new Row
            {
                Label = "Flags",
                Value = flags.Length > 60 ? flags.Substring(0, 57) + "..." : flags == "" ? "(none)" : flags,
                Help = $"{values.Count} flags set{(values.Unknown.Count > 0 ? $", {values.Unknown.Count} the screen doesn't know (kept)" : "")}. {FlagsHelp}",
                Activate = () => StartEdit("flags", flags),
            });
            rows.Add(new Row
            {
                Label = "Paste flags from clipboard",
                Help = "Replace the flags with the clipboard's flag string.",
                Activate = () =>
                {
                    var clip = GUIUtility.systemCopyBuffer ?? "";
                    if (!LooksLikeFlags(clip)) { Say("The clipboard has no flags."); return; }
                    values.Parse(clip);
                    presetName = "";
                    Say($"{values.Count} flags pasted{(values.Unknown.Count > 0 ? $"; {values.Unknown.Count} unknown tokens kept as typed" : "")}.");
                },
            });
            rows.Add(new Row
            {
                Label = "Copy flags to clipboard",
                Activate = () => { GUIUtility.systemCopyBuffer = values.Serialize(); Say("Flags copied."); },
            });
            rows.Add(new Row
            {
                Label = Gen.Running ? $"Generating... {Gen.Status}" : "Generate and play this seed",
                Disabled = !title || Gen.Running,
                Help = !title ? "Seeds change the whole game: return to the title screen to make or switch one." : GenerateHelp,
                Activate = Generate,
            });
            rows.Add(new Row { Header = true, Label = PortNote });
            var active = Lib.Active;
            rows.Add(new Row { Header = true, Label = "Current seed" });
            rows.Add(new Row
            {
                Label = active == null ? "None: the game's own data" : SeedLabel(active),
                Value = active == null ? "" : SeedValue(active),
                Indent = 1,
                Help = active == null ? $"Generate a seed to play {Name}." : $"{SeedDetails(active)}\nMade {active.Created} ({Name} {active.Version}). A: copy its seed and flags into the fields above.",
                Activate = active == null ? (Action)null : () =>
                {
                    values.Parse(active.Flags);
                    seedText = active.SeedText;
                    presetName = "";
                    OnActiveSeed(active);
                    Say("Seed and flags copied from the current seed.");
                },
            });
            var saved = Lib.All().Where(s => s.Id != active?.Id).ToList();
            bool deleteShown = false;
            if (saved.Count > 0)
            {
                rows.Add(new Row { Header = true, Label = "Earlier seeds (A: play it again   Left/Right: delete it)" });
                foreach (var s in saved)
                {
                    // Left/Right turn the row under the cursor to Delete; A asks once more, then deletes
                    bool del = deleteId == s.Id && cursor == rows.Count;
                    deleteShown |= del;
                    rows.Add(new Row
                    {
                        Key = s.Id,
                        Label = SeedLabel(s),
                        Value = !del ? $"{SeedValue(s)}   {s.Created}" : deleteSure ? "Delete it? A: yes, Left/Right: no" : "Delete   (A)",
                        Indent = 1,
                        Disabled = !title,
                        Help = !title ? "Return to the title screen to switch or delete seeds."
                             : del ? $"Deletes seed {Lib.Describe(s)}, made {s.Created}, from this game. Not if a save still uses it."
                             : SeedDetails(s),
                        Step = (d, _) => { deleteId = deleteId == s.Id ? null : s.Id; deleteSure = false; },
                        Activate = () =>
                        {
                            if (deleteId == s.Id && !deleteSure) { deleteSure = true; return; }
                            if (deleteId == s.Id)
                            {
                                deleteId = null; deleteSure = false;
                                var derr = Lib.Delete(s);
                                Say(derr == null ? $"Seed {Lib.Describe(s)} deleted." : "Not deleted: " + derr, 8);
                                return;
                            }
                            var err = Lib.Apply(s);
                            Say(err == null ? $"Seed {Lib.Describe(s)} is back. Start a New Game or load one of its saves." : "Couldn't apply: " + err, 8);
                        },
                    });
                }
            }
            if (!deleteShown) { deleteId = null; deleteSure = false; }   // the cursor left that row
            if (active != null)
                rows.Add(new Row
                {
                    Label = "Play without a seed (the game's own data)",
                    Disabled = !title,
                    Activate = () => { var err = Lib.Apply(null); Say(err == null ? "Back to the game's own data." : err); },
                });
            rows.Add(new Row { Label = "Close", Activate = () => Show(false) });
            return rows;
        }

        void Generate()
        {
            var seed = seedText.Trim();
            if (seed == "") seedText = seed = RandomSeed();
            if (!seed.All(SeedChar)) { Say(SeedRule); return; }
            var id = SeedLibrary.NewId(seed);
            var flags = values.Serialize();
            var err = Gen.Start(seed, flags, Lib.DirFor(id), ExtraArgs());
            Say(err ?? "Generating... (you can close this screen)", err == null ? 3 : 10);
        }

        // finished generations are applied here (main thread)
        public void Poll()
        {
            if (!Gen.TakeResult(out bool ok, out string error, out string dir)) return;
            if (!ok) { Say("The seed couldn't be made: " + error, 15); return; }
            var s = Lib.Load(System.IO.Path.GetFileName(dir));
            if (s == null) { Say("The seed was made but can't be read.", 10); return; }
            if (!onTitle) { Say($"Seed {Lib.Describe(s)} is ready; return to the title screen to play it (Earlier seeds).", 12); return; }
            var err = Lib.Apply(s);
            Lib.Prune();
            Say(err == null ? $"Seed {Lib.Describe(s)} is ready: start a New Game." : "Couldn't apply the seed: " + err, 12);
        }

        protected List<Row> FlagRows(Section sec)
        {
            var rows = new List<Row>();
            var hidden = new HashSet<string>(sec.Rows.OfType<FlagRow>().SelectMany(r => values.IsOn(r.Flag.Flag) ? Array.Empty<string>() : r.Flag.Children));
            foreach (var r in sec.Rows)
            {
                if (r is FlagRow fr)
                {
                    if (hidden.Contains(fr.Flag.Flag)) continue;     // a child of a flag that's off
                    rows.Add(FlagUi(fr.Flag, fr.Label, fr.Indent));
                }
                else if (r is ChoiceRow cr)
                {
                    int sel = values.Selected(cr);
                    var help = string.Join("\n", cr.Options.Select((o, i) => $"{cr.OptionLabels[i]}: {o.Help}"));
                    rows.Add(new Row
                    {
                        Label = cr.Label,
                        Value = sel < 0 ? "Off" : cr.OptionLabels[sel],
                        Help = help,
                        Step = (d, _) =>
                        {
                            int i = values.Selected(cr) + d;
                            if (i < -1) i = cr.Options.Count - 1;
                            if (i >= cr.Options.Count) i = -1;
                            values.Select(cr, i);
                            presetName = "";
                        },
                        Activate = () =>
                        {
                            int i = values.Selected(cr) + 1;
                            values.Select(cr, i >= cr.Options.Count ? -1 : i);
                            presetName = "";
                        },
                    });
                    if (sel >= 0 && cr.Options[sel].Nargs != 0)
                        rows.Add(FlagUi(cr.Options[sel], cr.Options[sel].Metavar.Length > 0 ? Pretty(string.Join(" / ", cr.Options[sel].Metavar)) : "Value", 1));
                }
            }
            return rows;
        }

        static string Pretty(string s) => s.Length == 0 ? s : s.Substring(0, 1).ToUpperInvariant() + s.Substring(1).ToLowerInvariant().Replace("_", " ");

        Row FlagUi(FlagDef d, string label, int indent)
        {
            var help = d.Help;
            if (d.Min.HasValue && d.Max.HasValue) help += $"  ({FlagValues.Num(d, d.Min.Value)} to {FlagValues.Num(d, d.Max.Value)}{(d.Special != null ? $", or {d.Special}" : "")})";
            if (d.Choices != null && d.Choices.Length <= 20) help += "  Choices: " + string.Join(", ", d.Choices.Select(c => c == "" ? "(none)" : c));
            if (d.Text) help += "  A: type it.";
            if (d.Nargs == 2) help += "  A: switch between the two values.";
            help += $"  [{d.Code ?? d.Flag}]";
            if (d.Port != "yes") help = PortHelp(d.Port) + help;
            var blocked = Blocked(d);
            var unmet = values.IsOn(d.Flag) ? null : values.Unmet(d);
            if (blocked != null) help = blocked + ". " + help;
            else if (unmet != null) help = Capital(unmet) + ". " + help;
            var value = values.Describe(d);
            if (d.Nargs == 2 && values.IsOn(d.Flag))
            {
                var v = values.Values(d.Flag);
                value = slot == 0 ? $"[{v[0]}] - {(v.Count > 1 ? v[1] : "?")}" : $"{v[0]} - [{(v.Count > 1 ? v[1] : "?")}]";
            }
            return new Row
            {
                Label = label,
                Value = value.Length > 48 ? value.Substring(0, 45) + "..." : value,
                Help = help,
                Indent = indent,
                Disabled = blocked != null || (unmet != null && !values.IsOn(d.Flag)),
                Step = (dir, amount) =>
                {
                    if (d.Text) { if (dir < 0 && values.IsOn(d.Flag)) values.Off(d.Flag); else StartEdit(d.Flag, string.Join(" ", values.Values(d.Flag))); return; }
                    values.Step(d, slot, dir, amount);
                    presetName = "";
                },
                Activate = () =>
                {
                    presetName = "";
                    if (d.Text) { StartEdit(d.Flag, string.Join(" ", values.Values(d.Flag))); return; }
                    if (d.Nargs == 2 && values.IsOn(d.Flag)) { slot = 1 - slot; return; }
                    if (values.IsOn(d.Flag)) values.Off(d.Flag); else values.Set(d.Flag);
                },
            };
        }

        static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        // headless check: styles build, every page's rows build, and every flag row steps both ways
        // and toggles without errors; the flags are put back afterwards
        public string SelfTest()
        {
            Init();
            var keep = values.Serialize();
            var keepPreset = presetName;
            int rowsSeen = 0, actions = 0;
            try
            {
                Styles();
                MainRows();
                for (int p = 1; p < PageCount; p++)
                {
                    page = p;
                    for (int i = 0; i < 400; i++)
                    {
                        var rows = Rows();
                        if (i >= rows.Count) break;
                        var r = rows[i];
                        rowsSeen++;
                        if (r.Header) continue;
                        r.Step?.Invoke(+1, 1); r.Step?.Invoke(+1, 10); r.Step?.Invoke(-1, 1);
                        if (editing == null) r.Activate?.Invoke();
                        editing = null;
                        r.Step?.Invoke(-1, 1000);
                        editing = null;
                        actions += 5;
                    }
                }
                return $"{PageCount} pages, {rowsSeen} rows, {actions} steps/toggles; flags after: {values.Count} on";
            }
            finally
            {
                page = 0; cursor = 0; scroll = 0; editing = null;
                values.Parse(keep);
                presetName = keepPreset;
            }
        }

        // --- text editing -----------------------------------------------------------------

        protected void StartEdit(string what, string text)
        {
            editing = what;
            editBuffer = text ?? "";
        }

        void CommitEdit()
        {
            var what = editing;
            editing = null;
            if (what == "seed") seedText = new string(editBuffer.Trim().Where(SeedChar).ToArray());
            else if (what == "flags") { values.Parse(editBuffer); presetName = ""; Say($"{values.Count} flags set."); }
            else if (Defs.Flags.TryGetValue(what, out var d)) { values.SetText(d, editBuffer); presetName = ""; }
        }

        void EditKeys(Event e)
        {
            if (e.type != EventType.KeyDown) return;
            var k = e.keyCode;
            if (k == KeyCode.Return || k == KeyCode.KeypadEnter) CommitEdit();
            else if (k == KeyCode.Escape) editing = null;
            else if (k == KeyCode.Backspace) { if (editBuffer.Length > 0) editBuffer = editBuffer.Substring(0, editBuffer.Length - 1); }
            else if (k == KeyCode.V && (e.control || e.command)) editBuffer += (GUIUtility.systemCopyBuffer ?? "").Replace("\r", " ").Replace("\n", " ");
            else if (k == KeyCode.Delete) editBuffer = "";
            else
            {
                char c = e.character;
                if (c >= ' ' && c != '\u007f' && !e.control) editBuffer += c;
                else return;
            }
            e.Use();
        }

        // --- drawing (OnGUI) --------------------------------------------------------------

        const float VW = 1280, VH = 720;
        GUIStyle sBg, sPanel, sSel, sText, sValue, sDim, sTitle, sHelp, sPage, sPageSel, sPageDim, sClick, sHeader, sEdit, sEditBox, sSelDim, leftValue;
        GUIContent content;

        static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        float S = 1f, builtFor = -1f;  // pixels per virtual unit; scale the styles were built for

        Rect R(float x, float y, float w, float h) => new Rect(x * S, y * S, w * S, h * S);
        Rect R(Rect v) => R(v.x, v.y, v.width, v.height);

        GUIStyle Style(int size, Color color, TextAnchor anchor = TextAnchor.MiddleLeft, bool wrap = false, Texture2D bg = null)
        {
            var s = new GUIStyle();
            if (font != null) s.font = font;
            s.fontSize = Math.Max(8, (int)Math.Round(size * S));
            s.normal.textColor = color;
            s.alignment = anchor;
            s.wordWrap = wrap;
            s.clipping = TextClipping.Clip;
            if (bg != null) s.normal.background = bg;
            return s;
        }

        // FF6-style window: vertical blue gradient, light rounded border, drawn 9-sliced
        static GUIStyle Window(Color top, Color bottom, Color edge, float edgeAlpha = 1f)
        {
            const int W = 32, H = 64, B = 8, Rad = 6;
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            t.hideFlags = HideFlags.HideAndDontSave;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    var c = Color.Lerp(bottom, top, y / (float)(H - 1));
                    float cx = Math.Max(0, Math.Max(Rad - x, x - (W - 1 - Rad))), cy = Math.Max(0, Math.Max(Rad - y, y - (H - 1 - Rad)));
                    float d = (float)Math.Sqrt(cx * cx + cy * cy);
                    if (d > Rad + 0.5f) c = new Color(0, 0, 0, 0);
                    else if (d > Rad - 2.5f || x < 2 || y < 2 || x > W - 3 || y > H - 3)
                        c = new Color(edge.r, edge.g, edge.b, edge.a * edgeAlpha);
                    t.SetPixel(x, y, c);
                }
            t.Apply();
            var s = new GUIStyle();
            s.normal.background = t;
            s.border = new RectOffset(B, B, B, B);
            return s;
        }

        Font font;
        bool fontFound;

        // the game's own menu font (from any UI text on screen); until one is up, Unity's default
        void FindFont()
        {
            if (fontFound) return;
            try
            {
                foreach (var t in Object.FindObjectsOfType<UnityEngine.UI.Text>())
                    if (t != null && t.font != null) { font = t.font; break; }
            }
            catch (Exception) { }
            if (font == null) return;
            fontFound = true;
            builtFor = -1f;  // rebuild the styles with it
            Kit.Info($"{Tag} using the game's font '{font.name}'");
        }

        void Styles()
        {
            if (sBg != null && Math.Abs(builtFor - S) < 0.01f) return;
            builtFor = S;
            leftValue = null;
            shadows.Clear();
            content = new GUIContent("");
            var edge = new Color(0.86f, 0.89f, 0.97f);
            sBg = Style(1, Color.white, bg: Solid(new Color(0, 0, 0, 0.55f)));
            sPanel = Window(new Color(0.16f, 0.24f, 0.62f, 0.97f), new Color(0.03f, 0.05f, 0.24f, 0.97f), edge);
            sSel = Window(new Color(0.45f, 0.58f, 0.98f, 0.85f), new Color(0.24f, 0.36f, 0.82f, 0.85f), edge, 0.7f);
            sSelDim = Window(new Color(0.3f, 0.38f, 0.7f, 0.45f), new Color(0.18f, 0.25f, 0.58f, 0.45f), edge, 0.25f);
            sText = Style(22, Color.white);
            sValue = Style(22, new Color(1f, 0.89f, 0.5f), TextAnchor.MiddleRight);
            sDim = Style(22, new Color(0.56f, 0.6f, 0.72f));
            sHeader = Style(19, new Color(0.62f, 0.82f, 1f));
            sTitle = Style(30, Color.white);
            sHelp = Style(18, new Color(0.88f, 0.9f, 0.97f), TextAnchor.UpperLeft, true);
            sPage = Style(17, new Color(0.8f, 0.83f, 0.92f));
            sPageDim = Style(17, new Color(0.5f, 0.53f, 0.64f));
            sPageSel = Style(17, Color.white);
            sClick = new GUIStyle();
            sEdit = Style(24, Color.white);
            sEditBox = Window(new Color(0.06f, 0.08f, 0.26f, 1f), new Color(0.02f, 0.03f, 0.14f, 1f), edge, 0.8f);
        }

        readonly Dictionary<GUIStyle, GUIStyle> shadows = new Dictionary<GUIStyle, GUIStyle>();

        // text with the game's drop shadow
        void Shadowed(Rect r, string text, GUIStyle st)
        {
            if (!shadows.TryGetValue(st, out var sh))
            {
                sh = new GUIStyle();
                sh.font = st.font;
                sh.fontSize = st.fontSize;
                sh.alignment = st.alignment;
                sh.wordWrap = st.wordWrap;
                sh.clipping = st.clipping;
                sh.normal.textColor = new Color(0, 0, 0, 0.75f);
                shadows[st] = sh;
            }
            float o = Math.Max(1f, 2f * S);
            GUI.Label(new Rect(r.x + o, r.y + o, r.width, r.height), C(text), sh);
            GUI.Label(r, C(text), st);
        }

        GUIContent C(string text)
        {
            content.text = text;
            return content;
        }

        void Label(float x, float y, float w, float h, string text, GUIStyle s) => Shadowed(R(x, y, w, h), text, s);
        bool Click(float x, float y, float w, float h) => GUI.Button(R(x, y, w, h), C(""), sClick);

        // a line over the game for a few seconds (the startup apply's warning, a mod's notes)
        string toast = "";
        float toastUntil;

        public void Toast(string text)
        {
            Active = true;
            Init();
            toast = text;
            toastUntil = Time.unscaledTime + 6;
        }

        GUIStyle LeftValue() => leftValue ??= Style(18, new Color(1f, 0.88f, 0.45f), TextAnchor.MiddleLeft);

        public void Draw()
        {
            var e = Event.current;
            if (e != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.F7 && editing == null)
            {
                Show(!Open);
                e.Use();
                return;
            }
            S = Mathf.Min(Screen.width / VW, Screen.height / VH);
            if (S <= 0) return;
            if (Open) FindFont();
            Styles();
            float w = Screen.width / S, h = Screen.height / S;  // virtual size
            if (!Open && Time.unscaledTime < toastUntil && toast != "")
            {
                FindFont();
                GUI.Box(R((w - 900) / 2, 30, 900, 48), C(""), sPanel);
                Label((w - 900) / 2 + 20, 30, 860, 48, toast, LeftValue());
            }
            if (!Open) return;
            if (editing != null && e != null) EditKeys(e);
            GUI.Box(R(0, 0, w, h), C(""), sBg);
            float px = (w - 1250) / 2, py = (h - 700) / 2;
            GUI.Box(R(px, py, 1250, 700), C(""), sPanel);
            Label(px + 20, py + 8, 800, 36, ModOn() ? Title : "Mods", sTitle);
            var status = Gen.Running ? $"Generating: {Gen.Status} ({Gen.Seconds:0} s)"
                       : Lib.Active != null ? $"Seed: {Lib.Describe(Lib.Active)}" : "No seed";
            if (ModOn()) Label(px + 600, py + 8, 630, 36, status, LeftValue());

            // pages
            int pages = PageCount;
            float lx = px + 14, ly = py + 52, lh = Mathf.Min(21, 630f / pages);
            for (int i = 0; i < pages; i++)
            {
                var port = PagePort(i);
                var title = PageTitle(i);
                var r = new Rect(lx, ly + i * lh, 236, lh);
                if (i == page) GUI.Box(R(r), C(""), onPages ? sSel : sSelDim);
                var sec = PageSection(i);
                int on = sec == null ? 0 : CountOn(sec);
                Shadowed(R(r.x + 10, r.y, r.width - 12, r.height), title + (on > 0 ? $"  ({on})" : ""), i == page ? sPageSel : port == "no" ? sPageDim : sPage);
                if (GUI.Button(R(r), C(""), sClick)) { page = i; cursor = 0; scroll = 0; slot = 0; onPages = false; }
            }

            // rows
            var rows = Rows();
            if (cursor >= rows.Count) cursor = Math.Max(0, rows.Count - 1);
            if (rows.Count > 0 && rows[cursor].Header) Move(rows, +1);
            float rx = px + 266, ry = py + 52, rw = 970, rh = 34;
            int visible = 15;
            if (cursor < scroll) scroll = cursor;
            if (cursor >= scroll + visible) scroll = cursor - visible + 1;
            if (e != null && e.type == EventType.ScrollWheel)
            {
                scroll = Mathf.Clamp(scroll + (e.delta.y > 0 ? 3 : -3), 0, Math.Max(0, rows.Count - visible));
                cursor = Mathf.Clamp(cursor, scroll, Math.Min(rows.Count - 1, scroll + visible - 1));
                e.Use();
            }
            for (int i = scroll; i < rows.Count && i < scroll + visible; i++)
            {
                var row = rows[i];
                float y = ry + (i - scroll) * rh;
                var rect = new Rect(rx, y, rw, rh - 2);
                if (i == cursor) GUI.Box(R(rect), C(""), onPages ? sSelDim : sSel);
                float ix = rx + 12 + row.Indent * 28;
                if (row.Header) { Label(ix, y, rw - 24, rh, row.Label, sHeader); continue; }
                Label(ix, y, 560 - row.Indent * 28, rh, row.Label, row.Disabled ? sDim : sText);
                if (row.Value != "") Label(rx + 420, y, rw - 432, rh, row.Value, row.Disabled ? sDim : sValue);
                if (Click(rect.x, rect.y, rect.width, rect.height))
                {
                    if (cursor == i && !row.Disabled)
                    {
                        // tap the right end of the value to step, elsewhere to activate
                        float mx = (e?.mousePosition.x ?? 0) / S;  // back to virtual units
                        if (row.Step != null && mx > rx + rw - 120) row.Step(+1, 1);
                        else if (row.Step != null && mx > rx + 420 && mx < rx + 540) row.Step(-1, 1);
                        else row.Activate?.Invoke();
                    }
                    cursor = i;
                    slot = 0;
                }
            }
            if (rows.Count > visible)
                Label(rx, ry + visible * rh, rw, 18, $"{scroll + 1}-{Math.Min(rows.Count, scroll + visible)} of {rows.Count}", sPage);

            // help + keys + notes
            float hy = py + 588;
            var help = cursor < rows.Count ? rows[cursor].Help : "";
            help = PortHelp(PagePort(Math.Min(page, pages - 1))) + help;
            Label(rx, hy, rw, 72, help, sHelp);
            var keys = onPages ? "Up/Down: choose a page   A / Right: go to its options   L1/R1: pages   B: close"
                     : page == 0 ? "Up/Down: choose   Left/Right: change   A: select   B: page list   L1/R1: pages"
                                 : "Up/Down: choose   Left/Right: change   A: toggle / edit   B: page list   L1/R1: pages";
            Label(rx, py + 668, rw, 24, Time.unscaledTime < noteUntil ? note : keys, Time.unscaledTime < noteUntil ? LeftValue() : sPage);

            if (editing != null) DrawEditor(w, h);
        }

        int CountOn(Section s) =>
            s.Rows.Sum(r => r is FlagRow f ? (values.IsOn(f.Flag.Flag) ? 1 : 0) : ((ChoiceRow)r).Options.Count(o => values.IsOn(o.Flag)));

        void DrawEditor(float w, float h)
        {
            float ex = (w - 1000) / 2, ey = (h - 170) / 2;
            GUI.Box(R(ex - 6, ey - 6, 1012, 182), C(""), sSel);
            GUI.Box(R(ex, ey, 1000, 170), C(""), sPanel);
            var what = editing == "seed" ? "Seed" : editing == "flags" ? "Flags" : Defs.Flags.TryGetValue(editing, out var d) ? $"{d.Label} ({d.Code ?? d.Flag})" : editing;
            Label(ex + 16, ey + 8, 960, 30, what, sHeader);
            var shown = editBuffer;
            if (shown.Length > 80) shown = "..." + shown.Substring(shown.Length - 77);
            bool caret = ((int)(Time.unscaledTime * 2)) % 2 == 0;
            GUI.Box(R(ex + 16, ey + 46, 968, 44), C(""), sEditBox);
            Label(ex + 26, ey + 46, 948, 44, shown + (caret ? "|" : ""), sEdit);
            Label(ex + 16, ey + 100, 968, 26, "Enter / A: OK    Esc / B: cancel    Ctrl+V: paste    Delete: clear", sPage);
            Label(ex + 16, ey + 128, 968, 26, "Steam Deck: Steam + X opens the on-screen keyboard.", sPage);
        }
    }
}
