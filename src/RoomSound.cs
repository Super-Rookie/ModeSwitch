// RoomSound.cs - the tray menu's "Volume" submenu: TV volume, mute and speakers/headphones, and
// the receiver's volume and mute, with sliders hosted right in the menu. Levels are read in the
// background when the submenu opens (the TV or receiver may be off, which takes a moment to tell).
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

sealed class RoomSoundMenu
{
    readonly Func<string> tvIp, avrIp;
    readonly Func<int> avrCap;                // highest receiver volume the slider allows (0 = its own max)
    readonly Func<LgTv, string> openTv;       // connects with the stored pairing key
    readonly Control ui;                      // marshals results back to the UI thread

    // All device calls run on one worker thread, in order, so slider moves never overlap.
    readonly BlockingCollection<Action> jobs = new BlockingCollection<Action>();
    LgTv tv;                                  // open while the submenu is, owned by the worker

    // Controls of the submenu currently built (the tray menu is rebuilt every time it opens).
    ToolStripMenuItem tvHeader, tvMute, avrHeader, avrMute;
    ToolStripMenuItem[] tvOutputs;
    TrackBar tvSlider, avrSlider;
    Label tvValue, avrValue;
    bool loading;                             // populating the controls: don't send anything back
    bool tvMuted, avrMuted;

    static readonly string[,] Outputs = { { "tv_speaker", "TV speakers" }, { "headphone", "Wired headphones" } };

    public RoomSoundMenu(Func<string> tvIp, Func<string> avrIp, Func<int> avrCap, Func<LgTv, string> openTv, Control ui)
    {
        this.tvIp = tvIp; this.avrIp = avrIp; this.avrCap = avrCap; this.openTv = openTv; this.ui = ui;
        var worker = new Thread(() =>
        {
            foreach (Action job in jobs.GetConsumingEnumerable())
            {
                try { job(); } catch { }
            }
        }) { IsBackground = true, Name = "RoomSound" };
        worker.Start();
    }

    void Run(Action job) { jobs.Add(job); }

    void Ui(Action a)
    {
        try { ui.BeginInvoke(a); } catch { }
    }

    public ToolStripMenuItem Build()
    {
        var root = new ToolStripMenuItem("Volume");
        var items = root.DropDownItems;

        tvHeader = Header("TV: reading...");
        items.Add(tvHeader);
        items.Add(SliderRow(out tvSlider, out tvValue, 0, 100));
        tvMute = new ToolStripMenuItem("Mute TV", null, (s, e) => { bool m = !tvMuted; Run(() => { if (TvCall(t => t.SetMute(m), "mute")) tvMuted = m; }); });
        items.Add(tvMute);
        // Output choices listed right here under a heading, not in a submenu of their own.
        items.Add(Header("TV sound output:"));
        tvOutputs = new ToolStripMenuItem[Outputs.GetLength(0)];
        for (int i = 0; i < tvOutputs.Length; i++)
        {
            string id = Outputs[i, 0];
            tvOutputs[i] = new ToolStripMenuItem(Outputs[i, 1], null, (s, e) => Run(() => TvCall(t => t.SetSoundOutput(id), "output")));
            items.Add(tvOutputs[i]);
        }
        items.Add(new ToolStripSeparator());

        avrHeader = Header("Receiver: reading...");
        items.Add(avrHeader);
        items.Add(SliderRow(out avrSlider, out avrValue, 0, 55));
        avrMute = new ToolStripMenuItem("Mute receiver", null, (s, e) => { bool m = !avrMuted; Run(() => { if (AvrResult(SonyAvr.SetMute(avrIp(), m), "mute")) avrMuted = m; }); });
        items.Add(avrMute);

        SetTv(false, tvIp().Length == 0 ? "TV: not configured (tv.ip)" : "TV: reading...");
        SetAvr(false, avrIp().Length == 0 ? "Receiver: not configured (avr.ip)" : "Receiver: reading...");

        tvSlider.ValueChanged += (s, e) => { tvValue.Text = tvSlider.Value.ToString(); if (!loading) QueueTvVolume(); };
        avrSlider.ValueChanged += (s, e) => { avrValue.Text = avrSlider.Value.ToString(); if (!loading) QueueAvrVolume(); };

        root.DropDownOpening += (s, e) => { Run(ReadTv); Run(ReadAvr); };
        root.DropDownClosed += (s, e) => Run(() => { if (tv != null) { tv.Dispose(); tv = null; } });
        return root;
    }

    static ToolStripMenuItem Header(string text)
    {
        return new ToolStripMenuItem(text) { Enabled = false };
    }

