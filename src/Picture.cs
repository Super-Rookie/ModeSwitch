// Picture.cs - picture settings: the projector's presets and adjustments (Sony SDCP items from the
// VPL-VW protocol manual), and GPU-side colour per display: brightness / contrast / gamma per
// channel through the display's gamma ramp (as NVIDIA Control Panel does it), and digital
// vibrance and hue through NVAPI.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

// ---------------------------------------------------------------- projector picture items
static class PjPicture
{
    public const int Preset = 0x0002, Contrast = 0x0010, Brightness = 0x0011, Colour = 0x0012, Hue = 0x0013,
                     Sharpness = 0x0014, ColourTemp = 0x0017, Gamma = 0x0022,
                     GainR = 0x0050, GainG = 0x0051, GainB = 0x0052, BiasR = 0x0053, BiasG = 0x0054, BiasB = 0x0055,
                     Depth3D = 0x0062;           // 3D depth adjust, -2..2; only answers while showing 3D

    // Calib. Preset values 0..8
    public static readonly string[] Presets =
        { "Cinema Film 1", "Cinema Film 2", "Reference", "TV", "Photo", "Game", "Bright Cinema", "Bright TV", "User" };

    public static readonly int[] ColourTempValues = { 0, 1, 2, 9, 3, 4, 5, 6, 8 };
    public static readonly string[] ColourTempNames = { "D93", "D75", "D65", "D55", "Custom 1", "Custom 2", "Custom 3", "Custom 4", "Custom 5" };

    public static readonly int[] GammaValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
    public static readonly string[] GammaNames = { "Off", "1.8", "2.0", "2.1", "2.2", "2.4", "2.6", "Gamma 7", "Gamma 8", "Gamma 9", "Gamma 10" };

    public static string PresetName(int v) { return v >= 0 && v < Presets.Length ? Presets[v] : "preset " + v; }

    // SDCP values are 16-bit; gain and bias are signed (-30..30).
    public static int Signed(int v) { return v >= 0x8000 ? v - 0x10000 : v; }
}

// ---------------------------------------------------------------- GPU colour per display
static class GpuColour
{
    // Brightness / contrast 0..100 (50 = unchanged), gamma x100 (100 = 1.00), per channel R, G, B.
    // Vibrance 0..100 (50 = unchanged, NVIDIA Control Panel's scale), hue 0..359 degrees.
    public sealed class Values
    {
        public int[] Brightness = { 50, 50, 50 }, Contrast = { 50, 50, 50 }, Gamma = { 100, 100, 100 };
        public int Vibrance = 50, Hue = 0;

        public Values Clone()
        {
            return new Values { Brightness = (int[])Brightness.Clone(), Contrast = (int[])Contrast.Clone(), Gamma = (int[])Gamma.Clone(), Vibrance = Vibrance, Hue = Hue };
        }

        public bool RampIsNeutral()
        {
            for (int c = 0; c < 3; c++) if (Brightness[c] != 50 || Contrast[c] != 50 || Gamma[c] != 100) return false;
            return true;
        }

        // "50,50,50" <-> int[3]
        public static int[] Triple(string s, int fallback)
        {
            var r = new[] { fallback, fallback, fallback };
            if (string.IsNullOrEmpty(s)) return r;
            string[] p = s.Split(',');
            for (int i = 0; i < 3 && i < p.Length; i++) int.TryParse(p[i].Trim(), out r[i]);
            if (p.Length == 1) r[1] = r[2] = r[0];
            return r;
        }

        public static string Triple(int[] v) { return v[0] + "," + v[1] + "," + v[2]; }

        public string Describe()
        {
            var parts = new List<string>();
            if (!RampIsNeutral())
            {
                if (Brightness[0] == Brightness[1] && Brightness[1] == Brightness[2]) { if (Brightness[0] != 50) parts.Add("brightness " + Brightness[0] + "%"); }
                else parts.Add("brightness " + Triple(Brightness));
                if (Contrast[0] == Contrast[1] && Contrast[1] == Contrast[2]) { if (Contrast[0] != 50) parts.Add("contrast " + Contrast[0] + "%"); }
                else parts.Add("contrast " + Triple(Contrast));
                if (Gamma[0] == Gamma[1] && Gamma[1] == Gamma[2]) { if (Gamma[0] != 100) parts.Add("gamma " + (Gamma[0] / 100.0).ToString("0.00", CultureInfo.InvariantCulture)); }
                else parts.Add("gamma " + Triple(Gamma));
            }
            if (Vibrance != 50) parts.Add("vibrance " + Vibrance + "%");
            if (Hue != 0) parts.Add("hue " + Hue + "°");
            return parts.Count == 0 ? "neutral" : string.Join(", ", parts.ToArray());
        }
    }

    // ---- gamma ramp ----
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateDC(string driver, string device, string output, IntPtr init);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool SetDeviceGammaRamp(IntPtr dc, ushort[] ramp);

    static ushort[] Ramp(Values v)
    {
        var ramp = new ushort[768];
        for (int c = 0; c < 3; c++)
        {
            double g = Math.Max(0.3, v.Gamma[c] / 100.0), k = v.Contrast[c] / 50.0, b = (v.Brightness[c] - 50) / 100.0;
            for (int i = 0; i < 256; i++)
            {
                double y = Math.Pow(i / 255.0, 1.0 / g);
                y = (y - 0.5) * k + 0.5 + b;
                ramp[c * 256 + i] = (ushort)Math.Round(Math.Max(0, Math.Min(1, y)) * 65535);
            }
        }
        return ramp;
    }

