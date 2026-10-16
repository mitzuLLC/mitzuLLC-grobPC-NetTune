using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GrobPC.NetTune
{
    // Registry value kinds, named as Microsoft.Win32.RegistryValueKind (the snapshot files store these names).
    public static class RegKind
    {
        public const string String = "String", ExpandString = "ExpandString", DWord = "DWord", QWord = "QWord";
        public const string MultiString = "MultiString", Binary = "Binary", None = "None", Unknown = "Unknown";

        public static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { String, "REG_SZ" }, { DWord, "REG_DWORD" }, { QWord, "REG_QWORD" }, { ExpandString, "REG_EXPAND_SZ" },
            { MultiString, "REG_MULTI_SZ" }, { Binary, "REG_BINARY" }, { None, "REG_NONE" },
        };

        public static string Name(string kind) { string n; return kind != null && Names.TryGetValue(kind, out n) ? n : kind; }
        public static bool IsScalar(string kind) { return kind == String || kind == ExpandString || kind == DWord || kind == QWord; }
    }

    // One registry value: DWord / QWord = unsigned decimal text, Binary / None = hex text, string kinds = text,
    // MultiString = Lines. Exactly the representation of the PowerShell tool, so snapshots stay interchangeable.
    public sealed class RegValue
    {
        public string Kind = RegKind.String;
        public string Data = "";
        public string[] Lines = new string[0];

        public RegValue() { }
        public RegValue(string kind, string data) { Kind = kind; Data = data ?? ""; }
        public static RegValue Multi(params string[] lines) { return new RegValue { Kind = RegKind.MultiString, Data = "", Lines = lines ?? new string[0] }; }

        public RegValue Clone() { return new RegValue { Kind = Kind, Data = Data, Lines = (string[])Lines.Clone() }; }

        public string Text { get { return Kind == RegKind.MultiString ? string.Join("|", Lines) : Data; } }

        // Test-RegValueEqual: same kind and data (hex compared case-insensitively, text exactly)
        public static bool Equal(RegValue a, RegValue b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Kind != b.Kind) return false;
            if (a.Kind == RegKind.MultiString) return string.Join("\n", a.Lines) == string.Join("\n", b.Lines);
            if (a.Kind == RegKind.Binary || a.Kind == RegKind.None) return string.Equals(a.Data, b.Data, StringComparison.OrdinalIgnoreCase);
            return string.Equals(a.Data, b.Data, StringComparison.Ordinal);
        }

        // Test-RegSameData: the same content stored in another scalar type ("1" REG_SZ ~ 1 REG_DWORD)
        public static bool SameData(RegValue a, RegValue b)
        {
            if (a == null || b == null) return false;
            if (!RegKind.IsScalar(a.Kind) || !RegKind.IsScalar(b.Kind)) return false;
            return string.Equals(a.Data, b.Data, StringComparison.Ordinal);
        }

        // Format-RegValue (log / restore tables)
        public static string Format(RegValue v)
        {
            if (v == null) return "(absent)";
            if (v.Kind == RegKind.String || v.Kind == RegKind.ExpandString) return v.Kind + ":\"" + v.Data + "\"";
            return v.Kind + ":" + v.Text;
        }

        // Format-PlanValue (preview tables)
        public static string FormatPlan(RegValue v)
        {
            if (v == null) return "(absent)";
            string t = RegKind.Name(v.Kind);
            if (v.Kind == RegKind.String || v.Kind == RegKind.ExpandString) return "\"" + v.Text + "\" " + t;
            return v.Text + " " + t;
        }

        // ConvertTo-KeptKind (R6 "keep the existing type"): my data in the kind already in the registry; null = impossible
        public static RegValue KeptKind(RegValue now, string data)
        {
            if (now == null) return null;
            ulong n;
            bool isNum = ulong.TryParse(data, NumberStyles.None, CultureInfo.InvariantCulture, out n);
            switch (now.Kind)
            {
                case RegKind.String: return new RegValue(RegKind.String, data);
                case RegKind.ExpandString: return new RegValue(RegKind.ExpandString, data);
                case RegKind.DWord: if (isNum && n <= uint.MaxValue) return new RegValue(RegKind.DWord, n.ToString(CultureInfo.InvariantCulture)); break;
                case RegKind.QWord: if (isNum) return new RegValue(RegKind.QWord, n.ToString(CultureInfo.InvariantCulture)); break;
            }
            return null;
        }
    }

    // The raw registry: the real one (Windows, 64-bit view) or the in-memory one (tests, selftest). Paths are
    // "HKLM\..." / "HKCU\...". Value names are literal ("*RSS" and "RSS" are different values).
    public interface IRegistry
    {
        bool KeyExists(string path);
        string[] GetSubKeyNames(string path);
        string[] GetValueNames(string path);
        RegValue GetValue(string path, string name);          // null = key or value absent
        void CreateKey(string path);
        void SetValue(string path, string name, RegValue value);
        void RemoveValue(string path, string name);
    }

    public static class RegPath
    {
        // Get-NormalPath
        public static string Normal(string path)
        {
            string p = (path ?? "").Trim().TrimEnd('\\');
            p = Regex.Replace(p, "^(?i)HKEY_LOCAL_MACHINE", "HKLM");
            p = Regex.Replace(p, "^(?i)HKEY_CURRENT_USER", "HKCU");
            p = Regex.Replace(p, "^(?i)(HKLM|HKCU):", "$1");
            return p;
        }

        public static bool Same(string a, string b) { return string.Equals(Normal(a), Normal(b), StringComparison.OrdinalIgnoreCase); }
        public static string ToRegExe(string path)
        {
            string p = Normal(path);
            p = Regex.Replace(p, "^HKLM", "HKEY_LOCAL_MACHINE");
            return Regex.Replace(p, "^HKCU", "HKEY_CURRENT_USER");
        }
    }

    // In-memory registry with the semantics of the PowerShell mock backend (keys and their parents are created on write).
    public sealed class MockRegistry : IRegistry
    {
        private readonly Dictionary<string, Dictionary<string, RegValue>> _keys =
            new Dictionary<string, Dictionary<string, RegValue>>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, RegValue> Key(string path, bool create)
        {
            string p = RegPath.Normal(path);
            Dictionary<string, RegValue> k;
            if (_keys.TryGetValue(p, out k)) return k;
            if (!create) return null;
            string cur = "";
            foreach (string part in p.Split('\\'))
            {
                cur = cur.Length == 0 ? part : cur + "\\" + part;
                if (!_keys.ContainsKey(cur)) _keys[cur] = new Dictionary<string, RegValue>(StringComparer.OrdinalIgnoreCase);
            }
            return _keys[p];
        }

        public bool KeyExists(string path) { return Key(path, false) != null; }

        public string[] GetSubKeyNames(string path)
        {
            string p = RegPath.Normal(path) + "\\";
            var names = new List<string>();
            foreach (string k in _keys.Keys)
                if (k.Length > p.Length && k.StartsWith(p, StringComparison.OrdinalIgnoreCase) && k.IndexOf('\\', p.Length) < 0)
                    names.Add(k.Substring(p.Length));
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names.ToArray();
        }

        public string[] GetValueNames(string path)
        {
            var k = Key(path, false);
            if (k == null) return new string[0];
            var n = k.Keys.ToList();
            n.Sort(StringComparer.OrdinalIgnoreCase);
            return n.ToArray();
        }

        public RegValue GetValue(string path, string name)
        {
            var k = Key(path, false);
            RegValue v;
            if (k == null || !k.TryGetValue(name, out v)) return null;
            return v.Clone();
        }

        public void CreateKey(string path) { Key(path, true); }

        public void SetValue(string path, string name, RegValue value)
        {
            var k = Key(path, true);
            RegValue s = value.Clone();
            // stored the way the real registry would give it back
            if (s.Kind == RegKind.DWord) s.Data = uint.Parse(s.Data, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            if (s.Kind == RegKind.QWord) s.Data = ulong.Parse(s.Data, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            if (s.Kind == RegKind.Binary || s.Kind == RegKind.None) s.Data = Hex.From(Hex.ToBytes(s.Data));
            k[name] = s;
        }

        public void RemoveValue(string path, string name)
        {
            var k = Key(path, false);
            if (k != null) k.Remove(name);
        }

        public void Clear() { _keys.Clear(); }
    }

    public static class Hex
    {
        public static string From(byte[] b)
        {
            var sb = new StringBuilder();
            if (b != null) foreach (byte x in b) sb.Append(x.ToString("X2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public static byte[] ToBytes(string hex)
        {
            string h = Regex.Replace(hex ?? "", "[^0-9A-Fa-f]", "");
            if (h.Length % 2 != 0) throw new FormatException("Odd hex string: " + hex);
            var o = new byte[h.Length / 2];
            for (int i = 0; i < o.Length; i++) o[i] = Convert.ToByte(h.Substring(i * 2, 2), 16);
            return o;
        }
    }
}
