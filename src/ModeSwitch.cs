// ModeSwitch - tray app that switches the PC between Movie and Game presets.
// Movie: HAGS off, G-Sync off, HDR off, stock GPU clocks, Afterburner/RTSS stopped.
// Game:  HAGS on,  G-Sync on,  HDR on,  overclock applied, Afterburner/RTSS started.
// Settings live in config.ini next to the exe. Runs elevated (scheduled task at logon).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

// ---------------------------------------------------------------- config
class Config
{
    readonly Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string Path;

    public Config(string path)
    {
        Path = path;
        if (!File.Exists(path)) return;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }
    }

    public string Get(string key, string fallback)
    {
        string v;
        return map.TryGetValue(key, out v) && v.Length > 0 ? v : fallback;
    }

    public int GetInt(string key, int fallback)
    {
        int v;
        string s = Get(key, null);
        if (s == null) return fallback;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            uint u;
            if (uint.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out u)) return unchecked((int)u);
            return fallback;
        }
        return int.TryParse(s, out v) ? v : fallback;
    }

    public bool GetBool(string key, bool fallback)
    {
        string s = Get(key, null);
        if (s == null) return fallback;
        s = s.Trim().ToLowerInvariant();
        return s == "1" || s == "on" || s == "true" || s == "yes";
    }

    // "0x1194F158:2,0x1094F157:1" -> list of (id, value)
    public List<KeyValuePair<uint, uint>> GetSettings(string key)
    {
        var list = new List<KeyValuePair<uint, uint>>();
        string s = Get(key, "");
        foreach (string part in s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = part.Split(':');
            if (kv.Length != 2) continue;
            uint id, val;
            if (ParseU32(kv[0], out id) && ParseU32(kv[1], out val))
                list.Add(new KeyValuePair<uint, uint>(id, val));
        }
        return list;
    }

    static bool ParseU32(string s, out uint v)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
        return uint.TryParse(s, out v);
    }
}

