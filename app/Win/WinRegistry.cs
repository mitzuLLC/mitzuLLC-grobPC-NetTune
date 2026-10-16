using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Win32;

namespace GrobPC.NetTune.App
{
    // IRegistry over the real registry, 64-bit view. Paths are "HKLM\..." / "HKCU\..." (as the engine uses them).
    // Reads are tolerant of access errors (return null / empty); writes surface their exception to the caller.
    // Data is formatted exactly like MockRegistry so snapshots are interchangeable: DWord/QWord decimal, Binary/None
    // uppercase hex, string kinds verbatim, MultiString in Lines.
    public sealed class WinRegistry : IRegistry
    {
        private static RegistryKey Base(string path, out string sub)
        {
            string p = RegPath.Normal(path);
            int bar = p.IndexOf('\\');
            string hive = bar < 0 ? p : p.Substring(0, bar);
            sub = bar < 0 ? "" : p.Substring(bar + 1);
            RegistryHive h;
            if (hive.Equals("HKLM", StringComparison.OrdinalIgnoreCase)) h = RegistryHive.LocalMachine;
            else if (hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase)) h = RegistryHive.CurrentUser;
            else if (hive.Equals("HKCR", StringComparison.OrdinalIgnoreCase)) h = RegistryHive.ClassesRoot;
            else if (hive.Equals("HKU", StringComparison.OrdinalIgnoreCase)) h = RegistryHive.Users;
            else throw new InvalidOperationException("Unknown registry hive: " + hive);
            return RegistryKey.OpenBaseKey(h, RegistryView.Registry64);
        }

        private RegistryKey Open(string path, bool write)
        {
            string sub;
            var b = Base(path, out sub);
            if (sub.Length == 0) return b;
            var k = b.OpenSubKey(sub, write);
            if (k == null) b.Dispose();
            return k;
        }

        public bool KeyExists(string path)
        {
            try { using (var k = Open(path, false)) return k != null; } catch { return false; }
        }

        public string[] GetSubKeyNames(string path)
        {
            try { using (var k = Open(path, false)) return k == null ? new string[0] : k.GetSubKeyNames(); }
            catch { return new string[0]; }
        }

        public string[] GetValueNames(string path)
        {
            try { using (var k = Open(path, false)) return k == null ? new string[0] : k.GetValueNames(); }
            catch { return new string[0]; }
        }

        public RegValue GetValue(string path, string name)
        {
            try
            {
                using (var k = Open(path, false))
                {
                    if (k == null) return null;
                    // literal name, no expansion of REG_EXPAND_SZ
                    object v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (v == null) return null;
                    switch (k.GetValueKind(name))
                    {
                        case RegistryValueKind.String: return new RegValue(RegKind.String, Convert.ToString(v, CultureInfo.InvariantCulture));
                        case RegistryValueKind.ExpandString: return new RegValue(RegKind.ExpandString, Convert.ToString(v, CultureInfo.InvariantCulture));
                        case RegistryValueKind.DWord: return new RegValue(RegKind.DWord, unchecked((uint)Convert.ToInt32(v, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture));
                        case RegistryValueKind.QWord: return new RegValue(RegKind.QWord, unchecked((ulong)Convert.ToInt64(v, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture));
                        case RegistryValueKind.Binary: return new RegValue(RegKind.Binary, Hex.From((byte[])v));
                        case RegistryValueKind.MultiString: return RegValue.Multi((string[])v);
                        case RegistryValueKind.None: return new RegValue(RegKind.None, Hex.From(v as byte[] ?? new byte[0]));
                        default: return new RegValue(RegKind.Unknown, Convert.ToString(v, CultureInfo.InvariantCulture));
                    }
                }
            }
            catch { return null; }
        }

        public void CreateKey(string path)
        {
            string sub;
            using (var b = Base(path, out sub))
                if (sub.Length > 0) b.CreateSubKey(sub, true).Dispose();
        }

        public void SetValue(string path, string name, RegValue value)
        {
            string sub;
            using (var b = Base(path, out sub))
            using (var k = b.CreateSubKey(sub, true))
            {
                switch (value.Kind)
                {
                    case RegKind.String: k.SetValue(name, value.Data ?? "", RegistryValueKind.String); break;
                    case RegKind.ExpandString: k.SetValue(name, value.Data ?? "", RegistryValueKind.ExpandString); break;
                    case RegKind.DWord: k.SetValue(name, unchecked((int)uint.Parse(value.Data, CultureInfo.InvariantCulture)), RegistryValueKind.DWord); break;
                    case RegKind.QWord: k.SetValue(name, unchecked((long)ulong.Parse(value.Data, CultureInfo.InvariantCulture)), RegistryValueKind.QWord); break;
                    case RegKind.Binary: k.SetValue(name, Hex.ToBytes(value.Data), RegistryValueKind.Binary); break;
                    case RegKind.None: k.SetValue(name, Hex.ToBytes(value.Data), RegistryValueKind.None); break;
                    case RegKind.MultiString: k.SetValue(name, value.Lines ?? new string[0], RegistryValueKind.MultiString); break;
                    default: throw new InvalidOperationException("Cannot write registry value of kind " + value.Kind);
                }
            }
        }

        public void RemoveValue(string path, string name)
        {
            string sub;
            using (var b = Base(path, out sub))
            using (var k = b.OpenSubKey(sub, true))
                if (k != null) k.DeleteValue(name, false);
        }
    }
}
