// Stereo.cs - shows a window readably while the projector is in frame-compatible 3D.
// In 3D the projector splits each frame between the eyes and stretches each half back to full
// size: Over-Under (TAB) gives the top half to the left eye and the bottom half to the right,
// Side-by-Side gives the left and right halves. A normal window would be cut in two and stretched.
// So the real window is moved off screen, and two copies squashed to half height (TAB) or half
// width (SBS) are shown, one in each half: each eye sees one copy stretched back to normal.
// Mouse input on either copy is mapped back onto the real window's controls.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

sealed class StereoMirror : IDisposable
{
    public const int SideBySide = 1, OverUnder = 2;      // the projector's 3D format values

    readonly Form source;
    readonly int format;
    readonly Point home;                                 // where the real window was
    readonly Mirror[] copies = new Mirror[2];
    readonly Timer timer = new Timer { Interval = 100 };
    Bitmap frame;

    public StereoMirror(Form source, int format)
    {
        this.source = source;
        this.format = format;
        home = source.Location;
        Rectangle screen = Screen.FromControl(source).Bounds;
        Size full = source.Size;
        Size half = format == OverUnder ? new Size(full.Width, full.Height / 2) : new Size(full.Width / 2, full.Height);

        // Centre each copy in its half of the screen.
        for (int eye = 0; eye < 2; eye++)
        {
            Point at;
            if (format == OverUnder)
            {
                int halfH = screen.Height / 2;
                at = new Point(screen.X + (screen.Width - half.Width) / 2, screen.Y + eye * halfH + (halfH - half.Height) / 2);
            }
            else
            {
                int halfW = screen.Width / 2;
                at = new Point(screen.X + eye * halfW + (halfW - half.Width) / 2, screen.Y + (screen.Height - half.Height) / 2);
            }
            var m = new Mirror(this) { Location = at, Size = half };
            copies[eye] = m;
        }

        source.Location = new Point(-20000, -20000);     // still open and focusable, just not visible
        Capture();
        foreach (var m in copies) m.Show();
        timer.Tick += (s, e) => { Capture(); foreach (var m in copies) m.Invalidate(); };
        timer.Start();
    }

    public void Dispose()
    {
        timer.Stop();
        timer.Dispose();
        foreach (var m in copies) if (m != null) m.Close();
        if (!source.IsDisposed) source.Location = home;
        if (frame != null) { frame.Dispose(); frame = null; }
    }

    void Capture()
    {
        if (source.IsDisposed) return;
        if (frame == null || frame.Size != source.Size) { if (frame != null) frame.Dispose(); frame = new Bitmap(source.Width, source.Height); }
        source.DrawToBitmap(frame, new Rectangle(Point.Empty, source.Size));
    }

    // ---- input: a point on a copy -> the real window ----
    Point ToWindow(Point p)
    {
        return format == OverUnder ? new Point(p.X, p.Y * 2) : new Point(p.X * 2, p.Y);
    }

    // The innermost visible control under a point in window coordinates, and the point in its client coordinates.
    Control Hit(Point windowPt, out Point local)
    {
        Point clientOrigin = source.PointToScreen(Point.Empty);
        Point pt = new Point(windowPt.X - (clientOrigin.X - source.Left), windowPt.Y - (clientOrigin.Y - source.Top));
        Control c = source;
        while (true)
        {
            Control child = c.GetChildAtPoint(pt, GetChildAtPointSkip.Invisible);
            if (child == null) break;
            pt = new Point(pt.X - child.Left, pt.Y - child.Top);
            c = child;
        }
        local = pt;
        return c;
    }

    TrackBar dragging;
    Control pressed;