    // A slider with its value, hosted as a menu row, and under it a scale with a notch every 5.
    // Clicking the scale jumps to the nearest notch.
    static ToolStripControlHost SliderRow(out TrackBar slider, out Label value, int min, int max)
    {
        var panel = new Panel { Size = new Size(360, 52), BackColor = SystemColors.Menu, Margin = Padding.Empty };
        var tb = new TrackBar { AutoSize = false, Location = new Point(4, 2), Size = new Size(306, 28), Minimum = min, Maximum = max,
                                TickStyle = TickStyle.None, LargeChange = 5, SmallChange = 1, BackColor = SystemColors.Menu };
        value = new Label { Location = new Point(314, 8), Size = new Size(42, 18), TextAlign = ContentAlignment.MiddleLeft,
                            Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        var scale = new Scale(tb) { Location = new Point(4, 30), Size = new Size(306, 20), BackColor = SystemColors.Menu };
        tb.ValueChanged += (s, e) => scale.Invalidate();
        tb.EnabledChanged += (s, e) => scale.Invalidate();
        tb.Resize += (s, e) => scale.Invalidate();
        panel.Controls.Add(tb);
        panel.Controls.Add(value);
        panel.Controls.Add(scale);
        slider = tb;
        var host = new ToolStripControlHost(panel) { AutoSize = false, Size = panel.Size, Margin = new Padding(0, 2, 0, 2) };

        // Clicking the slider gives it keyboard focus, which suspends the menu's hover tracking
        // until focus returns to the menu - so hand it straight back once the mouse is released.
        MouseEventHandler refocus = (s, e) =>
        {
            ToolStrip menu = host.GetCurrentParent();
            if (menu != null && !menu.IsDisposed) menu.BeginInvoke((Action)(() => { if (!menu.IsDisposed) menu.Focus(); }));
        };
        tb.MouseUp += refocus;
        scale.MouseUp += refocus;
        return host;
    }

    // The notches under a slider, lined up with the slider's own channel.
    sealed class Scale : Control
    {
        const int Step = 5;
        readonly TrackBar tb;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref RECT r);
        const int TBM_GETTHUMBRECT = 0x419, TBM_GETCHANNELRECT = 0x41A;

        public Scale(TrackBar tb)
        {
            this.tb = tb;
            SetStyle(ControlStyles.Selectable, false);        // clicking the scale shouldn't take focus
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI", 7f);
        }

        // Pixel range the thumb's centre travels over, in this control's coordinates.
        void Track(out int left, out int right)
        {
            left = 13; right = tb.Width - 13;                 // fallback before the slider has a handle
            if (!tb.IsHandleCreated) return;
            RECT ch = new RECT(), th = new RECT();
            SendMessage(tb.Handle, TBM_GETCHANNELRECT, IntPtr.Zero, ref ch);
            SendMessage(tb.Handle, TBM_GETTHUMBRECT, IntPtr.Zero, ref th);
            int half = (th.Right - th.Left) / 2;
            left = ch.Left + half + tb.Left - Left;
            right = ch.Right - half + tb.Left - Left;
        }

        int X(int v, int left, int right)
        {
            int span = Math.Max(1, tb.Maximum - tb.Minimum);
            return left + (int)Math.Round((double)(v - tb.Minimum) * (right - left) / span);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            int left, right;
            Track(out left, out right);
            var g = e.Graphics;
            int first = (tb.Minimum + Step - 1) / Step * Step;
            // Label every notch if the numbers fit, otherwise every other one (all stay clickable).
            int widest = TextRenderer.MeasureText(tb.Maximum.ToString(), Font).Width;
            int gap = X(first + Step, left, right) - X(first, left, right);
            int labelEvery = gap >= widest ? Step : Step * 2;
            int current = (int)Math.Round(tb.Value / (double)Step) * Step;
            using (var tick = new Pen(SystemColors.GrayText))
                for (int v = first; v <= tb.Maximum; v += Step)
                {
                    int x = X(v, left, right);
                    bool labelled = v % labelEvery == 0;
                    g.DrawLine(tick, x, 0, x, labelled ? 4 : 2);
                    if (!labelled) continue;
                    string s = v.ToString();
                    Size sz = TextRenderer.MeasureText(s, Font);
                    TextRenderer.DrawText(g, s, Font, new Point(x - sz.Width / 2, 5),
                        v == current && tb.Enabled ? SystemColors.MenuText : SystemColors.GrayText);
                }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (!tb.Enabled) return;
            int left, right;
            Track(out left, out right);
            double v = tb.Minimum + (double)(e.X - left) * (tb.Maximum - tb.Minimum) / Math.Max(1, right - left);
            int snapped = (int)Math.Round(v / Step) * Step;
            tb.Value = Math.Max(tb.Minimum, Math.Min(tb.Maximum, snapped));
        }
    }

