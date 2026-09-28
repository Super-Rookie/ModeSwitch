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
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

// ---------------------------------------------------------------- config
class Config
{
    readonly Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string Path;

    // Reads config.ini, then config.local.ini next to it (if present), then settings.ini; later files
    // win. The local file holds this home's device addresses; settings.ini is written by the
    // Settings window. Neither is in source control.
    public Config(string path)
    {
        Path = path;
        Reload();
    }

    public string SettingsPath { get { return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path), "settings.ini"); } }

    public void Reload()
    {
        map.Clear();
        Load(Path);
        Load(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path), "config.local.ini"));
        Load(SettingsPath);
    }

    // Merges `values` into settings.ini (a null value removes the key) and reloads.
    public void SaveSettings(IDictionary<string, string> values)
    {
        var all = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(SettingsPath))
            foreach (string raw in File.ReadAllLines(SettingsPath))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (line.Length == 0 || line.StartsWith("#") || eq <= 0) continue;
                all[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
        foreach (var kv in values)
        {
            if (kv.Value == null) all.Remove(kv.Key);
            else all[kv.Key] = kv.Value;
        }
        var sb = new StringBuilder();
        sb.AppendLine("# Written by ModeSwitch's Settings window. Overrides config.ini and config.local.ini.");
        foreach (var kv in all) sb.AppendLine(kv.Key + " = " + kv.Value);
        File.WriteAllText(SettingsPath, sb.ToString());
        Reload();
    }

    void Load(string path)
    {
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

    // Gives `mode` every `baseMode` setting it doesn't define itself, e.g. "hdr.movie" -> "hdr.3d",
    // "ab.movie.profile" -> "ab.3d.profile". Keys the mode sets explicitly win.
    public void Inherit(string mode, string baseMode)
    {
        var add = new List<KeyValuePair<string, string>>();
        string seg = "." + baseMode;
        foreach (var kv in map)
        {
            string k = kv.Key, nk = null;
            int i = k.IndexOf(seg + ".", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) nk = k.Substring(0, i) + "." + mode + k.Substring(i + seg.Length);
            else if (k.EndsWith(seg, StringComparison.OrdinalIgnoreCase)) nk = k.Substring(0, k.Length - seg.Length) + "." + mode;
            if (nk != null && !map.ContainsKey(nk)) add.Add(new KeyValuePair<string, string>(nk, kv.Value));
        }
        foreach (var kv in add) map[kv.Key] = kv.Value;
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

// ---------------------------------------------------------------- sound
// Presets act on the current default playback device (e.g. the TV or AV receiver over HDMI).
//  - Spatial presets (Dolby Atmos) use the documented WinRT SpatialAudioDeviceConfiguration API;
//    Windows then picks the output format itself (e.g. Dolby MAT 2.0 bitstream for Home Theater).
//  - PCM presets turn spatial sound off, then set the speaker layout and the default format
//    through the audio policy interface (IPolicyConfig), as Sound Control Panel does.
static class Snd
{
    public class Preset
    {
        public string Key, Label, Spatial;   // Spatial = format ID, or null for spatial sound off
        public int Channels, Rate, Bits;     // 0 = leave the format to Windows (Atmos Home Theater)
        public uint Mask;                    // speaker layout (Configure speakers)
        public uint FullRange;               // speakers marked full-range (same wizard, second page)
    }

    const string SpatialOff = "{00000000-0000-0000-0000-000000000000}";

    public static readonly Preset[] Presets =
    {
        // Home Theater bitstreams Atmos to a 7.1 receiver/TV, so the layout is 7.1; Windows picks the
        // HDMI format itself. Headphones renders binaural stereo, so the device is set up as stereo.
        new Preset { Key = "atmos-hometheater", Label = "Dolby Atmos for Home Theater", Spatial = Windows.Media.Audio.SpatialAudioFormatSubtype.DolbyAtmosForHomeTheater, Mask = 0x63F, FullRange = 0x633 },
        new Preset { Key = "atmos-headphones",  Label = "Dolby Atmos for Headphones",   Spatial = Windows.Media.Audio.SpatialAudioFormatSubtype.DolbyAtmosForHeadphones, Channels = 2, Rate = 96000, Bits = 24, Mask = 0x3, FullRange = 0x3 },
        // FullRange: front L/R (0x3); 7.1 adds back L/R (0x30) and side L/R (0x600).
        // Centre and LFE aren't offered as full-range by Windows' speaker setup.
        new Preset { Key = "stereo-24-96",      Label = "Stereo 24-bit 96 kHz",         Channels = 2, Rate = 96000, Bits = 24, Mask = 0x3,   FullRange = 0x3 },
        new Preset { Key = "7.1-24-96",         Label = "7.1 24-bit 96 kHz",            Channels = 8, Rate = 96000, Bits = 24, Mask = 0x63F, FullRange = 0x633 },
    };

    public static Preset Find(string key)
    {
        foreach (var p in Presets) if (p.Key.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    [StructLayout(LayoutKind.Sequential)] struct PKEY { public Guid fmt; public int pid; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] struct PV { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public uint u4; }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr fmt);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool def, out IntPtr fmt);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFmt, IntPtr mixFmt);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool def, out long a, out long b);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, ref long a);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr m);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr m);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool fx, ref PKEY k, out PV v);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool fx, ref PKEY k, ref PV v);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigClient { }

    // WinRT id "\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{guid}#{...}" -> endpoint id "{0.0.0.00000000}.{guid}"
    static string EndpointId(string winrtId)
    {
        int a = winrtId.IndexOf("MMDEVAPI#", StringComparison.OrdinalIgnoreCase);
        if (a < 0) return null;
        a += 9;
        int b = winrtId.IndexOf('#', a);
        return b > a ? winrtId.Substring(a, b - a) : winrtId.Substring(a);
    }

    public static string DeviceName(string endpointId)
    {
        try
        {
            string guid = endpointId.Substring(endpointId.LastIndexOf('.') + 1);
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\" + guid + @"\Properties"))
            {
                object name = k == null ? null : k.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2");
                return name as string ?? "default output";
            }
        }
        catch { return "default output"; }
    }

    // Makes the active playback device whose name contains `nameContains` the default (all roles).
    // Returns null on success, else a message. HDMI audio devices only appear once the display is on.
    public static string SetDefaultOutput(string nameContains, out string deviceName)
    {
        deviceName = null;
        string found = null;
        using (RegistryKey render = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render"))
        {
            if (render == null) return "no playback devices";
            foreach (string guid in render.GetSubKeyNames())
            {
                using (RegistryKey k = render.OpenSubKey(guid))
                {
                    object state = k == null ? null : k.GetValue("DeviceState");
                    if (state == null || Convert.ToInt32(state) != 1) continue;          // 1 = active
                    string ep = "{0.0.0.00000000}." + guid;
                    string name = DeviceName(ep);
                    if (name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    found = ep; deviceName = name;
                    break;
                }
            }
        }
        if (found == null) return nameContains + " audio isn't available";
        string winrtId, current;
        if (GetDefault(out winrtId, out current) && current.Equals(found, StringComparison.OrdinalIgnoreCase)) return null;
        var pc = (IPolicyConfig)new PolicyConfigClient();
        for (int role = 0; role < 3; role++)                  // console, multimedia, communications
        {
            int rc = pc.SetDefaultEndpoint(found, role);
            if (rc != 0) return string.Format("default output rc=0x{0:X}", rc);
        }
        return null;
    }

    static bool GetDefault(out string winrtId, out string endpointId)
    {
        winrtId = Windows.Media.Devices.MediaDevice.GetDefaultAudioRenderId(Windows.Media.Devices.AudioDeviceRole.Default);
        endpointId = string.IsNullOrEmpty(winrtId) ? null : EndpointId(winrtId);
        return endpointId != null;
    }

    static bool IsOff(string spatial)
    {
        return string.IsNullOrEmpty(spatial) || spatial.Equals(SpatialOff, StringComparison.OrdinalIgnoreCase);
    }

    static string SetSpatial(Windows.Media.Audio.SpatialAudioDeviceConfiguration cfg, string format)
    {
        // Wait on the WinRT operation through its own status rather than AsTask(), which needs
        // the SDK's unified Windows.winmd instead of the per-namespace files Windows ships.
        var op = cfg.SetDefaultSpatialAudioFormatAsync(format);
        var info = (Windows.Foundation.IAsyncInfo)op;
        var sw = Stopwatch.StartNew();
        while (info.Status == Windows.Foundation.AsyncStatus.Started)
        {
            if (sw.ElapsedMilliseconds > 10000) { info.Cancel(); return "timed out"; }
            System.Threading.Thread.Sleep(20);
        }
        if (info.Status != Windows.Foundation.AsyncStatus.Completed)
            return "failed (" + info.Status + ", 0x" + info.ErrorCode.HResult.ToString("X8") + ")";
        var r = op.GetResults();
        return r.Status == Windows.Media.Audio.SetDefaultSpatialAudioFormatStatus.Succeeded ? null : r.Status.ToString();
    }

    static IntPtr MakeFormat(int ch, int rate, int valid, int container, uint mask, bool isFloat)
    {
        IntPtr p = Marshal.AllocCoTaskMem(40);
        for (int i = 0; i < 40; i++) Marshal.WriteByte(p, i, 0);
        int block = ch * container / 8;
        Marshal.WriteInt16(p, 0, unchecked((short)0xFFFE));   // WAVE_FORMAT_EXTENSIBLE
        Marshal.WriteInt16(p, 2, (short)ch);
        Marshal.WriteInt32(p, 4, rate);
        Marshal.WriteInt32(p, 8, rate * block);
        Marshal.WriteInt16(p, 12, (short)block);
        Marshal.WriteInt16(p, 14, (short)container);
        Marshal.WriteInt16(p, 16, 22);
        Marshal.WriteInt16(p, 18, (short)valid);
        Marshal.WriteInt32(p, 20, unchecked((int)mask));
        byte[] sub = new Guid(isFloat ? "00000003-0000-0010-8000-00aa00389b71" : "00000001-0000-0010-8000-00aa00389b71").ToByteArray();
        Marshal.Copy(sub, 0, p + 24, 16);
        return p;
    }

    // Applies a preset to the default playback device. Returns null on success, else a message.
    public static string Apply(Preset p, out string deviceName)
    {
        deviceName = "default output";
        string winrtId, ep;
        if (!GetDefault(out winrtId, out ep)) return "no default playback device";
        deviceName = DeviceName(ep);
        var cfg = Windows.Media.Audio.SpatialAudioDeviceConfiguration.GetForDeviceId(winrtId);

        if (p.Spatial != null && (!cfg.IsSpatialAudioSupported || !cfg.IsSpatialAudioFormatSupported(p.Spatial)))
            return p.Label + " is not available on " + deviceName;

        // 1. Spatial sound off first: while it's on, it owns the output format.
        if (!IsOff(cfg.DefaultSpatialAudioFormat))
        {
            string err = SetSpatial(cfg, SpatialOff);
            if (err != null) return "turning spatial sound off: " + err;
        }

        var pc = (IPolicyConfig)new PolicyConfigClient();
        int rc;

        // 2. Speaker layout and full-range speakers (Configure speakers wizard).
        if (p.Mask != 0)
        {
            var key = new PKEY { fmt = new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), pid = 3 };      // speaker layout
            var val = new PV { vt = 19, u4 = p.Mask };                                                  // VT_UI4
            rc = pc.SetPropertyValue(ep, false, ref key, ref val);
            if (rc != 0) return string.Format("speaker layout rc=0x{0:X}", rc);

            var fullKey = new PKEY { fmt = new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), pid = 6 };  // full-range speakers
            var fullVal = new PV { vt = 19, u4 = p.FullRange };
            rc = pc.SetPropertyValue(ep, false, ref fullKey, ref fullVal);
            if (rc != 0) return string.Format("full-range speakers rc=0x{0:X}", rc);
        }

        // 3. Default format, read back to confirm. Skipped for Atmos Home Theater, where Windows
        //    chooses the HDMI bitstream format itself.
        if (p.Channels != 0)
        {
            IntPtr devFmt = MakeFormat(p.Channels, p.Rate, p.Bits, 32, p.Mask, false);
            IntPtr mixFmt = MakeFormat(p.Channels, p.Rate, 32, 32, p.Mask, true);
            try { rc = pc.SetDeviceFormat(ep, devFmt, mixFmt); }
            finally { Marshal.FreeCoTaskMem(devFmt); Marshal.FreeCoTaskMem(mixFmt); }
            if (rc != 0) return string.Format("{0}: format rc=0x{1:X} (not supported by {2}?)", p.Label, rc, deviceName);

            int ch, rate, bits;
            if (ReadFormat(pc, ep, out ch, out rate, out bits) && (ch != p.Channels || rate != p.Rate || bits != p.Bits))
                return string.Format("{0} did not take effect (device reports {1}ch {2}-bit {3} Hz)", p.Label, ch, bits, rate);
        }

        // 4. Spatial format last, on top of the layout/format set above.
        if (p.Spatial != null)
        {
            string err = SetSpatial(cfg, p.Spatial);
            if (err != null) return p.Label + ": " + err;
            if (!p.Spatial.Equals(cfg.DefaultSpatialAudioFormat, StringComparison.OrdinalIgnoreCase))
                return p.Label + " did not take effect";
        }
        return null;
    }

    static bool ReadFormat(IPolicyConfig pc, string ep, out int ch, out int rate, out int bits)
    {
        ch = rate = bits = 0;
        IntPtr f;
        if (pc.GetDeviceFormat(ep, false, out f) != 0 || f == IntPtr.Zero) return false;
        try
        {
            ch = Marshal.ReadInt16(f, 2);
            rate = Marshal.ReadInt32(f, 4);
            bits = Marshal.ReadInt16(f, 16) >= 22 ? Marshal.ReadInt16(f, 18) : Marshal.ReadInt16(f, 14);
            return true;
        }
        finally { Marshal.FreeCoTaskMem(f); }
    }

    // Describes the default device's current state, and which preset (if any) it matches.
    public static string Current(out Preset match, out string deviceName)
    {
        match = null;
        deviceName = "default output";
        string winrtId, ep;
        if (!GetDefault(out winrtId, out ep)) return "no playback device";
        deviceName = DeviceName(ep);

        var cfg = Windows.Media.Audio.SpatialAudioDeviceConfiguration.GetForDeviceId(winrtId);
        string spatial = cfg.DefaultSpatialAudioFormat;
        if (!IsOff(spatial))
        {
            foreach (var p in Presets)
                if (p.Spatial != null && p.Spatial.Equals(spatial, StringComparison.OrdinalIgnoreCase)) { match = p; return p.Label; }
            return "spatial sound " + spatial;
        }

        int ch, rate, bits;
        if (!ReadFormat((IPolicyConfig)new PolicyConfigClient(), ep, out ch, out rate, out bits)) return "unknown format";
        foreach (var p in Presets)
            if (p.Spatial == null && p.Channels == ch && p.Rate == rate && p.Bits == bits) { match = p; return p.Label; }
        string layout = ch == 2 ? "Stereo" : ch == 6 ? "5.1" : ch == 8 ? "7.1" : ch + "ch";
        return string.Format("{0} {1}-bit {2} kHz", layout, bits, rate / 1000.0);
    }
}