// ---------------------------------------------------------------- NVIDIA
static class Nv
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] static extern IntPtr LoadLibrary(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] static extern IntPtr GetProcAddress(IntPtr mod, string name);

    delegate IntPtr QueryInterface(uint id);
    delegate int Fn0();
    delegate int FnOut(out IntPtr a);
    delegate int FnIn(IntPtr a);
    delegate int FnInOut(IntPtr a, out IntPtr b);
    delegate int FnSetting(IntPtr session, IntPtr profile, IntPtr setting);
    delegate int FnEnumGpus([Out] IntPtr[] handles, out int count);
    delegate int FnPstates(IntPtr gpu, IntPtr info);

    static QueryInterface query;
    static bool ready;

    static T Get<T>(uint id) where T : class
    {
        IntPtr p = query(id);
        return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
    }

    static bool Init()
    {
        if (ready) return true;
        IntPtr lib = LoadLibrary("nvapi64.dll");
        if (lib == IntPtr.Zero) return false;
        IntPtr qi = GetProcAddress(lib, "nvapi_QueryInterface");
        if (qi == IntPtr.Zero) return false;
        query = (QueryInterface)Marshal.GetDelegateForFunctionPointer(qi, typeof(QueryInterface));
        var init = Get<Fn0>(0x0150E828);
        if (init == null || init() != 0) return false;
        ready = true;
        return true;
    }

    // ---- driver profile settings (V-Sync, G-Sync) ----
    const int SETTING_SIZE = 12320;
    static readonly uint SETTING_VER = (uint)SETTING_SIZE | (1u << 16);
    const int OFF_ID = 4 + 4096;
    const int OFF_TYPE = OFF_ID + 4;
    const int OFF_CURRENT = OFF_ID + 4 + 4 + 4 + 4 + 4 + 4100;

    public static string ApplyProfileSettings(List<KeyValuePair<uint, uint>> settings)
    {
        if (settings.Count == 0) return null;
        if (!Init()) return "NVAPI unavailable";

        var createSession = Get<FnOut>(0x0694D52E);
        var loadSettings = Get<FnIn>(0x375DBD6B);
        var getBase = Get<FnInOut>(0xDA8466A0);
        var setSetting = Get<FnSetting>(0x577DD202);
        var save = Get<FnIn>(0xFCBC7E14);
        var destroy = Get<FnIn>(0xDAD9CFF8);
        if (createSession == null || setSetting == null) return "NVAPI DRS entry points missing";

        IntPtr session;
        int rc = createSession(out session);
        if (rc != 0) return "DRS_CreateSession rc=" + rc;
        try
        {
            rc = loadSettings(session);
            if (rc != 0) return "DRS_LoadSettings rc=" + rc;
            IntPtr profile;
            rc = getBase(session, out profile);
            if (rc != 0) return "DRS_GetBaseProfile rc=" + rc;

            IntPtr buf = Marshal.AllocHGlobal(SETTING_SIZE);
            try
            {
                foreach (var kv in settings)
                {
                    for (int b = 0; b < SETTING_SIZE; b++) Marshal.WriteByte(buf, b, 0);
                    Marshal.WriteInt32(buf, 0, unchecked((int)SETTING_VER));
                    Marshal.WriteInt32(buf, OFF_ID, unchecked((int)kv.Key));
                    Marshal.WriteInt32(buf, OFF_TYPE, 0);           // NVDRS_DWORD_TYPE
                    Marshal.WriteInt32(buf, OFF_CURRENT, unchecked((int)kv.Value));
                    rc = setSetting(session, profile, buf);
                    if (rc != 0) return string.Format("DRS_SetSetting 0x{0:X8} rc={1}", kv.Key, rc);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }

            rc = save(session);
            if (rc != 0) return "DRS_SaveSettings rc=" + rc;
            return null;
        }
        finally { if (destroy != null) destroy(session); }
    }

    // ---- driver version ----
    delegate int FnDriverVersion(out uint version, StringBuilder branch);

    // Returns e.g. "616.64", or null if NVAPI is unavailable.
    public static string DriverVersion()
    {
        if (!Init()) return null;
        var fn = Get<FnDriverVersion>(0x2926AAAD);   // NvAPI_SYS_GetDriverAndBranchVersion
        if (fn == null) return null;
        uint v;
        var branch = new StringBuilder(64);
        if (fn(out v, branch) != 0) return null;
        return string.Format(CultureInfo.InvariantCulture, "{0}.{1:00}", v / 100, v % 100);
    }

    // ---- verification ----
    delegate int FnGetSetting(IntPtr session, IntPtr profile, uint id, IntPtr setting);
    const int NVAPI_SETTING_NOT_FOUND = -160;

    // Reads each setting back from the saved driver profile in a fresh session.
    // Returns one line per mismatch (empty list = all good), or null if the check itself failed.
    public static List<string> VerifyProfileSettings(List<KeyValuePair<uint, uint>> settings, out string error)
    {
        error = null;
        var problems = new List<string>();
        if (settings.Count == 0) return problems;
        if (!Init()) { error = "NVAPI unavailable"; return null; }

        var createSession = Get<FnOut>(0x0694D52E);
        var loadSettings = Get<FnIn>(0x375DBD6B);
        var getBase = Get<FnInOut>(0xDA8466A0);
        var getSetting = Get<FnGetSetting>(0x73BF8338);
        var destroy = Get<FnIn>(0xDAD9CFF8);
        if (createSession == null || getSetting == null) { error = "NVAPI DRS entry points missing"; return null; }

        IntPtr session;
        int rc = createSession(out session);
        if (rc != 0) { error = "DRS_CreateSession rc=" + rc; return null; }
        try
        {
            rc = loadSettings(session);
            if (rc != 0) { error = "DRS_LoadSettings rc=" + rc; return null; }
            IntPtr profile;
            rc = getBase(session, out profile);
            if (rc != 0) { error = "DRS_GetBaseProfile rc=" + rc; return null; }

            IntPtr buf = Marshal.AllocHGlobal(SETTING_SIZE);
            try
            {
                foreach (var kv in settings)
                {
                    for (int b = 0; b < SETTING_SIZE; b++) Marshal.WriteByte(buf, b, 0);
                    Marshal.WriteInt32(buf, 0, unchecked((int)SETTING_VER));
                    rc = getSetting(session, profile, kv.Key, buf);
                    if (rc == NVAPI_SETTING_NOT_FOUND)
                        problems.Add(string.Format("0x{0:X8} is not a known setting in this driver", kv.Key));
                    else if (rc != 0)
                        problems.Add(string.Format("0x{0:X8} could not be read (rc={1})", kv.Key, rc));
                    else
                    {
                        uint actual = (uint)Marshal.ReadInt32(buf, OFF_CURRENT);
                        if (actual != kv.Value)
                            problems.Add(string.Format("0x{0:X8} is 0x{1:X} instead of 0x{2:X}", kv.Key, actual, kv.Value));
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return problems;
        }
        finally { if (destroy != null) destroy(session); }
    }

    // ---- clock offsets ----
    const int CLOCK_SIZE = 44, BASEVOLT_SIZE = 24;
    const int PSTATE_SIZE = 4 + 4 + (8 * CLOCK_SIZE) + (4 * BASEVOLT_SIZE);
    const int HEADER_SIZE = 20, OV_SIZE = 4 + (4 * BASEVOLT_SIZE);
    const int INFO_SIZE = HEADER_SIZE + (16 * PSTATE_SIZE) + OV_SIZE;
    static readonly uint INFO_VER2 = (uint)INFO_SIZE | (2u << 16);
    static int ClockOff(int pstate, int clock) { return HEADER_SIZE + pstate * PSTATE_SIZE + 8 + clock * CLOCK_SIZE; }

    public static string SetClockOffsets(int coreKHz, int memKHz)
    {
        if (!Init()) return "NVAPI unavailable";
        var enumGpus = Get<FnEnumGpus>(0xE5AC921F);
        var setPstates = Get<FnPstates>(0x0F4DAE6B);
        if (enumGpus == null || setPstates == null) return "NVAPI pstate entry points missing";

        var gpus = new IntPtr[64];
        int n;
        int rc = enumGpus(gpus, out n);
        if (rc != 0 || n == 0) return "EnumPhysicalGPUs rc=" + rc;

        IntPtr req = Marshal.AllocHGlobal(INFO_SIZE);
        try
        {
            for (int b = 0; b < INFO_SIZE; b++) Marshal.WriteByte(req, b, 0);
            Marshal.WriteInt32(req, 0, unchecked((int)INFO_VER2));
            Marshal.WriteInt32(req, 8, 1);   // numPstates
            Marshal.WriteInt32(req, 12, 2);  // numClocks
            Marshal.WriteInt32(req, HEADER_SIZE, 0); // P0
            int c0 = ClockOff(0, 0);
            Marshal.WriteInt32(req, c0, 0);          // GRAPHICS
            Marshal.WriteInt32(req, c0 + 12, coreKHz);
            int c1 = ClockOff(0, 1);
            Marshal.WriteInt32(req, c1, 4);          // MEMORY
            Marshal.WriteInt32(req, c1 + 12, memKHz);
            rc = setPstates(gpus[0], req);
            return rc == 0 ? null : "SetPstates20 rc=" + rc + (rc == -178 ? " (needs admin)" : "");
        }
        finally { Marshal.FreeHGlobal(req); }
    }
}

// ---------------------------------------------------------------- Afterburner
// The core undervolt is a 127-point voltage/frequency curve, which the pstate API cannot set.
// Afterburner owns it, so we copy a saved profile section into [Startup] and relaunch Afterburner,
// which applies that section on start. Power limit and memory offset ride along in the same section.
static class Ab
{
    static readonly string[] Keys = { "Format", "PowerLimit", "ThermalLimit", "ThermalPrioritize", "CoreClkBoost", "VFCurve", "MemClkBoost", "FanMode", "FanSpeed" };

    public static string Apply(string cfgPath, string exePath, string exeArgs, string sourceSection,
                               int memClkOverride, int powerOverride, bool stopAfter, int applyDelayMs, StringBuilder log)
    {
        if (!File.Exists(cfgPath)) return "Afterburner config not found: " + cfgPath;
        if (!File.Exists(exePath)) return "Afterburner not found: " + exePath;

        var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] lines = File.ReadAllLines(cfgPath);
        string section = "";
        foreach (string line in lines)
        {
            if (line.StartsWith("[")) { section = line.Trim('[', ']'); continue; }
            if (!section.Equals(sourceSection, StringComparison.OrdinalIgnoreCase)) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line.Substring(0, eq).Trim();
            if (Array.IndexOf(Keys, key) >= 0) source[key] = line.Substring(eq + 1);
        }
        if (source.Count == 0) return "section [" + sourceSection + "] not found in Afterburner config";

        source["MemClkBoost"] = memClkOverride.ToString(CultureInfo.InvariantCulture);
        if (powerOverride > 0) source["PowerLimit"] = powerOverride.ToString(CultureInfo.InvariantCulture);

        // Afterburner rewrites its config on exit, so stop it before patching. This is an internal
        // restart, not the user-visible "closed" state, so it is not logged.
        KillAll(new[] { "MSIAfterburner" }, null);

        var outLines = new List<string>();
        section = "";
        foreach (string line in lines)
        {
            if (line.StartsWith("[")) { section = line.Trim('[', ']'); outLines.Add(line); continue; }
            int eq = line.IndexOf('=');
            if (section.Equals("Startup", StringComparison.OrdinalIgnoreCase) && eq > 0)
            {
                string key = line.Substring(0, eq).Trim();
                string val;
                if (source.TryGetValue(key, out val)) { outLines.Add(key + "=" + val); continue; }
            }
            outLines.Add(line);
        }
        try { File.WriteAllLines(cfgPath, outLines.ToArray()); }
        catch (Exception ex) { return "cannot write Afterburner config (needs admin): " + ex.Message; }

        try
        {
            Process.Start(new ProcessStartInfo(exePath, exeArgs) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exePath) });
        }
        catch (Exception ex) { return "cannot start Afterburner: " + ex.Message; }

        // Wait for Afterburner to load and apply the profile before calling this step done,
        // so nothing downstream (e.g. the reboot prompt) runs while it is still applying.
        System.Threading.Thread.Sleep(Math.Max(1000, applyDelayMs));

        // Only mention overrides that actually change something.
        var extras = new List<string>();
        if (memClkOverride != 0) extras.Add(string.Format("memory {0}{1} MHz", memClkOverride > 0 ? "+" : "", memClkOverride / 1000));
        if (powerOverride > 0) extras.Add("power " + powerOverride + "%");
        string detail = extras.Count > 0 ? " (" + string.Join(", ", extras.ToArray()) + ")" : "";

        if (stopAfter)
        {
            KillAll(new[] { "MSIAfterburner" }, null);
            log.AppendLine("Afterburner: " + sourceSection + " applied" + detail + ", then closed");
        }
        else if (Process.GetProcessesByName("MSIAfterburner").Length > 0)
        {
            log.AppendLine("Afterburner: " + sourceSection + " applied" + detail);
        }
        else
        {
            log.AppendLine("Afterburner: " + sourceSection + " sent" + detail + " but Afterburner exited - is \"Start with Windows\" enabled in its settings?");
        }
        return null;
    }

    public static void KillAll(IEnumerable<string> names, StringBuilder log)
    {
        foreach (string raw in names)
        {
            string name = raw.Trim();
            if (name.Length == 0) continue;
            foreach (Process p in Process.GetProcessesByName(name))
            {
                try { p.Kill(); p.WaitForExit(5000); if (log != null) log.AppendLine("stopped " + name); }
                catch { }
            }
        }
    }
}

