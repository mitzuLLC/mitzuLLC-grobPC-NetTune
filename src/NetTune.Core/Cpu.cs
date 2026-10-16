using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace GrobPC.NetTune
{
    // One logical processor as GetSystemCpuSetInformation reports it. Core / Llc are the group-relative CoreIndex /
    // LastLevelCacheIndex; Eff = EfficiencyClass (higher = faster on hybrid CPUs).
    public sealed class LpRecord
    {
        public int Id, Group, Lp, Core, Llc, Numa, Eff, Flags;
    }

    public sealed class CoreInfo
    {
        public int Core;          // ordinal, in order of the first LP
        public int[] Lps;
        public int Eff, Group;
        public bool IsP;
    }

    public sealed class CpuModel
    {
        public string Name = "", Source = "";
        public int Cores, Logical, Groups, EffMax;
        public int[] GroupIds = new int[0];
        public bool MultiGroup, Smt, Hybrid, TopologyKnown;
        public CoreInfo[] CoreList = new CoreInfo[0];
        public Dictionary<string, int> LpToCore = new Dictionary<string, int>();
        public Dictionary<int, int[]> GroupLps = new Dictionary<int, int[]>();
    }

    public sealed class RssCpuCheck
    {
        public bool Ok;
        public string Error = "", Info = "";
        public List<string> Warn = new List<string>();
    }

    public static class Topology
    {
        // New-SyntheticLpRecords: cores P-cores x threads, then single-thread E-cores; coresPerLlc > 0 splits L3 domains
        public static List<LpRecord> Synthetic(int cores, int threadsPerCore, int eCores, int coresPerLlc)
        {
            var list = new List<LpRecord>();
            int lp = 0, pEff = eCores > 0 ? 1 : 0;
            for (int c = 0; c < cores; c++)
            {
                int first = lp, llc = 0;
                if (coresPerLlc > 0) llc = (c / coresPerLlc) * coresPerLlc * threadsPerCore;
                for (int t = 0; t < threadsPerCore; t++) { list.Add(new LpRecord { Id = 256 + lp, Group = 0, Lp = lp, Core = first, Llc = llc, Eff = pEff }); lp++; }
            }
            for (int e = 0; e < eCores; e++) { list.Add(new LpRecord { Id = 256 + lp, Group = 0, Lp = lp, Core = lp, Llc = 0, Eff = 0 }); lp++; }
            return list;
        }

        // ConvertTo-CpuModel
        public static CpuModel Build(string name, string source, IEnumerable<LpRecord> records)
        {
            var recs = records.OrderBy(r => r.Group).ThenBy(r => r.Lp).ToList();
            var m = new CpuModel { Name = name, Source = source };
            m.GroupIds = recs.Select(r => r.Group).Distinct().OrderBy(g => g).ToArray();
            var effs = recs.Select(r => r.Eff).Distinct().OrderBy(e => e).ToList();
            m.EffMax = effs.Count > 0 ? effs[effs.Count - 1] : 0;
            m.Hybrid = effs.Count > 1;

            var order = new List<string>();
            var lpsOf = new Dictionary<string, List<int>>();
            var effOf = new Dictionary<string, int>();
            var grpOf = new Dictionary<string, int>();
            foreach (var r in recs)
            {
                string ck = r.Group + ":" + r.Core;
                if (!lpsOf.ContainsKey(ck)) { order.Add(ck); lpsOf[ck] = new List<int>(); effOf[ck] = r.Eff; grpOf[ck] = r.Group; }
                lpsOf[ck].Add(r.Lp);
            }
            var cores = new List<CoreInfo>();
            int ord = 0;
            foreach (string ck in order)
            {
                var lps = lpsOf[ck].OrderBy(x => x).ToArray();
                cores.Add(new CoreInfo { Core = ord, Lps = lps, Eff = effOf[ck], IsP = effOf[ck] == m.EffMax, Group = grpOf[ck] });
                foreach (int lp in lps) m.LpToCore[grpOf[ck] + ":" + lp] = ord;
                ord++;
            }
            m.CoreList = cores.ToArray();
            m.Cores = cores.Count;
            m.Logical = recs.Count;
            m.Groups = m.GroupIds.Length;
            m.MultiGroup = m.Groups > 1;
            m.Smt = cores.Any(c => c.Lps.Length > 1);
            foreach (int g in m.GroupIds) m.GroupLps[g] = recs.Where(r => r.Group == g).Select(r => r.Lp).ToArray();
            m.TopologyKnown = source != "UNKNOWN-topology";
            return m;
        }

        public static CoreInfo CoreOfLp(CpuModel cpu, int group, int lp)
        {
            int i;
            return cpu.LpToCore.TryGetValue(group + ":" + lp, out i) ? cpu.CoreList[i] : null;
        }

        // Test-RssCpu (R10): validates the CPU chosen for RSS
        public static RssCpuCheck TestRssCpu(CpuModel cpu, int group, int lp)
        {
            var res = new RssCpuCheck();
            int[] lps;
            if (!cpu.GroupLps.TryGetValue(group, out lps))
            {
                res.Error = string.Format(CultureInfo.InvariantCulture, "Processor group {0} does not exist (groups: {1}).", group, string.Join(",", cpu.GroupIds));
                return res;
            }
            if (lp < 0 || !lps.Contains(lp))
            {
                res.Error = string.Format(CultureInfo.InvariantCulture, "CPU {0} does not exist in group {1} (valid: 0..{2}).", lp, group, lps.Length - 1);
                return res;
            }
            if (lp > 255) { res.Error = "Set-NetAdapterRss accepts processor numbers 0..255 only."; return res; }
            res.Ok = true;
            var core = CoreOfLp(cpu, group, lp);
            if (core != null)
            {
                var sib = core.Lps.Where(x => x != lp).ToArray();
                string kind = "";
                if (cpu.Hybrid)
                {
                    if (core.IsP) kind = " [P-core]";
                    else
                    {
                        kind = " [E-core]";
                        res.Warn.Add(string.Format(CultureInfo.InvariantCulture, "CPU {0} is an E-core on this hybrid CPU: lower single-thread throughput for network DPCs.", lp));
                    }
                }
                string sibText = sib.Length > 0 ? "LP " + Masks.FormatLpList(sib) : "none (no SMT on this core)";
                int first = core.Lps.Min();
                if (lp != first)
                    res.Warn.Add(string.Format(CultureInfo.InvariantCulture, "CPU {0} is not the first SMT thread of core {1} (LP {2} is): RSS uses up to one logical processor per core, so Windows may put the queue on LP {2}. Check IndirectionTable in [8].", lp, core.Core, first));
                res.Info = string.Format(CultureInfo.InvariantCulture, "CPU {0} = core {1}{2}, SMT sibling: {3}", lp, core.Core, kind, sibText);
            }
            else res.Info = string.Format(CultureInfo.InvariantCulture, "CPU {0} (core relationship unknown)", lp);
            if (!cpu.TopologyKnown) res.Warn.Add("Core topology is unknown on this system: SMT sibling / P-E class cannot be checked.");
            return res;
        }

        // Show-CpuMap cells: "Core 0: LP 0,1 [P]"
        public static List<string> MapCells(CpuModel cpu)
        {
            var cells = new List<string>();
            foreach (var c in cpu.CoreList)
            {
                string kind = cpu.Hybrid ? (c.IsP ? " [P]" : " [E]") : "";
                string grp = cpu.MultiGroup ? "g" + c.Group + " " : "";
                cells.Add(grp + "Core " + c.Core + ": LP " + string.Join(",", c.Lps) + kind);
            }
            return cells;
        }
    }

    public static class Masks
    {
        public static ulong FromList(IEnumerable<int> lps)
        {
            ulong m = 0;
            foreach (int n in lps)
            {
                if (n < 0 || n > 63) throw new ArgumentOutOfRangeException("lps", "LP index out of range (0..63): " + n);
                m |= 1UL << n;
            }
            return m;
        }

        public static int[] ToList(ulong mask)
        {
            var o = new List<int>();
            for (int i = 0; i < 64; i++) if ((mask & (1UL << i)) != 0) o.Add(i);
            return o.ToArray();
        }

        public static ulong FromBytes(byte[] bytes)
        {
            var b = new byte[8];
            if (bytes != null) Array.Copy(bytes, b, Math.Min(8, bytes.Length));
            return BitConverter.ToUInt64(b, 0);
        }

        public static string Format(ulong mask) { return string.Format(CultureInfo.InvariantCulture, "0x{0:X} ({0})", mask); }

        // Format-LpList: 0,1,2,3,5 -> "0-3,5"; two in a row stay "4,5"
        public static string FormatLpList(IEnumerable<int> lps)
        {
            var a = lps.Distinct().OrderBy(x => x).ToList();
            if (a.Count == 0) return "-";
            var parts = new List<string>();
            int start = a[0], prev = a[0];
            for (int i = 1; i <= a.Count; i++)
            {
                bool has = i < a.Count;
                int cur = has ? a[i] : 0;
                if (has && cur == prev + 1) { prev = cur; continue; }
                if (start == prev) parts.Add(start.ToString(CultureInfo.InvariantCulture));
                else if (prev == start + 1) { parts.Add(start.ToString(CultureInfo.InvariantCulture)); parts.Add(prev.ToString(CultureInfo.InvariantCulture)); }
                else parts.Add(start + "-" + prev);
                if (has) { start = cur; prev = cur; }
            }
            return string.Join(",", parts);
        }
    }
}