    void SetTv(bool on, string status)
    {
        if (tvHeader == null) return;
        tvHeader.Text = status;
        tvSlider.Enabled = tvMute.Enabled = on;
        foreach (var o in tvOutputs) o.Enabled = on;
        if (!on) tvValue.Text = "";
    }

    void SetAvr(bool on, string status)
    {
        if (avrHeader == null) return;
        avrHeader.Text = status;
        avrSlider.Enabled = avrMute.Enabled = on;
        if (!on) avrValue.Text = "";
    }

    // ---- TV (worker thread) ----
    LgTv Tv()
    {
        if (tv != null) return tv;
        string ip = tvIp();
        if (ip.Length == 0 || !LgTv.IsUp(ip, 1500)) return null;
        var t = new LgTv(ip);
        if (openTv(t) != null) { t.Dispose(); return null; }
        return tv = t;
    }

    // Runs a TV call, reconnecting once if the connection dropped. Shows a failure in the header.
    bool TvCall(Func<LgTv, string> call, string what)
    {
        string err = "off or not reachable";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            LgTv t = Tv();
            if (t == null) break;
            err = call(t);
            if (err == null) return true;
            tv.Dispose(); tv = null;                             // stale connection: try a fresh one
        }
        string msg = "TV: " + what + " failed - " + err;
        Ui(() => { if (tvHeader != null) tvHeader.Text = msg; });
        return false;
    }

    void ReadTv()
    {
        if (tvIp().Length == 0) return;
        LgTv t = Tv();
        if (t == null) { Ui(() => SetTv(false, "TV: off or not reachable")); return; }
        int vol; bool muted; string output;
        string err = t.GetVolume(out vol, out muted);
        t.GetSoundOutput(out output);
        Ui(() =>
        {
            if (tvHeader == null) return;
            if (err != null) { SetTv(false, "TV: volume unavailable - " + err); return; }
            loading = true;
            tvSlider.Value = Math.Max(0, Math.Min(100, vol));
            tvValue.Text = tvSlider.Value.ToString();
            loading = false;
            tvMuted = muted;
            tvMute.Checked = muted;
            string outLabel = output;
            for (int i = 0; i < tvOutputs.Length; i++)
            {
                tvOutputs[i].Checked = Outputs[i, 0] == output;
                if (tvOutputs[i].Checked) outLabel = Outputs[i, 1];
            }
            SetTv(true, "TV" + (outLabel != null ? " - " + outLabel : "") + (muted ? " (muted)" : ""));
        });
    }

    // Slider moves are coalesced: only the latest position is sent once the worker gets to it.
    volatile bool tvQueued, avrQueued;
    volatile int tvWanted, avrWanted;

    void QueueTvVolume()
    {
        tvWanted = tvSlider.Value;
        if (tvQueued) return;
        tvQueued = true;
        Run(() => { tvQueued = false; int v = tvWanted; TvCall(t => t.SetVolume(v), "volume"); });
    }

    // ---- receiver (worker thread) ----
    void ReadAvr()
    {
        string ip = avrIp();
        if (ip.Length == 0) return;
        // With Network Standby on it still answers (volume included) while off, so check its power.
        bool on;
        string perr = SonyAvr.GetPower(ip, out on);
        if (perr == null && !on) { Ui(() => SetAvr(false, "Receiver: standby")); return; }
        int vol = 0, min = 0, max = 0; bool muted = false;
        string err = perr ?? SonyAvr.GetVolume(ip, out vol, out min, out max, out muted);
        Ui(() =>
        {
            if (avrHeader == null) return;
            if (err != null) { SetAvr(false, err.StartsWith("not reachable") ? "Receiver: off or not reachable" : "Receiver: volume unavailable - " + err); return; }
            loading = true;
            int cap = avrCap();
            int top = cap > 0 ? Math.Min(max, cap) : max;
            avrSlider.Minimum = min;
            avrSlider.Maximum = Math.Max(top, Math.Min(vol, max));   // never hide the current level
            avrSlider.Value = Math.Max(min, Math.Min(avrSlider.Maximum, vol));
            avrValue.Text = avrSlider.Value.ToString();
            avrSlider.Parent.Invalidate(true);                       // the scale follows the new range
            loading = false;
            avrMuted = muted;
            avrMute.Checked = muted;
            SetAvr(true, "Receiver" + (muted ? " (muted)" : ""));
        });
    }

    bool AvrResult(string err, string what)
    {
        if (err == null) return true;
        string msg = "Receiver: " + what + " failed - " + err;
        Ui(() => { if (avrHeader != null) avrHeader.Text = msg; });
        return false;
    }

    void QueueAvrVolume()
    {
        avrWanted = avrSlider.Value;
        if (avrQueued) return;
        avrQueued = true;
        Run(() => { avrQueued = false; int v = avrWanted; AvrResult(SonyAvr.SetVolume(avrIp(), v), "volume"); });
    }
}
