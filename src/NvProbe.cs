// NvProbe - read-only dump of the NVIDIA driver profile database (base profile).
// Used to discover the exact setting IDs/values for V-Sync and G-Sync (VRR) on this driver.
using System;
using System.Runtime.InteropServices;
using System.Text;

static class NvProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr LoadLibrary(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr mod, string name);

    delegate IntPtr QueryInterface(uint id);
    delegate int Fn0();
    delegate int Fn1(out IntPtr a);
    delegate int Fn1p(IntPtr a);
    delegate int Fn2(IntPtr a, out IntPtr b);
    delegate int FnEnumIds([In, Out] uint[] ids, ref uint count);
    delegate int FnNameFromId(uint id, IntPtr nameOut);
    delegate int FnGetSetting(IntPtr session, IntPtr profile, uint id, IntPtr setting);

    static QueryInterface query;
    static T Get<T>(uint id) where T : class
    {
        IntPtr p = query(id);
        if (p == IntPtr.Zero) return null;
        return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
    }

    // NVDRS_SETTING_V1 field offsets (bytes)
    const int OFF_VERSION = 0;
    const int OFF_NAME = 4;          // NvU16[2048]
    const int OFF_ID = 4 + 4096;
    const int OFF_TYPE = OFF_ID + 4;
    const int OFF_LOCATION = OFF_TYPE + 4;
    const int OFF_ISCURPRE = OFF_LOCATION + 4;
    const int OFF_ISPREVALID = OFF_ISCURPRE + 4;
    const int OFF_PREDEFINED = OFF_ISPREVALID + 4;   // union, 4100 bytes
    const int OFF_CURRENT = OFF_PREDEFINED + 4100;   // union, 4100 bytes
    const int SETTING_SIZE = OFF_CURRENT + 4100;     // 12320
    static readonly uint SETTING_VER = (uint)SETTING_SIZE | (1u << 16);

    static string ReadUnicode(IntPtr basePtr, int offset)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 2048; i++)
        {
            char c = (char)Marshal.ReadInt16(basePtr, offset + i * 2);
            if (c == '\0') break;
            sb.Append(c);
        }
        return sb.ToString();
    }

    static int Main()
    {
        IntPtr lib = LoadLibrary("nvapi64.dll");
        if (lib == IntPtr.Zero) { Console.WriteLine("nvapi64.dll not found"); return 1; }
        IntPtr qi = GetProcAddress(lib, "nvapi_QueryInterface");
        if (qi == IntPtr.Zero) { Console.WriteLine("nvapi_QueryInterface not found"); return 1; }
        query = (QueryInterface)Marshal.GetDelegateForFunctionPointer(qi, typeof(QueryInterface));

        var init = Get<Fn0>(0x0150E828);
        var createSession = Get<Fn1>(0x0694D52E);
        var loadSettings = Get<Fn1p>(0x375DBD6B);
        var getBaseProfile = Get<Fn2>(0xDA8466A0);
        var getSetting = Get<FnGetSetting>(0x73BF8338);
        var enumIds = Get<FnEnumIds>(0xF020614A);
        var nameFromId = Get<FnNameFromId>(0xD61CBE6E);
        var destroy = Get<Fn1p>(0xDAD9CFF8);

        Console.WriteLine("resolved: init={0} session={1} load={2} base={3} get={4} enumIds={5} name={6}",
            init != null, createSession != null, loadSettings != null, getBaseProfile != null,
            getSetting != null, enumIds != null, nameFromId != null);
        if (init == null || createSession == null) return 2;

        int rc = init();
        Console.WriteLine("NvAPI_Initialize rc={0}", rc);
        if (rc != 0) return 3;

        IntPtr session;
        rc = createSession(out session);
        Console.WriteLine("DRS_CreateSession rc={0}", rc);
        if (rc != 0) return 4;

        rc = loadSettings(session);
        Console.WriteLine("DRS_LoadSettings rc={0}", rc);

        IntPtr baseProfile;
        rc = getBaseProfile(session, out baseProfile);
        Console.WriteLine("DRS_GetBaseProfile rc={0}", rc);
        if (rc != 0) { destroy(session); return 5; }

        uint count = 4096;
        var ids = new uint[count];
        rc = enumIds(ids, ref count);
        Console.WriteLine("DRS_EnumAvailableSettingIds rc={0} count={1}", rc, count);
        if (rc != 0) { destroy(session); return 6; }

        IntPtr buf = Marshal.AllocHGlobal(SETTING_SIZE);
        IntPtr nameBuf = Marshal.AllocHGlobal(4096);
        Console.WriteLine();
        Console.WriteLine("{0,-12} {1,-52} {2,-10} {3}", "id", "name", "type", "current value");
        for (int i = 0; i < count; i++)
        {
            string name = "";
            if (nameFromId != null && nameFromId(ids[i], nameBuf) == 0) name = ReadUnicode(nameBuf, 0);

            // Only print settings whose name hints at the ones we need, plus anything with VRR/SYNC.
            bool interesting = name.IndexOf("SYNC", StringComparison.OrdinalIgnoreCase) >= 0
                            || name.IndexOf("VRR", StringComparison.OrdinalIgnoreCase) >= 0
                            || name.IndexOf("GSYNC", StringComparison.OrdinalIgnoreCase) >= 0
                            || name.IndexOf("FLIP", StringComparison.OrdinalIgnoreCase) >= 0
                            || name.IndexOf("PREFERRED_PSTATE", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!interesting) continue;

            for (int b = 0; b < SETTING_SIZE; b++) Marshal.WriteByte(buf, b, 0);
            Marshal.WriteInt32(buf, OFF_VERSION, unchecked((int)SETTING_VER));
            int grc = getSetting(session, baseProfile, ids[i], buf);
            string val;
            if (grc == 0)
            {
                uint type = (uint)Marshal.ReadInt32(buf, OFF_TYPE);
                uint cur = (uint)Marshal.ReadInt32(buf, OFF_CURRENT);
                uint pre = (uint)Marshal.ReadInt32(buf, OFF_PREDEFINED);
                uint isPre = (uint)Marshal.ReadInt32(buf, OFF_ISCURPRE);
                val = string.Format("0x{0:X8} (default 0x{1:X8}, atDefault={2}, type={3})", cur, pre, isPre, type);
            }
            else val = "get rc=" + grc;

            Console.WriteLine("0x{0:X8}   {1,-52} {2}", ids[i], name, val);
        }
        Marshal.FreeHGlobal(buf);
        Marshal.FreeHGlobal(nameBuf);
        destroy(session);
        return 0;
    }
}