// ---------------------------------------------------------------- projector (Sony PJ Talk / SDCP)
// SDCP over TCP 53484: version 0x02, category 0x0A, 4-byte community, request (0x00 set / 0x01 get),
// 16-bit item number, data length, data. The reply repeats the header with 0x01 = OK in byte 6.
// Item numbers for 3D were found by reading every setting before/after changing them on the
// remote (VPL-VW760ES): 0x0060 = 2D-3D display select, 0x0061 = 3D format.
static class Pj
{
    public const int ItemPower = 0x0102, ItemDisplaySelect = 0x0060, ItemFormat3D = 0x0061;
    // Error codes seen in NG replies (last two data bytes): 0x0180 = not available in the current
    // state (e.g. 3D settings while a 4K signal is shown), 0x0101 = no such item.
    public const int ErrNotAvailable = 0x0180;

    public static string Request(string ip, string community, bool set, int item, int value, out int result)
    {
        int code;
        return Request(ip, community, set, item, value, out result, out code);
    }

    public static string Request(string ip, string community, bool set, int item, int value, out int result, out int errorCode)
    {
        result = 0;
        errorCode = 0;
        using (var c = new System.Net.Sockets.TcpClient())
        {
            var ar = c.BeginConnect(ip, 53484, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(1200) || !c.Connected) return "not reachable (" + ip + ")";
            c.EndConnect(ar);
            c.ReceiveTimeout = 3000; c.SendTimeout = 3000;
            var s = c.GetStream();
            byte[] com = Encoding.ASCII.GetBytes((community + "    ").Substring(0, 4));
            var pkt = new List<byte> { 0x02, 0x0A, com[0], com[1], com[2], com[3], (byte)(set ? 0x00 : 0x01), (byte)(item >> 8), (byte)item };
            if (set) { pkt.Add(2); pkt.Add((byte)(value >> 8)); pkt.Add((byte)value); } else pkt.Add(0);
            s.Write(pkt.ToArray(), 0, pkt.Count);
            var buf = new byte[64];
            int n = s.Read(buf, 0, buf.Length);
            if (n < 10) return "short reply";
            if (buf[6] != 0x01)
            {
                errorCode = n >= 12 ? (buf[10] << 8) | buf[11] : -1;
                return errorCode == ErrNotAvailable
                    ? string.Format("item 0x{0:X4} not available right now", item)
                    : string.Format("item 0x{0:X4} refused (error 0x{1:X4})", item, errorCode);
            }
            if (!set && n >= 12) result = (buf[10] << 8) | buf[11];
            return null;
        }
    }

    public static string Get(string ip, string community, int item, out int value) { return Request(ip, community, false, item, 0, out value); }
    public static string Get(string ip, string community, int item, out int value, out int errorCode) { return Request(ip, community, false, item, 0, out value, out errorCode); }
    public static string Set(string ip, string community, int item, int value) { int d; return Request(ip, community, true, item, value, out d); }
    public static string Set(string ip, string community, int item, int value, out int errorCode) { int d; return Request(ip, community, true, item, value, out d, out errorCode); }

    public static string Describe(int displaySelect, int format)
    {
        string fmt = format == 1 ? "Side-by-Side" : format == 2 ? "Over-Under" : format == 0 ? "Simulated 3D" : "format " + format;
        return displaySelect == 1 ? "3D, " + fmt : displaySelect == 0 ? "2D (Auto)" : displaySelect == 2 ? "2D" : "display select " + displaySelect;
    }
}

