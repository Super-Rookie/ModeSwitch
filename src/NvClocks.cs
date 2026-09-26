// NvClocks - read (and optionally set) GPU clock offsets through NVAPI pstates20.
// Usage:  NvClocks.exe            -> read current core/memory offsets
//         NvClocks.exe <coreKHz> <memKHz>  -> set offsets (needs admin), then read back
// Offsets are in kHz, matching Afterburner's cfg values (e.g. memory +1500 MHz = 1500000).
using System;
using System.Runtime.InteropServices;

static class NvClocks
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr LoadLibrary(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr mod, string name);

    delegate IntPtr QueryInterface(uint id);
    delegate int FnInit();
    delegate int FnEnumGpus([Out] IntPtr[] handles, out int count);
    delegate int FnPstates20(IntPtr gpu, IntPtr info);

    static QueryInterface query;
    static T Get<T>(uint id) where T : class
    {
        IntPtr p = query(id);
        return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
    }

    // NV_GPU_PERF_PSTATES20_INFO_V2 layout (bytes), all fields NvU32/NvS32 unless noted:
    //   version, bIsEditable:1 flags, numPstates, numClocks, numBaseVoltages,
    //   pstates[16] { pstateId, bIsEditable flags, clocks[8] { domainId, typeId, bIsEditable flags,
    //       freqDelta_kHz { value, valueMin, valueMax }, data[2*4] },
    //     baseVoltages[4] { domainId, bIsEditable flags, volt_uV, voltDelta_uV { value, min, max } } },
    //   ov { numVoltages, voltages[4] {...} }
    // clock entry: domainId(4) typeId(4) flags(4) freqDelta{value,min,max}(12) data union(20) = 44
    const int CLOCK_SIZE = 4 + 4 + 4 + 12 + 20;         // 44
    const int BASEVOLT_SIZE = 4 + 4 + 4 + 12;           // 24
    const int PSTATE_SIZE = 4 + 4 + (8 * CLOCK_SIZE) + (4 * BASEVOLT_SIZE); // 8 + 352 + 96 = 456
    const int HEADER_SIZE = 4 + 4 + 4 + 4 + 4;          // 20
    const int OV_SIZE = 4 + (4 * BASEVOLT_SIZE);        // 4 + 96 = 100
    const int INFO_SIZE = HEADER_SIZE + (16 * PSTATE_SIZE) + OV_SIZE; // 20 + 7296 + 100 = 7416
    static readonly uint INFO_VER2 = (uint)INFO_SIZE | (2u << 16);

    const int OFF_NUMPSTATES = 8;
    const int OFF_NUMCLOCKS = 12;
    const int OFF_PSTATES = HEADER_SIZE;

    static int ClockOff(int pstate, int clock) { return OFF_PSTATES + pstate * PSTATE_SIZE + 8 + clock * CLOCK_SIZE; }

    static string DomainName(uint id)
    {
        switch (id)
        {
            case 0: return "GRAPHICS";
            case 4: return "MEMORY";
            case 7: return "PROCESSOR";
            case 8: return "VIDEO";
            default: return "domain" + id;
        }
    }

    static int Main(string[] args)
    {
        IntPtr lib = LoadLibrary("nvapi64.dll");
        if (lib == IntPtr.Zero) { Console.WriteLine("nvapi64.dll not found"); return 1; }
        IntPtr qi = GetProcAddress(lib, "nvapi_QueryInterface");
        query = (QueryInterface)Marshal.GetDelegateForFunctionPointer(qi, typeof(QueryInterface));

        var init = Get<FnInit>(0x0150E828);
        var enumGpus = Get<FnEnumGpus>(0xE5AC921F);
        var getPstates = Get<FnPstates20>(0x6FF81213);
        var setPstates = Get<FnPstates20>(0x0F4DAE6B);
        Console.WriteLine("resolved: init={0} enumGpus={1} getPstates20={2} setPstates20={3}",
            init != null, enumGpus != null, getPstates != null, setPstates != null);
        if (init == null || enumGpus == null || getPstates == null) return 2;

        int rc = init();
        if (rc != 0) { Console.WriteLine("Initialize rc={0}", rc); return 3; }

        var gpus = new IntPtr[64];
        int n;
        rc = enumGpus(gpus, out n);
        Console.WriteLine("EnumPhysicalGPUs rc={0} count={1}", rc, n);
        if (rc != 0 || n == 0) return 4;

        IntPtr info = Marshal.AllocHGlobal(INFO_SIZE);
        try
        {
            for (int b = 0; b < INFO_SIZE; b++) Marshal.WriteByte(info, b, 0);
            Marshal.WriteInt32(info, 0, unchecked((int)INFO_VER2));
            rc = getPstates(gpus[0], info);
            Console.WriteLine("GetPstates20 rc={0} (struct size {1}, version 0x{2:X})", rc, INFO_SIZE, INFO_VER2);
            if (rc != 0) return 5;

            int numPstates = Marshal.ReadInt32(info, OFF_NUMPSTATES);
            int numClocks = Marshal.ReadInt32(info, OFF_NUMCLOCKS);
            Console.WriteLine("numPstates={0} numClocks={1}", numPstates, numClocks);

            for (int p = 0; p < numPstates && p < 16; p++)
            {
                int pstateId = Marshal.ReadInt32(info, OFF_PSTATES + p * PSTATE_SIZE);
                for (int c = 0; c < numClocks && c < 8; c++)
                {
                    int o = ClockOff(p, c);
                    uint domain = (uint)Marshal.ReadInt32(info, o);
                    int delta = Marshal.ReadInt32(info, o + 12);
                    int dmin = Marshal.ReadInt32(info, o + 16);
                    int dmax = Marshal.ReadInt32(info, o + 20);
                    Console.WriteLine("  P{0} {1,-9} offset {2,9} kHz (allowed {3} .. {4})",
                        pstateId, DomainName(domain), delta, dmin, dmax);
                }
            }

            if (args.Length == 2 && setPstates != null)
            {
                int coreKHz = int.Parse(args[0]);
                int memKHz = int.Parse(args[1]);
                Console.WriteLine();
                Console.WriteLine("setting offsets: core={0} kHz memory={1} kHz", coreKHz, memKHz);

                // Build a minimal set request: P0 only, the clock domains we want to change.
                IntPtr req = Marshal.AllocHGlobal(INFO_SIZE);
                for (int b = 0; b < INFO_SIZE; b++) Marshal.WriteByte(req, b, 0);
                Marshal.WriteInt32(req, 0, unchecked((int)INFO_VER2));
                Marshal.WriteInt32(req, OFF_NUMPSTATES, 1);
                Marshal.WriteInt32(req, OFF_NUMCLOCKS, 2);
                Marshal.WriteInt32(req, OFF_PSTATES, 0);              // pstateId P0
                int c0 = ClockOff(0, 0);
                Marshal.WriteInt32(req, c0, 0);                        // GRAPHICS
                Marshal.WriteInt32(req, c0 + 12, coreKHz);
                int c1 = ClockOff(0, 1);
                Marshal.WriteInt32(req, c1, 4);                        // MEMORY
                Marshal.WriteInt32(req, c1 + 12, memKHz);
                int src = setPstates(gpus[0], req);
                Console.WriteLine("SetPstates20 rc={0}{1}", src, src == -178 ? " (needs admin / not permitted)" : "");
                Marshal.FreeHGlobal(req);

                for (int b = 0; b < INFO_SIZE; b++) Marshal.WriteByte(info, b, 0);
                Marshal.WriteInt32(info, 0, unchecked((int)INFO_VER2));
                if (getPstates(gpus[0], info) == 0)
                {
                    int c = ClockOff(0, 0);
                    Console.WriteLine("read back: core {0} kHz, memory {1} kHz",
                        Marshal.ReadInt32(info, c + 12), Marshal.ReadInt32(info, ClockOff(0, 1) + 12));
                }
            }
        }
        finally { Marshal.FreeHGlobal(info); }
        return 0;
    }
}