    void Down(Point p)
    {
        Point local;
        Control c = Hit(ToWindow(p), out local);
        if (c != null && c.Parent is NumericUpDown)                  // parts of a number box count as the box
        {
            local = new Point(local.X + c.Left, local.Y + c.Top);
            c = c.Parent;
        }
        pressed = c;
        if (c is TrackBar && c.Enabled) { dragging = (TrackBar)c; SetFromX(dragging, local.X); }
        else if (c is NumericUpDown && c.Enabled)
        {
            var num = (NumericUpDown)c;
            if (local.X >= num.Width - 18) { if (local.Y < num.Height / 2) num.UpButton(); else num.DownButton(); }
            else { source.Activate(); num.Focus(); num.Select(0, num.Text.Length); }   // type the number
        }
    }

    void Move(Point p)
    {
        if (dragging == null) return;
        // The slider's left edge, in window coordinates (both from screen positions, off screen or not).
        int sliderLeft = dragging.PointToScreen(Point.Empty).X - source.Left;
        SetFromX(dragging, ToWindow(p).X - sliderLeft);
    }

    void Up(Point p)
    {
        dragging = null;
        Point local;
        Control c = Hit(ToWindow(p), out local);
        if (c != null && c.Parent is NumericUpDown) c = c.Parent;
        if (c != pressed || c == null || !c.Enabled) return;
        if (c is CheckBox) ((CheckBox)c).Checked = !((CheckBox)c).Checked;
        else if (c is Button) ((Button)c).PerformClick();
        else if (c is ComboBox) Step((ComboBox)c, 1);         // its list can't open in 3D: step through it
    }

    void Wheel(Point p, int delta)
    {
        Point local;
        Control c = Hit(ToWindow(p), out local);
        while (c != null && c.Parent is NumericUpDown) c = c.Parent;
        if (c == null || !c.Enabled) return;
        int step = delta > 0 ? 1 : -1;
        if (c is ComboBox) Step((ComboBox)c, -step);
        else if (c is TrackBar) { var tb = (TrackBar)c; tb.Value = Math.Max(tb.Minimum, Math.Min(tb.Maximum, tb.Value + step)); }
        else if (c is NumericUpDown) { if (step > 0) ((NumericUpDown)c).UpButton(); else ((NumericUpDown)c).DownButton(); }
    }

    static void Step(ComboBox cb, int by)
    {
        if (cb.Items.Count == 0) return;
        int i = cb.SelectedIndex < 0 ? 0 : (cb.SelectedIndex + by + cb.Items.Count) % cb.Items.Count;
        cb.SelectedIndex = i;
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref RECT r);

    // x in the slider's own coordinates -> value, using the slider's real channel and thumb size.
    static void SetFromX(TrackBar tb, int x)
    {
        RECT ch = new RECT(), th = new RECT();
        SendMessage(tb.Handle, 0x41A, IntPtr.Zero, ref ch);   // TBM_GETCHANNELRECT
        SendMessage(tb.Handle, 0x419, IntPtr.Zero, ref th);   // TBM_GETTHUMBRECT
        int half = (th.Right - th.Left) / 2, left = ch.Left + half, right = ch.Right - half;
        double v = tb.Minimum + (double)(x - left) * (tb.Maximum - tb.Minimum) / Math.Max(1, right - left);
        tb.Value = Math.Max(tb.Minimum, Math.Min(tb.Maximum, (int)Math.Round(v)));
    }

    // ---- one squashed copy ----
    sealed class Mirror : Form
    {
        readonly StereoMirror owner;

        public Mirror(StereoMirror owner)
        {
            this.owner = owner;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = Color.Black;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x80 | 0x8;        // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (owner.frame == null) return;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.DrawImage(owner.frame, ClientRectangle);
        }

        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) owner.Down(e.Location); }
        protected override void OnMouseMove(MouseEventArgs e) { if (e.Button == MouseButtons.Left) owner.Move(e.Location); }
        protected override void OnMouseUp(MouseEventArgs e) { if (e.Button == MouseButtons.Left) owner.Up(e.Location); }
        protected override void OnMouseWheel(MouseEventArgs e) { owner.Wheel(e.Location, e.Delta); }
    }
}