// ---------------------------------------------------------------- ISO mounting
// Mounts an ISO through the virtual-disk API without "permanent lifetime": the mount lasts only
// while the handle is open, so Windows ejects it automatically when we close it - or if the
// app exits or crashes mid-film. Needs admin rights, which the tray app has.
sealed class IsoMount : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct VIRTUAL_STORAGE_TYPE { public uint DeviceId; public Guid VendorId; }
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    static extern int OpenVirtualDisk(ref VIRTUAL_STORAGE_TYPE type, string path, uint access, uint flags, IntPtr parameters, out IntPtr handle);
    [DllImport("virtdisk.dll")]
    static extern int AttachVirtualDisk(IntPtr handle, IntPtr securityDescriptor, uint flags, uint providerFlags, IntPtr parameters, IntPtr overlapped);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    const uint VIRTUAL_STORAGE_TYPE_DEVICE_ISO = 1;
    const uint VIRTUAL_DISK_ACCESS_READ = 0x000d0000;
    const uint ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY = 0x1;

    IntPtr handle = IntPtr.Zero;
    public string Drive;                    // e.g. "E:\"

    // Returns null on success (Drive is set), else a message.
    public string Mount(string isoPath)
    {
        var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in DriveInfo.GetDrives()) before.Add(d.Name);

        var type = new VIRTUAL_STORAGE_TYPE { DeviceId = VIRTUAL_STORAGE_TYPE_DEVICE_ISO, VendorId = new Guid("EC984AEC-A0F9-47e9-901F-71415A66345B") };
        int rc = OpenVirtualDisk(ref type, isoPath, VIRTUAL_DISK_ACCESS_READ, 0, IntPtr.Zero, out handle);
        if (rc != 0) { handle = IntPtr.Zero; return "can't open the ISO (error " + rc + ")"; }
        rc = AttachVirtualDisk(handle, IntPtr.Zero, ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY, 0, IntPtr.Zero, IntPtr.Zero);
        if (rc != 0)
        {
            Dispose();
            return rc == 32 ? "the ISO is already mounted - eject it first" : "can't mount the ISO (error " + rc + ")";
        }

        // Wait for the new drive letter to appear and become readable.
        for (int i = 0; i < 60; i++)
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (before.Contains(d.Name) || d.DriveType != DriveType.CDRom) continue;
                try { if (d.IsReady) { Drive = d.Name; return null; } } catch { }
            }
            System.Threading.Thread.Sleep(250);
        }
        Dispose();
        return "the ISO mounted but no drive letter appeared";
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; }   // detaches (no permanent lifetime)
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

    // ---- resolution per display (3D Movie mode needs the projector at 1080p) ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SNAME { public HDRHDR h; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string gdi; }
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] static extern int GetSourceName(ref SNAME r);

    // GDI device name (e.g. \\.\DISPLAY1) of the active display whose monitor name contains `nameContains`;
    // an empty `nameContains` means the primary display. Also returns the monitor's name.
    public static string FindDisplay(string nameContains, out string monitorName)
    {
        monitorName = null;
        uint pc, mc;
        if (GetDisplayConfigBufferSizes(2, out pc, out mc) != 0) return null;
        var ps = new PATH[pc]; var ms = new MODE[mc];
        if (QueryDisplayConfig(2, ref pc, ps, ref mc, ms, IntPtr.Zero) != 0) return null;
        for (int i = 0; i < pc; i++)
        {
            var n = new TNAME(); n.h.type = 2; n.h.size = Marshal.SizeOf(typeof(TNAME)); n.h.a = ps[i].t.a; n.h.id = ps[i].t.id;
            GetName(ref n);
            var s = new SNAME(); s.h.type = 1; s.h.size = Marshal.SizeOf(typeof(SNAME)); s.h.a = ps[i].s.a; s.h.id = ps[i].s.id;
            if (GetSourceName(ref s) != 0) continue;
            bool match = string.IsNullOrEmpty(nameContains)
                ? System.Windows.Forms.Screen.PrimaryScreen.DeviceName.Equals(s.gdi, StringComparison.OrdinalIgnoreCase)
                : (n.name ?? "").IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0;
            if (match) { monitorName = (n.name ?? "").Trim(); return s.gdi; }
        }
        return null;
    }

    // Sets resolution and refresh together. Returns null on success, else a message.
    public static string SetMode(string device, int width, int height, int hz)
    {
        bool available = false;
        for (int m = 0; ; m++)
        {
            var dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (!EnumDisplaySettings(device, m, ref dm)) break;
            if (dm.dmPelsWidth == width && dm.dmPelsHeight == height && dm.dmDisplayFrequency == hz) { available = true; break; }
        }
        if (!available) return string.Format("{0}x{1} @ {2} Hz is not offered by this display", width, height, hz);

        var cur = new DEVMODE(); cur.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        if (!EnumDisplaySettings(device, -1, ref cur)) return "cannot read current mode";
        if (cur.dmPelsWidth == width && cur.dmPelsHeight == height && cur.dmDisplayFrequency == hz) return null;   // already there
        cur.dmPelsWidth = width;
        cur.dmPelsHeight = height;
        cur.dmDisplayFrequency = hz;
        cur.dmFields = 0x80000 | 0x100000 | 0x400000;          // DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY
        int rc = ChangeDisplaySettingsEx(device, ref cur, IntPtr.Zero, 0x00000001, IntPtr.Zero);   // CDS_UPDATEREGISTRY
        return rc == 0 ? null : "ChangeDisplaySettingsEx rc=" + rc;
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
    volatile string pendingRoom;         // room being switched to, for the icon
    string room;                         // "tv" | "theatre"
    Control syncCtl;                     // marshals results back to the UI thread

    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // ModeSwitch.exe --sound <preset>   applies a sound preset and exits; the result goes to the log
        if (args.Length >= 2 && args[0].Equals("--sound", StringComparison.OrdinalIgnoreCase))
        {
            var app = new ModeSwitchApp(true);
            string result = ApplySound(args[1]);
            app.WriteLog("sound " + args[1], result);
            return result.IndexOf("FAILED", StringComparison.Ordinal) >= 0 || result.StartsWith("Sound: unknown") ? 1 : 0;
        }

        // ModeSwitch.exe --play3d <file>   3D Movie mode, mount (ISO), play, eject, switch back, exit
        if (args.Length >= 2 && args[0].Equals("--play3d", StringComparison.OrdinalIgnoreCase))
        {
            EnsureNetworkDrives(null);                     // in case this runs elevated and the file is on V:\ etc.
            if (!File.Exists(args[1])) return 2;
            new ModeSwitchApp(true).Play3D(Path.GetFullPath(args[1]));
            return 0;
        }

        // ModeSwitch.exe --room tv|theatre   powers the room's devices, moves Windows' display and sound
        if (args.Length >= 2 && args[0].Equals("--room", StringComparison.OrdinalIgnoreCase))
        {
            var roomApp = new ModeSwitchApp(true);
            string text = roomApp.DoRoomSwitch(ParseRoom(args[1]));
            return IsProblem(text) ? 1 : 0;
        }

        // ModeSwitch.exe --apply movie|game   applies a mode and exits (used by the uninstaller)
        if (args.Length >= 2 && args[0].Equals("--apply", StringComparison.OrdinalIgnoreCase))
        {
            string target = ParseMode(args[1]);
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
        cfg.Inherit("3d", "movie");            // 3D Movie = Movie plus its own overrides (e.g. display.3d)
        mode = ReadStoredMode();
        room = ReadStoredRoom();
        InitBootHags();
        if (headless) return;
        syncCtl = new Control();
        { IntPtr forceHandle = syncCtl.Handle; }   // create the handle so BeginInvoke works
        tray.Visible = true;
        roomSound = new RoomSoundMenu(() => cfg.Get("tv.ip", ""), () => cfg.Get("avr.ip", ""), () => cfg.GetInt("avr.volume.max", 0),
                                      t => OpenTv(t, 5000), syncCtl);
        // Left-click opens the same menu as right-click. Nothing switches on a click by itself, so a
        // stray click can't change modes (and GPU scheduling) by accident. NotifyIcon only opens
        // its menu on right-click; its private ShowContextMenu positions it the same way.
        tray.MouseUp += (s, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            var show = typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (show != null) show.Invoke(tray, null);
        };
        BuildMenu();
        UpdateIcon();
        CheckDriverVersion();

        var sync = new Timer();                         // follow mode changes made by --apply / --play3d
        sync.Interval = 3000;
        sync.Tick += (s, e) =>
        {
            SyncStoredMode();
            FollowRoomDisplay();
            // Never leave the "switching" icon up once nothing is switching.
            if (!busy && iconShowsBusy) { pendingRoom = null; pendingTarget = null; UpdateIcon(); }
        };
        sync.Start();

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
                    if (v != null) return ParseMode((string)v);
                }
        }
        catch { }
        return HagsValue() == 2 ? "game" : "movie";
    }

    static string ParseMode(string s)
    {
        s = (s ?? "").Trim().ToLowerInvariant();
        return s == "game" ? "game" : s == "3d" ? "3d" : "movie";
    }

    static string ModeName(string m)
    {
        return m == "game" ? "Game" : m == "3d" ? "3D Movie" : "Movie";
    }

    static string ParseRoom(string s)
    {
        s = (s ?? "").Trim().ToLowerInvariant();
        return s == "tv" ? "tv" : "theatre";
    }

    static string RoomName(string r)
    {
        return r == "tv" ? "TV" : "Theatre";
    }

    // Stored room, or on first run whichever display is in use.
    string ReadStoredRoom()
    {
        string r = ReadReg("Room");
        if (r == "tv" || r == "theatre") return r;
        try
        {
            string theatreDisplay = cfg.Get("room.theatre.display", "SONY");
            foreach (string n in DispTopo.Active())
                if (n.IndexOf(theatreDisplay, StringComparison.OrdinalIgnoreCase) >= 0) return "theatre";
        }
        catch { }
        return "tv";
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

    // Condenses a switch log into a notification. Windows cuts balloon text at ~255 characters,
    // so each step becomes one short line, problems go first (never the part that gets cut),
    // and routine lines (processes stopped) are left to the log.
    static string Summarize(string log)
    {
        var problems = new List<string>();
        var lines = new List<string>();
        var hdr = new List<string>();
        foreach (string raw in log.Split('\n'))
        {
            string l = raw.Trim();
            if (l.Length == 0) continue;
            if (l.IndexOf("FAILED", StringComparison.Ordinal) >= 0 || l.IndexOf("rc=", StringComparison.Ordinal) >= 0
                || l.IndexOf("did not take effect", StringComparison.Ordinal) >= 0 || l.IndexOf("exited", StringComparison.Ordinal) >= 0)
            { problems.Add(l); continue; }
            if (l.StartsWith("stopped ") || l.StartsWith("Run bin\\")) continue;
            if (l.EndsWith("not connected - resolution unchanged")) continue;   // normal while on the TV
            if (l.StartsWith("Projector: ") && l.EndsWith("settings skipped")) continue;   // projector off / TV in use

            Match m;
            if ((m = Regex.Match(l, @"^NVIDIA driver changed: (.+)$")).Success) lines.Add("Driver updated: " + m.Groups[1].Value);
            else if ((m = Regex.Match(l, @"^NVIDIA settings applied and verified \(driver (.+)\)$")).Success) lines.Add("NVIDIA settings verified (" + m.Groups[1].Value + ")");
            else if ((m = Regex.Match(l, @"^Afterburner: (\S+) applied(.*?)(, then closed)?$")).Success) lines.Add("Afterburner: " + m.Groups[1].Value + m.Groups[2].Value);
            else if ((m = Regex.Match(l, @"^HDR on (.+): (on|off)$")).Success)
            {
                if (hdr.Count == 0) lines.Add("\0HDR");          // placeholder keeps HDR in its place
                hdr.Add(m.Groups[2].Value + " (" + m.Groups[1].Value + ")");
            }
            else if ((m = Regex.Match(l, @"^Sound: (.+) on (.+)$")).Success) lines.Add("Sound: " + m.Groups[1].Value.Replace("Dolby Atmos for ", "Atmos "));
            else if (l.StartsWith("GPU scheduling: ")) lines.Add(l.Replace(" after reboot", " (after reboot)"));
            else lines.Add(l);
        }
        int at = lines.IndexOf("\0HDR");
        if (at >= 0) lines[at] = "HDR: " + string.Join(", ", hdr.ToArray());
        problems.AddRange(lines);
        return string.Join("\n", problems.ToArray());
    }

    // Sets projector items (item:value pairs) over the network and reads them back.
    // Returns a log line (null if no projector is configured); ok = everything applied.
    // retry = keep trying for a few seconds (the projector may still be re-syncing to a new signal).
    string ApplyProjector(List<KeyValuePair<uint, uint>> items, bool retry, out bool ok)
    {
        ok = false;
        string ip = cfg.Get("projector.ip", ""), com = cfg.Get("projector.community", "SONY");
        if (ip.Length == 0 || items.Count == 0) return null;
        try
        {
            int power;
            string err = Pj.Get(ip, com, Pj.ItemPower, out power);
            if (err != null) return "Projector: " + err + " - settings skipped";
            if (power != 3) return "Projector: not switched on - settings skipped";

            bool offAnyway = false;          // "2D" refused because 3D isn't available at this signal at all
            foreach (var kv in items)
            {
                bool isOff = kv.Key == Pj.ItemDisplaySelect && kv.Value == 0;
                string e = null;
                int code = 0;
                for (int attempt = 0; attempt < (retry ? 4 : 1); attempt++)
                {
                    e = Pj.Set(ip, com, (int)kv.Key, (int)kv.Value, out code);
                    if (e == null || (isOff && code == Pj.ErrNotAvailable)) break;
                    if (retry) System.Threading.Thread.Sleep(1000);
                }
                if (e == null) continue;
                if (isOff && code == Pj.ErrNotAvailable) { offAnyway = true; continue; }
                return "Projector FAILED: " + e;
            }

            System.Threading.Thread.Sleep(500);
            foreach (var kv in items)
            {
                bool isOff = kv.Key == Pj.ItemDisplaySelect && kv.Value == 0;
                int v, code;
                string e = Pj.Get(ip, com, (int)kv.Key, out v, out code);
                if (e == null && v != (int)kv.Value)
                    return string.Format("Projector: item 0x{0:X4} did not take effect (reads {1}, wanted {2})", kv.Key, v, kv.Value);
                if (e != null && !(isOff && code == Pj.ErrNotAvailable))
                    return "Projector: could not confirm - " + e;
            }
            ok = true;
            string preset = "";
            foreach (var kv in items)
                if (kv.Key == PjPicture.Preset) preset = PjPicture.PresetName((int)kv.Value) + ", ";
            int ds, fmt;
            if (Pj.Get(ip, com, Pj.ItemDisplaySelect, out ds) == null && Pj.Get(ip, com, Pj.ItemFormat3D, out fmt) == null)
                return "Projector: " + preset + Pj.Describe(ds, fmt);
            return "Projector: " + preset + (offAnyway ? "2D (3D isn't available at this resolution)" : "2D");
        }
        catch (Exception ex) { return "Projector FAILED: " + ex.Message; }
    }

    static List<KeyValuePair<uint, uint>> Projector3D(int format)
    {
        return new List<KeyValuePair<uint, uint>>
        {
            new KeyValuePair<uint, uint>(Pj.ItemDisplaySelect, 1),
            new KeyValuePair<uint, uint>(Pj.ItemFormat3D, (uint)format)
        };
    }

    // Frame-compatible files usually say so in the name: 1 = Side-by-Side, 2 = Over-Under.
    // Everything else (ISO / MVC) returns 0 = frame packing when madVR outputs real frame-packed 3D
    // (play3d.framepacking), which the projector detects by itself; otherwise Over-Under, matching
    // madVR's top-and-bottom output.
    static int Format3DFromName(string path, bool framePacking)
    {
        string n = Path.GetFileNameWithoutExtension(path);
        if (Regex.IsMatch(n, @"(?i)(^|[\W_])(h-?sbs|half-?sbs|f-?sbs|full-?sbs|sbs|side[\W_]?by[\W_]?side)([\W_]|$)")) return 1;
        if (Regex.IsMatch(n, @"(?i)(^|[\W_])(h-?ou|half-?ou|f-?ou|full-?ou|ou|h-?tab|half-?tab|f-?tab|full-?tab|tab|top[\W_]?(and|&)?[\W_]?bottom|over[\W_]?under)([\W_]|$)")) return 2;
        return framePacking ? 0 : 2;
    }

    // "1920x1080@23" on the display named by display.target (blank = primary). Returns a log line.
    static string ApplyResolution(string spec, string target)
    {
        var m = Regex.Match(spec, @"^\s*(\d+)\s*x\s*(\d+)\s*@\s*(\d+)\s*$");
        if (!m.Success) return "Display: can't read '" + spec + "' (expected e.g. 1920x1080@23)";
        int w = int.Parse(m.Groups[1].Value), h = int.Parse(m.Groups[2].Value), hz = int.Parse(m.Groups[3].Value);
        try
        {
            string name;
            string dev = Disp.FindDisplay(target, out name);
            if (dev == null)
                return string.Format("Display: {0} not connected - resolution unchanged", target.Length > 0 ? target : "primary display");
            string err = Disp.SetMode(dev, w, h, hz);
            if (err != null) return "Display FAILED: " + name + ": " + err;
            System.Threading.Thread.Sleep(2500);             // let the HDMI link settle before HDR/sound
            return string.Format("Display: {0} {1}x{2} @ {3} Hz", name, w, h, hz);
        }
        catch (Exception ex) { return "Display FAILED: " + ex.Message; }
    }

    // Applies a sound preset and returns a log/notification line.
    static string ApplySound(string key)
    {
        var preset = Snd.Find(key);
        if (preset == null) return "Sound: unknown preset '" + key + "' (see config.ini)";
        try
        {
            string device;
            string err = null;
            for (int attempt = 0; attempt < 3; attempt++)   // the HDMI audio device can be mid-reset
            {
                err = Snd.Apply(preset, out device);
                if (err == null) return string.Format("Sound: {0} on {1}", preset.Label, device);
                System.Threading.Thread.Sleep(1500);
            }
            return "Sound FAILED: " + err;
        }
        catch (Exception ex) { return "Sound FAILED: " + ex.Message; }
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

    // ---- is a reboot really pending? ----
    // HAGS only changes at boot, so compare the registry with the value it had when Windows started:
    // switching Game -> Movie without rebooting puts it back, and then no reboot is needed.
    [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
    int bootHags = -1;

    void InitBootHags()
    {
        DateTime boot = DateTime.Now - TimeSpan.FromMilliseconds(GetTickCount64());
        string stamp = boot.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        string storedStamp = ReadReg("BootTime");
        int stored;
        DateTime storedBoot;
        if (storedStamp != null && int.TryParse(ReadReg("BootHags") ?? "", out stored)
            && DateTime.TryParseExact(storedStamp, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out storedBoot)
            && Math.Abs((storedBoot - boot).TotalMinutes) <= 2)
            bootHags = stored;                               // same boot: keep what was active at boot
        else
        {
            bootHags = HagsValue();                          // first start since boot: registry = active value
            WriteReg("BootTime", stamp);
            WriteReg("BootHags", bootHags.ToString(CultureInfo.InvariantCulture));
        }
        rebootPending = HagsValue() != bootHags;
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
        string headerText = busy && pendingRoom != null ? "Switching to the " + RoomName(pendingRoom) + " room..."
            : busy && pendingTarget != null ? "Switching to " + ModeName(pendingTarget) + "..."
            : string.Format("Mode: {0}, {1} room{2}", ModeName(mode), RoomName(room), rebootPending ? "  (reboot pending)" : "");
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
        var movie3d = new ToolStripMenuItem("3D Movie mode", null, (s, e) => Switch("3d"));
        movie3d.Checked = mode == "3d";
        movie3d.Enabled = !busy;
        menu.Items.Add(movie);
        menu.Items.Add(movie3d);
        menu.Items.Add(game);
        string nowPlaying = playingTitle;
        var play3d = new ToolStripMenuItem(nowPlaying != null ? "Playing in 3D: " + nowPlaying : "Play 3D Blu-ray / 3D film...", null, (s, e) => Play3DFromMenu());
        play3d.Enabled = !busy && !playing;
        menu.Items.Add(play3d);
        menu.Items.Add(new ToolStripSeparator());

        var tvRoom = new ToolStripMenuItem("TV room", null, (s, e) => SwitchRoom("tv"));
        tvRoom.Checked = room == "tv";
        tvRoom.Enabled = !busy && !playing;
        var theatreRoom = new ToolStripMenuItem("Theatre room", null, (s, e) => SwitchRoom("theatre"));
        theatreRoom.Checked = room == "theatre";
        theatreRoom.Enabled = !busy && !playing;
        menu.Items.Add(tvRoom);
        menu.Items.Add(theatreRoom);
        if (roomSound != null) menu.Items.Add(roomSound.Build());
        var setup = new ToolStripMenuItem("Room setup");
        var subwoofer = new ToolStripMenuItem(SubwooferLabel(), null, (s, e) => ToggleSubwoofer());
        subwoofer.Checked = subState == 1;
        setup.DropDownItems.Add(subwoofer);
        subItem = subwoofer;
        setup.DropDownOpening += (s, e) => RefreshSubwoofer();
        setup.DropDownItems.Add(new ToolStripSeparator());
        setup.DropDownItems.Add(new ToolStripMenuItem("Check devices", null, (s, e) => CheckRoomDevices()));
        setup.DropDownItems.Add(new ToolStripMenuItem("Pair TV (accept the prompt on the TV)", null, (s, e) => PairTv()));
        setup.DropDownItems.Add(new ToolStripMenuItem("Subwoofer plug login...", null, (s, e) => AskTapoLogin()));        menu.Items.Add(setup);
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
        menu.Items.Add(BuildSoundMenu());
        var pjMenu = BuildProjectorMenu();
        if (pjMenu != null) menu.Items.Add(pjMenu);
        menu.Items.Add(new ToolStripSeparator());

        if (rebootPending) menu.Items.Add(new ToolStripMenuItem("Reboot now", null, (s, e) => Reboot()));
        menu.Items.Add(new ToolStripMenuItem("NVIDIA Control Panel", null, (s, e) =>
            OpenAsUser(cfg.Get("open.nvcp", @"shell:AppsFolder\NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj!NVIDIACorp.NVIDIAControlPanel"))));
        menu.Items.Add(new ToolStripMenuItem("Windows display settings", null, (s, e) =>
            OpenAsUser(cfg.Get("open.display", "ms-settings:display"))));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Settings...", null, (s, e) => ShowSettings()));
        menu.Items.Add(new ToolStripMenuItem("Open config.ini", null, (s, e) => Process.Start("notepad.exe", cfg.Path)));
        string localCfg = Path.Combine(exeDir, "config.local.ini");
        if (File.Exists(localCfg)) menu.Items.Add(new ToolStripMenuItem("Open config.local.ini", null, (s, e) => Process.Start("notepad.exe", localCfg)));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => { tray.Visible = false; Application.Exit(); }));
    }

    // "Projector: <state>" with 2D / 3D Over-Under / 3D Side-by-Side, when projector.ip is set.
    ToolStripMenuItem BuildProjectorMenu()
    {
        string ip = cfg.Get("projector.ip", ""), com = cfg.Get("projector.community", "SONY");
        if (ip.Length == 0) return null;

        int power = -1, ds = -1, fmt = -1, preset = -1;
        bool in3D = false;
        string err = null;
        try
        {
            err = Pj.Get(ip, com, Pj.ItemPower, out power);
            if (err == null && power == 3)
            {
                int code, depth;
                string e = Pj.Get(ip, com, Pj.ItemDisplaySelect, out ds, out code);
                if (e != null && code == Pj.ErrNotAvailable) ds = -2;          // 4K signal: 3D settings greyed out
                else if (e != null) err = e;
                else err = Pj.Get(ip, com, Pj.ItemFormat3D, out fmt);
                if (Pj.Get(ip, com, PjPicture.Preset, out preset) != null) preset = -1;
                in3D = Pj.Get(ip, com, PjPicture.Depth3D, out depth) == null;     // only answers while showing 3D
            }
        }
        catch (Exception ex) { err = ex.Message; }

        string state = err != null ? "not reachable" : power != 3 ? "standby" : ds == -2 ? "2D (3D needs 1080p)" : Pj.Describe(ds, fmt);
        if (err == null && power == 3 && preset >= 0) state += ", " + PjPicture.PresetName(preset);
        var item = new ToolStripMenuItem("Projector: " + state);
        bool on = err == null && power == 3;

        var choices = new[]
        {
            new { Label = "2D (Auto)",        Items = new List<KeyValuePair<uint, uint>> { new KeyValuePair<uint, uint>(Pj.ItemDisplaySelect, 0) }, Checked = ds == 0 || ds == -2 },
            new { Label = "3D Over-Under",    Items = Projector3D(2), Checked = ds == 1 && fmt == 2 },
            new { Label = "3D Side-by-Side",  Items = Projector3D(1), Checked = ds == 1 && fmt == 1 },
        };
        foreach (var c in choices)
        {
            var choice = c;
            var mi = new ToolStripMenuItem(choice.Label, null, (s, e) =>
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    bool ok;
                    string line = ApplyProjector(choice.Items, true, out ok) ?? "Projector: not configured";
                    if (!ok && line.IndexOf("not available", StringComparison.Ordinal) >= 0)
                        line = "Projector: 3D is only available at 1080p - use 3D Movie mode";
                    WriteLog("projector " + choice.Label, line);
                    Say(line, !ok);
                }));
            mi.Checked = choice.Checked;
            mi.Enabled = on;
            item.DropDownItems.Add(mi);
        }

        // Picture presets, listed right here under a heading. The projector keeps a separate preset
        // for 3D, so in 3D these pick the 3D one.
        item.DropDownItems.Add(new ToolStripSeparator());
        var heading = new ToolStripMenuItem(in3D ? "Picture preset (3D):" : "Picture preset:");
        heading.Enabled = false;
        item.DropDownItems.Add(heading);
        for (int i = 0; i < PjPicture.Presets.Length; i++)
        {
            int value = i;
            bool threeD = in3D;
            var pi = new ToolStripMenuItem(PjPicture.Presets[i], null, (s, e) =>
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    bool ok;
                    string line = ApplyProjector(new List<KeyValuePair<uint, uint>> { new KeyValuePair<uint, uint>(PjPicture.Preset, (uint)value) }, true, out ok)
                                  ?? "Projector: not configured";
                    WriteLog("projector preset " + PjPicture.Presets[value], line);
                    Say(ok ? "Projector picture: " + PjPicture.Presets[value] + (threeD ? " (3D)" : "") : line, !ok);
                }));
            pi.Checked = i == preset;
            pi.Enabled = on;
            item.DropDownItems.Add(pi);
        }
        item.DropDownItems.Add(new ToolStripMenuItem("Picture settings...", null, (s, e) => ShowSettings()));

        item.DropDownItems.Add(new ToolStripSeparator());
        item.DropDownItems.Add(new ToolStripMenuItem("Open projector web page", null, (s, e) => OpenAsUser("http://" + ip + "/")));
        return item;
    }

    // "Sound: <current>" with the presets underneath, for the current default playback device.
    ToolStripMenuItem BuildSoundMenu()
    {
        Snd.Preset current = null;
        string device = "default output", state;
        try { state = Snd.Current(out current, out device); }
        catch (Exception ex) { state = "unavailable (" + ex.Message + ")"; }

        var item = new ToolStripMenuItem("Sound: " + state);
        var output = new ToolStripMenuItem("Output: " + device);
        output.Enabled = false;
        item.DropDownItems.Add(output);
        item.DropDownItems.Add(new ToolStripSeparator());
        foreach (var p in Snd.Presets)
        {
            Snd.Preset preset = p;
            var mi = new ToolStripMenuItem(p.Label, null, (s, e) =>
            {
                // Runs in the background: setting a format can take a second or two.
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    string text = ApplySound(preset.Key);
                    WriteLog("sound " + preset.Key, text);
                    try { syncCtl.BeginInvoke((MethodInvoker)delegate { Notify(text, text.IndexOf("FAILED", StringComparison.Ordinal) >= 0); }); }
                    catch { }
                });
            });
            mi.Checked = current != null && current.Key == p.Key;
            item.DropDownItems.Add(mi);
        }
        return item;
    }

    // ---- 3D playback ----
    volatile bool playing;
    volatile string playingTitle;

    // ---- network drives ----
    // Mapped drives belong to the normal (non-admin) logon session; this app runs elevated, so it
    // doesn't see them. Reconnect the user's persistent mappings (HKCU\Network) in this session -
    // temporarily, with the user's saved credentials - so V:\ etc. work in the file dialog and paths.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NETRESOURCE { public int dwScope, dwType, dwDisplayType, dwUsage; public string lpLocalName, lpRemoteName, lpComment, lpProvider; }
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    static extern int WNetAddConnection2(ref NETRESOURCE nr, string password, string user, int flags);

    static List<string> EnsureNetworkDrives(StringBuilder log)
    {
        var shares = new List<string>();
        try
        {
            using (RegistryKey net = Registry.CurrentUser.OpenSubKey("Network"))
            {
                if (net == null) return shares;
                foreach (string letter in net.GetSubKeyNames())
                {
                    string remote;
                    using (RegistryKey k = net.OpenSubKey(letter)) remote = k == null ? null : k.GetValue("RemotePath") as string;
                    if (string.IsNullOrEmpty(remote) || letter.Length != 1) continue;
                    shares.Add(remote);
                    string root = letter.ToUpperInvariant() + ":";
                    if (Directory.Exists(root + "\\")) continue;              // already visible here
                    var nr = new NETRESOURCE { dwType = 1, lpLocalName = root, lpRemoteName = remote };   // RESOURCETYPE_DISK
                    int rc = WNetAddConnection2(ref nr, null, null, 0);        // 0 = temporary, not remembered
                    if (log != null) log.AppendLine(rc == 0 ? "connected " + root + " (" + remote + ")" : "couldn't connect " + root + " (" + remote + "), error " + rc);
                }
            }
        }
        catch (Exception ex) { if (log != null) log.AppendLine("network drives: " + ex.Message); }
        return shares;
    }

    void Play3DFromMenu()
    {
        string file = null;
        var netLog = new StringBuilder();
        List<string> shares = EnsureNetworkDrives(netLog);
        if (netLog.Length > 0) WriteLog("network drives", netLog.ToString().TrimEnd());
        using (var dlg = new OpenFileDialog())
        {
            dlg.Title = "Play in 3D";
            foreach (string unc in shares) { try { dlg.CustomPlaces.Add(unc); } catch { } }   // fallback shortcuts in the sidebar
            dlg.Filter = "3D Blu-ray ISO or 3D video (*.iso;*.mkv;*.m2ts;*.mp4)|*.iso;*.mkv;*.m2ts;*.mp4|All files (*.*)|*.*";
            string last = ReadReg("Last3DFolder");
            if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) dlg.InitialDirectory = last;
            // A tray app has no window of its own; a hidden topmost owner keeps the dialog in front.
            using (var owner = new Form { TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None,
                                          StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000), Size = new Size(1, 1) })
            {
                owner.Show();
                owner.Activate();
                if (dlg.ShowDialog(owner) == DialogResult.OK) file = dlg.FileName;
            }
        }
        if (file == null) return;
        WriteReg("Last3DFolder", Path.GetDirectoryName(file));
        System.Threading.ThreadPool.QueueUserWorkItem(delegate { Play3D(file); });
    }

    // Switch to 3D Movie, mount an ISO if needed, play fullscreen, then eject and switch back.
    // Runs on a background thread (or in the --play3d command-line process).
    void Play3D(string file)
    {
        if (playing) return;
        playing = true;
        playingTitle = Path.GetFileNameWithoutExtension(file);
        string previous = mode;
        bool keepHags = previous == "game";          // no reboot either way
        IsoMount iso = null;
        var log = new StringBuilder();
        try
        {
            if (room != "theatre")                                    // 3D only works on the projector
            {
                RunRoomSwitch("theatre");
                WaitForProjector();
            }
            if (mode != "3d") RunSwitch("3d", keepHags, false);

            // Projector: frame-packed 3D (ISO / MVC) is detected by the projector itself, so leave it on
            // Auto; frame-compatible files (named SBS / OU / TAB) need 3D forced in the right format.
            int fmt = Format3DFromName(file, cfg.GetBool("play3d.framepacking", false));
            bool pjOk;
            var pjWanted = fmt == 0
                ? new List<KeyValuePair<uint, uint>> { new KeyValuePair<uint, uint>(Pj.ItemDisplaySelect, 0) }
                : Projector3D(fmt);
            // The projector keeps a separate preset for 3D; set it once 3D is on (Settings > Projector 3D).
            int preset3d = cfg.GetInt("pj.3dplay.preset", -1);
            if (fmt != 0 && preset3d >= 0 && preset3d < PjPicture.Presets.Length)
                pjWanted.Add(new KeyValuePair<uint, uint>(PjPicture.Preset, (uint)preset3d));
            string pjLine = ApplyProjector(pjWanted, true, out pjOk);
            if (pjLine != null) log.AppendLine(pjLine);
            string pjNote = !pjOk ? cfg.Get("reminder.3d", "Set the projector's 3D format to match the film.")
                          : fmt == 0 ? "Frame-packed 3D - the projector switches to 3D by itself"
                          : "Projector: 3D, " + (fmt == 1 ? "Side-by-Side" : "Over-Under");

            string target = file;
            if (file.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            {
                iso = new IsoMount();
                string err = iso.Mount(file);
                if (err != null) { Say("3D FAILED: " + err, true); log.AppendLine("mount FAILED: " + err); return; }
                target = Path.Combine(iso.Drive, @"BDMV\index.bdmv");
                if (!File.Exists(target)) { Say("3D FAILED: " + Path.GetFileName(file) + " isn't a Blu-ray (no BDMV folder)", true); log.AppendLine("no BDMV\\index.bdmv on " + iso.Drive); return; }
                log.AppendLine("mounted " + Path.GetFileName(file) + " as " + iso.Drive.TrimEnd('\\'));
            }

            string player = PlayerPath();
            if (!File.Exists(player)) { Say("3D FAILED: player not found: " + player, true); log.AppendLine("player not found: " + player); return; }
            string args = "\"" + target + "\" " + cfg.Get("play3d.args", "/fullscreen /play");
            Process p = Process.Start(new ProcessStartInfo(player, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(player) });
            log.AppendLine("playing " + target + " with " + Path.GetFileName(player));
            Say("Playing " + playingTitle + " in 3D\n" + pjNote, false);

            WaitForPlayer(p, Path.GetFileNameWithoutExtension(player));
            log.AppendLine("player closed");
        }
        catch (Exception ex) { log.AppendLine("FAILED: " + ex.Message); Say("3D FAILED: " + ex.Message, true); }
        finally
        {
            if (iso != null) { iso.Dispose(); if (iso.Drive != null) log.AppendLine("ejected " + iso.Drive.TrimEnd('\\')); }
            // Projector back to 2D so the desktop is usable again - now, while the signal is still
            // 1080p (its 3D settings disappear once the mode switch below goes back to 4K).
            if (cfg.Get("projector.ip", "").Length > 0)
            {
                bool off;
                string line = ApplyProjector(new List<KeyValuePair<uint, uint>> { new KeyValuePair<uint, uint>(Pj.ItemDisplaySelect, 0) }, true, out off);
                if (line != null) log.AppendLine(line);
            }
            WriteLog("play 3d: " + Path.GetFileName(file), log.ToString().TrimEnd());
            playing = false;
            playingTitle = null;
            // Go back to where we started, unless someone picked a different mode meanwhile.
            if (previous != "3d" && mode == "3d") RunSwitch(previous, keepHags, false);
            else Ui(delegate { UpdateIcon(); });
        }
    }

    // MPC-HC may hand the file to an already-open window and exit straight away; if so, wait for
    // that window to close instead - otherwise the ISO would be ejected mid-film.
    static void WaitForPlayer(Process started, string exeName)
    {
        if (!started.WaitForExit(5000)) { started.WaitForExit(); return; }
        while (Process.GetProcessesByName(exeName).Length > 0) System.Threading.Thread.Sleep(1000);
    }

    // The player .mkv files open with (the K-Lite MPC-HC here), unless play3d.player is set.
    string PlayerPath()
    {
        string configured = cfg.Get("play3d.player", "");
        if (configured.Length > 0) return configured;
        try
        {
            using (RegistryKey uc = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.mkv\UserChoice"))
            {
                string progId = uc == null ? null : uc.GetValue("ProgId") as string;
                if (progId != null)
                    using (RegistryKey cmd = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command"))
                    {
                        string line = cmd == null ? null : cmd.GetValue("") as string;
                        var m = line == null ? Match.Empty : Regex.Match(line, "^\\s*\"([^\"]+)\"|^\\s*(\\S+)");
                        if (m.Success) return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    }
            }
        }
        catch { }
        return @"C:\Program Files (x86)\K-Lite Codec Pack\MPC-HC64\mpc-hc64.exe";
    }

    void Say(string text, bool warn) { Ui(delegate { Notify(text, warn); }); }

    // The tray keeps its icon in step if the mode is changed by another process (--apply / --play3d).
    void SyncStoredMode()
    {
        if (busy || playing) return;
        string stored = ReadReg("Mode");
        if (stored == null) return;
        stored = ParseMode(stored);
        string storedRoom = ReadReg("Room");
        storedRoom = storedRoom == "tv" || storedRoom == "theatre" ? storedRoom : room;
        if (stored != mode || storedRoom != room) { mode = stored; room = storedRoom; UpdateIcon(); }
    }

    // ---- switching ----
    // A switch takes several seconds (Afterburner has to start and apply), so run it off the UI
    // thread and show a "switching" icon meanwhile, otherwise the tray looks frozen.
    void Switch(string target)
    {
        if (headless) { DoSwitch(target, false); return; }
        if (busy) return;
        busy = true;
        pendingTarget = target;
        UpdateIcon();
        System.Threading.ThreadPool.QueueUserWorkItem(delegate { RunSwitch(target, false, true); });
    }

    static bool IsProblem(string text)
    {
        return text.IndexOf("rc=", StringComparison.Ordinal) >= 0 || text.StartsWith("failed")
            || text.IndexOf("FAILED", StringComparison.Ordinal) >= 0
            || text.IndexOf("did not take effect", StringComparison.Ordinal) >= 0
            || text.IndexOf("exited", StringComparison.Ordinal) >= 0;
    }

    // Runs a switch on the calling (background) thread and updates the tray around it.
    // keepHags leaves GPU scheduling alone (used by 3D playback, so it never needs a reboot);
    // askReboot = false suppresses the reboot dialog for the same reason.
    string RunSwitch(string target, bool keepHags, bool askReboot)
    {
        busy = true;
        pendingTarget = target;
        Ui(delegate { UpdateIcon(); Notify("Switching to " + ModeName(target) + " mode...", false); });

        string text;
        try { text = DoSwitch(target, keepHags); }
        catch (Exception ex) { text = "failed: " + ex.Message; }

        Ui(delegate
        {
            busy = false;
            pendingTarget = null;
            UpdateIcon();
            bool problem = IsProblem(text);
            // Everything has been applied by now. If a reboot is needed, the summary goes into the
            // reboot dialog itself (a balloon shown just before a dialog gets hidden by it).
            if (rebootPending && askReboot)
            {
                var answer = MessageBox.Show(
                    string.Format("{0} mode applied:\n\n{1}\n\nGPU scheduling only changes after a reboot.\n\nReboot now?",
                        ModeName(target), text),
                    "ModeSwitch", MessageBoxButtons.YesNo, problem ? MessageBoxIcon.Warning : MessageBoxIcon.Question);
                if (answer == DialogResult.Yes) Reboot();
            }
            else Notify(Summarize(text), problem);
        });
        return text;
    }

    // Runs `action` on the UI thread (no-op in headless mode).
    void Ui(MethodInvoker action)
    {
        if (headless || syncCtl == null) return;
        try { syncCtl.BeginInvoke(action); }
        catch { }
    }

    string DoSwitch(string target, bool keepHags)
    {
        var log = new StringBuilder();
        bool isGame = target == "game";
        string p = ParseMode(target);

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

        // 2b. Projector over the network (3D on/off, 3D format). Its 3D settings only exist while a
        //     1080p-or-lower signal is shown, so leaving 3D has to happen before the switch to 4K:
        //     try once now...
        bool pjOk = false;
        string pjLine = null;
        var pjItems = PjItemsFor(p);
        if (pjItems.Count > 0) pjLine = ApplyProjector(pjItems, false, out pjOk);

        // 2c. Display resolution/refresh (e.g. 3D Movie: projector to 1080p, Movie: back to 4K).
        //     Before HDR and sound, since a mode change re-negotiates the HDMI link.
        string res = cfg.Get("display." + p, "");
        if (res.Length > 0) log.AppendLine(ApplyResolution(res, cfg.Get("display.target", "")));

        // 2d. ...and entering 3D only works once the 1080p signal is there, so retry after the change.
        if (pjItems.Count > 0 && !pjOk) pjLine = ApplyProjector(pjItems, true, out pjOk);
        if (pjLine != null) log.AppendLine(pjLine);

        // 3. HDR
        bool hdrWanted = cfg.GetBool("hdr." + p, isGame);
        string herr = Disp.SetHdr(hdrWanted);
        if (herr != null) log.AppendLine(herr);
        foreach (var t in Disp.Targets())                 // report what the display actually reports
            if (t.HdrCapable) log.AppendLine(string.Format("HDR on {0}: {1}", t.Name, t.HdrOn ? "on" : "off"));

        // 3a. GPU colour (Settings > GPU colour): after the resolution and HDR changes, which reset
        //     the gamma ramp.
        ApplyGpuColour(p, log);

        // 3b. Sound preset. After HDR, because an HDR change re-negotiates the HDMI link, which can
        //     briefly reset the TV/receiver audio device.
        string soundLine = ApplyRoomSound(p);
        if (soundLine != null) log.AppendLine(soundLine);

        // 3c. Anything the app can't do itself, e.g. the projector's 3D format for frame-compatible 3D.
        //     With network control, a different note (e.g. 3D: how the projector gets switched to 3D).
        string reminder = pjOk ? cfg.Get("reminder." + p + ".networked", "") : cfg.Get("reminder." + p, "");
        if (reminder.Length > 0) log.AppendLine(reminder);

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

        // 5. HAGS (needs reboot). Skipped for 3D playback started from Game mode, so it round-trips
        //    without ever needing a reboot.
        int hagsWanted = cfg.GetInt("hags." + p, isGame ? 2 : 1);
        int hagsNow = HagsValue();
        if (keepHags && hagsNow != hagsWanted)
            log.AppendLine("GPU scheduling: left " + (hagsNow == 2 ? "on" : "off") + " for 3D playback");
        else if (hagsNow != hagsWanted)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(HagsKey, true))
                    k.SetValue("HwSchMode", hagsWanted, RegistryValueKind.DWord);
                rebootPending = hagsWanted != bootHags;
                log.AppendLine("GPU scheduling: " + (hagsWanted == 2 ? "on" : "off")
                    + (rebootPending ? " after reboot" : " (back to what's running - no reboot needed)"));
            }
            catch (Exception ex) { log.AppendLine("GPU scheduling: " + ex.Message + " (run elevated)"); }
        }

        mode = target;
        StoreMode();
        WriteLog(target, log.ToString().TrimEnd());
        return log.ToString().TrimEnd();
    }

    // ---- picture (Settings window) ----
    // projector.<mode> items, with the mode's projector preset (pj.<mode>.preset) first.
    List<KeyValuePair<uint, uint>> PjItemsFor(string p)
    {
        var items = cfg.GetSettings("projector." + p);
        int preset = cfg.GetInt("pj." + p + ".preset", -1);
        if (preset >= 0 && preset < PjPicture.Presets.Length)
        {
            items.RemoveAll(kv => kv.Key == PjPicture.Preset);
            items.Insert(0, new KeyValuePair<uint, uint>(PjPicture.Preset, (uint)preset));
        }
        return items;
    }

    public static readonly string[] ColourRooms = { "tv", "theatre" };

    // Display names for a room, most preferred first (room.<room>.display).
    public string[] RoomDisplays(string r)
    {
        string names = cfg.Get("room." + r + ".display", r == "theatre" ? "SONY PJ, SONY AVSYSTEM" : "LG TV");
        return Array.ConvertAll(names.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries), s => s.Trim());
    }

    // gpu.<mode>.<room>.* -> custom (on/off) and the values; defaults are neutral.
    public GpuColour.Values GpuValues(string p, string r, out bool custom)
    {
        string k = "gpu." + p + "." + r + ".";
        custom = cfg.GetBool(k + "custom", false);
        return new GpuColour.Values
        {
            Brightness = GpuColour.Values.Triple(cfg.Get(k + "brightness", ""), 50),
            Contrast = GpuColour.Values.Triple(cfg.Get(k + "contrast", ""), 50),
            Gamma = GpuColour.Values.Triple(cfg.Get(k + "gamma", ""), 100),
            Vibrance = cfg.GetInt(k + "vibrance", 50),
            Hue = cfg.GetInt(k + "hue", 0)
        };
    }

    // For each room display in use: the mode's custom colour, or neutral in modes without it.
    // Displays never given custom colour in any mode are left alone (e.g. set in NVIDIA Control Panel).
    void ApplyGpuColour(string p, StringBuilder log)
    {
        foreach (string r in ColourRooms)
        {
            bool anyCustom = false, custom;
            foreach (string m in new[] { "movie", "3d", "game" }) { GpuValues(m, r, out custom); anyCustom |= custom; }
            if (!anyCustom) continue;
            foreach (string name in RoomDisplays(r))
            {
                string monitor;
                string gdi = Disp.FindDisplay(name, out monitor);
                if (gdi == null) continue;
                var v = GpuValues(p, r, out custom);
                if (!custom) v = new GpuColour.Values();
                bool hdr = false;
                foreach (var t in Disp.Targets()) if (t.Name.Trim() == monitor && t.HdrOn) hdr = true;
                string err = GpuColour.Apply(gdi, v, hdr);
                log.AppendLine(err != null ? "GPU colour on " + monitor + " FAILED: " + err
                    : "GPU colour on " + monitor + ": " + v.Describe() + (hdr && !v.RampIsNeutral() ? " (brightness/contrast/gamma not used in HDR)" : ""));
                break;
            }
        }
    }

    SettingsForm settingsForm;

    void ShowSettings()
    {
        if (settingsForm != null && !settingsForm.IsDisposed) { settingsForm.Activate(); return; }
        settingsForm = new SettingsForm(this);
        settingsForm.Show();
        settingsForm.Activate();
    }

    // Used by the Settings window.
    public Config Cfg { get { return cfg; } }
    public string CurrentMode { get { return ParseMode(mode); } }
    public string CurrentRoom { get { return room; } }
    public static string ModeLabel(string m) { return ModeName(m); }
    public static string RoomLabel(string r) { return RoomName(r); }

    public void ReloadConfig()
    {
        cfg.Reload();
        cfg.Inherit("3d", "movie");
    }

    // Writes keys to settings.ini and reloads the config (with 3D inheriting Movie again).
    public void SaveSettingsNow(IDictionary<string, string> values)
    {
        cfg.SaveSettings(values);
        cfg.Inherit("3d", "movie");
    }

    // After saving: apply the current mode's GPU colour (and projector preset, if it changed).
    public void ApplySavedPicture(bool presetChanged)
    {
        string p = ParseMode(mode);
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            var log = new StringBuilder();
            ApplyGpuColour(p, log);
            if (presetChanged && room == "theatre" && cfg.GetInt("pj." + p + ".preset", -1) >= 0)
            {
                bool ok;
                string line = ApplyProjector(PjItemsFor(p), true, out ok);
                if (line != null) log.AppendLine(line);
            }
            string text = log.ToString().TrimEnd();
            if (text.Length > 0) WriteLog("settings saved", text);
        });
    }

    // ---- rooms ----
    // TV: the LG TV on the PC's input; receiver, subwoofer plug and projector off.
    // Theatre: receiver, subwoofer plug and projector on; TV off.
    // Windows' display and default sound device follow, then the current mode's resolution, HDR
    // and sound preset are applied for the display now in use. Independent of Movie/3D/Game.

    // Sound preset for mode `p` in the current room: sound.<room>.<mode>, else sound.<mode>.
    // "none" leaves sound alone. Returns a log line, or null if nothing is configured.
    string ApplyRoomSound(string p)
    {
        string key = cfg.Get("sound." + room + "." + p, cfg.Get("sound." + p, ""));
        if (key.Length == 0 || key.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        return ApplySound(key);
    }

    void SwitchRoom(string target)
    {
        if (busy) return;
        busy = true;
        pendingRoom = target;
        UpdateIcon();
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            for (int i = 0; i < 150 && following; i++) System.Threading.Thread.Sleep(100);   // let a display correction finish
            RunRoomSwitch(target);
        });
    }

    // Runs a room switch on the calling (background) thread and updates the tray around it.
    string RunRoomSwitch(string target)
    {
        busy = true;
        pendingRoom = target;
        Ui(delegate { UpdateIcon(); Notify("Switching to the " + RoomName(target) + " room...", false); });
        string text;
        try { text = DoRoomSwitch(target); }
        catch (Exception ex) { text = "failed: " + ex.Message; }
        Ui(delegate
        {
            busy = false;
            pendingRoom = null;
            UpdateIcon();
            Notify(Summarize(text), IsProblem(text));
        });
        return text;
    }

    string DoRoomSwitch(string target)
    {
        var log = new StringBuilder();
        bool theatre = target == "theatre";
        string tvIp = cfg.Get("tv.ip", ""), avrIp = cfg.Get("avr.ip", ""), subIp = cfg.Get("sub.ip", "");
        string pjIp = cfg.Get("projector.ip", ""), pjCom = cfg.Get("projector.community", "SONY");

        // How long each step took, for the log file only (the notification stays short).
        var timings = new List<string>();
        var clock = Stopwatch.StartNew();
        long lap = 0;
        Action<string> timed = label => { long now = clock.ElapsedMilliseconds; timings.Add(string.Format("{0} {1:0.0}s", label, (now - lap) / 1000.0)); lap = now; };

        // 1. Power on what the room needs. The projector first: it takes longest to warm up and
        //    can do that while the receiver starts (or waits for the remote). Receiver before the
        //    subwoofer, so the sub doesn't thump while the receiver's amp starts.
        int waitSec = theatre ? 60 : 45;    // for the room's display to appear (projector warm-up)
        if (theatre)
        {
            if (pjIp.Length > 0)
            {
                string line = ProjectorPower(pjIp, pjCom, true);
                if (line.IndexOf("FAILED", StringComparison.Ordinal) >= 0) waitSec = 5;   // it won't be showing up
                log.AppendLine(line);
                timed("projector");
            }
            if (avrIp.Length > 0) { log.AppendLine(ReceiverOn(avrIp)); timed("receiver"); }
        }
        else if (tvIp.Length > 0) { log.AppendLine(TvOn(tvIp)); timed("tv on"); }

        // 2. Windows: only the room's display, and its sound device as the default.
        log.AppendLine(ShowRoomDisplay(cfg.Get("room." + target + ".display", theatre ? "SONY PJ, SONY AVSYSTEM" : "LG TV"), waitSec));
        timed("display");

        // The subwoofer after the picture is there, i.e. once the receiver is up (no thump).
        if (theatre && subIp.Length > 0)
        {
            string e = Tapo.Switch(subIp, true);
            if (e == null) subState = 1;
            log.AppendLine(e == null ? "Subwoofer: on" : "Subwoofer FAILED: " + e);
            timed("subwoofer");
        }
        log.AppendLine(SetRoomAudio(cfg.Get("room." + target + ".audio", theatre ? "SONY AVSYSTEM" : "LG TV")));
        timed("sound output");

        // 3. Power off the other room. Subwoofer before the receiver, again against thumps.
        if (theatre)
        {
            if (tvIp.Length > 0) { log.AppendLine(TvOff(tvIp)); timed("tv off"); }
        }
        else
        {
            if (subIp.Length > 0)
            {
                string e = Tapo.Switch(subIp, false);
                if (e == null) subState = 0;
                log.AppendLine(e == null ? "Subwoofer: off" : "Subwoofer FAILED: " + e);
            }
            if (pjIp.Length > 0) log.AppendLine(ProjectorPower(pjIp, pjCom, false));
            if (avrIp.Length > 0 && cfg.GetBool("room.tv.receiveroff", true))
            {
                string e = SonyAvr.SetPower(avrIp, false);
                log.AppendLine(e == null ? "Receiver: off" : "Receiver FAILED: " + e);
            }
            timed("devices off");
        }

        room = target;
        WriteReg("Room", room);

        // 4. The current mode's resolution, HDR and sound for the display now in use.
        string p = ParseMode(mode);
        string res = cfg.Get("display." + p, "");
        if (res.Length > 0) log.AppendLine(ApplyResolution(res, cfg.Get("display.target", "")));
        string herr = Disp.SetHdr(cfg.GetBool("hdr." + p, p == "game"));
        if (herr != null) log.AppendLine(herr);
        ApplyGpuColour(p, log);
        string soundLine = ApplyRoomSound(p);
        if (soundLine != null) log.AppendLine(soundLine);
        timed("mode settings");

        // The mode's projector preset once it has warmed up (it refuses settings until then).
        if (theatre && pjIp.Length > 0 && cfg.GetInt("pj." + p + ".preset", -1) >= 0)
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                WaitForProjector();
                bool ok;
                string line = ApplyProjector(PjItemsFor(p), true, out ok);
                if (line != null) WriteLog("projector preset", line);
            });

        string text = log.ToString().TrimEnd();
        WriteLog("room " + target, text + string.Format("\ntook {0:0}s: {1}", clock.Elapsed.TotalSeconds, string.Join(", ", timings.ToArray())));
        return text;
    }

    // ---- room devices ----
    string TvKey()
    {
        string k = cfg.Get("tv.clientkey", "");
        return k.Length > 0 ? k : ReadReg("TvClientKey");
    }

    // Connects to the TV (pairing on screen if there's no key yet). Returns null on success.
    string OpenTv(LgTv tv, int pairWaitMs)
    {
        string stored = TvKey(), key;
        string err = tv.Open(stored, pairWaitMs, out key);
        if (err == null && key != null && key != stored) WriteReg("TvClientKey", key);
        return err;
    }

    string TvOn(string ip)
    {
        string mac = cfg.Get("tv.mac", "");
        try
        {
            // Off, or in "Active Standby" (network up, screen off): wake it, then wait for it to answer.
            // After a longer spell off it can take well over 30 s until it answers on the network,
            // though its picture comes up sooner - so give it 20 s, then carry on and select the
            // input in the background (it usually comes back on the PC's input anyway).
            string result, state = null;
            var clock = Stopwatch.StartNew();
            long nextWake = 0;
            while (clock.ElapsedMilliseconds < 20000)
            {
                if (TvReady(ip, out result, ref state)) return result;
                if (mac.Length == 0) return "TV FAILED: not on, and tv.mac isn't set to wake it";
                if (clock.ElapsedMilliseconds >= nextWake)
                {
                    string werr = LgTv.Wake(mac, ip);
                    if (werr != null) return "TV FAILED: " + werr;
                    nextWake = clock.ElapsedMilliseconds + 5000;
                }
                System.Threading.Thread.Sleep(1000);
            }

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string line = null, st = state;
                long next = 0;
                while (clock.ElapsedMilliseconds < 120000)
                {
                    if (TvReady(ip, out line, ref st)) break;
                    line = null;
                    if (clock.ElapsedMilliseconds >= next) { LgTv.Wake(mac, ip); next = clock.ElapsedMilliseconds + 5000; }
                    System.Threading.Thread.Sleep(2000);
                }
                WriteLog("tv input", line != null
                    ? string.Format("{0} (answered after {1:0}s)", line, clock.Elapsed.TotalSeconds)
                    : string.Format("TV didn't answer within {0:0}s (last state: {1}) - check General > Mobile TV On > Turn on via Wi-Fi", clock.Elapsed.TotalSeconds, st ?? "not reachable"));
            });
            return "TV: waking up (input selected when it answers)";
        }
        catch (Exception ex) { return "TV FAILED: " + ex.Message; }
    }

    // One attempt: if the TV is on and answering, select the PC's input. state = last power state seen.
    bool TvReady(string ip, out string result, ref string state)
    {
        result = null;
        try
        {
            if (!LgTv.IsUp(ip, 1000)) return false;
            string input = cfg.Get("tv.input", "HDMI_1");
            using (var tv = new LgTv(ip))
            {
                if (OpenTv(tv, 60000) != null) return false;
                state = tv.PowerState() ?? state;
                if (state != null && state != "Active") return false;
                string e = input.Length > 0 ? tv.SwitchInput(input) : null;
                result = e == null ? "TV: on" + (input.Length > 0 ? ", " + input : "") : "TV: on, but switching to " + input + " FAILED: " + e;
                return true;
            }
        }
        catch { return false; }
    }

    string TvOff(string ip)
    {
        // Just after the PC's picture moves away the TV can report a passing state (not "Active")
        // while it's still on, so only a standby/suspend state counts as off. Then check it went
        // off, and try again once if not.
        string err = null, state = null;
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!LgTv.IsUp(ip, 1500)) return "TV: off";
                using (var tv = new LgTv(ip))
                {
                    err = OpenTv(tv, 5000);
                    if (err != null) continue;
                    state = tv.PowerState();
                    if (IsTvOff(state)) return "TV: off";
                    err = tv.TurnOff();
                }
                for (int i = 0; i < 8; i++)                   // it drops off the network, or reports standby
                {
                    System.Threading.Thread.Sleep(1000);
                    if (!LgTv.IsUp(ip, 1000)) return "TV: off";
                    using (var tv = new LgTv(ip))
                        if (OpenTv(tv, 3000) == null && IsTvOff(state = tv.PowerState())) return "TV: off";
                }
            }
            return "TV FAILED: still on" + (err != null ? " (" + err + ")" : state != null ? " (reports " + state + ")" : "");
        }
        catch (Exception ex) { return "TV FAILED: " + ex.Message; }
    }

    static bool IsTvOff(string state)
    {
        return state != null && (state.IndexOf("Standby", StringComparison.OrdinalIgnoreCase) >= 0
                              || state.IndexOf("Suspend", StringComparison.OrdinalIgnoreCase) >= 0
                              || state.Equals("Off", StringComparison.OrdinalIgnoreCase));
    }

    string ReceiverOn(string ip)
    {
        bool on;
        string err = SonyAvr.GetPower(ip, out on);
        if (err != null)
        {
            // Not on the network: its Network Standby is off (e.g. after a reset; it's switched back
            // on whenever the receiver is reached) or it was unplugged - so ask for the remote. Its
            // picture and sound are up in seconds but its network takes over a minute, so don't hold
            // the switch up for that: select the input in the background once it answers.
            Say("Switch the receiver on with the remote", false);
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                bool isOn;
                string e = "not reachable";
                for (int i = 0; i < 90 && e != null; i++)
                {
                    System.Threading.Thread.Sleep(2000);
                    e = SonyAvr.GetPower(ip, out isOn);
                }
                string uri = cfg.Get("avr.input", "");
                if (e == null && uri.Length > 0) e = SonyAvr.SetInput(ip, uri);
                WriteLog("receiver input", e == null ? "Receiver: input " + cfg.Get("avr.inputname", uri) : "Receiver: input not set - " + e);
            });
            return "Receiver: switch on with the remote";
        }
        if (!on)
        {
            err = SonyAvr.SetPower(ip, true);
            if (err != null) return "Receiver FAILED: " + err;
            for (int i = 0; i < 20 && !on; i++) { System.Threading.Thread.Sleep(500); SonyAvr.GetPower(ip, out on); }
        }
        string note = EnsureReceiverNetworkStart(ip);
        string input = cfg.Get("avr.input", "");
        if (input.Length == 0) return "Receiver: on" + note;
        for (int i = 0; i < 6; i++)                          // the input can be refused while it boots
        {
            err = SonyAvr.SetInput(ip, input);
            if (err == null) return "Receiver: on, input " + cfg.Get("avr.inputname", input) + note;
            System.Threading.Thread.Sleep(1000);
        }
        return "Receiver: on, but input FAILED: " + err + note;
    }

    // Keeps Network Standby + Remote Start on (hidden settings on UK/EU models, see SonyAvr), so the
    // receiver can be switched on over the network. A reset or firmware update could turn them off.
    // Returns "" or a note for the log line.
    static string EnsureReceiverNetworkStart(string ip)
    {
        bool standby, remote;
        if (SonyAvr.GetNetworkStart(ip, out standby, out remote) != null || (standby && remote)) return "";
        string err = SonyAvr.EnableNetworkStart(ip);
        return err == null ? " (network standby switched back on)" : " (network standby FAILED: " + err + ")";
    }

    // SDCP power: 0x0130 = set (1 on, 0 off); 0x0102 = status (0 standby, 1-2 starting, 3 on, 4+ cooling).
    static string ProjectorPower(string ip, string com, bool on)
    {
        try
        {
            int status;
            string err = Pj.Get(ip, com, Pj.ItemPower, out status);
            if (err != null) return "Projector FAILED: " + err + (on ? " (projector setting: Remote Start / network standby on)" : "");
            if (on && status >= 1 && status <= 3) return "Projector: on";
            if (!on && (status == 0 || status >= 4)) return "Projector: off";
            if (on && status >= 4)
            {
                // Still cooling down from being switched off: it only accepts "on" once in standby.
                for (int i = 0; i < 120 && status >= 4; i++)
                {
                    System.Threading.Thread.Sleep(1000);
                    if (Pj.Get(ip, com, Pj.ItemPower, out status) != null) break;
                }
            }
            err = Pj.Set(ip, com, 0x0130, on ? 1 : 0);
            if (err != null) return "Projector FAILED: power " + (on ? "on" : "off") + ": " + err;
            return on ? "Projector: on (warming up)" : "Projector: off (cooling down)";
        }
        catch (Exception ex) { return "Projector FAILED: " + ex.Message; }
    }

    // A projector that was just switched on takes a minute or so to warm up and accept settings.
    void WaitForProjector()
    {
        string ip = cfg.Get("projector.ip", ""), com = cfg.Get("projector.community", "SONY");
        if (ip.Length == 0) return;
        for (int i = 0; i < 120; i++)
        {
            int status;
            if (Pj.Get(ip, com, Pj.ItemPower, out status) != null || status == 3 || status == 0 || status >= 4) return;
            System.Threading.Thread.Sleep(1000);
        }
    }

    // `names` = comma-separated preference list, e.g. "SONY PJ, SONY AVSYSTEM": the projector
    // through the receiver, else the receiver's own screen. Waits up to waitSec for the first.
    static string ShowRoomDisplay(string names, int waitSec)
    {
        try
        {
            string[] list = Array.ConvertAll(names.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries), s => s.Trim());
            if (list.Length == 0) return null;
            // The display appears once the TV / receiver has started and sent its EDID.
            for (int i = 0; i < waitSec && !DispTopo.Available(list[0]); i++) System.Threading.Thread.Sleep(1000);
            string pick = Array.Find(list, n => DispTopo.Available(n)) ?? list[0];
            string shown;
            string err = DispTopo.ShowOnly(pick, out shown);
            if (err != null) return "Display FAILED: " + err;
            System.Threading.Thread.Sleep(3000);                   // let the HDMI link settle
            return "Display: " + shown + " only";
        }
        catch (Exception ex) { return "Display FAILED: " + ex.Message; }
    }

    // Windows remembers a layout per set of connected displays, so switching a device on by hand
    // (or one finishing its start-up late) can bring back an old layout, e.g. "TV only" in the
    // Theatre room. When the set of connected displays changes, put the room's display back.
    // Only on a change, so a layout picked afterwards with Win+P is left alone.
    string lastAvailable;
    volatile bool following;             // a display-follow correction is running

    void FollowRoomDisplay()
    {
        if (busy || following || playing || !cfg.GetBool("room.followdisplay", true)) return;
        string now;
        try { now = string.Join("|", DispTopo.AvailableNames().ToArray()); }
        catch { return; }
        if (lastAvailable == null || now == lastAvailable) { lastAvailable = now; return; }
        lastAvailable = now;

        string target = room, p = ParseMode(mode);
        following = true;               // its own flag: `busy` belongs to mode/room switches and the icon
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            var log = new StringBuilder();
            try
            {
                System.Threading.Thread.Sleep(4000);               // let Windows finish its own re-layout
                string wanted = cfg.Get("room." + target + ".display", target == "theatre" ? "SONY PJ, SONY AVSYSTEM" : "LG TV");
                string first = Array.Find(Array.ConvertAll(wanted.Split(','), s => s.Trim()), n => n.Length > 0 && DispTopo.Available(n));
                if (first == null) return;
                var active = DispTopo.Active();
                if (active.Count == 1 && active[0].IndexOf(first, StringComparison.OrdinalIgnoreCase) >= 0) return;   // already right
                log.AppendLine(ShowRoomDisplay(wanted, 0));
                log.AppendLine(SetRoomAudio(cfg.Get("room." + target + ".audio", target == "theatre" ? "SONY AVSYSTEM" : "LG TV")));
                string res = cfg.Get("display." + p, "");
                if (res.Length > 0) log.AppendLine(ApplyResolution(res, cfg.Get("display.target", "")));
                ApplyGpuColour(p, log);
                string soundLine = ApplyRoomSound(p);
                if (soundLine != null) log.AppendLine(soundLine);
            }
            catch (Exception ex) { log.AppendLine("FAILED: " + ex.Message); }
            finally
            {
                try { lastAvailable = string.Join("|", DispTopo.AvailableNames().ToArray()); } catch { }
                following = false;
            }
            string text = log.ToString().TrimEnd();
            if (text.Length == 0) return;
            WriteLog("display follow (" + RoomName(target) + " room)", text);
            Say(Summarize(text), IsProblem(text));
        });
    }

    static string SetRoomAudio(string name)
    {
        try
        {
            string device = null, err = null;
            for (int i = 0; i < 20; i++)                            // HDMI audio shows up after the display
            {
                err = Snd.SetDefaultOutput(name, out device);
                if (err == null) return "Sound output: " + device;
                System.Threading.Thread.Sleep(1000);
            }
            return "Sound output FAILED: " + err;
        }
        catch (Exception ex) { return "Sound output FAILED: " + ex.Message; }
    }

    // "Room setup > Pair TV": shows the TV's allow prompt and remembers the key.
    void PairTv()
    {
        string ip = cfg.Get("tv.ip", "");
        if (ip.Length == 0) { Notify("Set tv.ip in config.ini first", true); return; }
        Notify("Accept the prompt on the TV to let ModeSwitch control it", false);
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            string text;
            try
            {
                if (!LgTv.IsUp(ip, 2000)) text = "TV FAILED: not reachable at " + ip + " - is it on?";
                else
                    using (var tv = new LgTv(ip))
                    {
                        string key, err = tv.Open(null, 90000, out key);
                        if (err == null && key != null) { WriteReg("TvClientKey", key); tv.Toast("ModeSwitch connected"); }
                        text = err == null ? "TV paired" : "TV pairing FAILED: " + err;
                    }
            }
            catch (Exception ex) { text = "TV pairing FAILED: " + ex.Message; }
            WriteLog("pair tv", text);
            Say(text, IsProblem(text));
        });
    }

    // "Room setup > Subwoofer plug login": TP-Link account for the Tapo plug, stored encrypted.
    void AskTapoLogin()
    {
        string oldUser, oldPass;
        Tapo.LoadLogin(out oldUser, out oldPass);
        using (var f = new Form { Text = "Subwoofer plug - TP-Link login", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                                  StartPosition = FormStartPosition.CenterScreen, TopMost = true, ClientSize = new Size(380, 170), ShowInTaskbar = false })
        {
            var info = new Label { Text = "The e-mail and password of the TP-Link account the Tapo app uses.\nStored encrypted for this Windows user only.", Location = new Point(12, 10), Size = new Size(360, 34) };
            var userLbl = new Label { Text = "E-mail", Location = new Point(12, 56), AutoSize = true };
            var user = new TextBox { Location = new Point(90, 52), Width = 276, Text = oldUser ?? "" };
            var passLbl = new Label { Text = "Password", Location = new Point(12, 88), AutoSize = true };
            var pass = new TextBox { Location = new Point(90, 84), Width = 276, UseSystemPasswordChar = true };
            var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(210, 128), Width = 75 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(291, 128), Width = 75 };
            f.Controls.AddRange(new Control[] { info, userLbl, user, passLbl, pass, ok, cancel });
            f.AcceptButton = ok; f.CancelButton = cancel;
            if (f.ShowDialog() != DialogResult.OK || user.Text.Trim().Length == 0 || pass.Text.Length == 0) return;
            Tapo.SaveLogin(user.Text.Trim(), pass.Text);
        }
        System.Threading.ThreadPool.QueueUserWorkItem(delegate { Say(CheckTapo(), false); });
    }

    string CheckTapo()
    {
        string ip = cfg.Get("sub.ip", "");
        if (ip.Length == 0) return "Subwoofer: sub.ip not set";
        string user, pass, name = null;
        bool on = false;
        if (!Tapo.LoadLogin(out user, out pass)) return "Subwoofer: no TP-Link login saved";
        var t = new Tapo(ip);
        string err = t.Login(user, pass) ?? t.GetInfo(out name, out on);
        return err != null ? "Subwoofer FAILED: " + err : string.Format("Subwoofer plug '{0}': {1}", name, on ? "on" : "off");
    }

    // The "Volume" submenu (TV and receiver volume, TV sound output).
    RoomSoundMenu roomSound;

    // "Room setup > Subwoofer": one item, ticked while the sub is on; a click switches it over.
    // The plug's state is read in the background when Room setup opens (a Tapo login takes ~1 s).
    volatile int subState = -1;          // -1 unknown, 0 off, 1 on
    ToolStripMenuItem subItem;           // the item in the menu currently built

    string SubwooferLabel()
    {
        return subState == 1 ? "Subwoofer: on" : subState == 0 ? "Subwoofer: off" : "Subwoofer";
    }

    void ShowSubwooferState()
    {
        Ui(delegate
        {
            if (subItem == null || subItem.IsDisposed) return;
            subItem.Text = SubwooferLabel();
            subItem.Checked = subState == 1;
        });
    }

    // Reads the plug's state; returns null on success.
    string ReadSubwoofer()
    {
        string ip = cfg.Get("sub.ip", ""), user, pass, name;
        bool on = false;
        if (ip.Length == 0) return "sub.ip isn't set";
        if (!Tapo.LoadLogin(out user, out pass)) return "no TP-Link login saved";
        var t = new Tapo(ip);
        string err = t.Login(user, pass) ?? t.GetInfo(out name, out on);
        if (err == null) subState = on ? 1 : 0;
        return err;
    }

    volatile bool subBusy;

    void RefreshSubwoofer()
    {
        if (subBusy || cfg.Get("sub.ip", "").Length == 0) return;
        subBusy = true;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            try { ReadSubwoofer(); } catch { }
            finally { subBusy = false; }
            ShowSubwooferState();
        });
    }

    void ToggleSubwoofer()
    {
        string ip = cfg.Get("sub.ip", "");
        if (ip.Length == 0) { Notify("Set sub.ip in config.local.ini first", true); return; }
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            for (int i = 0; i < 50 && subBusy; i++) System.Threading.Thread.Sleep(100);   // a read in progress
            string err = subState < 0 ? ReadSubwoofer() : null;        // unknown: find out which way to switch
            bool on = subState != 1;
            if (err == null) err = Tapo.Switch(ip, on);
            if (err == null) subState = on ? 1 : 0;
            string text = err == null ? "Subwoofer: " + (on ? "on" : "off") : "Subwoofer FAILED: " + err;
            WriteLog("subwoofer " + (on ? "on" : "off"), text);
            Say(text, err != null);
            ShowSubwooferState();
        });
    }

    // "Room setup > Check devices": reads every device's state, changes nothing.
    void CheckRoomDevices()
    {
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            var sb = new StringBuilder();
            try
            {
                string ip = cfg.Get("tv.ip", "");
                if (ip.Length > 0)
                {
                    if (!LgTv.IsUp(ip, 1500)) sb.AppendLine("TV: off / not reachable");
                    else using (var tv = new LgTv(ip))
                    {
                        string err = OpenTv(tv, 5000);
                        sb.AppendLine(err != null ? "TV FAILED: " + err + " (Room setup > Pair TV)" : "TV: " + (tv.PowerState() ?? "on") + ", " + (tv.ForegroundApp() ?? "?"));
                    }
                }
                ip = cfg.Get("avr.ip", "");
                if (ip.Length > 0)
                {
                    bool on;
                    string err = SonyAvr.GetPower(ip, out on);
                    sb.AppendLine(err != null ? "Receiver FAILED: " + err : "Receiver: " + (on ? "on" : "standby"));
                }
                ip = cfg.Get("projector.ip", "");
                if (ip.Length > 0)
                {
                    int status;
                    string err = Pj.Get(ip, cfg.Get("projector.community", "SONY"), Pj.ItemPower, out status);
                    sb.AppendLine(err != null ? "Projector FAILED: " + err
                        : "Projector: " + (status == 3 ? "on" : status == 0 ? "standby" : status <= 2 ? "starting" : "cooling down"));
                }
                if (cfg.Get("sub.ip", "").Length > 0) sb.AppendLine(CheckTapo());
            }
            catch (Exception ex) { sb.AppendLine("check FAILED: " + ex.Message); }
            string text = sb.ToString().TrimEnd();
            WriteLog("check room devices", text);
            Say(text, IsProblem(text));
        });
    }

    // ModeSwitch runs elevated, and Store apps and Settings pages don't launch reliably from an
    // elevated process. Explorer runs as the normal user, so let it open the target.
    void OpenAsUser(string target)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = false }); }
        catch (Exception ex) { Notify("Could not open " + target + ": " + ex.Message, true); }
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
    bool iconShowsBusy;

    void UpdateIcon()
    {
        iconShowsBusy = busy && (pendingTarget != null || pendingRoom != null);
        if (iconShowsBusy)
        {
            tray.Icon = MakeIcon("", Color.FromArgb(255, 186, 8));   // sync glyph, amber
            tray.Text = pendingRoom != null ? "ModeSwitch - switching to the " + RoomName(pendingRoom) + " room..."
                                            : "ModeSwitch - switching to " + ModeName(pendingTarget) + "...";
            return;
        }
        string glyph = mode == "game" ? "" : "";           // gamepad / video
        Color colour = rebootPending ? Color.FromArgb(255, 186, 8)
                     : mode == "game" ? Color.FromArgb(118, 219, 92)
                     : mode == "3d"   ? Color.FromArgb(200, 140, 255)
                                      : Color.FromArgb(120, 180, 255);
        tray.Icon = mode == "3d" ? MakeTextIcon("3D", colour) : MakeIcon(glyph, colour);
        tray.Text = string.Format("ModeSwitch - {0} mode, {1} room{2}", ModeName(mode), RoomName(room), rebootPending ? " (reboot pending)" : "");
    }

    static Icon MakeIcon(string glyph, Color colour)
    {
        return RenderIcon(glyph, "Segoe MDL2 Assets", FontStyle.Regular, colour, 2.2f);
    }

    // Plain-text icon, used for 3D Movie mode ("3D" reads better than any glyph at tray size).
    static Icon MakeTextIcon(string text, Color colour)
    {
        return RenderIcon(text, "Segoe UI Black", FontStyle.Regular, colour, 1.2f);
    }

    // Draws `text` as a shape scaled to fill the 32x32 icon (keeping its proportions), then fills
    // it and traces its outline `stroke` pixels wide in the same colour, which thickens thin
    // glyph strokes so the icon stays bold when Windows shrinks it to tray size.
    static Icon RenderIcon(string text, string family, FontStyle style, Color colour, float stroke)
    {
        using (var bmp = new Bitmap(32, 32))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var ff = new FontFamily(family))
                    path.AddString(text, ff, (int)style, 100f, PointF.Empty, StringFormat.GenericTypographic);
                RectangleF b = path.GetBounds();
                if (b.Width > 0 && b.Height > 0)
                {
                    float box = 32 - 1 - stroke;                    // room for the outline at the edges
                    float scale = Math.Min(box / b.Width, box / b.Height);
                    using (var m = new System.Drawing.Drawing2D.Matrix())
                    {
                        m.Translate(16, 16);
                        m.Scale(scale, scale);
                        m.Translate(-(b.X + b.Width / 2), -(b.Y + b.Height / 2));
                        path.Transform(m);
                    }
                }
                using (var brush = new SolidBrush(colour)) g.FillPath(brush, path);
                using (var pen = new Pen(colour, stroke) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
                    g.DrawPath(pen, path);
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

            // Keep the log bounded: past the limit, it becomes ModeSwitch.old.log (replacing the
            // previous one) and a fresh log starts, so at most ~2x the limit is ever on disk.
            long maxBytes = Math.Max(16, cfg.GetInt("log.maxkb", 256)) * 1024L;
            var info = new FileInfo(path);
            if (info.Exists && info.Length > maxBytes)
            {
                string old = Path.Combine(exeDir, "ModeSwitch.old.log");
                if (File.Exists(old)) File.Delete(old);
                File.Move(path, old);
            }

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
        tray.BalloonTipTitle = "ModeSwitch - " + ModeName(mode) + " mode";
        tray.BalloonTipText = text.Length > 250 ? text.Substring(0, 250) : text;
        tray.BalloonTipIcon = warn ? ToolTipIcon.Warning : ToolTipIcon.Info;
        tray.ShowBalloonTip(4000);
    }
}