// ---------------------------------------------------------------- display
static class Disp
{
    [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint Lo; public int Hi; }
    [StructLayout(LayoutKind.Sequential)] struct SRC { public LUID a; public uint id; public uint m; public uint f; }
    [StructLayout(LayoutKind.Sequential)] struct RAT { public uint N; public uint D; }
    [StructLayout(LayoutKind.Sequential)] struct TGT { public LUID a; public uint id; public uint m; public int ot; public int rot; public int sc; public RAT rr; public int sl; public int av; public uint f; }
    [StructLayout(LayoutKind.Sequential)] struct PATH { public SRC s; public TGT t; public uint f; }
    [StructLayout(LayoutKind.Sequential, Size = 64)] struct MODE { public int it; public uint id; public LUID a; }
    [StructLayout(LayoutKind.Sequential)] struct HDRHDR { public int type; public int size; public LUID a; public uint id; }
    [StructLayout(LayoutKind.Sequential)] struct ACI { public HDRHDR h; public uint value; public int enc; public uint bits; }
    [StructLayout(LayoutKind.Sequential)] struct ACI2 { public HDRHDR h; public uint value; public int enc; public uint bits; public int activeColorMode; }
    [StructLayout(LayoutKind.Sequential)] struct SETACS { public HDRHDR h; public uint value; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TNAME { public HDRHDR h; public uint flags; public int ot; public ushort m; public ushort p; public uint ci; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string name; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string path; }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint f, out uint p, out uint m);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint f, ref uint p, [Out] PATH[] ps, ref uint m, [Out] MODE[] ms, IntPtr t);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] static extern int GetName(ref TNAME r);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] static extern int GetAci(ref ACI r);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] static extern int GetAci2(ref ACI2 r);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")] static extern int SetAcs(ref SETACS r);

    public class Target { public string Name; public LUID Adapter; public uint Id; public bool HdrOn; public bool HdrCapable; }

    public static List<Target> Targets()
    {
        var list = new List<Target>();
        uint pc, mc;
        if (GetDisplayConfigBufferSizes(2, out pc, out mc) != 0) return list;
        var ps = new PATH[pc]; var ms = new MODE[mc];
        if (QueryDisplayConfig(2, ref pc, ps, ref mc, ms, IntPtr.Zero) != 0) return list;
        for (int i = 0; i < pc; i++)
        {
            var n = new TNAME(); n.h.type = 2; n.h.size = Marshal.SizeOf(typeof(TNAME)); n.h.a = ps[i].t.a; n.h.id = ps[i].t.id;
            GetName(ref n);
            var t = new Target { Name = string.IsNullOrEmpty(n.name) ? "Display " + i : n.name, Adapter = ps[i].t.a, Id = ps[i].t.id };
            ReadHdr(t);
            list.Add(t);
        }
        return list;
    }

    // Windows 11 reports "advanced colour" as on whenever Auto Color Management is active, even in
    // SDR, so the old GET_ADVANCED_COLOR_INFO bit is not an HDR flag. Use the v2 query (24H2+),
    // which separates HDR from ACM/wide colour; fall back to v1 on older builds.
    static void ReadHdr(Target t)
    {
        var a2 = new ACI2(); a2.h.type = 15; a2.h.size = Marshal.SizeOf(typeof(ACI2)); a2.h.a = t.Adapter; a2.h.id = t.Id;
        if (GetAci2(ref a2) == 0)
        {
            t.HdrCapable = (a2.value & 16) != 0;                             // highDynamicRangeSupported
            t.HdrOn = (a2.value & 32) != 0 || a2.activeColorMode == 2;       // HDR user-enabled / active HDR
            return;
        }
        var a = new ACI(); a.h.type = 9; a.h.size = Marshal.SizeOf(typeof(ACI)); a.h.a = t.Adapter; a.h.id = t.Id;
        GetAci(ref a);
        t.HdrCapable = (a.value & 1) != 0;
        t.HdrOn = (a.value & 2) != 0;
    }

    public static string SetHdr(bool on)
    {
        string err = null;
        foreach (var t in Targets())
        {
            if (!t.HdrCapable || t.HdrOn == on) continue;
            string e = SetHdrFor(t, on);
            if (e != null) err = e;
        }
        return err;
    }

    public static string SetHdrFor(Target t, bool on)
    {
        // 16 = SET_HDR_STATE (24H2+) toggles HDR itself. 10 = SET_ADVANCED_COLOR_STATE toggles
        // "advanced colour", which ACM already holds on, so it can be a no-op for turning HDR on.
        // Try the precise call first, then the older one, and confirm by reading the state back.
        int rc16 = SetHdrVia(16, t, on);
        if (rc16 == 0 && Confirm(t, on)) return null;
        int rc10 = SetHdrVia(10, t, on);
        if (rc10 == 0 && Confirm(t, on)) return null;
        return string.Format("HDR {0} on {1} did not take effect (SET_HDR_STATE rc={2}, SET_ADVANCED_COLOR_STATE rc={3})",
            on ? "on" : "off", t.Name, rc16, rc10);
    }

    static bool Confirm(Target t, bool on)
    {
        for (int i = 0; i < 10; i++)                 // the display may take a moment to switch
        {
            ReadHdr(t);
            if (t.HdrOn == on) return true;
            System.Threading.Thread.Sleep(300);
        }
        return false;
    }

    static int SetHdrVia(int type, Target t, bool on)
    {
        var s = new SETACS();
        s.h.type = type;
        s.h.size = Marshal.SizeOf(typeof(SETACS));
        s.h.a = t.Adapter; s.h.id = t.Id;
        s.value = on ? 1u : 0u;
        return SetAcs(ref s);
    }

    // ---- refresh rates ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string dev, uint i, ref DISPLAY_DEVICE dd, uint f);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string dev, ref DEVMODE dm, IntPtr wnd, uint flags, IntPtr param);

    public class Screen { public string Device; public string Monitor; public int W, H, Hz; public List<int> Rates = new List<int>(); }

    public static List<Screen> Screens()
    {
        var list = new List<Screen>();
        for (uint i = 0; i < 16; i++)
        {
            var d = new DISPLAY_DEVICE(); d.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            if (!EnumDisplayDevices(null, i, ref d, 0)) break;
            if ((d.StateFlags & 1) == 0) continue;            // not attached to desktop

            var cur = new DEVMODE(); cur.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (!EnumDisplaySettings(d.DeviceName, -1, ref cur)) continue;

            var s = new Screen { Device = d.DeviceName, Monitor = d.DeviceString, W = cur.dmPelsWidth, H = cur.dmPelsHeight, Hz = cur.dmDisplayFrequency };
            var mon = new DISPLAY_DEVICE(); mon.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            if (EnumDisplayDevices(d.DeviceName, 0, ref mon, 0) && !string.IsNullOrEmpty(mon.DeviceString)) s.Monitor = mon.DeviceString;

            for (int m = 0; ; m++)
            {
                var dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                if (!EnumDisplaySettings(d.DeviceName, m, ref dm)) break;
                if (dm.dmPelsWidth != s.W || dm.dmPelsHeight != s.H) continue;
                if (!s.Rates.Contains(dm.dmDisplayFrequency)) s.Rates.Add(dm.dmDisplayFrequency);
            }
            s.Rates.Sort();
            list.Add(s);
        }
        return list;
    }

    public static string SetRefresh(string device, int hz)
    {
        var dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        if (!EnumDisplaySettings(device, -1, ref dm)) return "cannot read current mode";
        dm.dmDisplayFrequency = hz;
        dm.dmFields = 0x400000;                                // DM_DISPLAYFREQUENCY
        int rc = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, 0x00000001, IntPtr.Zero); // CDS_UPDATEREGISTRY
        return rc == 0 ? null : "ChangeDisplaySettingsEx rc=" + rc;
    }
}

