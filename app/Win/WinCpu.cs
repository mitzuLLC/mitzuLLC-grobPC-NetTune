using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GrobPC.NetTune.App
{
    // ICpuSource on Windows: GetSystemCpuSetInformation for the per-LP topology (group, core, LLC, efficiency class),
    // ProcessorNameString from the registry for the name. Same P/Invoke as grobyq's Native.GetCpuSets.
    public sealed class WinCpu : ICpuSource
    {
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemCpuSetInformation(IntPtr info, uint len, out uint ret, IntPtr proc, uint flags);

        public string GetName()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", false))
                    if (k != null) return Convert.ToString(k.GetValue("ProcessorNameString"), System.Globalization.CultureInfo.InvariantCulture).Trim();
            }
            catch { }
            return "";
        }

        // SYSTEM_CPU_SET_INFORMATION: Size+0 Type+4 Id+8 Group+12 LP+14 Core+15 LLC+16 Numa+17 Eff+18 Flags+19
        public List<LpRecord> GetRecords(out string source)
        {
            source = "CpuSets";
            var list = new List<LpRecord>();
            uint need = 0;
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out need, GetCurrentProcess(), 0);
            if (need == 0) { source = "UNKNOWN-topology"; return list; }
            IntPtr buf = Marshal.AllocHGlobal((int)need);
            try
            {
                uint got;
                if (!GetSystemCpuSetInformation(buf, need, out got, GetCurrentProcess(), 0)) { source = "UNKNOWN-topology"; return list; }
                long off = 0;
                while (off < (long)got)
                {
                    IntPtr p = new IntPtr(buf.ToInt64() + off);
                    int size = Marshal.ReadInt32(p, 0);
                    int type = Marshal.ReadInt32(p, 4);
                    if (size <= 0) break;
                    if (type == 0)
                        list.Add(new LpRecord
                        {
                            Id = Marshal.ReadInt32(p, 8),
                            Group = (ushort)Marshal.ReadInt16(p, 12),
                            Lp = Marshal.ReadByte(p, 14),
                            Core = Marshal.ReadByte(p, 15),
                            Llc = Marshal.ReadByte(p, 16),
                            Numa = Marshal.ReadByte(p, 17),
                            Eff = Marshal.ReadByte(p, 18),
                            Flags = Marshal.ReadByte(p, 19),
                        });
                    off += size;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            if (list.Count == 0) source = "UNKNOWN-topology";
            return list;
        }
    }

    public static class Admin
    {
        // Test-IsAdmin: the app manifest requires administrator, so this is a guard, not the elevation path.
        public static bool IsAdmin()
        {
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}