    // gdi = "\\.\DISPLAY1". Returns null on success, else a message.
    public static string SetRamp(string gdi, Values v)
    {
        IntPtr dc = CreateDC(null, gdi, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) return "can't open " + gdi;
        try
        {
            // Windows refuses ramps that stray too far from neutral unless
            // HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM\GdiIcmGammaRange = 256.
            return SetDeviceGammaRamp(dc, Ramp(v)) ? null : "Windows refused the gamma ramp (too far from neutral?)";
        }
        finally { DeleteDC(dc); }
    }

    // ---- NVAPI: digital vibrance and hue ----
    [DllImport("kernel32.dll")] static extern IntPtr LoadLibrary(string name);
    [DllImport("kernel32.dll")] static extern IntPtr GetProcAddress(IntPtr mod, string name);
    delegate IntPtr QI(uint id);
    delegate int Fn0();
    delegate int FnEnumDisp(int index, out IntPtr handle);
    delegate int FnName(IntPtr handle, StringBuilder name);
    delegate int FnInfo(IntPtr handle, uint outputId, IntPtr info);
    delegate int FnSetHue(IntPtr handle, uint outputId, uint angle);
    static QI q;

    static T Get<T>(uint id) where T : class
    {
        IntPtr p = q(id);
        return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
    }

    static bool Init()
    {
        if (q != null) return true;
        IntPtr lib = LoadLibrary("nvapi64.dll");
        IntPtr qi = lib == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(lib, "nvapi_QueryInterface");
        if (qi == IntPtr.Zero) return false;
        var query = (QI)Marshal.GetDelegateForFunctionPointer(qi, typeof(QI));
        IntPtr init = query(0x0150E828);
        if (init == IntPtr.Zero || ((Fn0)Marshal.GetDelegateForFunctionPointer(init, typeof(Fn0)))() != 0) return false;
        q = query;
        return true;
    }

    // NVIDIA display handle for a GDI name, or IntPtr.Zero.
    static IntPtr Handle(string gdi)
    {
        var enumDisp = Get<FnEnumDisp>(0x9ABDD40D);
        var name = Get<FnName>(0x22A78B05);
        if (enumDisp == null || name == null) return IntPtr.Zero;
        for (int i = 0; i < 16; i++)
        {
            IntPtr h;
            if (enumDisp(i, out h) != 0) break;
            var sb = new StringBuilder(64);
            if (name(h, sb) == 0 && sb.ToString().Equals(gdi, StringComparison.OrdinalIgnoreCase)) return h;
        }
        return IntPtr.Zero;
    }

    // NV_DISPLAY_DVC_INFO_EX: version, current, min, max, default (current in %, 0..100)
    const int DVC_EX_SIZE = 20;

    public static string ReadVibranceHue(string gdi, out int vibrance, out int hue)
    {
        vibrance = 50; hue = 0;
        if (!Init()) return "NVAPI unavailable";
        IntPtr h = Handle(gdi);
        if (h == IntPtr.Zero) return "not an NVIDIA display";
        var getDvc = Get<FnInfo>(0x0E45002D);
        var getHue = Get<FnInfo>(0x95B64341);
        IntPtr b = Marshal.AllocHGlobal(DVC_EX_SIZE);
        try
        {
            Zero(b, DVC_EX_SIZE);
            Marshal.WriteInt32(b, 0, DVC_EX_SIZE | (1 << 16));
            if (getDvc != null && getDvc(h, 0, b) == 0) vibrance = Marshal.ReadInt32(b, 4);
            Zero(b, 12);
            Marshal.WriteInt32(b, 0, 12 | (1 << 16));
            if (getHue != null && getHue(h, 0, b) == 0) hue = Marshal.ReadInt32(b, 4);
        }
        finally { Marshal.FreeHGlobal(b); }
        return null;
    }

    public static string SetVibranceHue(string gdi, int vibrance, int hue)
    {
        if (!Init()) return "NVAPI unavailable";
        IntPtr h = Handle(gdi);
        if (h == IntPtr.Zero) return "not an NVIDIA display";
        var getDvc = Get<FnInfo>(0x0E45002D);
        var setDvc = Get<FnInfo>(0x4A82C2B1);
        var setHue = Get<FnSetHue>(0xF5A0F22C);
        if (getDvc == null || setDvc == null || setHue == null) return "vibrance/hue not supported by this driver";
        IntPtr b = Marshal.AllocHGlobal(DVC_EX_SIZE);
        try
        {
            Zero(b, DVC_EX_SIZE);
            Marshal.WriteInt32(b, 0, DVC_EX_SIZE | (1 << 16));
            int rc = getDvc(h, 0, b);
            if (rc != 0) return "reading vibrance rc=" + rc;
            Marshal.WriteInt32(b, 4, Math.Max(0, Math.Min(100, vibrance)));
            rc = setDvc(h, 0, b);
            if (rc != 0) return "vibrance rc=" + rc;
            rc = setHue(h, 0, (uint)(((hue % 360) + 360) % 360));
            if (rc != 0) return "hue rc=" + rc;
            return null;
        }
        finally { Marshal.FreeHGlobal(b); }
    }

    static void Zero(IntPtr b, int n) { for (int i = 0; i < n; i++) Marshal.WriteByte(b, i, 0); }

    // Everything for one display. hdrOn: the gamma ramp does nothing in HDR, so it's skipped.
    public static string Apply(string gdi, Values v, bool hdrOn)
    {
        string err = hdrOn ? null : SetRamp(gdi, v);
        string err2 = SetVibranceHue(gdi, v.Vibrance, v.Hue);
        if (err != null && err2 != null) return err + "; " + err2;
        return err ?? err2;
    }
}