// ---------------------------------------------------------------- app
class ModeSwitchApp : ApplicationContext
{
    readonly NotifyIcon tray = new NotifyIcon();
    readonly Config cfg;
    readonly string exeDir;
    string mode;                 // "movie" | "game"
    bool rebootPending;

    const string RegKey = @"Software\ModeSwitch";
    const string HagsKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

    bool headless;
    volatile string driverNote;          // "NVIDIA driver changed: A -> B", reported with the next switch
    volatile bool busy;                  // a switch is running on a background thread
    volatile string pendingTarget;       // mode being switched to, for the icon
    Control syncCtl;                     // marshals results back to the UI thread

    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // ModeSwitch.exe --apply movie|game   applies a mode and exits (used by the uninstaller)
        if (args.Length >= 2 && args[0].Equals("--apply", StringComparison.OrdinalIgnoreCase))
        {
            string target = args[1].Equals("game", StringComparison.OrdinalIgnoreCase) ? "game" : "movie";
            var headlessApp = new ModeSwitchApp(true);
            headlessApp.Switch(target);
            return 0;
        }

        Application.Run(new ModeSwitchApp(false));
        return 0;
    }

    public ModeSwitchApp() : this(false) { }

    public ModeSwitchApp(bool headless)
    {
        this.headless = headless;
        exeDir = Path.GetDirectoryName(Application.ExecutablePath);
        cfg = new Config(Path.Combine(exeDir, "config.ini"));
        mode = ReadStoredMode();
        if (headless) return;
        syncCtl = new Control();
        { IntPtr forceHandle = syncCtl.Handle; }   // create the handle so BeginInvoke works
        tray.Visible = true;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Switch(mode == "movie" ? "game" : "movie"); };
        BuildMenu();
        UpdateIcon();
        CheckDriverVersion();

        // Things like the Afterburner curve and Windows HDR do not survive a reboot, so re-apply
        // the stored mode shortly after logon. GPU scheduling already matches, so no reboot prompt.
        if (cfg.GetBool("apply.onstart", true))
        {
            var t = new Timer();
            t.Interval = Math.Max(1000, cfg.GetInt("apply.onstart.delayms", 15000));
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); Switch(mode); };
            t.Start();
        }
    }

    string ReadStoredMode()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegKey))
                if (k != null)
                {
                    object v = k.GetValue("Mode");
                    if (v != null && (string)v == "game") return "game";
                    if (v != null) return "movie";
                }
        }
        catch { }
        return HagsValue() == 2 ? "game" : "movie";
    }

    void StoreMode()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegKey)) k.SetValue("Mode", mode);
        }
        catch { }
    }

    static string ReadReg(string name)
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegKey))
                return k == null ? null : k.GetValue(name) as string;
        }
        catch { return null; }
    }

    static void WriteReg(string name, string value)
    {
        try { using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegKey)) k.SetValue(name, value); }
        catch { }
    }

    // A driver update can reset NVIDIA settings or, rarely, change setting IDs/values. Remember the
    // version; when it changes, say so and make sure the settings get re-applied and read back.
    void CheckDriverVersion()
    {
        try { CheckDriverVersionCore(); }
        catch (Exception ex) { WriteLog("startup", "driver version check failed: " + ex.Message); }
    }

    void CheckDriverVersionCore()
    {
        string now = Nv.DriverVersion();
        if (now == null) { WriteLog("startup", "could not read the NVIDIA driver version"); return; }
        string last = ReadReg("DriverVersion");
        WriteReg("DriverVersion", now);
        if (ReadReg("DriverVersion") != now) WriteLog("startup", "could not store the driver version in the registry");
        if (last == null || last == now) return;

        driverNote = string.Format("NVIDIA driver changed: {0} -> {1}", last, now);
        if (cfg.GetBool("apply.onstart", true))
        {
            Notify(driverNote + "\nSettings will be re-applied and checked in a moment.", false);
            return;                                  // the startup re-apply reports the result
        }

        // No automatic re-apply configured: just check the current mode's settings in place.
        string checkMode = mode;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            var settings = cfg.GetSettings("vsync.both");
            settings.AddRange(cfg.GetSettings("gsync." + checkMode));
            string text = driverNote + "\n" + DescribeVerification(settings);
            driverNote = null;
            WriteLog("check after driver change", text);
            try { syncCtl.BeginInvoke((MethodInvoker)delegate { Notify(text, text.IndexOf("FAILED", StringComparison.Ordinal) >= 0); }); }
            catch { }
        });
    }

    static string DescribeVerification(List<KeyValuePair<uint, uint>> settings)
    {
        string err;
        List<string> problems = Nv.VerifyProfileSettings(settings, out err);
        string drv = Nv.DriverVersion();
        string onDriver = drv == null ? "" : " (driver " + drv + ")";
        if (problems == null) return "NVIDIA check FAILED" + onDriver + ": " + err;
        if (problems.Count == 0) return "NVIDIA settings applied and verified" + onDriver;
        return "NVIDIA check FAILED" + onDriver + ": " + string.Join("; ", problems.ToArray())
             + "\nRun bin\\NvProbe.exe and update config.ini (see README: Driver updates).";
    }

    static int HagsValue()
    {
        try
        {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(HagsKey))
            {
                object v = k == null ? null : k.GetValue("HwSchMode");
                return v == null ? 0 : Convert.ToInt32(v);
            }
        }
        catch { return 0; }
    }

    // ---- menu ----
    void BuildMenu()
    {
        var menu = new ContextMenuStrip();
        // WinForms pre-cancels opening a menu that is empty at that moment, so un-cancel after
        // filling it; the initial fill means the very first right-click is never empty anyway.
        menu.Opening += (s, e) => { menu.Items.Clear(); FillMenu(menu); e.Cancel = false; };
        FillMenu(menu);
        tray.ContextMenuStrip = menu;
    }

    void FillMenu(ContextMenuStrip menu)
    {
        string headerText = busy && pendingTarget != null
            ? "Switching to " + (pendingTarget == "game" ? "Game" : "Movie") + "..."
            : string.Format("Mode: {0}{1}", mode == "game" ? "Game" : "Movie", rebootPending ? "  (reboot pending)" : "");
        var header = new ToolStripMenuItem(headerText);
        header.Enabled = false;
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());

        var movie = new ToolStripMenuItem("Movie mode", null, (s, e) => Switch("movie"));
        movie.Checked = mode == "movie";
        movie.Enabled = !busy;
        var game = new ToolStripMenuItem("Game mode", null, (s, e) => Switch("game"));
        game.Checked = mode == "game";
        game.Enabled = !busy;
        menu.Items.Add(movie);
        menu.Items.Add(game);
        menu.Items.Add(new ToolStripSeparator());

        var refresh = new ToolStripMenuItem("Refresh rate");
        foreach (var scr in Disp.Screens())
        {
            var sub = new ToolStripMenuItem(string.Format("{0} ({1}x{2})", scr.Monitor, scr.W, scr.H));
            string dev = scr.Device;
            foreach (int hz in scr.Rates)
            {
                int rate = hz;
                var item = new ToolStripMenuItem(hz + " Hz", null, (s, e) =>
                {
                    string err = Disp.SetRefresh(dev, rate);
                    Notify(err ?? string.Format("Refresh rate set to {0} Hz", rate), err != null);
                });
                item.Checked = hz == scr.Hz;
                sub.DropDownItems.Add(item);
            }
            refresh.DropDownItems.Add(sub);
        }
        if (refresh.DropDownItems.Count == 0) refresh.Enabled = false;
        menu.Items.Add(refresh);

        var targets = Disp.Targets();
        var capable = targets.FindAll(t => t.HdrCapable);
        int onCount = capable.FindAll(t => t.HdrOn).Count;
        string hdrState = capable.Count == 0 ? "not supported"
                        : onCount == 0 ? "off"
                        : onCount == capable.Count ? "on"
                        : onCount + " of " + capable.Count + " on";
        var hdr = new ToolStripMenuItem("HDR: " + hdrState);
        foreach (var t in capable)
        {
            Disp.Target target = t;
            var item = new ToolStripMenuItem(string.Format("{0} - {1}", t.Name, t.HdrOn ? "on" : "off"), null, (s, e) =>
            {
                bool wantOn = !target.HdrOn;
                string err = Disp.SetHdrFor(target, wantOn);
                Notify(err ?? string.Format("HDR turned {0} on {1}", wantOn ? "on" : "off", target.Name), err != null);
            });
            item.Checked = t.HdrOn;
            item.CheckOnClick = false;
            hdr.DropDownItems.Add(item);
        }
        foreach (var t in targets.FindAll(x => !x.HdrCapable))
        {
            var item = new ToolStripMenuItem(t.Name + " - no HDR support");
            item.Enabled = false;
            hdr.DropDownItems.Add(item);
        }
        if (hdr.DropDownItems.Count == 0) hdr.Enabled = false;
        menu.Items.Add(hdr);
        menu.Items.Add(new ToolStripSeparator());

        if (rebootPending) menu.Items.Add(new ToolStripMenuItem("Reboot now", null, (s, e) => Reboot()));
        menu.Items.Add(new ToolStripMenuItem("Open config.ini", null, (s, e) => Process.Start("notepad.exe", cfg.Path)));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => { tray.Visible = false; Application.Exit(); }));
    }

    // ---- switching ----
    // A switch takes several seconds (Afterburner has to start and apply), so run it off the UI
    // thread and show a "switching" icon meanwhile, otherwise the tray looks frozen.
    void Switch(string target)
    {
        if (headless) { DoSwitch(target); return; }
        if (busy) return;
        busy = true;
        pendingTarget = target;
        UpdateIcon();
        Notify("Switching to " + (target == "game" ? "Game" : "Movie") + " mode...", false);

        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            string text;
            try { text = DoSwitch(target); }
            catch (Exception ex) { text = "failed: " + ex.Message; }
            try
            {
                syncCtl.BeginInvoke((MethodInvoker)delegate
                {
                    busy = false;
                    pendingTarget = null;
                    UpdateIcon();
                    bool problem = text.IndexOf("rc=", StringComparison.Ordinal) >= 0 || text.StartsWith("failed")
                                || text.IndexOf("FAILED", StringComparison.Ordinal) >= 0
                                || text.IndexOf("did not take effect", StringComparison.Ordinal) >= 0
                                || text.IndexOf("exited", StringComparison.Ordinal) >= 0;

                    // Everything has been applied by now. If a reboot is needed, the summary goes into
                    // the reboot dialog itself (a balloon shown just before a dialog gets hidden by it).
                    if (rebootPending)
                    {
                        var answer = MessageBox.Show(
                            string.Format("{0} mode applied:\n\n{1}\n\nGPU scheduling only changes after a reboot.\n\nReboot now?",
                                target == "game" ? "Game" : "Movie", text),
                            "ModeSwitch", MessageBoxButtons.YesNo, problem ? MessageBoxIcon.Warning : MessageBoxIcon.Question);
                        if (answer == DialogResult.Yes) Reboot();
                    }
                    else Notify(text, problem);
                });
            }
            catch { busy = false; }
        });
    }

    string DoSwitch(string target)
    {
        var log = new StringBuilder();
        bool isGame = target == "game";
        string p = isGame ? "game" : "movie";

        string note = driverNote;
        if (note != null) { log.AppendLine(note); driverNote = null; }

        // 1. NVIDIA profile settings: V-Sync (both modes) + G-Sync (per mode), then read them back
        var settings = cfg.GetSettings("vsync.both");
        settings.AddRange(cfg.GetSettings("gsync." + p));
        if (settings.Count > 0)
        {
            string err = Nv.ApplyProfileSettings(settings);
            log.AppendLine(err != null ? "NVIDIA settings FAILED: " + err : DescribeVerification(settings));
        }

        // 2. GPU clocks. The undervolt is a 127-point V/F curve that only Afterburner can apply,
        //    so copy the wanted profile into its [Startup] section and let it apply that.
        string abCfg = cfg.Get("ab.cfg", "");
        string abExe = cfg.Get("ab.exe", "");
        string abSection = cfg.Get("ab." + p + ".profile", "");
        if (abCfg.Length > 0 && abExe.Length > 0 && abSection.Length > 0)
        {
            string aerr = Ab.Apply(abCfg, abExe, cfg.Get("ab.args", "/s"), abSection,
                                   cfg.GetInt("ab." + p + ".memclk", 0),
                                   cfg.GetInt("ab." + p + ".power", 0),
                                   cfg.GetBool("ab." + p + ".stopafter", !isGame),
                                   cfg.GetInt("ab.applydelayms", 6000), log);
            if (aerr != null) log.AppendLine("Afterburner: " + aerr);
        }

        // Clear any leftover flat pstate offsets (separate from the curve).
        if (cfg.GetBool("oc." + p + ".clearoffsets", !isGame))
        {
            string cerr = Nv.SetClockOffsets(0, 0);
            if (cerr != null) log.AppendLine("clock offsets: " + cerr);
        }

        // 3. HDR
        bool hdrWanted = cfg.GetBool("hdr." + p, isGame);
        string herr = Disp.SetHdr(hdrWanted);
        if (herr != null) log.AppendLine(herr);
        foreach (var t in Disp.Targets())                 // report what the display actually reports
            if (t.HdrCapable) log.AppendLine(string.Format("HDR on {0}: {1}", t.Name, t.HdrOn ? "on" : "off"));

        // 4. Afterburner / RTSS
        foreach (string procName in cfg.Get("apps." + p + ".stop", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (Process proc in Process.GetProcessesByName(procName.Trim()))
            {
                try { proc.Kill(); log.AppendLine("stopped " + procName.Trim()); } catch { }
            }
        }
        string start = cfg.Get("apps." + p + ".start", "");
        if (start.Length > 0)
        {
            string[] parts = start.Split('|');
            if (File.Exists(parts[0]) && Process.GetProcessesByName(Path.GetFileNameWithoutExtension(parts[0])).Length == 0)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(parts[0], parts.Length > 1 ? parts[1] : "") { UseShellExecute = true });
                    log.AppendLine("started " + Path.GetFileName(parts[0]));
                }
                catch (Exception ex) { log.AppendLine("start failed: " + ex.Message); }
            }
        }

        // 5. HAGS (needs reboot)
        int hagsWanted = cfg.GetInt("hags." + p, isGame ? 2 : 1);
        int hagsNow = HagsValue();
        if (hagsNow != hagsWanted)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(HagsKey, true))
                    k.SetValue("HwSchMode", hagsWanted, RegistryValueKind.DWord);
                rebootPending = true;
                log.AppendLine("GPU scheduling: " + (hagsWanted == 2 ? "on" : "off") + " after reboot");
            }
            catch (Exception ex) { log.AppendLine("GPU scheduling: " + ex.Message + " (run elevated)"); }
        }

        mode = target;
        StoreMode();
        WriteLog(target, log.ToString().TrimEnd());
        return log.ToString().TrimEnd();
    }

    static void RunHidden(string exe, string args, StringBuilder log, string label)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (Process p = Process.Start(psi))
            {
                p.WaitForExit(10000);
                log.AppendLine(p.ExitCode == 0 ? label : label + " failed (exit " + p.ExitCode + ")");
            }
        }
        catch (Exception ex) { log.AppendLine(label + ": " + ex.Message); }
    }

    void Reboot()
    {
        rebootPending = false;
        Process.Start(new ProcessStartInfo("shutdown.exe", "/g /t 5 /c \"ModeSwitch: applying GPU scheduling change\"") { UseShellExecute = false, CreateNoWindow = true });
    }

    // ---- icon ----
    void UpdateIcon()
    {
        if (busy && pendingTarget != null)
        {
            tray.Icon = MakeIcon("", Color.FromArgb(255, 186, 8));   // sync glyph, amber
            tray.Text = "ModeSwitch - switching to " + (pendingTarget == "game" ? "Game" : "Movie") + "...";
            return;
        }
        string glyph = mode == "game" ? "" : "";           // gamepad / video
        Color colour = rebootPending ? Color.FromArgb(255, 186, 8)
                     : mode == "game" ? Color.FromArgb(118, 219, 92)
                                      : Color.FromArgb(120, 180, 255);
        tray.Icon = MakeIcon(glyph, colour);
        tray.Text = string.Format("ModeSwitch - {0} mode{1}", mode == "game" ? "Game" : "Movie", rebootPending ? " (reboot pending)" : "");
    }

    static Icon MakeIcon(string glyph, Color colour)
    {
        using (var bmp = new Bitmap(32, 32))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (var font = new Font("Segoe MDL2 Assets", 20f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(colour))
                using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(glyph, font, brush, new RectangleF(0, 0, 32, 32), fmt);
            }
            IntPtr h = bmp.GetHicon();
            using (Icon tmp = Icon.FromHandle(h))
                return (Icon)tmp.Clone();
        }
    }

    // Every switch is appended here, so a failed step can be read back afterwards.
    void WriteLog(string target, string text)
    {
        try
        {
            string path = Path.Combine(exeDir, "ModeSwitch.log");
            var sb = new StringBuilder();
            sb.AppendLine(string.Format("=== {0:yyyy-MM-dd HH:mm:ss}  switch to {1}{2} ===",
                DateTime.Now, target, headless ? " (headless)" : ""));
            sb.AppendLine(text);
            File.AppendAllText(path, sb.ToString());
        }
        catch { }
    }

    void Notify(string text, bool warn)
    {
        if (string.IsNullOrEmpty(text)) return;
        tray.BalloonTipTitle = "ModeSwitch - " + (mode == "game" ? "Game" : "Movie") + " mode";
        tray.BalloonTipText = text.Length > 250 ? text.Substring(0, 250) : text;
        tray.BalloonTipIcon = warn ? ToolTipIcon.Warning : ToolTipIcon.Info;
        tray.ShowBalloonTip(4000);
    }
}
