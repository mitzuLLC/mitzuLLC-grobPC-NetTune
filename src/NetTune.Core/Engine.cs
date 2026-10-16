using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace GrobPC.NetTune
{
    // ================================================================================================================
    //  grobPC NetTune engine: RSS queue of the active network adapter -> one CPU, registry profile import, verify,
    //  backup / exact restore. Port of grobPC-NetTune.cmd v1.0 (mitzu LLC / grobPC, AerialOS). The rules R1..R18 and
    //  the texts are the ones of the PowerShell tool; the selftest of that tool is the acceptance suite of this one.
    //  UI-agnostic: output and questions go through IUi, every system access through IRegistry / INetSystem / ICpuSource.
    // ================================================================================================================
    public sealed class Engine
    {
        public const string Version = "2.0.0";

        // ---- dependencies ----
        public readonly IRegistry Raw;
        public readonly RegRoots Roots;
        public readonly INetSystem Net;
        public readonly ICpuSource CpuSource;
        public IUi Ui;
        public Action<string> LogSink;                        // "HH:mm:ss text" lines (file log)
        public Action<int> Sleep = ms => System.Threading.Thread.Sleep(ms);
        public Func<DateTime> Clock = () => DateTime.Now;
        public string Computer = Environment.MachineName;
        public string BaseDir;                                // backups: <BaseDir>\NetTune-backup\...

        // ---- session state ----
        public CpuModel Cpu;
        public AdapterRecord[] Adapters = new AdapterRecord[0];
        public TargetContext Ctx;
        public int RssCpu = 0, RssGroup = 0;
        public string RssProfile = "Closest";                 // Closest | ClosestStatic
        public bool DryRun;
        public string LastBackup = "", BackupKey = "";
        public bool QueuesRejected, RestartPending;
        public VerifyResult LastVerify;

        public Engine(IRegistry raw, RegRoots roots, INetSystem net, ICpuSource cpu, IUi ui, string baseDir)
        {
            Raw = raw; Roots = roots; Net = net; CpuSource = cpu; Ui = ui; BaseDir = baseDir;
        }

        // =============================================================================================== output
        public void Log(string text)
        {
            var s = LogSink;
            if (s == null) return;
            try { s(Clock().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + text); } catch { }
        }

        public void Say(string text, Tone tone = Tone.Normal) { Ui.Say(text ?? "", tone); Log(text ?? ""); }
        public void St(string status, string text) { Say(Status.Line(status, text), Status.ToneOf(status)); }

        public void Table(string[] headers, List<string[]> rows, int[] widths)
        {
            Ui.Table(headers, rows, widths);
            foreach (string l in TableText.Render(headers, rows, widths)) Log(l);
        }

        private string AnswerOf(Question q)
        {
            string a = (Ui.Answer(q) ?? "").Trim();
            Log("? " + q.Text + " -> " + a);
            return a;
        }

        // Ask: free text; Enter = default
        public string Ask(string prompt, string def)
        {
            string a = AnswerOf(new Question { Prompt = prompt, Default = def });
            return a.Length == 0 && def != null ? def : a;
        }

        public string AskChoice(string prompt, string def, params Choice[] options)
        {
            string a = AnswerOf(new Question { Prompt = prompt, Default = def, Options = options });
            return a.Length == 0 && def != null ? def : a;
        }

        // Confirm-YN: Enter = default; anything starting with y = yes
        public bool Confirm(string prompt, bool defaultYes)
        {
            string a = AnswerOf(new Question { Prompt = prompt, YesNo = true, DefaultYes = defaultYes });
            if (a.Length == 0) return defaultYes;
            return a.StartsWith("y", StringComparison.OrdinalIgnoreCase);
        }

        // =============================================================================================== registry (guarded)
        // The only writers. Allowed locations only (Assert-WritablePath), nothing in dry-run, everything logged.
        public bool RegCreateKey(string path)
        {
            Roots.AssertWritable(path, "");
            if (DryRun) { Log("DRYRUN: would create key [" + RegPath.Normal(path) + "]"); return false; }
            Raw.CreateKey(path);
            return true;
        }

        public bool RegSet(string path, string name, RegValue v)
        {
            Roots.AssertWritable(path, name);
            if (DryRun) { Log(string.Format("DRYRUN: would set [{0}] \"{1}\" = {2}:{3}", RegPath.Normal(path), name, v.Kind, v.Text)); return false; }
            Raw.SetValue(path, name, v);
            Log(string.Format("SET [{0}] \"{1}\" = {2}:{3}", RegPath.Normal(path), name, v.Kind, v.Text));
            return true;
        }

        public bool RegSet(string path, string name, string kind, string data) { return RegSet(path, name, new RegValue(kind, data)); }

        public bool RegRemove(string path, string name)
        {
            Roots.AssertWritable(path, name);
            if (DryRun) { Log(string.Format("DRYRUN: would delete [{0}] \"{1}\"", RegPath.Normal(path), name)); return false; }
            Raw.RemoveValue(path, name);
            Log(string.Format("DEL [{0}] \"{1}\"", RegPath.Normal(path), name));
            return true;
        }

        // =============================================================================================== CPU
        public CpuModel ReadCpuMap()
        {
            string source;
            List<LpRecord> recs = null;
            try { recs = CpuSource.GetRecords(out source); } catch (Exception ex) { Log("CPU topology: " + ex.Message); source = "UNKNOWN-topology"; }
            if (recs == null || recs.Count == 0)
            {
                recs = new List<LpRecord>();
                for (int i = 0; i < Environment.ProcessorCount; i++) recs.Add(new LpRecord { Id = 256 + i, Lp = i, Core = i });
                source = "UNKNOWN-topology";
            }
            string name = "Unknown CPU";
            try { name = CpuSource.GetName(); } catch { }
            return Topology.Build(string.IsNullOrWhiteSpace(name) ? "Unknown CPU" : System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ").Trim(), source, recs);
        }

        public RssCpuCheck CheckRssCpu() { return Topology.TestRssCpu(Cpu, RssGroup, RssCpu); }

        public void ShowCpuMap()
        {
            var cpu = Cpu;
            Say("");
            Say(" CPU     : " + cpu.Name);
            Say(string.Format(" Cores   : {0}   Logical: {1}   SMT: {2}   Hybrid: {3}   Groups: {4}   Source: {5}",
                cpu.Cores, cpu.Logical, cpu.Smt ? "yes" : "no", cpu.Hybrid ? "yes (P/E)" : "no", cpu.Groups, cpu.Source));
            if (!cpu.TopologyKnown) St(Status.Warn, "Core topology could not be read (irregular CPU or no CPU-set information): core map below is one LP per line.");
            var cells = Topology.MapCells(cpu);
            for (int i = 0; i < cells.Count; i += 4)
                Say((i == 0 ? " Map     : " : "           ") + string.Join(" | ", cells.Skip(i).Take(4)));
            foreach (int g in cpu.GroupIds)
            {
                var lps = cpu.GroupLps[g].Where(x => x <= 63);
                Say(string.Format("{0}: {1}", cpu.MultiGroup ? " All-LP mask (group " + g + ")" : " All-LP mask", Masks.Format(Masks.FromList(lps))));
            }
            var t = CheckRssCpu();
            if (t.Ok)
            {
                string m = RssCpu <= 63 ? Masks.Format(Masks.FromList(new[] { RssCpu })) : "-";
                Say(string.Format(" RSS CPU : {0}   mask {1}   profile {2}", t.Info, m, RssProfile));
                foreach (string w in t.Warn) St(Status.Warn, w);
            }
            else St(Status.Warn, string.Format("RSS CPU {0}: {1}", RssCpu, t.Error));
        }

        // [3] programmatic form: returns the check; on success the session target changes
        public RssCpuCheck SetRssTarget(int group, int lp, string profile)
        {
            var t = Topology.TestRssCpu(Cpu, group, lp);
            if (!t.Ok) return t;
            if (profile != "Closest" && profile != "ClosestStatic") { t.Ok = false; t.Error = "Unknown RSS profile: " + profile; return t; }
            RssGroup = group; RssCpu = lp; RssProfile = profile; LastVerify = null;
            St(Status.Pass, string.Format("RSS target: {0}, profile {1}", t.Info, profile));
            foreach (string w in t.Warn) St(Status.Warn, w);
            Say("   Registry profiles ([5]) and Apply RSS ([6]) now use this CPU. Run them again after a change.", Tone.Dim);
            return t;
        }

        // [3] interactive form (Invoke-ChooseCpu)
        public void ChooseCpu()
        {
            ShowCpuMap();
            Say("");
            Say(" CPU 0 for the network queue is the owner's choice (v7.0 matrix): CPU 0 also takes other system", Tone.Dim);
            Say(" interrupts and DPCs. Tier T2: verify with a WPR trace, not by feel.", Tone.Dim);
            int grp = RssGroup;
            if (Cpu.MultiGroup)
            {
                string g = Ask(string.Format(" Processor group ({0}), Enter = keep {1}", string.Join(",", Cpu.GroupIds), RssGroup), RssGroup.ToString(CultureInfo.InvariantCulture));
                if (!int.TryParse(g, out grp)) { St(Status.Warn, "Not a number - nothing changed."); return; }
            }
            string a = Ask(string.Format(" RSS CPU number (logical processor), Enter = keep {0}", RssCpu), RssCpu.ToString(CultureInfo.InvariantCulture));
            int n;
            if (!int.TryParse(a, out n)) { St(Status.Warn, "Not a number - nothing changed."); return; }
            var t = Topology.TestRssCpu(Cpu, grp, n);
            if (!t.Ok) { St(Status.Fail, t.Error + " Nothing changed."); return; }
            string p = AskChoice(" RSS profile (Enter = keep " + RssProfile + ")", RssProfile == "ClosestStatic" ? "2" : "1",
                new Choice("1", "Closest (dynamic, *RSSProfile 1)"), new Choice("2", "ClosestStatic (no load balancing, *RSSProfile 2)"));
            string prof;
            if (p == "1" || p.Equals("closest", StringComparison.OrdinalIgnoreCase)) prof = "Closest";
            else if (p == "2" || p.Equals("closeststatic", StringComparison.OrdinalIgnoreCase)) prof = "ClosestStatic";
            else { St(Status.Warn, "Unknown profile - nothing changed."); return; }
            SetRssTarget(grp, n, prof);
        }

        // =============================================================================================== adapters / target
        public int RefreshAdapters()
        {
            Adapters = new AdapterRecord[0];
            try { Adapters = Net.GetAdapters() ?? new AdapterRecord[0]; }
            catch (Exception ex) { St(Status.Fail, "Get-NetAdapter failed (NetAdapter module missing?): " + ex.Message); return 0; }
            return Adapters.Length;
        }

        public void SetTarget(AdapterRecord rec)
        {
            Ctx = ClassKeys.NewContext(rec, Raw, Roots, Net);
            QueuesRejected = false; LastVerify = null; RestartPending = false;
            Log(string.Format("Target: \"{0}\" [{1}] {2} ifIndex {3} class {4} ({5})", Ctx.Alias, Ctx.Media, Ctx.Description, Ctx.IfIndex, Ctx.ClassIndex, Ctx.ClassMode));
        }

        public void SetTargetKeepState(AdapterRecord rec)
        {
            bool q = QueuesRejected; var v = LastVerify; bool rp = RestartPending;
            Ctx = ClassKeys.NewContext(rec, Raw, Roots, Net);
            QueuesRejected = q; LastVerify = v; RestartPending = rp;
        }

        // re-reads the selected adapter (status, speed, ifIndex) after a restart
        public void UpdateTarget()
        {
            if (Ctx == null) return;
            string guid = Ctx.Guid;
            bool rp = RestartPending;
            if (RefreshAdapters() == 0) return;
            foreach (var r in Adapters) if (string.Equals(r.Guid, guid, StringComparison.OrdinalIgnoreCase)) { SetTargetKeepState(r); RestartPending = rp; return; }
        }

        public void InitializeSession()
        {
            Cpu = ReadCpuMap();
            if (!CheckRssCpu().Ok) { RssCpu = 0; RssGroup = 0; }
            if (RssProfile != "Closest" && RssProfile != "ClosestStatic") RssProfile = "Closest";
            Ctx = null;
            if (RefreshAdapters() > 0)
            {
                var rec = NetUtil.SelectDefault(Adapters);
                if (rec != null) SetTarget(rec);
            }
        }

        // [1]
        public void Detect()
        {
            Cpu = ReadCpuMap();
            ShowCpuMap();
            if (RefreshAdapters() == 0) { St(Status.Warn, "No physical network adapter found."); Ctx = null; return; }
            AdapterRecord rec = null;
            if (Ctx != null) rec = Adapters.FirstOrDefault(r => string.Equals(r.Guid, Ctx.Guid, StringComparison.OrdinalIgnoreCase));
            if (rec != null) SetTargetKeepState(rec);
            else { rec = NetUtil.SelectDefault(Adapters); if (rec != null) SetTarget(rec); }
            ShowAdapters();
            ShowTarget();
        }

        public bool TargetWritable(bool quiet)
        {
            if (Ctx == null) { if (!quiet) St(Status.Fail, "No adapter selected. Use [2]."); return false; }
            if (string.IsNullOrEmpty(Ctx.ClassPath))
            {
                if (!quiet) { St(Status.Fail, "Driver key not resolved: " + Ctx.ClassError); ShowClassCandidates(); }
                return false;
            }
            return true;
        }

        public void ShowClassCandidates()
        {
            if (Ctx == null || Ctx.Candidates.Count == 0) return;
            Say("   Candidate keys (nothing is written until exactly one matches):");
            foreach (var k in Ctx.Candidates) Say(string.Format("     {0}  NetCfgInstanceId={1}  DriverDesc=\"{2}\"", k.Index, k.NetCfgInstanceId, k.DriverDesc));
        }

        public void ShowTarget()
        {
            var c = Ctx;
            if (c == null) { St(Status.Warn, "No adapter selected."); return; }
            Say("");
            Say(string.Format(" Selected adapter: \"{0}\"  [{1}]  {2}  {3}", c.Alias, c.Media, c.Status, c.LinkSpeed), Tone.Title);
            Say("   Description : " + c.Description);
            Say(string.Format("   Driver      : {0} {1}", c.DriverProvider, c.DriverVersion));
            Say(string.Format("   ifIndex     : {0}   InterfaceGuid: {1}", c.IfIndex, c.Guid));
            Say("   PnP id      : " + c.PnpId);
            if (!string.IsNullOrEmpty(c.ClassPath))
            {
                Say(string.Format("   Driver key  : Class\\{0}  (matched by {1})", c.ClassIndex, c.ClassMode));
                Say("                 " + c.ClassPath, Tone.Dim);
                if (c.ClassWarn.Length > 0) St(Status.Warn, c.ClassWarn);
                if (c.EnumAgrees.HasValue)
                {
                    if (c.EnumAgrees.Value) Say(string.Format("   Cross-check : Enum\\..\\Driver = {0}  (agrees)", c.EnumDriver), Tone.Dim);
                    else St(Status.Warn, string.Format("Enum\\..\\Driver = {0} does not agree with Class\\{1}", c.EnumDriver, c.ClassIndex));
                }
                Say(string.Format("   Ndi\\Params  : {0} keyword(s) declared by the driver INF", c.NdiParams.Length));
            }
            else { St(Status.Fail, "Driver key not resolved: " + c.ClassError); ShowClassCandidates(); }
            if (c.RssSupported) Say("   RSS         : supported (Get-NetAdapterRss returns an object)");
            else St(Status.Warn, "Driver does not support RSS (Get-NetAdapterRss returns nothing). RSS cmdlets will be skipped.");
        }

        public void ShowAdapters()
        {
            Say("");
            Say(" Physical network adapters:");
            if (Adapters.Length == 0) { Say("   none found"); return; }
            var rows = new List<string[]>();
            int i = 1;
            foreach (var r in Adapters)
            {
                var rk = ClassKeys.Resolve(Raw, Roots.ClassRoot, r.Guid, r.Description);
                bool sel = Ctx != null && string.Equals(r.Guid, Ctx.Guid, StringComparison.OrdinalIgnoreCase);
                rows.Add(new[] { (sel ? "*" : " ") + i, r.Alias, r.Description, r.Status, r.LinkSpeed, NetUtil.MediaKind(r), rk.Ok ? rk.Index : "?",
                    Net.GetRss(r.Alias) != null ? "yes" : "no", r.DriverProvider + " " + r.DriverVersion });
                i++;
            }
            Table(new[] { "#", "Alias", "InterfaceDescription", "Status", "Speed", "Media", "Class", "RSS", "Driver" }, rows, new[] { 0, 22, 40, 12, 10, 6, 5, 3, 28 });
            i = 1;
            foreach (var r in Adapters) { Say(string.Format("   {0}: InterfaceGuid {1}   PnP {2}", i, r.Guid, r.PnpId), Tone.Dim); i++; }
            Say("   * = selected.  Media: NdisPhysicalMedium 14 (802.3) = LAN, 9 (Native 802.11) = Wi-Fi.", Tone.Dim);
        }

        // [2] programmatic form
        public void SelectAdapter(AdapterRecord rec) { SetTarget(rec); ShowTarget(); }

        // [2] interactive form (Invoke-SelectAdapter)
        public void InvokeSelectAdapter()
        {
            if (RefreshAdapters() == 0) { St(Status.Fail, "No physical network adapter found."); return; }
            ShowAdapters();
            string a = Ask(" Adapter number (A = auto: physical + Up, wired first; 0 = keep current)", "0");
            if (a == "0") return;
            AdapterRecord rec = null;
            int n;
            if (a.Equals("a", StringComparison.OrdinalIgnoreCase)) rec = NetUtil.SelectDefault(Adapters);
            else if (int.TryParse(a, out n) && n >= 1 && n <= Adapters.Length) rec = Adapters[n - 1];
            if (rec == null) { St(Status.Warn, "No such adapter."); return; }
            SelectAdapter(rec);
        }

        // =============================================================================================== RSS state
        public void ShowRssState(TargetContext c)
        {
            var r = Net.GetRss(c.Alias);
            Say("");
            if (r == null) { St(Status.Warn, string.Format("RSS of \"{0}\": not supported by the driver (Get-NetAdapterRss returns nothing).", c.Alias)); return; }
            Say(string.Format(" RSS state of \"{0}\" (Get-NetAdapterRss):", c.Alias));
            Say(string.Format("   Enabled: {0}   Profile: {1}   Base {2}:{3}   Max {4}:{5}   MaxProcessors: {6}   NumberOfReceiveQueues: {7}",
                r.Enabled ? "True" : "False", r.Profile, r.BaseGroup, r.BaseNumber, r.MaxGroup, r.MaxNumber, r.MaxProcessors, r.Queues));
            Say("   IndirectionTable : " + NetUtil.FormatRssTable(r.Table));
            if (r.ProcArray.Length > 0) Say("   RssProcessorArray: " + string.Join(" ", r.ProcArray));
        }

        // =============================================================================================== backup
        public string BackupRoot { get { return Path.Combine(BaseDir, "NetTune-backup"); } }

        // every registry value this tool can change: all profile values + the standardized RSS keywords that
        // Set-NetAdapterRss writes. MessageNumberLimit in the class key is never written (R7).
        public List<SnapItem> SnapshotScope(TargetContext c)
        {
            var items = new List<SnapItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(c.ClassPath))
            {
                var names = Profiles.Lan.Concat(Profiles.Wifi).Concat(Profiles.WifiBuffers).Where(e => e.Scope == Profiles.Class).Select(e => e.Name).Concat(Profiles.StdRssKeywords);
                foreach (string n in names)
                {
                    if (n.Equals("MessageNumberLimit", StringComparison.OrdinalIgnoreCase) || !seen.Add("c|" + n)) continue;
                    items.Add(new SnapItem(Profiles.Class, c.ClassPath, n));
                }
            }
            foreach (var e in Profiles.NdisParams)
                if (seen.Add("n|" + e.Name)) items.Add(new SnapItem(Profiles.Ndis, Roots.NdisKey, e.Name));
            return items;
        }

        private string NewBackupDir(string tag)
        {
            string alias = Ctx != null ? NetUtil.SafeName(Ctx.Alias) : "none";
            string name = Clock().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + alias;
            if (!string.IsNullOrEmpty(tag)) name += "_" + tag;
            string dir = Path.Combine(BackupRoot, name);
            int n = 2;
            while (Directory.Exists(dir)) { dir = Path.Combine(BackupRoot, name + "-" + n); n++; }
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string KeyOf(string guid, string classPath) { return ((guid ?? "") + "|" + RegPath.Normal(classPath ?? "")).ToLowerInvariant(); }
        public string CurrentBackupKey() { return Ctx == null ? "" : KeyOf(Ctx.Guid, Ctx.ClassPath); }

        public bool BackupValid()
        {
            return LastBackup.Length > 0 && File.Exists(Path.Combine(LastBackup, "snapshot.json"));
        }

        // [4] New-Backup: returns the folder, or "" in dry-run. Throws when the snapshot cannot be saved.
        public string NewBackup(string tag)
        {
            if (Ctx == null) { St(Status.Fail, "No adapter selected. Use [2]."); return ""; }
            if (DryRun) { St(Status.Info, "Dry-run: backup not written."); return ""; }
            var c = Ctx;
            var snap = Snapshot.New(Raw, SnapshotScope(c), tag, Version, Computer, Clock());
            snap.Root["adapter"] = new System.Text.Json.Nodes.JsonObject
            {
                ["alias"] = c.Alias, ["description"] = c.Description, ["guid"] = c.Guid, ["pnpId"] = c.PnpId, ["ifIndex"] = c.IfIndex,
                ["media"] = c.Media, ["classPath"] = c.ClassPath, ["classIndex"] = c.ClassIndex,
            };
            snap.Root["mtu"] = new System.Text.Json.Nodes.JsonObject { ["ipv4"] = Net.GetNlMtu(c.IfIndex, "IPv4"), ["ipv6"] = Net.GetNlMtu(c.IfIndex, "IPv6") };
            snap.Root["rss"] = new System.Text.Json.Nodes.JsonObject { ["cpu"] = RssCpu, ["group"] = RssGroup, ["profile"] = RssProfile };
            string dir = NewBackupDir(tag);
            string file = snap.Save(dir);
            if (!File.Exists(file)) throw new IOException("Snapshot was not saved: " + file);
            var utf8 = new UTF8Encoding(true);
            try
            {
                var cap = new CaptureUi();
                var keep = Ui; Ui = cap;
                try { ShowTarget(); ShowRssState(c); } finally { Ui = keep; }
                File.WriteAllText(Path.Combine(dir, "state.txt"), string.Join("\r\n", cap.Lines), utf8);
            }
            catch (Exception ex) { Log("state.txt not written: " + ex.Message); }
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("RegistryKeyword | RegistryValue | RegistryDataType | DisplayName | DisplayValue");
                foreach (var p in Net.GetAdvanced(c.Alias)) sb.AppendLine(string.Format("{0} | {1} | {2} | {3} | {4}", p.Keyword, p.Value, p.DataType, p.Display, p.DisplayValue));
                File.WriteAllText(Path.Combine(dir, "advanced-properties.txt"), sb.ToString(), utf8);
            }
            catch (Exception ex) { Log("advanced-properties.txt not written: " + ex.Message); }
            try
            {
                if (!string.IsNullOrEmpty(c.ClassPath)) Net.RegExport(c.ClassPath, Path.Combine(dir, "class-" + c.ClassIndex + ".reg"));
                Net.RegExport(Roots.NdisKey, Path.Combine(dir, "ndis-parameters.reg"));
            }
            catch (Exception ex) { Log("reg export failed: " + ex.Message); }
            LastBackup = dir;
            BackupKey = CurrentBackupKey();
            St(Status.Pass, "Backup saved: " + dir);
            if (string.IsNullOrEmpty(c.ClassPath)) St(Status.Warn, "The driver key is not resolved: this backup holds NDIS\\Parameters and the MTU only, no driver-key values.");
            return dir;
        }

        // R4: a backup of every value this tool can touch exists before the first write of this session for this
        // adapter and this driver key (its snapshot.json must still be there)
        public void ConfirmBackup(string tag)
        {
            if (DryRun) return;
            if (Ctx != null && BackupValid() && BackupKey == CurrentBackupKey()) return;
            string dir = NewBackup(tag);
            if (string.IsNullOrEmpty(dir)) throw new InvalidOperationException("No backup could be made: nothing is written.");
        }

        public List<string> BackupList()
        {
            if (!Directory.Exists(BackupRoot)) return new List<string>();
            return Directory.GetDirectories(BackupRoot).Where(d => File.Exists(Path.Combine(d, "snapshot.json")))
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).ToList();
        }

        // =============================================================================================== restore
        // [R] interactive form (Invoke-Restore)
        public void InvokeRestore()
        {
            var list = BackupList();
            if (list.Count == 0) { St(Status.Info, "No backup found (" + BackupRoot + ")."); return; }
            Say("");
            for (int i = 0; i < list.Count; i++) Say(string.Format("  {0,2} {1}", i + 1, Path.GetFileName(list[i])));
            string a = Ask(" Backup number (0 = cancel)", "0");
            int n;
            if (!int.TryParse(a, out n) || n < 1 || n > list.Count) return;
            RestoreFrom(list[n - 1]);
        }

        public RestorePlan PlanRestore(string dir)
        {
            var plan = new RestorePlan { Dir = dir, Name = Path.GetFileName(dir) };
            plan.Snap = Snapshot.Read(Path.Combine(dir, "snapshot.json"));
            var ad = plan.Snap.Obj("adapter");
            plan.Guid = ad == null ? "" : (Snapshot.Str(ad["guid"]) ?? "");
            plan.Alias = ad == null ? "" : (Snapshot.Str(ad["alias"]) ?? "");
            plan.SavedClassPath = ad == null ? "" : (Snapshot.Str(ad["classPath"]) ?? "");
            return plan;
        }

        public void RestoreFrom(string dir)
        {
            var plan = PlanRestore(dir);
            RefreshAdapters();
            var rec = Adapters.FirstOrDefault(r => string.Equals(r.Guid, plan.Guid, StringComparison.OrdinalIgnoreCase));
            if (rec == null) { St(Status.Fail, string.Format("The adapter of this backup is not present now: \"{0}\" {1}. Nothing restored.", plan.Alias, plan.Guid)); return; }
            if (Ctx == null || !string.Equals(Ctx.Guid, plan.Guid, StringComparison.OrdinalIgnoreCase))
            {
                St(Status.Info, string.Format("This backup belongs to \"{0}\", which is not the selected adapter.", rec.Alias));
                if (!Confirm(" Select that adapter and continue?", false)) return;
                SetTarget(rec);
            }
            var c = Ctx;
            string classNow = "";
            if (TargetWritable(true)) classNow = c.ClassPath;
            else St(Status.Warn, string.Format("Driver key not resolved ({0}): the driver-key values of this backup are skipped; NDIS\\Parameters and the MTU can still be restored.", c.ClassError));
            if (classNow.Length > 0 && plan.SavedClassPath.Length > 0 && !RegPath.Same(plan.SavedClassPath, classNow))
                St(Status.Warn, string.Format("Driver key moved since the backup: was {0}, now {1}. Restoring into the current key.", Roots.ScopeLabel(plan.SavedClassPath), Roots.ScopeLabel(classNow)));
            var acts = plan.Snap.GetRestoreActions(Raw, Roots, classNow);
            var mtuActs = new List<int[]>();                 // family index, now, will
            var mtu = plan.Snap.Obj("mtu");
            string[] fams = { "IPv4", "IPv6" };
            for (int fi = 0; fi < 2; fi++)
            {
                int old = mtu == null ? 0 : Snapshot.Int(mtu[fams[fi].ToLowerInvariant()], 0);
                int cur = Net.GetNlMtu(c.IfIndex, fams[fi]);
                if (old > 0 && cur > 0 && old != cur) mtuActs.Add(new[] { fi, cur, old });
            }
            if (acts.Count == 0 && mtuActs.Count == 0) { St(Status.Pass, "Registry and MTU already equal this backup - nothing to restore."); return; }
            var rows = acts.Select(x => new[] { Roots.ScopeLabel(x.Path), x.Name, x.Now, x.Will }).ToList();
            foreach (var m in mtuActs) rows.Add(new[] { "MTU " + fams[m[0]], "NlMtu", m[1].ToString(CultureInfo.InvariantCulture), m[2].ToString(CultureInfo.InvariantCulture) });
            Table(new[] { "Where", "Value", "Now", "After restore" }, rows, new[] { 16, 24, 28, 28 });
            if (!Confirm(string.Format(" Restore {0} item(s) from \"{1}\"?", acts.Count + mtuActs.Count, plan.Name), false)) return;
            if (DryRun) { St(Status.Info, "Dry-run: nothing restored."); return; }
            NewBackup("pre-restore");
            int done = ApplyRestore(acts);
            foreach (var m in mtuActs)
            {
                var r = Net.SetMtu(c.Alias, m[2], fams[m[0]]);
                Log(string.Format("netsh interface {0} set subinterface \"{1}\" mtu={2} store=persistent -> {3} {4}", fams[m[0]].ToLowerInvariant(), c.Alias, m[2], r.Code, r.Text));
                if (r.Code == 0) { done++; St(Status.Pass, string.Format("MTU {0}: {1} -> {2}", fams[m[0]], m[1], m[2])); }
                else St(Status.Fail, string.Format("MTU {0} not restored: netsh exit code {1}. {2}", fams[m[0]], r.Code, r.Text));
            }
            St(Status.Pass, string.Format("{0} change(s) restored.", done));
            if (acts.Count > 0) Say("   Driver-key values apply after an adapter restart ([7]); NDIS\\Parameters values after a reboot.", Tone.Dim);
            if (acts.Any(x => Roots.IsClassInstance(x.Path))) { RestartPending = true; RestartTarget(true); }
        }

        // Restore-Snapshot: returns the number of changes made
        public int ApplyRestore(List<RestoreAction> acts)
        {
            int n = 0;
            foreach (var a in acts)
            {
                if (a.Op == "set") { if (RegSet(a.Path, a.Name, a.Value)) n++; }
                else if (RegRemove(a.Path, a.Name)) n++;
            }
            return n;
        }

        // =============================================================================================== profiles
        // Get-ProfilePlan: rows with Action 'write' | 'already set' | 'skip' | 'type differs'. A value that already holds
        // the same data in another type is "already set" (R18); the type question (R6) only when the data must change.
        public List<PlanRow> GetProfilePlan(IEnumerable<ProfileEntry> entries, TargetContext c)
        {
            var rows = new List<PlanRow>();
            foreach (var e in entries)
            {
                string path = e.Scope == Profiles.Ndis ? Roots.NdisKey : c.ClassPath;
                string data = Profiles.Resolve(e.Data, RssCpu, RssProfile);
                string cls = Profiles.Classify(e.Scope, e.Name, c.NdiParams);
                var now = Raw.GetValue(path, e.Name);
                var want = new RegValue(e.Kind, data);
                string action = "write", note = "";
                if (cls == "SKIP") { action = "skip"; note = "no effect in the class key"; }
                else if (RegValue.Equal(now, want)) action = "already set";
                else if (now != null && now.Kind != e.Kind)
                {
                    if (RegValue.SameData(now, want)) { action = "already set"; note = "same data, stored as " + RegKind.Name(now.Kind); }
                    else { action = "type differs"; note = "now " + RegKind.Name(now.Kind); }
                }
                if (cls == "UNKNOWN" && action != "skip") note = (note.Length > 0 ? note + "; " : "") + "driver may ignore";
                rows.Add(new PlanRow { Scope = e.Scope, Path = path, Name = e.Name, Kind = e.Kind, Data = data, Class = cls, Now = now, Action = action, Note = note });
            }
            return rows;
        }

        public void ShowPreview(string title, List<PlanRow> plan)
        {
            Say("");
            Say(string.Format(" Profile \"{0}\"  ->  RSS CPU {1}, profile {2}", title, RssCpu, RssProfile), Tone.Title);
            var rows = plan.Select(r => new[] { r.Name, RegValue.FormatPlan(r.Now), r.Kind == RegKind.String ? "\"" + r.Data + "\"" : r.Data,
                RegKind.Name(r.Kind), r.Class, r.Note.Length > 0 ? r.Action + " (" + r.Note + ")" : r.Action }).ToList();
            Table(new[] { "Name", "Now", "New", "Type", "Class", "Action" }, rows, new[] { 22, 26, 10, 10, 8, 48 });
            Say("   Class: T1-STD = standardized NDIS keyword | VENDOR = declared in Ndi\\Params of this driver |", Tone.Dim);
            Say("          UNKNOWN = neither (the driver may ignore it) | SKIP = never written by this tool", Tone.Dim);
        }

        public string MessageNumberLimitText(TargetContext c)
        {
            if (string.IsNullOrEmpty(c.PnpId)) return "n/a";
            try
            {
                var v = Raw.GetValue(Roots.EnumRoot + "\\" + c.PnpId + "\\Device Parameters\\Interrupt Management\\MessageSignaledInterruptProperties", "MessageNumberLimit");
                return v == null ? "(absent)" : v.Data;
            }
            catch { return "n/a"; }
        }

        // [5] Write-Profile: preview -> mode -> confirmations -> backup -> write. Returns the number of values written.
        // mode: "" = ask; "A" = as in the .reg; "S" = only T1-STD + VENDOR.
        public int WriteProfile(string which, string mode)
        {
            if (!TargetWritable(false)) return 0;
            var c = Ctx;
            ProfileEntry[] entries; string title, media;
            if (!Profiles.TryGet(which, out entries, out title, out media)) { Say(" Unknown profile."); return 0; }
            var t = CheckRssCpu();
            if (!t.Ok) { St(Status.Fail, t.Error + " Choose the RSS CPU in [3]."); return 0; }
            var plan = GetProfilePlan(entries, c);
            ShowPreview(title, plan);
            if (plan.Any(r => r.Class == "SKIP"))
            {
                St(Status.Skip, "MessageNumberLimit is not written: it has no effect in the class key. Its real place is");
                Say(string.Format("          Enum\\<InstanceId>\\Device Parameters\\Interrupt Management\\MessageSignaledInterruptProperties (now: {0}).", MessageNumberLimitText(c)));
                Say("          Change it with GoInterruptPolicy / grobyq.");
            }
            string w = which.ToUpperInvariant();
            if (w == "N") St(Status.Warn, "NDIS\\Parameters is GLOBAL (all adapters) and is read at boot: a reboot is needed. Class UNKNOWN: effect on Windows 10/11 is not confirmed.");
            if (w == "B")
                foreach (string k in Profiles.StdOtherKeywords)
                {
                    var v = Raw.GetValue(c.ClassPath, k);
                    if (v != null) Say(string.Format("   For comparison (read-only): {0} = {1}", k, RegValue.FormatPlan(v)), Tone.Dim);
                }
            if (media.Length > 0 && c.Media != media)
                St(Status.Warn, string.Format("Profile \"{0}\" is meant for a {1} adapter; the selected adapter is {2}.", title, media, c.Media));
            var todo = plan.Where(r => r.Action == "write" || r.Action == "type differs").ToList();
            if (todo.Count == 0) { St(Status.Pass, "Every value is already set - nothing to write."); return 0; }

            if (string.IsNullOrEmpty(mode))
                mode = AskChoice(" Write mode", "A", new Choice("A", "as in the .reg (default)"), new Choice("S", "only T1-STD + VENDOR"), new Choice("0", "cancel"));
            if (mode == "0") return 0;
            bool strict = mode.Equals("S", StringComparison.OrdinalIgnoreCase);
            if (!strict && !mode.Equals("A", StringComparison.OrdinalIgnoreCase)) { Say(" Unknown mode - cancelled."); return 0; }
            if (strict)
            {
                todo = todo.Where(r => r.Class != "UNKNOWN").ToList();
                if (todo.Count == 0) { St(Status.Info, "Mode [S]: every value left to write is class UNKNOWN - nothing written."); return 0; }
            }
            bool confirmed = false;
            var unknown = todo.Where(r => r.Class == "UNKNOWN").ToList();
            if (w == "B" && unknown.Count > 0)
            {
                // R9: buffer values the driver does not declare are written only on request
                if (Confirm(string.Format(" {0} buffer value(s) are not declared in Ndi\\Params (class UNKNOWN): the driver may ignore them. Write them anyway?", unknown.Count), false))
                { if (unknown.Count == todo.Count) confirmed = true; }
                else todo = todo.Where(r => r.Class != "UNKNOWN").ToList();
            }
            // R6: a value that exists with another type and other data
            var final = new List<PlanRow>();
            foreach (var r in todo)
            {
                if (r.Action != "type differs") { final.Add(r); continue; }
                var keep = RegValue.KeptKind(r.Now, r.Data);
                St(Status.Warn, string.Format("\"{0}\" exists as {1}, the profile has {2}.", r.Name, RegValue.FormatPlan(r.Now), RegValue.FormatPlan(new RegValue(r.Kind, r.Data))));
                var opts = new List<Choice>();
                if (keep != null) opts.Add(new Choice("K", "keep the existing type"));
                opts.Add(new Choice("M", "write mine"));
                opts.Add(new Choice("S", "skip (default)"));
                string ch = AskChoice("   \"" + r.Name + "\"", "S", opts.ToArray());
                if (ch.Equals("M", StringComparison.OrdinalIgnoreCase)) final.Add(r);
                else if (ch.Equals("K", StringComparison.OrdinalIgnoreCase) && keep != null) { r.Kind = keep.Kind; r.Data = keep.Data; final.Add(r); }
            }
            if (final.Count == 0) { St(Status.Info, "Nothing left to write."); return 0; }
            if (!confirmed)
            {
                string q = string.Format(" Write {0} value(s) to {1}?", final.Count, Roots.ScopeLabel(final[0].Path));
                if ((w == "L" || w == "W") && !c.RssSupported)
                    q = string.Format(" Driver does not support RSS: it may ignore this whole profile. Write {0} value(s) to {1} anyway?", final.Count, Roots.ScopeLabel(final[0].Path));   // R8
                if (!Confirm(q, false)) return 0;
            }
            ConfirmBackup("pre-profile");
            int n = 0;
            foreach (var r in final) if (RegSet(r.Path, r.Name, r.Kind, r.Data)) n++;
            if (DryRun) { St(Status.Info, string.Format("Dry-run: {0} value(s) would be written. Registry untouched.", final.Count)); return 0; }
            if (w == "N") St(Status.Pass, string.Format("{0} value(s) written. They are read at boot: reboot to apply.", n));
            else
            {
                if (n > 0) RestartPending = true;
                St(Status.Pass, string.Format("{0} value(s) written. They take effect after an adapter restart ([7]).", n));
            }
            return n;
        }

        // =============================================================================================== [6] Apply RSS
        public bool RssMatches(RssRecord r)
        {
            if (r == null || !r.Enabled) return false;
            string cpu = RssCpu.ToString(CultureInfo.InvariantCulture), grp = RssGroup.ToString(CultureInfo.InvariantCulture);
            if (r.Profile != RssProfile) return false;
            if (r.BaseNumber != cpu || r.MaxNumber != cpu) return false;
            if (Cpu.MultiGroup && (r.BaseGroup != grp || r.MaxGroup != grp)) return false;
            if (r.MaxProcessors != "1") return false;
            if (r.Queues != "1" && !QueuesRejected) return false;
            return true;
        }

        public string RssCommandText(TargetContext c, bool withQueues)
        {
            string grp = Cpu.MultiGroup ? string.Format(" -BaseProcessorGroup {0} -MaxProcessorGroup {0}", RssGroup) : "";
            string q = withQueues ? " -NumberOfReceiveQueues 1" : "";
            return string.Format("Set-NetAdapterRss -Name \"{0}\"{1} -BaseProcessorNumber {2} -MaxProcessorNumber {2} -MaxProcessors 1{3} -Profile {4} -NoRestart", c.Alias, grp, RssCpu, q, RssProfile);
        }

        // Enable-NetAdapterRss + Set-NetAdapterRss (-NoRestart). True when RSS is set (or already was).
        public bool ApplyRss()
        {
            if (!TargetWritable(false)) return false;
            var c = Ctx;
            var t = CheckRssCpu();
            if (!t.Ok) { St(Status.Fail, t.Error + " Choose the RSS CPU in [3]."); return false; }
            var cur = Net.GetRss(c.Alias);
            if (cur == null)
            {
                c.RssSupported = false;   // R8
                St(Status.Warn, string.Format("Driver does not support RSS (\"{0}\": Get-NetAdapterRss returns nothing). RSS cmdlets skipped.", c.Alias));
                if (c.Media != "Wi-Fi") Say("   If a registry profile was just imported ([5]), restart the adapter ([7]) and run [6] again: some drivers expose RSS only after that.", Tone.Dim);
                return false;
            }
            string want = RssGroup + ":" + RssCpu;
            var rows = new List<string[]>
            {
                new[] { "Enabled", cur.Enabled ? "True" : "False", "True" },
                new[] { "Profile", cur.Profile, RssProfile },
                new[] { "BaseProcessor (group:CPU)", cur.BaseGroup + ":" + cur.BaseNumber, want },
                new[] { "MaxProcessor (group:CPU)", cur.MaxGroup + ":" + cur.MaxNumber, want },
                new[] { "MaxProcessors", cur.MaxProcessors, "1" },
                new[] { "NumberOfReceiveQueues", cur.Queues, "1" },
            };
            Say("");
            Say(string.Format(" RSS of \"{0}\" -> one queue on {1}", c.Alias, t.Info), Tone.Title);
            Table(new[] { "Parameter", "Now", "New" }, rows, null);
            if (RssMatches(cur)) { St(Status.Pass, "RSS is already set - nothing to change."); return true; }
            foreach (string w in t.Warn) St(Status.Warn, w);
            Say(string.Format("   Enable-NetAdapterRss -Name \"{0}\" -NoRestart", c.Alias), Tone.Dim);
            Say("   " + RssCommandText(c, true), Tone.Dim);
            if (!Confirm(" Apply these RSS settings?", false)) return false;
            ConfirmBackup("pre-rss");
            if (DryRun) { St(Status.Info, "Dry-run: the two cmdlets above would run. Nothing changed."); return false; }
            string err = Net.EnableRss(c.Alias);
            Log("Enable-NetAdapterRss \"" + c.Alias + "\" -> " + err);
            if (err.Length > 0) { St(Status.Fail, "Enable-NetAdapterRss failed: RSS cannot be enabled on this driver. RSS cmdlets skipped. " + err); return false; }
            var p = new RssParams { BaseProcessorNumber = RssCpu, MaxProcessorNumber = RssCpu, MaxProcessors = 1, NumberOfReceiveQueues = 1, Profile = RssProfile };
            if (Cpu.MultiGroup) { p.BaseProcessorGroup = RssGroup; p.MaxProcessorGroup = RssGroup; }
            err = Net.SetRss(c.Alias, p);
            Log(RssCommandText(c, true) + " -> " + err);
            if (err.Length > 0)
            {
                // R11: retry without -NumberOfReceiveQueues
                string first = err;
                p.NumberOfReceiveQueues = null;
                err = Net.SetRss(c.Alias, p);
                Log(RssCommandText(c, false) + " -> " + err);
                if (err.Length > 0)
                {
                    St(Status.Fail, "Set-NetAdapterRss failed: " + first);
                    if (err != first) St(Status.Fail, "Retry without -NumberOfReceiveQueues failed: " + err);
                    Say("   Enable-NetAdapterRss has already run (*RSS = 1). [R] puts the previous state back.", Tone.Dim);
                    RestartPending = true;
                    return false;
                }
                QueuesRejected = true;
                St(Status.Warn, string.Format("The driver rejected -NumberOfReceiveQueues 1 ({0}). Applied without it.", first));
            }
            else QueuesRejected = false;
            RestartPending = true;
            St(Status.Pass, "RSS settings written (-NoRestart). Restart the adapter ([7]) to apply them.");
            ShowRssState(c);
            return true;
        }

        // =============================================================================================== [7] restart
        // offer = called from another step ("restart now?"). True when the adapter was restarted.
        public bool RestartTarget(bool offer)
        {
            if (Ctx == null) { St(Status.Fail, "No adapter selected. Use [2]."); return false; }
            string alias = Ctx.Alias;
            Say("");
            St(Status.Warn, string.Format("Restarting \"{0}\": the network drops for a few seconds. Without a restart, changes of the driver key do not apply.", alias));
            if (!Confirm(" Restart the adapter now?", false)) { if (offer) Say("   Not restarted: use [7] later.", Tone.Dim); return false; }
            if (DryRun) { St(Status.Info, string.Format("Dry-run: would run Restart-NetAdapter -Name \"{0}\".", alias)); return false; }
            string err = Net.RestartAdapter(alias);
            Log("Restart-NetAdapter \"" + alias + "\" -> " + err);
            if (err.Length > 0) { St(Status.Fail, "Restart-NetAdapter failed: " + err); return false; }
            RestartPending = false;
            bool up = false;
            for (int i = 0; i < 60; i++) { Sleep(500); if (Net.GetAdapterStatus(alias) == "Up") { up = true; break; } }
            UpdateTarget();
            RestartPending = false;
            if (!up) { St(Status.Warn, "Adapter restarted, but the link is not Up after 30 s (cable / Wi-Fi reconnect?)."); return true; }
            string gw = "";
            for (int i = 0; i < 30; i++) { gw = Net.GetGateway(Ctx.IfIndex); if (gw.Length > 0) break; Sleep(500); }
            if (gw.Length > 0) St(Status.Pass, string.Format("Adapter restarted: link Up {0}, gateway {1}.", Ctx.LinkSpeed, gw));
            else St(Status.Warn, string.Format("Adapter restarted: link Up {0}, but no IPv4 default gateway yet (DHCP still running?).", Ctx.LinkSpeed));
            return true;
        }

        // =============================================================================================== [8] verify
        public VerifyResult Verify()
        {
            var res = new VerifyResult();
            Action<string, string> emit = (st, text) => { res.Add(st, text); St(st, text); };
            if (!TargetWritable(false)) { res.Fail = 1; return res; }
            var c = Ctx;
            string cpu = RssCpu.ToString(CultureInfo.InvariantCulture);
            string want = RssGroup + ":" + RssCpu;
            string profId = Profiles.ProfileId(RssProfile);
            Say("");
            Say(string.Format(" Verify \"{0}\" [{1}]: RSS -> CPU {2} (group {3}), 1 queue, 1 processor, profile {4}", c.Alias, c.Media, cpu, RssGroup, RssProfile), Tone.Title);

            // ---- A. Get-NetAdapterRss ----
            Say(" -- A. Get-NetAdapterRss");
            var r = Net.GetRss(c.Alias);
            bool rssOk = r != null;
            // the indirection table is what the driver really uses; a queue count the driver refuses to change is
            // harmless once every table entry sits on the target CPU
            bool tableOk = false;
            if (!rssOk) emit(Status.Warn, "RSS  Driver does not support RSS (no RSS object): cmdlet checks skipped; registry values below may be ignored.");
            else
            {
                var table = r.Table.Where(x => x != null).ToList();
                var other = table.Where(x => x != want).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                tableOk = table.Count > 0 && other.Count == 0;
                if (r.Enabled) emit(Status.Pass, "RSS  Enabled = True"); else emit(Status.Fail, "RSS  Enabled = False (expected True)");
                if (r.Profile == RssProfile) emit(Status.Pass, "RSS  Profile = " + r.Profile);
                else emit(Status.Fail, string.Format("RSS  Profile = {0} (expected {1})", r.Profile, RssProfile));
                string bse = r.BaseGroup + ":" + r.BaseNumber, max = r.MaxGroup + ":" + r.MaxNumber;
                string grp = RssGroup.ToString(CultureInfo.InvariantCulture);
                if (r.BaseNumber == cpu && (!Cpu.MultiGroup || r.BaseGroup == grp)) emit(Status.Pass, "RSS  BaseProcessor = " + bse);
                else emit(Status.Fail, string.Format("RSS  BaseProcessor = {0} (expected {1})", bse, want));
                if (r.MaxNumber == cpu && (!Cpu.MultiGroup || r.MaxGroup == grp)) emit(Status.Pass, "RSS  MaxProcessor = " + max);
                else emit(Status.Fail, string.Format("RSS  MaxProcessor = {0} (expected {1})", max, want));
                if (r.MaxProcessors == "1") emit(Status.Pass, "RSS  MaxProcessors = 1");
                else emit(Status.Fail, string.Format("RSS  MaxProcessors = {0} (expected 1)", r.MaxProcessors));
                if (r.Queues == "1") emit(Status.Pass, "RSS  NumberOfReceiveQueues = 1");
                else if (tableOk) emit(Status.Warn, string.Format("RSS  NumberOfReceiveQueues = {0} (expected 1): the driver keeps its own queue count; every table entry is on {1}.", r.Queues, want));
                else emit(Status.Fail, string.Format("RSS  NumberOfReceiveQueues = {0} (expected 1)", r.Queues));
                if (table.Count == 0) emit(Status.Warn, "RSS  IndirectionTable is empty (link down, RSS off, or adapter not restarted yet).");
                else if (tableOk) emit(Status.Pass, string.Format("RSS  IndirectionTable: all {0} entries on {1}", table.Count, want));
                else emit(Status.Fail, string.Format("RSS  IndirectionTable also uses {0} (expected only {1}). Restart the adapter ([7]) if RSS was just applied.", string.Join(" ", other), want));
            }

            // ---- B. standardized keywords as the driver key reports them ----
            Say(" -- B. Get-NetAdapterAdvancedProperty (exact keyword match)");
            if (!rssOk) St(Status.Skip, "ADV  RSS keywords: driver has no RSS support.");
            else
            {
                var adv = Net.GetAdvanced(c.Alias);
                var expect = new[] { new[] { "*RSS", "1" }, new[] { "*RSSProfile", profId }, new[] { "*NumRssQueues", "1" },
                    new[] { "*RssBaseProcNumber", cpu }, new[] { "*MaxRssProcessors", "1" }, new[] { "*RssMaxProcNumber", cpu } };
                foreach (var e in expect)
                {
                    var hit = adv.FirstOrDefault(x => string.Equals(x.Keyword, e[0], StringComparison.OrdinalIgnoreCase));
                    bool isQueues = e[0].Equals("*NumRssQueues", StringComparison.OrdinalIgnoreCase);
                    if (hit == null) emit(Status.Warn, string.Format("ADV  {0}: keyword not present in the driver key (expected {1}).", e[0], e[1]));
                    else if (hit.Value == e[1]) emit(Status.Pass, string.Format("ADV  {0} = {1}", e[0], hit.Value));
                    else if (isQueues && tableOk) emit(Status.Warn, string.Format("ADV  {0} = {1} (expected {2}): the driver keeps its own queue count.", e[0], hit.Value, e[1]));
                    else emit(Status.Fail, string.Format("ADV  {0} = {1} (expected {2})", e[0], hit.Value, e[1]));
                }
            }

            // ---- C. registry profile ----
            Say(" -- C. Registry profile (my .reg values, RSS CPU substituted)");
            var sets = new List<Tuple<ProfileEntry[], bool>>();
            if (c.Media == "Wi-Fi") { sets.Add(Tuple.Create(Profiles.Wifi, true)); sets.Add(Tuple.Create(Profiles.WifiBuffers, false)); }
            else sets.Add(Tuple.Create(Profiles.Lan, true));
            sets.Add(Tuple.Create(Profiles.NdisParams, false));
            foreach (var s in sets)
                foreach (var row in GetProfilePlan(s.Item1, c))
                {
                    string label = string.Format("REG  {0} \"{1}\"", Roots.ScopeLabel(row.Path), row.Name);
                    if (row.Class == "SKIP") { St(Status.Skip, label + ": not written by this tool (R7); real place shows " + MessageNumberLimitText(c) + "."); continue; }
                    // soft = the driver is not known to read this value: it can only be WARN, never PASS / FAIL
                    bool soft = row.Class == "UNKNOWN" || (s.Item2 && !rssOk);
                    if (row.Action == "already set")
                    {
                        if (soft) emit(Status.Warn, label + " = " + RegValue.FormatPlan(row.Now) + ": set, but the driver may not read it (class " + row.Class + ").");
                        else emit(Status.Pass, label + " = " + RegValue.FormatPlan(row.Now));
                    }
                    else
                    {
                        string exp = RegValue.FormatPlan(new RegValue(row.Kind, row.Data));
                        if (soft) emit(Status.Warn, label + " = " + RegValue.FormatPlan(row.Now) + ": not as in the profile (" + exp + "); class " + row.Class + ", the driver may not read it anyway.");
                        else if (row.Name.Equals("*NumRssQueues", StringComparison.OrdinalIgnoreCase) && tableOk) emit(Status.Warn, label + " = " + RegValue.FormatPlan(row.Now) + " (expected " + exp + "): the driver keeps its own queue count.");
                        else emit(Status.Fail, label + " = " + RegValue.FormatPlan(row.Now) + " (expected " + exp + ").");
                    }
                }
            if (RestartPending)
                emit(Status.Warn, "Driver-key values or RSS settings were changed in this session after the last adapter restart: the lines above show what is configured, not what is in effect. Restart the adapter ([7]).");
            Say("");
            Say(string.Format(" Verify result: {0} PASS, {1} WARN, {2} FAIL", res.Pass, res.Warn, res.Fail), res.Fail > 0 ? Tone.Bad : res.Warn > 0 ? Tone.Warn : Tone.Good);
            Say(string.Format(" Manual check: Get-NetAdapterRss -Name \"{0}\" | Format-List Name, Enabled, Profile, BaseProcessorNumber,", c.Alias), Tone.Dim);
            Say("               MaxProcessorNumber, MaxProcessors, NumberOfReceiveQueues, IndirectionTable", Tone.Dim);
            Say(" Final proof of where network DPC/ISR run is a WPR/WPA trace, not this table and not ping.", Tone.Dim);
            res.Time = Clock();
            LastVerify = res;
            return res;
        }

        // =============================================================================================== [A] run all (core steps)
        // 4 -> 5 -> 6 -> 7 -> 8. Every writing step shows its own preview and asks Y/N.
        public void RunAll()
        {
            if (!TargetWritable(false)) return;
            var c = Ctx;
            Say("");
            Say(string.Format(" Run all on \"{0}\" [{1}] -> RSS CPU {2}, profile {3}", c.Alias, c.Media, RssCpu, RssProfile), Tone.Title);
            Say(" Steps: [4] backup -> [5] registry profile -> [6] Apply RSS -> [7] restart -> [8] verify");
            if (Confirm(" Step [4] Backup current state?", true)) NewBackup("manual");
            Say("");
            Say(" Step [5] Import registry profile", Tone.Title);
            string mode = AskChoice(" Write mode for this run", "A", new Choice("A", "as in the .reg (default)"), new Choice("S", "only T1-STD + VENDOR"), new Choice("0", "skip step 5"));
            if (mode.Equals("A", StringComparison.OrdinalIgnoreCase) || mode.Equals("S", StringComparison.OrdinalIgnoreCase))
            {
                if (c.Media == "Wi-Fi") { WriteProfile("W", mode); WriteProfile("B", mode); }
                else WriteProfile("L", mode);
                WriteProfile("N", mode);
            }
            else if (mode != "0") Say(" Unknown mode - step 5 skipped.");
            Say("");
            Say(" Step [6] Apply RSS", Tone.Title);
            bool hadRss = Net.GetRss(c.Alias) != null;
            ApplyRss();
            Say("");
            Say(" Step [7] Restart adapter", Tone.Title);
            bool restarted = false;
            if (RestartPending || DryRun) restarted = RestartTarget(false);
            else St(Status.Info, "Nothing was changed in the driver key or in RSS since the last restart: restart skipped ([7] by hand if needed).");
            if (restarted && !hadRss && Ctx != null && Net.GetRss(Ctx.Alias) != null)
            {
                // drivers that expose the RSS object only after the registry profile + restart
                St(Status.Info, "The RSS object is available after the restart: step [6] again.");
                if (ApplyRss()) RestartTarget(false);
            }
            Say("");
            Say(" Step [8] Verify", Tone.Title);
            Verify();
        }
    }

    public sealed class PlanRow
    {
        public string Scope, Path, Name, Kind, Data, Class, Action, Note;
        public RegValue Now;
    }

    public sealed class RestorePlan
    {
        public string Dir, Name, Guid, Alias, SavedClassPath;
        public Snapshot Snap;
    }

    public sealed class VerifyResult
    {
        public int Pass, Warn, Fail;
        public DateTime Time;
        public readonly List<string[]> Lines = new List<string[]>();     // status, text

        public void Add(string status, string text)
        {
            Lines.Add(new[] { status, text });
            if (status == Status.Pass) Pass++;
            else if (status == Status.Warn) Warn++;
            else if (status == Status.Fail) Fail++;
        }
    }
}
