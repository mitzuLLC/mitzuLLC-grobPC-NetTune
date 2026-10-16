using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GrobPC.NetTune
{
    // ----------------------------------------------------------------------------------------------------------------
    //  PROFILE DATA (from RSS-LAN-UPD.reg, RSS-WI-FI.reg, buffer-wi-fi.reg - mitzu LLC / grobPC).
    //    Scope : Class = driver key Control\Class\{4d36e972-...}\000X of the selected adapter
    //            NDIS  = HKLM\SYSTEM\CurrentControlSet\Services\NDIS\Parameters (global)
    //    Kind  : String = REG_SZ, DWord = REG_DWORD (written exactly as in the .reg files)
    //    Data  : value text. Tokens: {CPU} = RSS CPU of the session (default 0), {PROFILE} = 1 Closest, 2 ClosestStatic
    // ----------------------------------------------------------------------------------------------------------------
    public sealed class ProfileEntry
    {
        public readonly string Scope, Name, Kind, Data;
        public ProfileEntry(string scope, string name, string kind, string data) { Scope = scope; Name = name; Kind = kind; Data = data; }
    }

    public static class Profiles
    {
        public const string Class = "Class", Ndis = "NDIS";

        private static ProfileEntry C(string n, string k, string d) { return new ProfileEntry(Class, n, k, d); }
        private static ProfileEntry N(string n, string k, string d) { return new ProfileEntry(Ndis, n, k, d); }

        public static readonly ProfileEntry[] Lan =
        {
            C("*RSS", RegKind.String, "1"), C("*RSSProfile", RegKind.String, "{PROFILE}"), C("RSS", RegKind.String, "1"),
            C("RssCapabilities", RegKind.DWord, "1"), C("ForceRssEnable", RegKind.String, "1"),
            C("BaseProcessorNumber", RegKind.DWord, "{CPU}"), C("RssBaseProcNumber", RegKind.DWord, "{CPU}"),
            C("MaxProcessorNumber", RegKind.DWord, "{CPU}"), C("RssMaxProcNumber", RegKind.DWord, "{CPU}"),
            C("MaxRssProcessors", RegKind.DWord, "1"), C("*NumRssQueues", RegKind.String, "1"), C("MessageNumberLimit", RegKind.DWord, "1"),
        };

        public static readonly ProfileEntry[] Wifi =
        {
            C("*RSS", RegKind.String, "1"), C("*RSSProfile", RegKind.String, "{PROFILE}"), C("RSS", RegKind.String, "1"),
            C("ForceRssEnable", RegKind.String, "1"),
            C("BaseProcessorNumber", RegKind.DWord, "{CPU}"), C("RssBaseProcNumber", RegKind.DWord, "{CPU}"),
            C("MaxProcessorNumber", RegKind.DWord, "{CPU}"), C("RssMaxProcNumber", RegKind.DWord, "{CPU}"),
            C("MaxRssProcessors", RegKind.DWord, "1"), C("*NumRssQueues", RegKind.String, "1"),
        };

        public static readonly ProfileEntry[] WifiBuffers =
        {
            C("BufferDescriptorsRx", RegKind.DWord, "256"), C("BufferDescriptorsTx", RegKind.DWord, "128"),
        };

        public static readonly ProfileEntry[] NdisParams =
        {
            N("RssBaseCpu", RegKind.DWord, "{CPU}"), N("RssBaseCpuNumber", RegKind.DWord, "{CPU}"),
            N("RssMaxCpu", RegKind.DWord, "{CPU}"), N("RssMaxCpuNumber", RegKind.DWord, "{CPU}"),
        };

        // profile letter of menu [5] -> entries, title, adapter media it is meant for ('' = any)
        public static bool TryGet(string which, out ProfileEntry[] entries, out string title, out string media)
        {
            entries = null; title = ""; media = "";
            switch ((which ?? "").ToUpperInvariant())
            {
                case "L": entries = Lan; title = "LAN RSS"; media = "LAN"; return true;
                case "W": entries = Wifi; title = "Wi-Fi RSS"; media = "Wi-Fi"; return true;
                case "B": entries = WifiBuffers; title = "Wi-Fi buffers"; media = "Wi-Fi"; return true;
                case "N": entries = NdisParams; title = "NDIS\\Parameters"; media = ""; return true;
            }
            return false;
        }

        // Standardized NDIS keywords (MS Learn: Standardized INF Keywords for RSS / performance)
        public static readonly string[] StdRssKeywords = { "*RSS", "*RSSProfile", "*RssBaseProcNumber", "*RssMaxProcNumber", "*MaxRssProcessors",
            "*NumRssQueues", "*RssBaseProcGroup", "*RssMaxProcGroup", "*NumaNodeId" };
        public static readonly string[] StdOtherKeywords = { "*ReceiveBuffers", "*TransmitBuffers" };

        public static readonly Dictionary<string, string> RssProfileIds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Closest", "1" }, { "ClosestStatic", "2" }, { "NUMA", "3" }, { "NUMAStatic", "4" }, { "Conservative", "5" }, { "Balanced", "6" },
        };

        public static string ProfileId(string profile) { string id; return profile != null && RssProfileIds.TryGetValue(profile, out id) ? id : "1"; }

        // ConvertTo-RssProfileName: "1" / "Closest" -> "Closest"
        public static string ProfileName(string value)
        {
            string s = (value ?? "").Trim();
            foreach (var kv in RssProfileIds) if (kv.Value == s) return kv.Key;
            return s;
        }

        // Resolve-ProfileData
        public static string Resolve(string data, int cpu, string profile)
        {
            return (data ?? "").Replace("{CPU}", cpu.ToString(System.Globalization.CultureInfo.InvariantCulture)).Replace("{PROFILE}", ProfileId(profile));
        }

        // Classify-Value (R5): T1-STD = standardized NDIS keyword; VENDOR = declared in Ndi\Params of this driver;
        // UNKNOWN = neither (driver may ignore it); SKIP = a rule forbids writing it
        public static string Classify(string scope, string name, IEnumerable<string> ndiParams)
        {
            if (scope == Class && string.Equals(name, "MessageNumberLimit", StringComparison.OrdinalIgnoreCase)) return "SKIP";
            if (scope == Class)
            {
                foreach (string k in StdRssKeywords.Concat(StdOtherKeywords)) if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return "T1-STD";
                if (ndiParams != null) foreach (string k in ndiParams) if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return "VENDOR";
            }
            return "UNKNOWN";
        }
    }

    // Registry locations. Instance (not constants) so the selftest can point them at a scratch tree.
    public sealed class RegRoots
    {
        public string ClassRoot = @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
        public string NdisKey = @"HKLM\SYSTEM\CurrentControlSet\Services\NDIS\Parameters";
        public string EnumRoot = @"HKLM\SYSTEM\CurrentControlSet\Enum";
        public string ScratchRoot = @"HKCU\Software\NetTuneTest";
        public const string NetClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}";

        public static RegRoots Scratch()
        {
            var r = new RegRoots();
            r.ClassRoot = r.ScratchRoot + @"\Class";
            r.NdisKey = r.ScratchRoot + @"\NDIS\Parameters";
            r.EnumRoot = r.ScratchRoot + @"\Enum";
            return r;
        }

        // true for <network class key>\NNNN exactly (the driver key of one adapter)
        public bool IsClassInstance(string path)
        {
            string p = RegPath.Normal(path), root = RegPath.Normal(ClassRoot);
            if (p.Length != root.Length + 5) return false;
            if (!p.Substring(0, root.Length).Equals(root, StringComparison.OrdinalIgnoreCase)) return false;
            return Regex.IsMatch(p.Substring(root.Length), @"^\\\d{4}$");
        }

        // Assert-WritablePath: defense in depth - the tool writes only to these locations
        public void AssertWritable(string path, string name)
        {
            string p = RegPath.Normal(path), scratch = RegPath.Normal(ScratchRoot);
            bool ok = IsClassInstance(p) || RegPath.Same(p, NdisKey) ||
                      p.Equals(scratch, StringComparison.OrdinalIgnoreCase) || p.StartsWith(scratch + "\\", StringComparison.OrdinalIgnoreCase);
            if (!ok) throw new InvalidOperationException("Refusing to write outside the allowed registry locations: " + p);
            if (IsClassInstance(p) && string.Equals(name, "MessageNumberLimit", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to write MessageNumberLimit into the class key (its real place is Enum\\<id>\\Device Parameters\\Interrupt Management).");
        }

        public string ScopeLabel(string path)
        {
            if (RegPath.Same(path, NdisKey)) return "NDIS\\Parameters";
            if (IsClassInstance(path)) { string p = RegPath.Normal(path); return "Class\\" + p.Substring(p.Length - 4); }
            return path;
        }
    }
}
