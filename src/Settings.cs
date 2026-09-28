// Settings.cs - the Settings window (tray menu > Settings...), dark and always on top:
//   Projector:  a picture preset for each mode, and live adjustments of the preset showing now
//               (the projector keeps adjustments inside each preset itself), with 3 save slots
//               per preset to try things and go back.
//   GPU colour: brightness / contrast / gamma per channel, digital vibrance and hue, per mode and
//               per display, applied by ModeSwitch on every switch, with 3 save slots each.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

sealed class SettingsForm : Form
{
    readonly ModeSwitchApp app;
    static readonly string[] Modes = { "movie", "3d", "game" };
    const int Slots = 3;

    // ---- dark theme ----
    static readonly Color Back = Color.FromArgb(32, 32, 32), Panel = Color.FromArgb(43, 43, 43), Field = Color.FromArgb(55, 55, 55),
                          Text1 = Color.FromArgb(235, 235, 235), Text2 = Color.FromArgb(160, 160, 160), Accent = Color.FromArgb(76, 160, 255),
                          Error = Color.FromArgb(255, 120, 110), Border = Color.FromArgb(80, 80, 80);

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref on, 4);   // dark title bar
    }

    static void Dark(Control c)
    {
        if (!"note".Equals(c.Tag)) c.ForeColor = Text1;          // notes keep their grey
        if (c is Button)
        {
            var b = (Button)c;
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Field;
            b.FlatAppearance.BorderColor = Border;
        }
        else if (c is ComboBox)
        {
            var cb = (ComboBox)c;
            cb.FlatStyle = FlatStyle.Flat;
            cb.BackColor = Field;
            cb.HandleCreated += (s, e) => SetWindowTheme(cb.Handle, "DarkMode_CFD", null);
        }
        else if (c is CheckBox) c.BackColor = Color.Transparent;      // standard style: a flat box hides its white tick
        else if (c is NumericUpDown) { c.BackColor = Field; ((NumericUpDown)c).BorderStyle = BorderStyle.FixedSingle; }
        else if (c is TrackBar) c.BackColor = Panel;
        else if (c is GroupBox || c is System.Windows.Forms.Panel) c.BackColor = Panel;
        foreach (Control child in c.Controls) Dark(child);
    }

    public SettingsForm(ModeSwitchApp app)
    {
        this.app = app;
        Text = "ModeSwitch settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        Font = new Font("Segoe UI", 9f);
        BackColor = Back;
        ClientSize = new Size(540, 720);

        // Tab buttons + pages (a stock TabControl can't be drawn dark).
        var pages = new System.Windows.Forms.Panel[3];
        var tabs = new Button[3];
        string[] tabNames = { "Projector 2D", "Projector 3D", "GPU colour" };
        for (int i = 0; i < 3; i++)
        {
            pages[i] = new System.Windows.Forms.Panel { Location = new Point(8, 44), Size = new Size(524, 626), Visible = false };
            tabs[i] = new Button { Text = tabNames[i], Location = new Point(8 + i * 174, 8), Size = new Size(170, 32) };
        }
        pj2d = new PjEditor(this, false);
        pj3d = new PjEditor(this, true);
        BuildPresetsFor2D(pages[0]);
        pj2d.Build(pages[0], 120);
        BuildPresetFor3D(pages[1]);
        pj3d.Build(pages[1], 68);
        BuildGpuTab(pages[2]);

        var ok = new Button { Text = "OK", Location = new Point(290, 682), Size = new Size(75, 28) };
        var cancel = new Button { Text = "Cancel", Location = new Point(372, 682), Size = new Size(75, 28) };
        var apply = new Button { Text = "Apply", Location = new Point(454, 682), Size = new Size(75, 28) };
        ok.Click += (s, e) => { Save(); saved = true; Close(); };
        apply.Click += (s, e) => Save();
        cancel.Click += (s, e) => Close();
        CancelButton = cancel;       // no AcceptButton: Enter in a number box applies the number, not OK

        // For adjusting during a film: see the picture through the window, and reopen where it was left.
        var seeThrough = new CheckBox { Text = "See-through", Location = new Point(12, 688), AutoSize = true };
        seeThrough.CheckedChanged += (s, e) => { Opacity = seeThrough.Checked ? 0.75 : 1.0; WriteReg("SettingsSeeThrough", seeThrough.Checked ? "1" : "0"); };
        seeThrough.Checked = ReadReg("SettingsSeeThrough") == "1";
        // While the projector shows frame-compatible 3D, draw this window as a pair (see Stereo.cs).
        var view3d = new CheckBox { Text = "3D view", Location = new Point(120, 688), AutoSize = true };
        view3d.Checked = ReadReg("SettingsStereo") != "0";
        view3d.CheckedChanged += (s, e) =>
        {
            WriteReg("SettingsStereo", view3d.Checked ? "1" : "0");
            stereoWanted = view3d.Checked;
            SetStereo(view3d.Checked ? stereoFormat : 0);
        };
        stereoWanted = view3d.Checked;
        RestorePosition();
        Controls.AddRange(tabs);
        Controls.AddRange(pages);
        Controls.AddRange(new Control[] { ok, cancel, apply, seeThrough, view3d });
        foreach (Control c in Controls) Dark(c);

        Action<int> select = n =>
        {
            for (int i = 0; i < 3; i++)
            {
                pages[i].Visible = i == n;
                tabs[i].BackColor = i == n ? Accent : Field;
                tabs[i].ForeColor = i == n ? Color.Black : Text1;
            }
            if (n == 0) Run(pj2d.Read);                   // the projector may have gone into / out of 3D
            if (n == 1) Run(pj3d.Read);
        };
        for (int i = 0; i < 3; i++) { int n = i; tabs[i].Click += (s, e) => select(n); }

        var worker = new Thread(() =>
        {
            foreach (Action job in jobs.GetConsumingEnumerable()) { try { job(); } catch { } }
        }) { IsBackground = true, Name = "Settings" };
        worker.Start();
        select(0);
        Run(pj3d.Read);

        stereoTimer.Tick += (s, e) => Run(CheckStereo);
        stereoTimer.Start();
        Run(CheckStereo);
    }

    PjEditor pj2d, pj3d;

    // ---- 3D view ----
    readonly System.Windows.Forms.Timer stereoTimer = new System.Windows.Forms.Timer { Interval = 2000 };
    StereoMirror stereo;
    int stereoShown;                 // format the mirror was made for (0 = none)
    volatile int stereoFormat;       // what the projector shows: 0 2D, 1 SBS, 2 OU
    bool stereoWanted = true;

    // Worker: is the projector in frame-compatible 3D, and which format? (3D Depth only answers in 3D.)
    void CheckStereo()
    {
        string ip = PjIp;
        int fmt = 0, v;
        if (ip.Length > 0 && Pj.Get(ip, PjCom, PjPicture.Depth3D, out v) == null && Pj.Get(ip, PjCom, Pj.ItemFormat3D, out v) == null
            && (v == StereoMirror.SideBySide || v == StereoMirror.OverUnder))
            fmt = v;
        if (fmt == stereoFormat) return;
        stereoFormat = fmt;
        Ui(() =>
        {
            SetStereo(stereoWanted ? fmt : 0);
            Run(pj2d.Read);                                  // 2D <-> 3D: each tab unlocks / locks
            Run(pj3d.Read);
        });
    }

    void SetStereo(int format)
    {
        if (format == stereoShown) return;
        if (stereo != null) { stereo.Dispose(); stereo = null; }
        stereoShown = format;
        if (format != 0 && !IsDisposed) stereo = new StereoMirror(this, format);
    }

    bool saved, previewed;

    // ---- window position, kept between openings ----
    static string ReadReg(string name)
    {
        try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\ModeSwitch")) return k == null ? null : k.GetValue(name) as string; }
        catch { return null; }
    }

    static void WriteReg(string name, string value)
    {
        try { using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\ModeSwitch")) k.SetValue(name, value); }
        catch { }
    }

    // Back where it was last closed, if that spot is still on a connected screen.
    void RestorePosition()
    {
        string[] p = (ReadReg("SettingsPos") ?? "").Split(',');
        int x, y;
        if (p.Length != 2 || !int.TryParse(p[0], out x) || !int.TryParse(p[1], out y)) return;
        var spot = new Rectangle(x, y, Width, 40);                   // the title bar must be reachable
        foreach (var screen in Screen.AllScreens)
            if (screen.WorkingArea.IntersectsWith(spot))
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(x, y);
                return;
            }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        stereoTimer.Stop();
        if (stereo != null) { stereo.Dispose(); stereo = null; }      // puts the window back first
        if (WindowState == FormWindowState.Normal) WriteReg("SettingsPos", Left + "," + Top);
        jobs.CompleteAdding();
        // Closed without saving after previewing: put back what's saved.
        if (!saved && previewed) app.ApplySavedPicture(false);
        base.OnFormClosed(e);
    }

    // ---- helpers ----
    readonly BlockingCollection<Action> jobs = new BlockingCollection<Action>();
    void Run(Action job) { try { jobs.Add(job); } catch (InvalidOperationException) { } }

    void Ui(Action a)
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
    }

    // A slider with a box beside it for typing the number; the two follow each other.
    // scale: the slider works in whole steps, the box shows value / scale (gamma: 115 -> 1.15).
    static TrackBar Slider(Control parent, string label, int y, int min, int max, string unit, int scale = 1)
    {
        parent.Controls.Add(new Label { Text = label, Location = new Point(12, y + 6), AutoSize = true });
        var tb = new TrackBar { AutoSize = false, Location = new Point(110, y), Size = new Size(300, 28), Minimum = min, Maximum = max,
                                TickStyle = TickStyle.None, SmallChange = 1, LargeChange = 5 };
        int places = scale == 100 ? 2 : scale == 10 ? 1 : 0;
        var num = new NumericUpDown
        {
            Location = new Point(416, y + 3), Width = 62, DecimalPlaces = places, TextAlign = HorizontalAlignment.Right,
            Minimum = (decimal)min / scale, Maximum = (decimal)max / scale, Increment = 1m / scale, Value = (decimal)tb.Value / scale
        };
        ValidateTyping(num, min < 0, places);
        bool syncing = false;
        tb.ValueChanged += (s, e) => { if (syncing) return; syncing = true; num.Value = (decimal)tb.Value / scale; syncing = false; };
        num.ValueChanged += (s, e) =>
        {
            if (syncing) return;
            syncing = true;
            tb.Value = Math.Max(min, Math.Min(max, (int)Math.Round(num.Value * scale)));
            syncing = false;
        };
        tb.EnabledChanged += (s, e) => num.Enabled = tb.Enabled;
        parent.Controls.Add(tb);
        parent.Controls.Add(num);
        if (unit.Length > 0) parent.Controls.Add(new Label { Text = unit, Location = new Point(482, y + 6), AutoSize = true });
        return tb;
    }

    // Checks what's typed (or pasted) into a number box on every change, so it can only ever hold
    // a partial or complete number of the right shape: digits, a leading minus only if the range
    // has negatives, and at most `places` decimals. Anything else is undone at once (with a beep).
    // The range itself is enforced by the box when the number is committed (Enter / leaving it).
    static void ValidateTyping(NumericUpDown num, bool negative, int places)
    {
        var rx = new Regex("^" + (negative ? "-?" : "") + @"\d{0,3}" + (places > 0 ? @"(\.\d{0," + places + "})?" : "") + "$");
        TextBox edit = null;
        foreach (Control c in num.Controls) if (c is TextBox) edit = (TextBox)c;   // the box's text part
        if (edit == null) return;
        string lastGood = edit.Text;
        bool reverting = false;
        edit.TextChanged += (s, e) =>
        {
            if (reverting) return;
            if (rx.IsMatch(edit.Text)) { lastGood = edit.Text; return; }
            reverting = true;
            int caret = Math.Max(0, edit.SelectionStart - 1);
            edit.Text = lastGood;
            edit.SelectionStart = Math.Min(caret, edit.Text.Length);
            reverting = false;
            System.Media.SystemSounds.Beep.Play();
        };
    }

    static ComboBox Combo(Control parent, int x, int y, int width, string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(x, y), Width = width };
        c.Items.AddRange(items);
        parent.Controls.Add(c);
        return c;
    }

    static Label Note(Control parent, int x, int y, int w, int h, string text)
    {
        var l = new Label { Location = new Point(x, y), Size = new Size(w, h), Text = text, Tag = "note" };
        parent.Controls.Add(l);
        l.ForeColor = Text2;
        return l;
    }

    // A "Saved settings: [slot] [Save] [Restore]" row. Returns the slot combo. Slots 1-3 are the
    // user's; the 4th is "Default": the settings as they were when first captured, locked (it can
    // be restored but never saved over). Slot keys: <prefix>.1 .. .3 and <prefix>.default.
    ComboBox SlotRow(Control parent, int y, Action<string> save, Action<string> restore)
    {
        parent.Controls.Add(new Label { Text = "Saved settings", Location = new Point(12, y + 4), AutoSize = true });
        var slot = Combo(parent, 110, y, 200, new[] { "Slot 1", "Slot 2", "Slot 3", "Default (locked)" });
        slot.SelectedIndex = 0;
        var saveBtn = new Button { Text = "Save", Location = new Point(318, y - 1), Size = new Size(70, 26) };
        var restoreBtn = new Button { Text = "Restore", Location = new Point(394, y - 1), Size = new Size(80, 26) };
        saveBtn.Click += (s, e) => { if (slot.SelectedIndex < Slots) save(SlotKey(slot.SelectedIndex)); };
        restoreBtn.Click += (s, e) => restore(SlotKey(slot.SelectedIndex));
        slot.SelectedIndexChanged += (s, e) => saveBtn.Enabled = slot.SelectedIndex < Slots;    // Default is locked
        parent.Controls.Add(saveBtn);
        parent.Controls.Add(restoreBtn);
        return slot;
    }

    static string SlotKey(int index) { return index < Slots ? (index + 1).ToString(CultureInfo.InvariantCulture) : "default"; }
    static string SlotName(string key) { return key == "default" ? "the Default slot" : "slot " + key; }

    // Slot names with when each was saved (keys <prefix>.<n> and <prefix>.<n>.when).
    void RefreshSlots(ComboBox slot, string prefix)
    {
        int keep = Math.Max(0, slot.SelectedIndex);
        slot.Items.Clear();
        for (int i = 0; i <= Slots; i++)
        {
            string key = SlotKey(i);
            bool has = app.Cfg.Get(prefix + "." + key, "").Length > 0;
            string when = app.Cfg.Get(prefix + "." + key + ".when", "");
            string name = i < Slots ? "Slot " + key : "Default (locked)";
            slot.Items.Add(name + (has ? " - " + (i < Slots ? "saved " : "") + when : " - empty"));
        }
        slot.SelectedIndex = keep;
    }

    static string Now() { return DateTime.Now.ToString("d MMM HH:mm", CultureInfo.InvariantCulture); }

    // ================================================================ projector
    // The projector keeps separate picture memory for 2D and 3D: its own preset and its own
    // adjustments for each preset. Each tab edits one of them, and only while the projector is
    // showing that kind of picture (3D is detected by the 3D Depth item, which only answers in 3D).
    readonly ComboBox[] presetFor = new ComboBox[3];
    int[] savedPresets = new int[3];
    ComboBox preset3dPlay;
    int savedPreset3dPlay;

    static string[] PresetChoices()
    {
        string[] items = new string[PjPicture.Presets.Length + 1];
        items[0] = "(leave as it is)";
        Array.Copy(PjPicture.Presets, 0, items, 1, PjPicture.Presets.Length);
        return items;
    }

    void BuildPresetsFor2D(Control tab)
    {
        var presets = new GroupBox { Text = "Picture preset for each mode (set when switching)", Location = new Point(0, 0), Size = new Size(524, 112) };
        for (int i = 0; i < 3; i++)
        {
            presets.Controls.Add(new Label { Text = ModeSwitchApp.ModeLabel(Modes[i]), Location = new Point(12, 26 + i * 27), AutoSize = true });
            presetFor[i] = Combo(presets, 110, 22 + i * 27, 200, PresetChoices());
            int v = app.Cfg.GetInt("pj." + Modes[i] + ".preset", -1);
            savedPresets[i] = v;
            presetFor[i].SelectedIndex = v >= 0 && v < PjPicture.Presets.Length ? v + 1 : 0;
        }
        tab.Controls.Add(presets);
    }

    void BuildPresetFor3D(Control tab)
    {
        var box = new GroupBox { Text = "3D picture preset (set when a 3D film starts with Play 3D...)", Location = new Point(0, 0), Size = new Size(524, 60) };
        box.Controls.Add(new Label { Text = "3D films", Location = new Point(12, 28), AutoSize = true });
        preset3dPlay = Combo(box, 110, 24, 200, PresetChoices());
        savedPreset3dPlay = app.Cfg.GetInt("pj.3dplay.preset", -1);
        preset3dPlay.SelectedIndex = savedPreset3dPlay >= 0 && savedPreset3dPlay < PjPicture.Presets.Length ? savedPreset3dPlay + 1 : 0;
        tab.Controls.Add(box);
    }

    string PjIp { get { return app.Cfg.Get("projector.ip", ""); } }
    string PjCom { get { return app.Cfg.Get("projector.community", "SONY"); } }

    sealed class PjEditor
    {
        readonly SettingsForm f;
        readonly bool threeD;
        ComboBox showing, colourTemp, gammaBox, slot;
        Label status;
        readonly Dictionary<int, TrackBar> sliders = new Dictionary<int, TrackBar>();
        readonly List<Control> live = new List<Control>();
        bool loading;
        volatile int shown = -1;                  // preset on screen, as last read

        static readonly int[] SliderItems = { PjPicture.Contrast, PjPicture.Brightness, PjPicture.Colour, PjPicture.Hue, PjPicture.Sharpness,
                                              PjPicture.GainR, PjPicture.GainG, PjPicture.GainB, PjPicture.BiasR, PjPicture.BiasG, PjPicture.BiasB };
        // Everything a slot holds, in the order it's restored (plus 3D Depth in 3D).
        static readonly int[] Items2D = { PjPicture.ColourTemp, PjPicture.Gamma, PjPicture.Contrast, PjPicture.Brightness, PjPicture.Colour,
                                          PjPicture.Hue, PjPicture.Sharpness, PjPicture.GainR, PjPicture.GainG, PjPicture.GainB,
                                          PjPicture.BiasR, PjPicture.BiasG, PjPicture.BiasB };
        int[] SlotItems
        {
            get
            {
                if (!threeD) return Items2D;
                var all = new List<int>(Items2D) { PjPicture.Depth3D };
                return all.ToArray();
            }
        }
        string SlotPrefix { get { return (threeD ? "pj3dslot." : "pjslot.") + shown; } }
        string Kind { get { return threeD ? "3D" : "2D"; } }

        public PjEditor(SettingsForm f, bool threeD) { this.f = f; this.threeD = threeD; }

        public void Build(Control tab, int top)
        {
            var adjust = new GroupBox
            {
                Text = "Adjust the projector's " + Kind + " picture (kept by the projector in the preset showing)",
                Location = new Point(0, top), Size = new Size(524, threeD ? 540 : 500)
            };
            adjust.Controls.Add(new Label { Text = "Showing now", Location = new Point(12, 28), AutoSize = true });
            showing = Combo(adjust, 110, 24, 200, PjPicture.Presets);
            var readAgain = new Button { Text = "Read again", Location = new Point(318, 23), Size = new Size(90, 26) };
            readAgain.Click += (s, e) => f.Run(Read);
            adjust.Controls.Add(readAgain);
            status = Note(adjust, 12, 56, 500, 18, "Reading the projector...");

            string[] names = { "Contrast", "Brightness", "Colour", "Hue", "Sharpness" };
            for (int i = 0; i < 5; i++) AddSlider(adjust, SliderItems[i], names[i], 80 + i * 30, 0, 100);

            adjust.Controls.Add(new Label { Text = "Colour temp", Location = new Point(12, 236), AutoSize = true });
            colourTemp = Combo(adjust, 110, 232, 110, PjPicture.ColourTempNames);
            adjust.Controls.Add(new Label { Text = "Gamma", Location = new Point(240, 236), AutoSize = true });
            gammaBox = Combo(adjust, 300, 232, 110, PjPicture.GammaNames);

            string[] rgb = { "Gain red", "Gain green", "Gain blue", "Bias red", "Bias green", "Bias blue" };
            for (int i = 0; i < 6; i++) AddSlider(adjust, SliderItems[5 + i], rgb[i], 264 + i * 30, -30, 30);
            int y = 444;
            if (threeD) { AddSlider(adjust, PjPicture.Depth3D, "3D depth", y, -2, 2); y += 40; }

            slot = f.SlotRow(adjust, y + 8, SaveSlot, RestoreSlot);
            Note(adjust, 12, y + 34, 500, 18, "Slots belong to the " + Kind + " preset showing now; Restore sends them to the projector.");
            tab.Controls.Add(adjust);

            live.AddRange(new Control[] { showing, colourTemp, gammaBox });
            foreach (var tb in sliders.Values) live.Add(tb);
            SetLive(false);

            showing.SelectedIndexChanged += (s, e) =>
            {
                if (loading) return;
                int preset = showing.SelectedIndex;
                f.Run(() => { Set(PjPicture.Preset, preset); Thread.Sleep(800); Read(); });   // values differ per preset
            };
            colourTemp.SelectedIndexChanged += (s, e) => { if (!loading && colourTemp.SelectedIndex >= 0) Queue(PjPicture.ColourTemp, PjPicture.ColourTempValues[colourTemp.SelectedIndex]); };
            gammaBox.SelectedIndexChanged += (s, e) => { if (!loading && gammaBox.SelectedIndex >= 0) Queue(PjPicture.Gamma, PjPicture.GammaValues[gammaBox.SelectedIndex]); };
        }

        void AddSlider(Control parent, int item, string label, int y, int min, int max)
        {
            var tb = Slider(parent, label, y, min, max, "");
            sliders[item] = tb;
            tb.ValueChanged += (s, e) => { if (!loading) Queue(item, tb.Value); };
        }

        void SetLive(bool on) { foreach (var c in live) c.Enabled = on; }

        void Status(string text, bool error)
        {
            f.Ui(() => { status.Text = text; status.ForeColor = error ? Error : Text2; });
        }

        // Is the projector on and showing this editor's kind of picture? Explains why not, if not.
        bool Ready()
        {
            string ip = f.PjIp;
            if (ip.Length == 0) { Status("No projector configured (projector.ip)", false); return false; }
            int power;
            string err = Pj.Get(ip, f.PjCom, Pj.ItemPower, out power);
            if (err != null || power != 3)
            {
                Status(err != null ? "Projector: " + err : "The projector is " + (power == 0 ? "in standby" : power <= 2 ? "starting up" : "cooling down")
                    + " - switch it on to adjust its picture", false);
                return false;
            }
            int depth;
            bool in3D = Pj.Get(ip, f.PjCom, PjPicture.Depth3D, out depth) == null;
            if (in3D != threeD)
            {
                Status(threeD ? "The projector is showing 2D. Start a 3D film (Play 3D...) to adjust its 3D picture."
                              : "The projector is showing 3D - its 3D picture is on the Projector 3D tab.", false);
                return false;
            }
            return true;
        }

        // Worker thread: read every item and fill the controls.
        public void Read()
        {
            if (!Ready()) { f.Ui(() => SetLive(false)); return; }
            var values = ReadItems();
            int p;
            shown = values.TryGetValue(PjPicture.Preset, out p) ? p : -1;
            f.Ui(() =>
            {
                loading = true;
                foreach (var kv in sliders)
                {
                    int v;
                    if (values.TryGetValue(kv.Key, out v)) kv.Value.Value = Math.Max(kv.Value.Minimum, Math.Min(kv.Value.Maximum, v));
                    kv.Value.Enabled = values.ContainsKey(kv.Key);
                }
                int pv;
                showing.SelectedIndex = shown >= 0 && shown < PjPicture.Presets.Length ? shown : -1;
                colourTemp.SelectedIndex = values.TryGetValue(PjPicture.ColourTemp, out pv) ? Array.IndexOf(PjPicture.ColourTempValues, pv) : -1;
                gammaBox.SelectedIndex = values.TryGetValue(PjPicture.Gamma, out pv) ? Array.IndexOf(PjPicture.GammaValues, pv) : -1;
                showing.Enabled = colourTemp.Enabled = gammaBox.Enabled = true;
                f.RefreshSlots(slot, SlotPrefix);
                loading = false;
            });
            Status(values.ContainsKey(PjPicture.Contrast) ? "Changes go to the projector straight away and stay in this " + Kind + " preset."
                                                          : "Picture items can't be adjusted with the current signal", !values.ContainsKey(PjPicture.Contrast));
        }

        Dictionary<int, int> ReadItems()
        {
            var values = new Dictionary<int, int>();
            var all = new List<int>(SlotItems) { PjPicture.Preset };
            foreach (int item in all)
            {
                int v;
                if (Pj.Get(f.PjIp, f.PjCom, item, out v) == null) values[item] = PjPicture.Signed(v);
            }
            return values;
        }

        // Slot = "0017:1,0022:4,0010:99,..." for the preset showing now.
        void SaveSlot(string n)
        {
            f.Run(() =>
            {
                if (!Ready() || shown < 0) return;
                var values = ReadItems();
                var parts = new List<string>();
                foreach (int item in SlotItems)
                {
                    int v;
                    if (values.TryGetValue(item, out v)) parts.Add(item.ToString("X4") + ":" + v.ToString(CultureInfo.InvariantCulture));
                }
                if (parts.Count == 0) { Status("Nothing to save - the projector didn't answer", true); return; }
                string prefix = SlotPrefix;
                int preset = shown;
                f.Ui(() =>
                {
                    f.app.SaveSettingsNow(new Dictionary<string, string> { { prefix + "." + n, string.Join(",", parts.ToArray()) }, { prefix + "." + n + ".when", Now() } });
                    f.RefreshSlots(slot, prefix);
                    Status("Saved " + Kind + " " + PjPicture.PresetName(preset) + " to " + SlotName(n), false);
                });
            });
        }

        void RestoreSlot(string n)
        {
            f.Run(() =>
            {
                if (!Ready()) return;                        // never write 3D values into 2D memory or back
                string data = f.app.Cfg.Get(SlotPrefix + "." + n, "");
                if (data.Length == 0) { Status(SlotName(n) + " is empty for this " + Kind + " preset", true); return; }
                string err = null;
                foreach (string part in data.Split(','))
                {
                    string[] kv = part.Split(':');
                    int item, value;
                    if (kv.Length != 2 || !int.TryParse(kv[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out item)
                        || !int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) continue;
                    int code;
                    string e = Pj.Set(f.PjIp, f.PjCom, item, value, out code);
                    if (e != null && err == null) err = e;
                }
                Read();
                Status(err == null ? "Restored " + SlotName(n) : "Restored " + SlotName(n) + ", but some items weren't accepted (" + err + ")", err != null);
            });
        }

        // Slider moves are coalesced per item: only the latest value is sent.
        readonly Dictionary<int, int> pending = new Dictionary<int, int>();

        void Queue(int item, int value)
        {
            bool queue;
            lock (pending) { queue = pending.Count == 0; pending[item] = value; }
            if (queue) f.Run(Flush);
        }

        void Flush()
        {
            while (true)
            {
                KeyValuePair<int, int> next;
                lock (pending)
                {
                    if (pending.Count == 0) return;
                    var e = pending.GetEnumerator(); e.MoveNext(); next = e.Current;
                    pending.Remove(next.Key);
                }
                Set(next.Key, next.Value);
            }
        }

        void Set(int item, int value)
        {
            int code;
            string err = Pj.Set(f.PjIp, f.PjCom, item, value, out code);
            if (err != null) Status("Not accepted: " + err, true);
        }
    }

    // ================================================================ GPU colour
    ComboBox gpuMode, gpuRoom, gpuChannel, gpuSlot;
    CheckBox gpuCustom, gpuPreview;
    TrackBar gpuBrightness, gpuContrast, gpuGamma, gpuVibrance, gpuHue;
    Label gpuStatus;
    readonly Dictionary<string, GpuColour.Values> gpu = new Dictionary<string, GpuColour.Values>();
    readonly Dictionary<string, bool> gpuOn = new Dictionary<string, bool>();
    bool gpuLoading;

    string GpuKey { get { return Modes[gpuMode.SelectedIndex] + "|" + ModeSwitchApp.ColourRooms[gpuRoom.SelectedIndex]; } }
    string GpuSlotPrefix { get { return "gpuslot." + Modes[gpuMode.SelectedIndex] + "." + ModeSwitchApp.ColourRooms[gpuRoom.SelectedIndex]; } }

    void BuildGpuTab(Control tab)
    {
        foreach (string m in Modes)
            foreach (string r in ModeSwitchApp.ColourRooms)
            {
                bool custom;
                gpu[m + "|" + r] = app.GpuValues(m, r, out custom);
                gpuOn[m + "|" + r] = custom;
            }

        var box = new GroupBox { Text = "Colour per mode and display (applied when switching)", Location = new Point(0, 0), Size = new Size(524, 620) };
        tab.Controls.Add(box);

        box.Controls.Add(new Label { Text = "Mode", Location = new Point(12, 30), AutoSize = true });
        gpuMode = Combo(box, 110, 26, 140, Array.ConvertAll(Modes, ModeSwitchApp.ModeLabel));
        box.Controls.Add(new Label { Text = "Display", Location = new Point(270, 30), AutoSize = true });
        string[] rooms = Array.ConvertAll(ModeSwitchApp.ColourRooms, r => (r == "tv" ? "TV" : "Projector") + " (" + app.RoomDisplays(r)[0] + ")");
        gpuRoom = Combo(box, 330, 26, 170, rooms);

        gpuCustom = new CheckBox { Text = "Custom colour for this display in this mode (otherwise neutral)", Location = new Point(12, 62), AutoSize = true };
        box.Controls.Add(gpuCustom);
        box.Controls.Add(new Label { Text = "Colour channel", Location = new Point(12, 96), AutoSize = true });
        gpuChannel = Combo(box, 110, 92, 140, new[] { "All channels", "Red", "Green", "Blue" });

        gpuBrightness = Slider(box, "Brightness", 124, 0, 100, "%");
        gpuContrast = Slider(box, "Contrast", 154, 0, 100, "%");
        gpuGamma = Slider(box, "Gamma", 184, 30, 280, "", 100);
        gpuVibrance = Slider(box, "Digital vibrance", 224, 0, 100, "%");
        gpuHue = Slider(box, "Hue", 254, 0, 359, "°");

        var reset = new Button { Text = "Reset to neutral", Location = new Point(110, 292), Size = new Size(130, 26) };
        box.Controls.Add(reset);
        gpuSlot = SlotRow(box, 330, SaveGpuSlot, RestoreGpuSlot);
        Note(box, 12, 356, 500, 18, "Restore loads a slot into the controls above; OK makes it the saved setting.");

        gpuPreview = new CheckBox { Text = "Preview on screen (for the current mode, while this display is in use)", Location = new Point(12, 386), AutoSize = true, Checked = true };
        box.Controls.Add(gpuPreview);
        gpuStatus = Note(box, 12, 414, 500, 36, "");
        Note(box, 12, 458, 500, 150,
            "Brightness, contrast and gamma work through the display's gamma ramp, like NVIDIA Control Panel's "
            + "desktop colour settings, so they apply to films and games too. They have no effect while Windows HDR "
            + "is on. Digital vibrance and hue always apply.\n\n"
            + "A display that has no custom colour in any mode is left alone, so settings made in NVIDIA Control "
            + "Panel stay. Once one mode has custom colour, the other modes use neutral on that display.");

        gpuMode.SelectedIndex = Math.Max(0, Array.IndexOf(Modes, app.CurrentMode));
        gpuRoom.SelectedIndex = Math.Max(0, Array.IndexOf(ModeSwitchApp.ColourRooms, app.CurrentRoom));
        gpuChannel.SelectedIndex = 0;
        LoadGpu();

        gpuMode.SelectedIndexChanged += (s, e) => LoadGpu();
        gpuRoom.SelectedIndexChanged += (s, e) => LoadGpu();
        gpuChannel.SelectedIndexChanged += (s, e) => LoadGpu();
        gpuCustom.CheckedChanged += (s, e) => { if (!gpuLoading) { gpuOn[GpuKey] = gpuCustom.Checked; SetGpuEnabled(); Preview(); } };
        foreach (var tb in new[] { gpuBrightness, gpuContrast, gpuGamma, gpuVibrance, gpuHue })
            tb.ValueChanged += (s, e) => { if (!gpuLoading) { StoreGpu(); Preview(); } };
        reset.Click += (s, e) => { gpu[GpuKey] = new GpuColour.Values(); LoadGpu(); Preview(); };
        gpuPreview.CheckedChanged += (s, e) => { if (gpuPreview.Checked) Preview(); else if (previewed) app.ApplySavedPicture(false); };
    }

    // Which channels the sliders edit: 0..2, or all three.
    int[] Channels { get { int c = gpuChannel.SelectedIndex; return c <= 0 ? new[] { 0, 1, 2 } : new[] { c - 1 }; } }

    void LoadGpu()
    {
        gpuLoading = true;
        var v = gpu[GpuKey];
        int c = Channels[0];
        gpuBrightness.Value = v.Brightness[c];
        gpuContrast.Value = v.Contrast[c];
        gpuGamma.Value = Math.Max(30, Math.Min(280, v.Gamma[c]));
        gpuVibrance.Value = v.Vibrance;
        gpuHue.Value = ((v.Hue % 360) + 360) % 360;
        gpuCustom.Checked = gpuOn[GpuKey];
        SetGpuEnabled();
        RefreshSlots(gpuSlot, GpuSlotPrefix);
        gpuLoading = false;
    }

    void SetGpuEnabled()
    {
        foreach (Control c in new Control[] { gpuChannel, gpuBrightness, gpuContrast, gpuGamma, gpuVibrance, gpuHue })
            c.Enabled = gpuCustom.Checked;
    }

    void StoreGpu()
    {
        var v = gpu[GpuKey];
        foreach (int c in Channels)
        {
            v.Brightness[c] = gpuBrightness.Value;
            v.Contrast[c] = gpuContrast.Value;
            v.Gamma[c] = gpuGamma.Value;
        }
        v.Vibrance = gpuVibrance.Value;
        v.Hue = gpuHue.Value;
    }

    // Slot = "custom|brightness r,g,b|contrast r,g,b|gamma r,g,b|vibrance|hue"
    void SaveGpuSlot(string n)
    {
        var v = gpu[GpuKey];
        string data = string.Join("|", new[] { gpuOn[GpuKey] ? "custom" : "neutral", GpuColour.Values.Triple(v.Brightness),
            GpuColour.Values.Triple(v.Contrast), GpuColour.Values.Triple(v.Gamma),
            v.Vibrance.ToString(CultureInfo.InvariantCulture), v.Hue.ToString(CultureInfo.InvariantCulture) });
        string prefix = GpuSlotPrefix;
        app.SaveSettingsNow(new Dictionary<string, string> { { prefix + "." + n, data }, { prefix + "." + n + ".when", Now() } });
        RefreshSlots(gpuSlot, prefix);
        GpuStatus("Saved to " + SlotName(n), false);
    }

    void RestoreGpuSlot(string n)
    {
        string[] p = app.Cfg.Get(GpuSlotPrefix + "." + n, "").Split('|');
        if (p.Length < 6) { GpuStatus(SlotName(n) + " is empty for this mode and display", true); return; }
        var v = new GpuColour.Values
        {
            Brightness = GpuColour.Values.Triple(p[1], 50),
            Contrast = GpuColour.Values.Triple(p[2], 50),
            Gamma = GpuColour.Values.Triple(p[3], 100)
        };
        int.TryParse(p[4], out v.Vibrance);
        int.TryParse(p[5], out v.Hue);
        gpu[GpuKey] = v;
        gpuOn[GpuKey] = p[0] == "custom";
        LoadGpu();
        Preview();
        if (!gpuPreview.Checked || Modes[gpuMode.SelectedIndex] != app.CurrentMode) GpuStatus("Restored " + SlotName(n) + " - OK saves it", false);
    }

    void GpuStatus(string text, bool error)
    {
        gpuStatus.Text = text;
        gpuStatus.ForeColor = error ? Error : Text2;
    }

    // Shows the edited colour on screen, if it's for the current mode and the display is in use.
    void Preview()
    {
        if (!gpuPreview.Checked) return;
        string m = Modes[gpuMode.SelectedIndex], r = ModeSwitchApp.ColourRooms[gpuRoom.SelectedIndex];
        if (m != app.CurrentMode) { GpuStatus("Preview shows only for the current mode (" + ModeSwitchApp.ModeLabel(app.CurrentMode) + ").", false); return; }
        foreach (string name in app.RoomDisplays(r))
        {
            string monitor;
            string gdi = Disp.FindDisplay(name, out monitor);
            if (gdi == null) continue;
            bool hdr = false;
            foreach (var t in Disp.Targets()) if (t.Name.Trim() == monitor && t.HdrOn) hdr = true;
            var v = gpuOn[GpuKey] ? gpu[GpuKey] : new GpuColour.Values();
            string err = GpuColour.Apply(gdi, v, hdr);
            previewed = true;
            GpuStatus(err != null ? "Preview: " + err : "Previewing on " + monitor + (hdr ? " (HDR is on: only vibrance and hue show)" : ""), err != null);
            return;
        }
        GpuStatus("That display isn't in use right now - the colour is applied when it is.", false);
    }

    // ================================================================ save
    void Save()
    {
        var values = new Dictionary<string, string>();
        bool presetChanged = false;
        for (int i = 0; i < 3; i++)
        {
            int v = presetFor[i].SelectedIndex - 1;           // -1 = leave as it is (explicit, so 3D doesn't inherit Movie's)
            values["pj." + Modes[i] + ".preset"] = v.ToString(CultureInfo.InvariantCulture);
            if (v != savedPresets[i] && Modes[i] == app.CurrentMode) presetChanged = true;
            savedPresets[i] = v;
        }
        savedPreset3dPlay = preset3dPlay.SelectedIndex - 1;
        values["pj.3dplay.preset"] = savedPreset3dPlay.ToString(CultureInfo.InvariantCulture);
        foreach (string m in Modes)
            foreach (string r in ModeSwitchApp.ColourRooms)
            {
                string k = "gpu." + m + "." + r + ".";
                var v = gpu[m + "|" + r];
                values[k + "custom"] = gpuOn[m + "|" + r] ? "true" : "false";
                values[k + "brightness"] = GpuColour.Values.Triple(v.Brightness);
                values[k + "contrast"] = GpuColour.Values.Triple(v.Contrast);
                values[k + "gamma"] = GpuColour.Values.Triple(v.Gamma);
                values[k + "vibrance"] = v.Vibrance.ToString(CultureInfo.InvariantCulture);
                values[k + "hue"] = v.Hue.ToString(CultureInfo.InvariantCulture);
            }
        try
        {
            app.SaveSettingsNow(values);
            app.ApplySavedPicture(presetChanged);
            previewed = false;
        }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't save settings.ini: " + ex.Message, "ModeSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
