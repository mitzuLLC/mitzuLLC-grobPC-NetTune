using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace GrobPC.NetTune
{
    // A physical network adapter (what Get-NetAdapter -Physical reports).
    public sealed class AdapterRecord
    {
        public string Alias = "", Description = "", Status = "", LinkSpeed = "", MediaText = "", Guid = "", PnpId = "";
        public string DriverProvider = "", DriverVersion = "";
        public int Medium = -1, IfIndex = -1;
    }

    // RSS state of one adapter (Get-NetAdapterRss). Table / ProcArray entries are "group:CPU".
    public sealed class RssRecord
    {
        public bool Enabled;
        public string Profile = "", BaseGroup = "", BaseNumber = "", MaxGroup = "", MaxNumber = "", MaxProcessors = "", Queues = "";
        public string[] Table = new string[0], ProcArray = new string[0];
    }

    // One driver keyword (Get-NetAdapterAdvancedProperty -AllProperties)
    public sealed class AdvancedRecord
    {
        public string Keyword = "", Value = "", Display = "", DisplayValue = "", DataType = "";
    }

    // Set-NetAdapterRss parameters (without -Name / -NoRestart)
    public sealed class RssParams
    {
        public int BaseProcessorNumber, MaxProcessorNumber;
        public int MaxProcessors = 1;
        public int? NumberOfReceiveQueues = 1;
        public string Profile = "Closest";
        public int? BaseProcessorGroup, MaxProcessorGroup;

        // "k=v" pairs in name order - the selftest compares calls by this text
        public string Describe()
        {
            var kv = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            kv["BaseProcessorNumber"] = BaseProcessorNumber.ToString(CultureInfo.InvariantCulture);
            kv["MaxProcessorNumber"] = MaxProcessorNumber.ToString(CultureInfo.InvariantCulture);
            kv["MaxProcessors"] = MaxProcessors.ToString(CultureInfo.InvariantCulture);
            if (NumberOfReceiveQueues.HasValue) kv["NumberOfReceiveQueues"] = NumberOfReceiveQueues.Value.ToString(CultureInfo.InvariantCulture);
            kv["Profile"] = Profile;
            if (BaseProcessorGroup.HasValue) kv["BaseProcessorGroup"] = BaseProcessorGroup.Value.ToString(CultureInfo.InvariantCulture);
            if (MaxProcessorGroup.HasValue) kv["MaxProcessorGroup"] = MaxProcessorGroup.Value.ToString(CultureInfo.InvariantCulture);
            return string.Join(",", kv.Select(p => p.Key + "=" + p.Value));
        }
    }

    public sealed class NativeResult { public int Code; public string Text = ""; }

    // Everything that reads or changes the network stack (the NetAdapter cmdlets, netsh, reg export).
    // Windows: PowerShell / netsh bridge in the app. Tests and /selftest: StubNet. Text results: "" = ok, else the error.
    public interface INetSystem
    {
        AdapterRecord[] GetAdapters();                        // throws when the NetAdapter module is missing
        RssRecord GetRss(string alias);                       // null = the driver exposes no RSS object
        string EnableRss(string alias);
        string SetRss(string alias, RssParams p);
        AdvancedRecord[] GetAdvanced(string alias);
        string RestartAdapter(string alias);
        string GetAdapterStatus(string alias);
        string GetGateway(int ifIndex);                       // "" = no IPv4 default gateway
        int GetNlMtu(int ifIndex, string family);             // 0 = not available
        NativeResult SetMtu(string alias, int mtu, string family);
        int RegExport(string keyPath, string file);
    }

    // CPU name + per-LP records (GetSystemCpuSetInformation on Windows, synthetic in tests)
    public interface ICpuSource
    {
        string GetName();
        List<LpRecord> GetRecords(out string source);
    }

    public static class NetUtil
    {
        // Get-MediaKind: NdisPhysicalMedium 14 = 802.3, 9 = Native 802.11, 1 = Wireless LAN
        public static string MediaKind(AdapterRecord r)
        {
            if (r.Medium == 14) return "LAN";
            if (r.Medium == 9 || r.Medium == 1) return "Wi-Fi";
            string t = r.MediaText ?? "";
            if (Regex.IsMatch(t, @"802\.3")) return "LAN";
            if (Regex.IsMatch(t, @"802\.11") || Regex.IsMatch(t, "(?i)wireless")) return "Wi-Fi";
            return "Other";
        }

        // Select-DefaultAdapter: physical + Up, wired first; then Wi-Fi; then anything
        public static AdapterRecord SelectDefault(IEnumerable<AdapterRecord> records)
        {
            var recs = records.Where(r => r != null).ToList();
            foreach (string kind in new[] { "LAN", "Wi-Fi" })
                foreach (var r in recs) if (r.Status == "Up" && MediaKind(r) == kind) return r;
            foreach (string kind in new[] { "LAN", "Wi-Fi" })
                foreach (var r in recs) if (MediaKind(r) == kind) return r;
            return recs.Count > 0 ? recs[0] : null;
        }

        // Format-RssTable: "0:0 x16   (16 entries, group:CPU)"
        public static string FormatRssTable(IEnumerable<string> table)
        {
            var t = (table ?? new string[0]).Where(x => x != null).ToList();
            if (t.Count == 0) return "(empty: link down or RSS off)";
            var order = new List<string>();
            var counts = new Dictionary<string, int>();
            foreach (string e in t)
            {
                if (!counts.ContainsKey(e)) { counts[e] = 0; order.Add(e); }
                counts[e]++;
            }
            return string.Join(", ", order.Select(k => k + " x" + counts[k])) + string.Format(CultureInfo.InvariantCulture, "   ({0} entries, group:CPU)", t.Count);
        }

        public static string SafeName(string text)
        {
            string s = Regex.Replace(text ?? "", "[^A-Za-z0-9._-]", "_").Trim('_');
            if (s.Length > 40) s = s.Substring(0, 40);
            return s.Length == 0 ? "adapter" : s;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    //  Test double for INetSystem (unit tests and /selftest): adapters, RSS records and MTU come from these tables and
    //  every call that would change the system is only recorded, together with "did a backup exist at that moment".
    // ---------------------------------------------------------------------------------------------------------------
    public sealed class StubNet : INetSystem
    {
        public List<AdapterRecord> Adapters = new List<AdapterRecord>();
        public Dictionary<string, RssRecord> Rss = new Dictionary<string, RssRecord>();
        public Dictionary<string, AdvancedRecord[]> Adv = new Dictionary<string, AdvancedRecord[]>();
        public Dictionary<string, int> Mtu = new Dictionary<string, int>();
        public string Gateway = "192.168.1.1";
        public bool RejectQueues;
        public List<string> Calls = new List<string>();
        public List<bool> Backed = new List<bool>();
        public Func<bool> BackupExists = () => false;

        private void Record(string text) { Calls.Add(text); Backed.Add(BackupExists()); }

        public AdapterRecord[] GetAdapters() { return Adapters.ToArray(); }
        public RssRecord GetRss(string alias) { RssRecord r; return Rss.TryGetValue(alias, out r) ? r : null; }
        public string EnableRss(string alias) { Record("Enable|" + alias); return ""; }

        public string SetRss(string alias, RssParams p)
        {
            Record("Set|" + alias + "|" + p.Describe());
            if (RejectQueues && p.NumberOfReceiveQueues.HasValue) return "No matching keyword value found.";
            return "";
        }

        public AdvancedRecord[] GetAdvanced(string alias) { AdvancedRecord[] a; return Adv.TryGetValue(alias, out a) ? a : new AdvancedRecord[0]; }
        public string RestartAdapter(string alias) { Record("Restart|" + alias); return ""; }
        public string GetAdapterStatus(string alias) { return "Up"; }
        public string GetGateway(int ifIndex) { return Gateway; }
        public int GetNlMtu(int ifIndex, string family) { int m; return Mtu.TryGetValue(ifIndex + "|" + family, out m) ? m : 0; }

        public NativeResult SetMtu(string alias, int mtu, string family)
        {
            Record(string.Format(CultureInfo.InvariantCulture, "Mtu|{0}|{1}|{2}", alias, family, mtu));
            foreach (var a in Adapters) if (a.Alias == alias) Mtu[a.IfIndex + "|" + family] = mtu;
            return new NativeResult { Code = 0, Text = "Ok." };
        }

        public int RegExport(string keyPath, string file) { return 0; }
    }

    public sealed class FixedCpu : ICpuSource
    {
        private readonly string _name; private readonly List<LpRecord> _recs; private readonly string _source;
        public FixedCpu(string name, List<LpRecord> recs, string source) { _name = name; _recs = recs; _source = source; }
        public string GetName() { return _name; }
        public List<LpRecord> GetRecords(out string source) { source = _source; return _recs; }
    }
}
