using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace GrobPC.NetTune
{
    // ================================================================================================================
    //  Self-test: sections A..E (+ [R] of G) of the PowerShell tool, ported check by check with the same names and the
    //  same expectations. Collectors are stubbed: no real adapter, RSS setting or MTU is touched. The registry is the
    //  one passed in (MockRegistry on Linux / in CI; the real registry under HKCU\Software\NetTuneTest on Windows).
    // ================================================================================================================
    public sealed class SelfTest
    {
        public int Pass, Fail;
        private readonly IUi _out;
        private readonly IRegistry _reg;
        private readonly Action _removeScratch;
        private readonly string _backend;

        private Engine E;
        private CaptureUi Cap;
        private StubNet Stub;
        private RegRoots R;
        private string Quiet = "";

        public SelfTest(IRegistry reg, string backend, Action removeScratch, IUi output)
        {
            _reg = reg; _backend = backend; _removeScratch = removeScratch; _out = output;
        }

        private void Say(string text, Tone tone = Tone.Normal) { _out.Say(text, tone); }

        private void Check(string name, bool cond, string detail = "")
        {
            if (cond) { Pass++; _out.Say(Status.Line(Status.Pass, name), Tone.Good); }
            else { Fail++; _out.Say(Status.Line(Status.Fail, name + "   " + detail), Tone.Bad); }
        }

        // Invoke-Quiet: runs the action with the engine output captured into Quiet
        private T Q<T>(Func<T> f) { Cap.Clear(); try { return f(); } finally { Quiet = Cap.Text; } }
        private void Q(Action a) { Cap.Clear(); try { a(); } finally { Quiet = Cap.Text; } }
        private void Answers(params string[] a) { Cap.SetAnswers(a); }

        private bool Throws(Action a) { try { a(); return false; } catch { return true; } }

        private void Set(string path, string name, string kind, string data) { E.RegSet(path, name, kind, data); }
        private RegValue Get(string path, string name) { return _reg.GetValue(path, name); }
        private string Fmt(string path, string name) { return RegValue.Format(_reg.GetValue(path, name)); }

        private string KeyDump(string path)
        {
            return string.Join("; ", _reg.GetValueNames(path).Select(n => n + "=" + RegValue.Format(_reg.GetValue(path, n))).OrderBy(x => x, StringComparer.Ordinal));
        }

        // ------------------------------------------------------------------------------------------------ run
        public int Run()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "nettune-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            Say("");
            Say(string.Format(" grobPC NetTune v{0} self-test   registry backend: {1}", Engine.Version, _backend), Tone.Title);
            Say(" Collectors are stubbed: no real adapter, RSS setting, MTU or route is touched; no packet leaves the PC.", Tone.Dim);
            try
            {
                R = RegRoots.Scratch();
                Stub = new StubNet();
                Cap = new CaptureUi();
                E = new Engine(_reg, R, Stub, new FixedCpu("SelfTest CPU", Topology.Synthetic(6, 2, 0, 0), "synthetic"), Cap, tmp);
                E.Sleep = ms => { };
                E.Computer = "SELFTEST";
                Stub.BackupExists = () => E.BackupValid();
                TestCpu();
                var w = NewWorld();
                TestClassKey(w);
                TestRegistry(w);
                TestProfile(w);
                TestRss(w);
                TestRestoreFlow(w);
                TestRunAll(w);
            }
            catch (Exception ex)
            {
                Fail++;
                Say(Status.Line(Status.Fail, "Self-test aborted: " + ex.Message), Tone.Bad);
                Say(ex.StackTrace ?? "", Tone.Dim);
            }
            finally
            {
                try { if (_removeScratch != null) _removeScratch(); } catch (Exception ex) { Say("   scratch cleanup: " + ex.Message, Tone.Dim); }
                try { Directory.Delete(tmp, true); } catch { }
            }
            Say("");
            Say(string.Format(" Self-test: {0} passed, {1} failed", Pass, Fail), Fail > 0 ? Tone.Bad : Tone.Good);
            return Fail;
        }

        // ------------------------------------------------------------------------------------------------ world
        public sealed class World
        {
            public string GuidLan = "{11111111-AAAA-BBBB-CCCC-000000000001}", GuidWifi = "{22222222-AAAA-BBBB-CCCC-000000000009}";
            public string GuidTwin = "{33333333-AAAA-BBBB-CCCC-000000000011}", GuidNone = "{99999999-0000-0000-0000-000000000000}";
            public string DescLan = "Realtek Gaming 2.5GbE Family Controller", DescWifi = "MediaTek Wi-Fi 6E RZ616 160MHz Wireless LAN Card";
            public string AliasLan = "NetTune-SelfTest-LAN", AliasWifi = "NetTune-SelfTest-WiFi";
            public string PnpLan = @"PCI\VEN_10EC&DEV_8125&SUBSYS_87D71043&REV_05\01000000684CE00000";
            public string PnpWifi = @"PCI\VEN_14C3&DEV_0616&SUBSYS_E0C614C3&REV_00\4&2F1A3B4C&0&0020";
            public string ClassGuid = RegRoots.NetClassGuid;
            public string LanKey, WifiKey;
            public AdapterRecord RecLan, RecWifi;
        }

        // New-TestWorld: scratch Class\0000..0012, NDIS\Parameters, Enum, two stub adapters
        private World NewWorld()
        {
            if (_removeScratch != null) _removeScratch();
            var w = new World();
            string root = R.ClassRoot;
            for (int i = 0; i <= 12; i++)
            {
                string k = string.Format(CultureInfo.InvariantCulture, "{0}\\{1:0000}", root, i);
                Set(k, "DriverDesc", RegKind.String, "Filler adapter " + i);
                Set(k, "NetCfgInstanceId", RegKind.String, "{" + i.ToString("00000000", CultureInfo.InvariantCulture) + "-1111-2222-3333-444444444444}");
            }
            w.LanKey = root + "\\0001";
            Set(w.LanKey, "DriverDesc", RegKind.String, w.DescLan);
            Set(w.LanKey, "NetCfgInstanceId", RegKind.String, w.GuidLan);
            foreach (string kw in new[] { "*RSS", "*RSSProfile", "*NumRssQueues", "*RssBaseProcNumber", "*MaxRssProcessors", "*ReceiveBuffers", "*TransmitBuffers", "*FlowControl" })
                Set(w.LanKey + "\\Ndi\\Params\\" + kw, "ParamDesc", RegKind.String, kw);
            w.WifiKey = root + "\\0009";
            Set(w.WifiKey, "DriverDesc", RegKind.String, w.DescWifi);
            Set(w.WifiKey, "NetCfgInstanceId", RegKind.String, w.GuidWifi);
            Set(w.WifiKey + "\\Ndi\\Params\\BufferDescriptorsRx", "ParamDesc", RegKind.String, "Rx buffers");
            foreach (int i in new[] { 6, 10 }) Set(string.Format(CultureInfo.InvariantCulture, "{0}\\{1:0000}", root, i), "DriverDesc", RegKind.String, "Dup Adapter");
            Set(root + "\\0011", "DriverDesc", RegKind.String, "Twin GUID adapter A");
            Set(root + "\\0012", "DriverDesc", RegKind.String, "Twin GUID adapter B");
            foreach (int i in new[] { 11, 12 }) Set(string.Format(CultureInfo.InvariantCulture, "{0}\\{1:0000}", root, i), "NetCfgInstanceId", RegKind.String, w.GuidTwin);
            Set(root + "\\0008", "DriverDesc", RegKind.String, "No GUID adapter");
            Set(root + "\\0008", "NetCfgInstanceId", RegKind.String, "");
            Set(root + "\\Properties", "x", RegKind.String, "not an adapter key");
            // values that exist before the tool runs
            Set(w.LanKey, "*RSS", RegKind.String, "0");
            Set(w.LanKey, "*NumRssQueues", RegKind.String, "4");
            Set(w.LanKey, "MaxRssProcessors", RegKind.DWord, "8");
            Set(w.LanKey, "RssCapabilities", RegKind.String, "0");
            Set(R.NdisKey, "RssBaseCpu", RegKind.DWord, "4");
            Set(R.NdisKey, "MaxNumRssCpus", RegKind.DWord, "4");
            string en = R.EnumRoot + "\\" + w.PnpLan;
            Set(en, "Driver", RegKind.String, w.ClassGuid + "\\0001");
            string im = en + "\\Device Parameters\\Interrupt Management";
            Set(im + "\\MessageSignaledInterruptProperties", "MSISupported", RegKind.DWord, "1");
            Set(im + "\\MessageSignaledInterruptProperties", "MessageNumberLimit", RegKind.DWord, "4");
            Set(im + "\\Affinity Policy", "DevicePolicy", RegKind.DWord, "4");
            Set(im + "\\Affinity Policy", "AssignmentSetOverride", RegKind.Binary, "04");
            Set(R.EnumRoot + "\\" + w.PnpWifi, "Driver", RegKind.String, w.ClassGuid + "\\0009");
            w.RecLan = new AdapterRecord { Alias = w.AliasLan, Description = w.DescLan, Status = "Up", LinkSpeed = "2.5 Gbps", Medium = 14, MediaText = "802.3",
                Guid = w.GuidLan, PnpId = w.PnpLan, IfIndex = 9001, DriverProvider = "SelfTest", DriverVersion = "1.0.0.0" };
            w.RecWifi = new AdapterRecord { Alias = w.AliasWifi, Description = w.DescWifi, Status = "Up", LinkSpeed = "1.2 Gbps", Medium = 9, MediaText = "Native 802.11",
                Guid = w.GuidWifi, PnpId = w.PnpWifi, IfIndex = 9002, DriverProvider = "SelfTest", DriverVersion = "1.0.0.0" };
            Stub.Adapters = new List<AdapterRecord> { w.RecLan, w.RecWifi };
            Stub.Mtu = new Dictionary<string, int> { { "9001|IPv4", 1500 }, { "9001|IPv6", 1500 }, { "9002|IPv4", 1500 }, { "9002|IPv6", 1500 } };
            var spread = new string[16];
            for (int i = 0; i < 16; i++) spread[i] = "0:" + ((i % 4) * 2).ToString(CultureInfo.InvariantCulture);
            // the driver's own defaults: RSS on, 4 queues spread over 4 cores
            Stub.Rss[w.AliasLan] = new RssRecord { Enabled = true, Profile = "NUMAStatic", BaseGroup = "0", BaseNumber = "0", MaxGroup = "0", MaxNumber = "11",
                MaxProcessors = "4", Queues = "4", Table = spread, ProcArray = new[] { "0:0", "0:2", "0:4", "0:6" } };
            return w;
        }

        // Set-StubRss: what Get-NetAdapterRss reports after Set-NetAdapterRss -NoRestart (restarted = false: old table)
        private void SetStubRss(string alias, string cpu, string queues, bool restarted)
        {
            var old = Stub.Rss[alias];
            var table = old.Table;
            if (restarted) table = Enumerable.Repeat("0:" + cpu, 16).ToArray();
            Stub.Rss[alias] = new RssRecord { Enabled = true, Profile = E.RssProfile, BaseGroup = "0", BaseNumber = cpu, MaxGroup = "0", MaxNumber = cpu,
                MaxProcessors = "1", Queues = queues, Table = table, ProcArray = new[] { "0:" + cpu } };
            string prof = Profiles.ProfileId(E.RssProfile);
            Stub.Adv[alias] = new[] { new[] { "*RSS", "1" }, new[] { "*RSSProfile", prof }, new[] { "*NumRssQueues", queues }, new[] { "*RssBaseProcNumber", cpu },
                new[] { "*MaxRssProcessors", "1" }, new[] { "*RssMaxProcNumber", cpu }, new[] { "*FlowControl", "3" } }
                .Select(e => new AdvancedRecord { Keyword = e[0], Value = e[1], DataType = "REG_SZ" }).ToArray();
        }

        private static JsonObject SnapEntry(Snapshot s, string scope, string name)
        {
            if (s == null) return null;
            foreach (var n in Snapshot.Arr(s.Root["values"]))
            {
                var o = n as JsonObject;
                if (o != null && Snapshot.Str(o["scope"]) == scope && Snapshot.Str(o["name"]) == name) return o;
            }
            return null;
        }

        private static string Esc(string p) { return p.Replace("\\", "\\\\"); }

        // ------------------------------------------------------------------------------------------------ A
        private void TestCpu()
        {
            Say(" -- A. masks, CPU map, RSS CPU checks");
            Check("mask of LP 0 = 0x1 (1); LP 0-11 = 0xFFF (4095)",
                Masks.Format(Masks.FromList(new[] { 0 })) == "0x1 (1)" && Masks.Format(Masks.FromList(Enumerable.Range(0, 12))) == "0xFFF (4095)", Masks.Format(Masks.FromList(new[] { 0 })));
            Check("LP 63 -> 9223372036854775808; bytes 0080 -> LP 15",
                Masks.FromList(new[] { 63 }).ToString(CultureInfo.InvariantCulture) == "9223372036854775808" && Masks.FormatLpList(Masks.ToList(Masks.FromBytes(Hex.ToBytes("0080")))) == "15");
            var c12 = Topology.Build("12th Gen Intel(R) Core(TM) i5-12400F", "synthetic", Topology.Synthetic(6, 2, 0, 0));
            E.Cpu = c12; E.RssCpu = 0; E.RssGroup = 0; E.RssProfile = "Closest";
            Q(() => E.ShowCpuMap());
            string map = Quiet;
            Check("6C/12T map: \"Core 0: LP 0,1 | Core 1: LP 2,3\" ... \"Core 5: LP 10,11\", all-LP mask 0xFFF (4095)",
                map.Contains("Core 0: LP 0,1 | Core 1: LP 2,3") && map.Contains("Core 5: LP 10,11") && map.Contains("0xFFF (4095)") && c12.Cores == 6 && c12.Logical == 12 && c12.Smt && !c12.Hybrid, map);
            var t0 = Topology.TestRssCpu(c12, 0, 0);
            Check("RSS CPU 0 valid on 6C/12T, SMT sibling LP 1", t0.Ok && t0.Info.Contains("SMT sibling: LP 1") && t0.Warn.Count == 0, t0.Info);
            var t1 = Topology.TestRssCpu(c12, 0, 1);
            Check("RSS CPU 1 = second SMT thread: allowed, warned that RSS uses one LP per core (LP 0)",
                t1.Ok && t1.Warn.Count == 1 && t1.Warn[0].Contains("one logical processor per core") && t1.Warn[0].Contains("LP 0"), t1.Warn.Count.ToString(CultureInfo.InvariantCulture));
            Check("RSS CPU 12 rejected on 12 LP; group 1 rejected on one group", !Topology.TestRssCpu(c12, 0, 12).Ok && !Topology.TestRssCpu(c12, 1, 0).Ok);
            var ch = Topology.Build("Test 8P+8E", "synthetic", Topology.Synthetic(8, 2, 8, 0));
            var te = Topology.TestRssCpu(ch, 0, 16);
            Check("hybrid 8P+8E: LP 16 is an E-core -> allowed with a warning; LP 0 is a P-core",
                ch.Hybrid && te.Ok && te.Warn.Count == 1 && Topology.TestRssCpu(ch, 0, 0).Warn.Count == 0);
            var rg = Topology.Synthetic(2, 2, 0, 0); var rg2 = Topology.Synthetic(2, 2, 0, 0);
            foreach (var r in rg2) r.Group = 1;
            var cg = Topology.Build("Test 2 groups", "synthetic", rg.Concat(rg2));
            Check("two processor groups: group 1 CPU 3 valid, group 2 rejected", cg.MultiGroup && Topology.TestRssCpu(cg, 1, 3).Ok && !Topology.TestRssCpu(cg, 2, 0).Ok);
            Func<int, string, string> mk = (m, t) => NetUtil.MediaKind(new AdapterRecord { Medium = m, MediaText = t });
            Check("media: NdisPhysicalMedium 14 = LAN, 9 = Wi-Fi, 10 = Other; text fallback",
                mk(14, "") == "LAN" && mk(9, "") == "Wi-Fi" && mk(10, "BlueTooth") == "Other" && mk(-1, "Native 802.11") == "Wi-Fi" && mk(-1, "802.3") == "LAN");
            var recs = new[] { new AdapterRecord { Alias = "w", Status = "Up", Medium = 9 }, new AdapterRecord { Alias = "e", Status = "Up", Medium = 14 },
                new AdapterRecord { Alias = "e2", Status = "Disconnected", Medium = 14 } };
            Check("auto-select: physical + Up, wired first; Wi-Fi when no wired adapter is Up",
                NetUtil.SelectDefault(recs).Alias == "e" && NetUtil.SelectDefault(new[] { recs[0], recs[2] }).Alias == "w");
            E.Cpu = c12;
        }

        // ------------------------------------------------------------------------------------------------ B
        private void TestClassKey(World w)
        {
            Say(" -- B. driver key Class\\000X: resolve, cross-check, classify");
            string root = R.ClassRoot;
            var r1 = ClassKeys.Resolve(_reg, root, w.GuidLan, w.DescLan);
            Check("1 NetCfgInstanceId match -> Class\\0001, no warning", r1.Ok && r1.Index == "0001" && r1.Mode == "NetCfgInstanceId" && r1.Warn == "", r1.Index + " " + r1.Warn);
            var r1b = ClassKeys.Resolve(_reg, root, w.GuidLan.ToLowerInvariant(), w.DescLan + " #2");
            Check("GUID compare ignores case; \"#2\" suffix of InterfaceDescription is accepted", r1b.Ok && r1b.Index == "0001" && r1b.Warn == "", r1b.Warn);
            var r1c = ClassKeys.Resolve(_reg, root, w.GuidLan, "Some other name");
            Check("GUID match with a different DriverDesc -> resolved, with a warning", r1c.Ok && r1c.Warn.Contains("differs"), r1c.Warn);
            var r0 = ClassKeys.Resolve(_reg, root, w.GuidNone, "No such adapter");
            Check("0 matches -> stop, nothing resolved", !r0.Ok && r0.Path == "" && r0.Reason != "", r0.Reason);
            var r2 = ClassKeys.Resolve(_reg, root, w.GuidTwin, "Twin GUID adapter A");
            Check("2 NetCfgInstanceId matches -> stop with 2 candidates, although DriverDesc alone would pick one",
                !r2.Ok && r2.Path == "" && r2.Candidates.Count == 2 && r2.Reason.StartsWith("NetCfgInstanceId matches 2 keys", StringComparison.Ordinal), r2.Reason);
            var rf = ClassKeys.Resolve(_reg, root, w.GuidNone, w.DescWifi);
            Check("fallback: no GUID match, unique DriverDesc -> Class\\0009 with a warning", rf.Ok && rf.Index == "0009" && rf.Mode == "DriverDesc" && rf.Warn != "", rf.Warn);
            var rd = ClassKeys.Resolve(_reg, root, w.GuidNone, "Dup Adapter");
            Check("fallback: DriverDesc on 2 keys -> stop", !rd.Ok && rd.Candidates.Count == 2, rd.Reason);
            var stray = new AdapterRecord { Alias = "NetTune-SelfTest-X", Description = w.DescWifi, Status = "Up", Medium = 9, Guid = w.GuidNone, PnpId = @"PCI\VEN_FFFF&DEV_0001\1", IfIndex = 9009 };
            var cx = ClassKeys.NewContext(stray, _reg, R, Stub);
            Check("fallback key that holds the GUID of another interface and is not confirmed by Enum -> not resolved",
                cx.ClassPath == "" && cx.ClassError.Contains("nothing will be written") && cx.Candidates.Count == 1, cx.ClassError);
            Set(R.EnumRoot + "\\" + stray.PnpId, "Driver", RegKind.String, w.ClassGuid + "\\0009");
            cx = ClassKeys.NewContext(stray, _reg, R, Stub);
            Check("the same fallback confirmed by Enum\\..\\Driver -> resolved (DriverDesc), with a warning",
                cx.ClassIndex == "0009" && cx.ClassMode == "DriverDesc" && cx.EnumAgrees == true && cx.ClassWarn != "", cx.ClassError);
            var nog = new AdapterRecord { Alias = "NetTune-SelfTest-Y", Description = "No GUID adapter", Status = "Up", Medium = 14, Guid = w.GuidNone, PnpId = "", IfIndex = 9010 };
            cx = ClassKeys.NewContext(nog, _reg, R, Stub);
            Check("fallback to a key without NetCfgInstanceId -> resolved (DriverDesc)", cx.ClassIndex == "0008" && cx.ClassMode == "DriverDesc", cx.ClassError);
            string en = R.EnumRoot + "\\" + w.PnpLan;
            Set(en, "Driver", RegKind.String, w.ClassGuid + "\\0005");
            cx = ClassKeys.NewContext(w.RecLan, _reg, R, Stub);
            Check("GUID match but Enum\\..\\Driver points at another key -> not resolved", cx.ClassPath == "" && cx.EnumAgrees == false && cx.ClassError.Contains("does not agree"), cx.ClassError);
            Set(en, "Driver", RegKind.String, w.ClassGuid + "\\0001");
            var ndiLan = ClassKeys.NdiParams(_reg, w.LanKey); var ndiWifi = ClassKeys.NdiParams(_reg, w.WifiKey);
            Check("Ndi\\Params read: LAN 8 keywords, Wi-Fi 1", ndiLan.Length == 8 && ndiWifi.Length == 1, ndiLan.Length + "/" + ndiWifi.Length);
            string cls = string.Join(",", new[]
            {
                Profiles.Classify("Class", "*RSS", ndiLan), Profiles.Classify("Class", "*NumRSSQueues", new string[0]), Profiles.Classify("Class", "*FlowControl", ndiLan),
                Profiles.Classify("Class", "BufferDescriptorsRx", ndiWifi), Profiles.Classify("Class", "BufferDescriptorsTx", ndiWifi), Profiles.Classify("Class", "RSS", ndiLan),
                Profiles.Classify("Class", "RssBaseProcNumber", ndiLan), Profiles.Classify("Class", "MessageNumberLimit", ndiLan), Profiles.Classify("NDIS", "RssBaseCpu", ndiLan),
            });
            Check("Classify-Value: T1-STD / T1-STD / VENDOR / VENDOR / UNKNOWN / UNKNOWN / UNKNOWN (no star) / SKIP / UNKNOWN",
                cls == "T1-STD,T1-STD,VENDOR,VENDOR,UNKNOWN,UNKNOWN,UNKNOWN,SKIP,UNKNOWN", cls);
        }

        // ------------------------------------------------------------------------------------------------ C
        private void TestRegistry(World w)
        {
            Say(" -- C. registry: types, literal names, write guards");
            string kt = R.ScratchRoot + "\\KindTest";
            Set(kt, "AsString", RegKind.String, "1");
            Set(kt, "AsDword", RegKind.DWord, "1");
            var ks = Get(kt, "AsString"); var kd = Get(kt, "AsDword");
            Check("REG_SZ \"1\" and REG_DWORD 1 are different values",
                ks.Kind == RegKind.String && kd.Kind == RegKind.DWord && !RegValue.Equal(ks, kd) && RegValue.SameData(ks, kd), RegValue.Format(ks) + " / " + RegValue.Format(kd));
            Set(kt, "Big", RegKind.DWord, "4294967295");
            Set(kt, "Q", RegKind.QWord, "18446744073709551615");
            Set(kt, "Bin", RegKind.Binary, "00FF10");
            E.RegSet(kt, "Multi", RegValue.Multi("a", "b"));
            var mv = Get(kt, "Multi");
            Check("REG_DWORD 0xFFFFFFFF, REG_QWORD max, REG_BINARY and REG_MULTI_SZ survive a round trip",
                Get(kt, "Big").Data == "4294967295" && Get(kt, "Q").Data == "18446744073709551615" && Get(kt, "Bin").Data == "00FF10" &&
                mv.Kind == RegKind.MultiString && string.Join("|", mv.Lines) == "a|b", KeyDump(kt));
            Set(kt, "*RSS", RegKind.String, "star");
            Set(kt, "RSS", RegKind.String, "plain");
            Set(kt, "xRSS", RegKind.String, "other");
            Check("value name \"*RSS\" is literal: read is not a wildcard", Get(kt, "*RSS").Data == "star" && Get(kt, "RSS").Data == "plain");
            E.RegRemove(kt, "*RSS");
            Check("deleting \"*RSS\" leaves \"RSS\" and \"xRSS\" in place", Get(kt, "*RSS") == null && Get(kt, "RSS").Data == "plain" && Get(kt, "xRSS").Data == "other", KeyDump(kt));
            E.DryRun = true;
            bool d1 = E.RegSet(kt, "DryNew", RegKind.String, "x"), d2 = E.RegRemove(kt, "RSS"), d3 = E.RegCreateKey(kt + "\\DrySub");
            E.DryRun = false;
            Check("dry-run: the registry layer itself does not set, delete or create anything",
                !d1 && !d2 && !d3 && Get(kt, "DryNew") == null && Get(kt, "RSS").Data == "plain" && !_reg.KeyExists(kt + "\\DrySub"), KeyDump(kt));
            Check("write outside the allowed locations is refused", Throws(() => E.RegSet(@"HKLM\SOFTWARE\grobPC-must-not-exist", "x", RegKind.DWord, "1")));
            bool okPaths = R.IsClassInstance(w.LanKey) && !R.IsClassInstance(w.LanKey + "\\Ndi\\Params") && !R.IsClassInstance(R.ClassRoot) &&
                           !R.IsClassInstance(R.ClassRoot + "\\Properties") && !R.IsClassInstance(R.ClassRoot + "\\00011");
            Check("only Class\\NNNN itself counts as a driver key (not the class root, not Ndi\\Params, not Properties)", okPaths);
            Check("write into Ndi\\Params of a real driver key is refused",
                Throws(() => E.RegSet(@"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0001\Ndi\Params\*RSS", "default", RegKind.String, "0")));
            Check("write into Enum\\..\\Interrupt Management is refused (interrupt policy is never written)",
                Throws(() => E.RegSet(@"HKLM\SYSTEM\CurrentControlSet\Enum\" + w.PnpLan + @"\Device Parameters\Interrupt Management\Affinity Policy", "DevicePolicy", RegKind.DWord, "4")));
            bool threw = Throws(() => E.RegSet(w.LanKey, "MessageNumberLimit", RegKind.DWord, "1"));
            Check("MessageNumberLimit into the class key is refused (R7)", threw && Get(w.LanKey, "MessageNumberLimit") == null);
            // the production roots refuse the scratch-only locations of the selftest world as well
            var real = new RegRoots();
            Check("production roots: only Class\\NNNN of the network class, NDIS\\Parameters and the scratch key are writable",
                real.IsClassInstance(real.ClassRoot + "\\0001") && Throws(() => real.AssertWritable(real.EnumRoot + "\\x", "y")) &&
                Throws(() => real.AssertWritable(@"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000", "x")) &&
                !Throws(() => real.AssertWritable(real.NdisKey, "RssBaseCpu")));
        }

        // ------------------------------------------------------------------------------------------------ D
        private void TestProfile(World w)
        {
            Say(" -- D. profile import: preview, modes, types, backup first, idempotency, dry-run, exact restore");
            string lanKey = w.LanKey;
            E.SetTarget(w.RecLan);
            var c = E.Ctx;
            Check("stub adapter selected: Class\\0001 by NetCfgInstanceId, Enum agrees, RSS object present",
                c.ClassIndex == "0001" && c.ClassMode == "NetCfgInstanceId" && c.EnumAgrees == true && c.RssSupported, c.ClassError);
            E.RssCpu = 2; E.RssProfile = "ClosestStatic";
            string before = KeyDump(lanKey), beforeNdis = KeyDump(R.NdisKey);
            var snap = Snapshot.New(_reg, E.SnapshotScope(c), "selftest", Engine.Version, "SELFTEST", DateTime.Now);
            var vals = Snapshot.Arr(snap.Root["values"]);
            Check("snapshot scope: 19 class values + 4 NDIS values, MessageNumberLimit not in scope",
                vals.Count == 23 && !vals.Any(v => string.Equals(Snapshot.Str(v["name"]), "MessageNumberLimit", StringComparison.OrdinalIgnoreCase)), vals.Count.ToString(CultureInfo.InvariantCulture));
            var pm = E.GetProfilePlan(Profiles.Lan, c).ToDictionary(r => r.Name, StringComparer.Ordinal);
            Check("plan: {CPU} -> 2, {PROFILE} -> 2 (ClosestStatic), kinds as in the .reg",
                pm["BaseProcessorNumber"].Data == "2" && pm["BaseProcessorNumber"].Kind == RegKind.DWord && pm["*RSSProfile"].Data == "2" && pm["*RSSProfile"].Kind == RegKind.String && pm["*RSS"].Kind == RegKind.String);
            Check("plan: MessageNumberLimit = SKIP, RssCapabilities = type differs, *RSS = write, classes T1-STD / UNKNOWN",
                pm["MessageNumberLimit"].Action == "skip" && pm["RssCapabilities"].Action == "type differs" && pm["*RSS"].Action == "write" && pm["*RSS"].Class == "T1-STD" && pm["ForceRssEnable"].Class == "UNKNOWN");
            // dry-run
            E.DryRun = true;
            Answers("A", "M", "y"); int a0 = Cap.AskCount;
            int n0 = Q(() => E.WriteProfile("L", ""));
            E.DryRun = false;
            Check("dry-run: 3 questions, \"11 value(s) would be written\", nothing written, no backup",
                n0 == 0 && Cap.AskCount - a0 == 3 && Quiet.Contains("Dry-run: 11 value(s) would be written") && KeyDump(lanKey) == before && E.BackupList().Count == 0, KeyDump(lanKey));
            // Enter at every question = nothing is written
            Answers(); a0 = Cap.AskCount;
            int nE = Q(() => E.WriteProfile("L", ""));
            Check("Enter at every question (mode, type, final): nothing written, no backup",
                nE == 0 && Cap.AskCount - a0 == 3 && Cap.LastPrompt.Contains("Write 10 value(s) to Class\\0001?") && KeyDump(lanKey) == before && E.BackupList().Count == 0, Cap.LastPrompt);
            // mode S
            Answers("S", "y"); a0 = Cap.AskCount;
            int nS = Q(() => E.WriteProfile("L", ""));
            Check("mode [S]: only the 3 T1-STD values are written (*RSS, *RSSProfile, *NumRssQueues)",
                nS == 3 && Cap.AskCount - a0 == 2 && Get(lanKey, "*RSS").Data == "1" && Get(lanKey, "*RSSProfile").Data == "2" && Get(lanKey, "*NumRssQueues").Data == "1" &&
                Get(lanKey, "ForceRssEnable") == null && E.RestartPending, nS.ToString(CultureInfo.InvariantCulture));
            var bl = E.BackupList();
            Snapshot bs = bl.Count == 1 ? Snapshot.Read(Path.Combine(bl[0], "snapshot.json")) : null;
            var e1 = SnapEntry(bs, "Class", "*RSS"); var e2 = SnapEntry(bs, "Class", "*NumRssQueues"); var e3 = SnapEntry(bs, "Class", "*RSSProfile"); var e4 = SnapEntry(bs, "NDIS", "RssBaseCpu");
            Check("the backup was made BEFORE the first write: its snapshot holds the original values (\"0\", \"4\", absent, 4) and MTU 1500",
                bl.Count == 1 && e1 != null && Snapshot.Bool(e1["existed"]) && Snapshot.Str(e1["data"]) == "0" && Snapshot.Str(e2["data"]) == "4" && e3 != null && !Snapshot.Bool(e3["existed"]) &&
                Snapshot.Str(e4["data"]) == "4" && Snapshot.Str(bs.Obj("mtu")["ipv4"]) == "1500", bl.Count.ToString(CultureInfo.InvariantCulture));
            // mode A: the rest; RssCapabilities exists as REG_SZ -> [K] keep the type
            Answers("A", "K", "y");
            int nA = Q(() => E.WriteProfile("L", ""));
            var capv = Get(lanKey, "RssCapabilities");
            Check("mode [A]: 8 more values; existing REG_SZ type kept on request (\"1\" REG_SZ, not DWORD); no second backup",
                nA == 8 && capv.Kind == RegKind.String && capv.Data == "1" && Cap.Answers.Count == 0 && E.BackupList().Count == 1, nA + " " + RegValue.Format(capv));
            Check("written exactly as in the .reg: DWord 1 / DWord 2 / \"1\" REG_SZ; MessageNumberLimit absent",
                Fmt(lanKey, "MaxRssProcessors") == "DWord:1" && Fmt(lanKey, "BaseProcessorNumber") == "DWord:2" && Fmt(lanKey, "ForceRssEnable") == "String:\"1\"" && Get(lanKey, "MessageNumberLimit") == null,
                Fmt(lanKey, "MaxRssProcessors") + " " + Fmt(lanKey, "BaseProcessorNumber") + " " + Fmt(lanKey, "ForceRssEnable"));
            Answers(); a0 = Cap.AskCount;
            int nI = Q(() => E.WriteProfile("L", ""));
            Check("next run is idempotent: the kept-type value counts as set, no question, nothing written",
                nI == 0 && Cap.AskCount - a0 == 0 && Quiet.Contains("already set - nothing to write"), (Cap.AskCount - a0).ToString(CultureInfo.InvariantCulture));
            Set(lanKey, "RssCapabilities", RegKind.String, "0");
            Answers("A"); a0 = Cap.AskCount;
            int nT = Q(() => E.WriteProfile("L", ""));
            Check("type differs: Enter = skip (default), the value stays REG_SZ \"0\"", nT == 0 && Cap.AskCount - a0 == 2 && Fmt(lanKey, "RssCapabilities") == "String:\"0\"", Fmt(lanKey, "RssCapabilities"));
            Answers("A", "M", "y");
            int nM = Q(() => E.WriteProfile("L", ""));
            Check("type differs: [M] writes mine as DWORD 1", nM == 1 && Fmt(lanKey, "RssCapabilities") == "DWord:1", nM.ToString(CultureInfo.InvariantCulture));
            Answers("A", "y");
            int nN = Q(() => E.WriteProfile("N", ""));
            Check("NDIS\\Parameters profile: 4 DWORD values = RSS CPU 2; foreign value untouched",
                nN == 4 && Fmt(R.NdisKey, "RssBaseCpu") == "DWord:2" && Fmt(R.NdisKey, "RssMaxCpuNumber") == "DWord:2" && Fmt(R.NdisKey, "MaxNumRssCpus") == "DWord:4", nN.ToString(CultureInfo.InvariantCulture));
            // R4: a backup counts only for this adapter + this driver key, and only while its snapshot.json exists
            string b1 = E.LastBackup;
            File.Delete(Path.Combine(b1, "snapshot.json"));
            Set(lanKey, "*RSS", RegKind.String, "0");
            Answers("S", "y");
            Q(() => E.WriteProfile("L", ""));
            string b2 = E.LastBackup;
            Check("backup folder without snapshot.json does not count: a new backup is made before the write", b2 != b1 && File.Exists(Path.Combine(b2, "snapshot.json")), b2);
            E.BackupKey = Engine.KeyOf(c.Guid, "");
            Set(lanKey, "*RSS", RegKind.String, "0");
            Answers("S", "y");
            Q(() => E.WriteProfile("L", ""));
            Check("backup taken while the driver key was not resolved does not count either", E.LastBackup != b2 && E.BackupKey == E.CurrentBackupKey(), E.LastBackup);
            // restore through a JSON round trip ("4" REG_SZ was the original: the same data as REG_DWORD must be put back too)
            Set(lanKey, "*NumRssQueues", RegKind.DWord, "4");
            string snapFile = snap.Save(E.BaseDir);
            var snap2 = Snapshot.Read(snapFile);
            var acts = snap2.GetRestoreActions(_reg, R, c.ClassPath);
            int nd = acts.Count(x => x.Op == "delete"), ns = acts.Count(x => x.Op == "set");
            Check("restore plan: 7 values to delete + 4 to set back (driver key), 3 to delete + 1 to set back (NDIS)", nd == 10 && ns == 5, nd + " delete / " + ns + " set");
            E.ApplyRestore(acts);
            Check("restore is exact: driver key and NDIS\\Parameters equal the original (types, data, added values removed)", KeyDump(lanKey) == before && KeyDump(R.NdisKey) == beforeNdis, KeyDump(lanKey));
            Check("restore: nothing left to restore", snap2.GetRestoreActions(_reg, R, c.ClassPath).Count == 0);
            var q51 = Snapshot.Parse("{\"tool\":\"grobPC-NetTune\",\"values\":{\"value\":[{\"scope\":\"NDIS\",\"path\":\"" + Esc(R.NdisKey) +
                "\",\"name\":\"RssBaseCpu\",\"existed\":true,\"kind\":\"DWord\",\"data\":\"7\"}],\"Count\":1}}", "ps51");
            var qa = q51.GetRestoreActions(_reg, R, "");
            Check("snapshot in the PS 5.1 \"value / Count\" array form is still readable", qa.Count == 1 && qa[0].Op == "set" && qa[0].Value.Data == "7", qa.Count.ToString(CultureInfo.InvariantCulture));
            var moved = snap2.GetRestoreActions(_reg, R, R.ClassRoot + "\\0005");
            Check("restore follows the adapter when its Class\\000X index changed (class values retargeted)",
                moved.Count(x => x.Path.EndsWith("\\0005", StringComparison.Ordinal)) >= 1 && moved.Count(x => x.Path.EndsWith("\\0001", StringComparison.Ordinal)) == 0);
            var evil = Snapshot.Parse("{\"tool\":\"grobPC-NetTune\",\"values\":[" +
                "{\"scope\":\"NDIS\",\"path\":\"" + Esc(w.WifiKey) + "\",\"name\":\"RssBaseCpu\",\"existed\":true,\"kind\":\"DWord\",\"data\":\"7\"}," +
                "{\"scope\":\"Enum\",\"path\":\"" + Esc(R.EnumRoot + "\\" + w.PnpLan) + "\",\"name\":\"Driver\",\"existed\":false,\"kind\":\"\",\"data\":\"\"}," +
                "{\"scope\":\"Class\",\"path\":\"" + Esc(w.WifiKey) + "\",\"name\":\"*RSS\",\"existed\":true,\"kind\":\"String\",\"data\":\"1\"}]}", "evil");
            var ea = evil.GetRestoreActions(_reg, R, lanKey);
            var eb = evil.GetRestoreActions(_reg, R, "");
            string paths = string.Join(" ; ", ea.Select(x => x.Path).OrderBy(x => x, StringComparer.Ordinal));
            string want = string.Join(" ; ", new[] { RegPath.Normal(lanKey), RegPath.Normal(R.NdisKey) }.OrderBy(x => x, StringComparer.Ordinal));
            Check("restore takes the key from the scope, never from the path text of the file; unknown scopes are ignored",
                ea.Count == 2 && paths == want && eb.Count == 1 && eb[0].Path == RegPath.Normal(R.NdisKey), paths);
            Check("a file that is not a grobPC-NetTune snapshot is refused", Throws(() => Snapshot.Parse("{\"tool\":\"other\",\"values\":[]}", "x")) && Throws(() => Snapshot.Parse("not json", "y")));
            // Wi-Fi: no RSS object, buffers by class
            E.SetTarget(w.RecWifi);
            string wifiKey = w.WifiKey;
            Check("Wi-Fi stub adapter: Class\\0009, RSS not supported", E.Ctx.ClassIndex == "0009" && E.Ctx.Media == "Wi-Fi" && !E.Ctx.RssSupported, E.Ctx.ClassError);
            Answers("A", "n", "y"); a0 = Cap.AskCount;
            int nB = Q(() => E.WriteProfile("B", ""));
            Check("Wi-Fi buffers: VENDOR value written (256), UNKNOWN value only after its own confirmation (declined)",
                nB == 1 && Cap.AskCount - a0 == 3 && Fmt(wifiKey, "BufferDescriptorsRx") == "DWord:256" && Get(wifiKey, "BufferDescriptorsTx") == null, nB.ToString(CultureInfo.InvariantCulture));
            Answers("A", "y"); a0 = Cap.AskCount;
            int nB2 = Q(() => E.WriteProfile("B", ""));
            Check("Wi-Fi buffers: the UNKNOWN value is written after its own confirmation, without a second question",
                nB2 == 1 && Cap.AskCount - a0 == 2 && Cap.LastPrompt.Contains("not declared in Ndi\\Params") && Fmt(wifiKey, "BufferDescriptorsTx") == "DWord:128", Cap.LastPrompt);
            Answers("A", "n"); a0 = Cap.AskCount;
            int nW = Q(() => E.WriteProfile("W", ""));
            Check("Wi-Fi RSS profile on a driver without RSS: the question says \"driver does not support RSS\"; N writes nothing",
                nW == 0 && Cap.AskCount - a0 == 2 && Cap.LastPrompt.Contains("Driver does not support RSS") && Cap.LastPrompt.Contains("anyway") && Get(wifiKey, "*RSS") == null, Cap.LastPrompt);
            int c0 = Stub.Calls.Count;
            Answers(); a0 = Cap.AskCount;
            bool ok6 = Q(() => E.ApplyRss());
            Check("Apply RSS on a driver without RSS object: cmdlets skipped, no question asked",
                !ok6 && Cap.AskCount - a0 == 0 && Stub.Calls.Count == c0 && Quiet.Contains("Driver does not support RSS"));
            var vw = Q(() => E.Verify());
            Check("verify on a driver without RSS object: no PASS for RSS lines, profile lines are WARN, no FAIL",
                vw.Fail == 0 && vw.Warn >= 10 && Quiet.Contains("ADV  RSS keywords: driver has no RSS support"), string.Format("PASS {0} WARN {1} FAIL {2}", vw.Pass, vw.Warn, vw.Fail));
        }

        // ------------------------------------------------------------------------------------------------ E
        private void TestRss(World w)
        {
            Say(" -- E. Apply RSS, restart, verify (collectors stubbed, nothing real is called)");
            string alias = w.AliasLan;
            E.SetTarget(w.RecLan);
            E.RssCpu = 0; E.RssGroup = 0; E.RssProfile = "Closest";
            Answers("M", "y");
            int nL = Q(() => E.WriteProfile("L", "A"));
            Answers("y");
            int nN = Q(() => E.WriteProfile("N", "A"));
            Check("registry profile for CPU 0 written with a given mode: 11 + 4 values, no mode question", nL == 11 && nN == 4 && Cap.Answers.Count == 0, nL + " / " + nN);
            int c0 = Stub.Calls.Count;
            Answers("n"); int a0 = Cap.AskCount;
            bool r = Q(() => E.ApplyRss());
            Check("[6] answer N: no cmdlet is called", !r && Stub.Calls.Count == c0 && Cap.AskCount - a0 == 1);
            Answers(); r = Q(() => E.ApplyRss());
            E.DryRun = true;
            Answers("y");
            bool rd = Q(() => E.ApplyRss());
            E.DryRun = false;
            Check("[6] Enter = No; dry-run with Y: preview only, no cmdlet", !r && !rd && Stub.Calls.Count == c0 && Quiet.Contains("Dry-run: the two cmdlets above would run"));
            E.LastBackup = ""; int nb = E.BackupList().Count;
            Answers("y");
            r = Q(() => E.ApplyRss());
            var calls = Stub.Calls.ToArray();
            string wantSet = "Set|" + alias + "|BaseProcessorNumber=0,MaxProcessorNumber=0,MaxProcessors=1,NumberOfReceiveQueues=1,Profile=Closest";
            Check("[6] Y: Enable-NetAdapterRss, then one Set-NetAdapterRss with base = max = CPU 0, 1 processor, 1 queue, Closest",
                r && calls.Length == c0 + 2 && calls[c0] == "Enable|" + alias && calls[c0 + 1] == wantSet && E.RestartPending, string.Join(" ; ", calls));
            Check("[6] a backup with snapshot.json existed before the first cmdlet ran",
                E.BackupList().Count == nb + 1 && Stub.Backed.Count == c0 + 2 && Stub.Backed[c0] && Stub.Backed[c0 + 1]);
            SetStubRss(alias, "0", "1", false);
            var v1 = Q(() => E.Verify());
            Check("[8] before the restart: settings PASS, old IndirectionTable FAIL, \"restart pending\" WARN",
                v1.Fail == 1 && Quiet.Contains("IndirectionTable also uses 0:2 0:4 0:6") && Quiet.Contains("not what is in effect"), string.Format("PASS {0} WARN {1} FAIL {2}", v1.Pass, v1.Warn, v1.Fail));
            int c1 = Stub.Calls.Count;
            Answers(); bool r1 = Q(() => E.RestartTarget(false));
            Answers("y"); bool r2 = Q(() => E.RestartTarget(false));
            Check("[7] Enter = No; Y restarts the selected adapter only and waits for link + gateway",
                !r1 && r2 && Stub.Calls.Count == c1 + 1 && Stub.Calls[c1] == "Restart|" + alias && !E.RestartPending && Quiet.Contains("link Up 2.5 Gbps, gateway 192.168.1.1"), Quiet);
            SetStubRss(alias, "0", "1", true);
            var v2 = Q(() => E.Verify());
            Check("[8] after the restart: 16 PASS, 12 WARN (UNKNOWN-class values), 0 FAIL; IndirectionTable all on 0:0",
                v2.Pass == 16 && v2.Warn == 12 && v2.Fail == 0 && Quiet.Contains("IndirectionTable: all 16 entries on 0:0"), string.Format("PASS {0} WARN {1} FAIL {2}", v2.Pass, v2.Warn, v2.Fail));
            Answers(); a0 = Cap.AskCount; int c2 = Stub.Calls.Count;
            r = Q(() => E.ApplyRss());
            Check("[6] again: already set, no question, no cmdlet", r && Cap.AskCount - a0 == 0 && Stub.Calls.Count == c2);
            SetStubRss(alias, "0", "4", true);
            Answers(); a0 = Cap.AskCount;
            r = Q(() => E.ApplyRss());
            Check("[6] 4 queues while the driver never rejected -NumberOfReceiveQueues: not \"already set\", asks (Enter = No)", !r && Cap.AskCount - a0 == 1 && Stub.Calls.Count == c2);
            SetStubRss(alias, "0", "1", true);
            // the driver rejects -NumberOfReceiveQueues
            Stub.RejectQueues = true;
            E.RssCpu = 2;
            Answers("y");
            r = Q(() => E.ApplyRss());
            calls = Stub.Calls.ToArray();
            Check("[6] driver rejects -NumberOfReceiveQueues: one retry without it, WARN",
                r && calls.Length == c2 + 3 && calls[c2 + 1].Contains("NumberOfReceiveQueues=1") && calls[c2 + 2] == "Set|" + alias + "|BaseProcessorNumber=2,MaxProcessorNumber=2,MaxProcessors=1,Profile=Closest" &&
                E.QueuesRejected && Quiet.Contains("rejected -NumberOfReceiveQueues 1"), string.Join(" ; ", calls));
            Answers("y"); Q(() => E.RestartTarget(false));
            SetStubRss(alias, "2", "4", true);
            var v3 = Q(() => E.Verify());
            Check("[8] queue count the driver keeps (4) is WARN, not FAIL, once every table entry is on the target CPU",
                Quiet.Contains("WARN] RSS  NumberOfReceiveQueues = 4") && Quiet.Contains("WARN] ADV  *NumRssQueues = 4") && Quiet.Contains("IndirectionTable: all 16 entries on 0:2") && v3.Fail == 0,
                string.Format("PASS {0} WARN {1} FAIL {2}", v3.Pass, v3.Warn, v3.Fail));
            Stub.RejectQueues = false;
            E.RssCpu = 0;
        }

        // ------------------------------------------------------------------------------------------------ G ([R] part)
        private void TestRestoreFlow(World w)
        {
            Say(" -- G. [R] restore flow (collectors stubbed)");
            string alias = w.AliasLan;
            E.SetTarget(w.RecLan);
            E.LastBackup = "";
            string pre = Q(() => E.NewBackup("pre-mtu"));
            Stub.Mtu["9001|IPv4"] = 1480;                      // what [9] would have set, without a recorded call
            Set(w.LanKey, "*NumRssQueues", RegKind.String, "8");
            var list = E.BackupList();
            int ix = list.IndexOf(pre) + 1;
            int c1 = Stub.Calls.Count, nb = list.Count;
            string sx = ix.ToString(CultureInfo.InvariantCulture);
            E.DryRun = true;
            Answers(sx, "y");
            Q(() => E.InvokeRestore());
            E.DryRun = false;
            Check("[R] dry-run: preview and question, then nothing restored (no netsh, no registry write, no new backup)",
                ix > 0 && Quiet.Contains("Dry-run: nothing restored") && Stub.Calls.Count == c1 && Fmt(w.LanKey, "*NumRssQueues") == "String:\"8\"" && E.BackupList().Count == nb, Quiet);
            Answers(sx); int a0 = Cap.AskCount;
            Q(() => E.InvokeRestore());
            Check("[R] Enter at \"Restore ...?\" = No: nothing restored", Cap.AskCount - a0 == 2 && Stub.Calls.Count == c1 && Fmt(w.LanKey, "*NumRssQueues") == "String:\"8\"");
            E.SetTarget(w.RecWifi);
            Answers(sx); a0 = Cap.AskCount;
            Q(() => E.InvokeRestore());
            Check("[R] backup of another adapter: asks before switching (Enter = No), nothing restored",
                Cap.AskCount - a0 == 2 && Cap.LastPrompt.Contains("Select that adapter and continue?") && E.Ctx.Alias == w.AliasWifi && Stub.Calls.Count == c1, Cap.LastPrompt);
            E.SetTarget(w.RecLan);
            Answers(sx, "y", "n");
            Q(() => E.InvokeRestore());
            string t2 = Quiet;
            Check("[R] Y: registry value and MTU back (netsh 1500), pre-restore backup made first, adapter restart offered",
                Fmt(w.LanKey, "*NumRssQueues") == "String:\"1\"" && Stub.Calls.Count == c1 + 1 && Stub.Calls[c1] == "Mtu|" + alias + "|IPv4|1500" && Stub.Backed[c1] &&
                Stub.GetNlMtu(9001, "IPv4") == 1500 && E.BackupList().Count(d => Path.GetFileName(d).EndsWith("_pre-restore", StringComparison.Ordinal)) == 1 &&
                t2.Contains("Restarting \"" + alias + "\"") && E.RestartPending, string.Join(" ; ", Stub.Calls));
            Answers("0"); a0 = Cap.AskCount; int c2 = Stub.Calls.Count;
            Q(() => E.InvokeRestore());
            Check("[R] 0 = cancel: one question, nothing restored", Cap.AskCount - a0 == 1 && Stub.Calls.Count == c2);
        }

        // ------------------------------------------------------------------------------------------------ run all
        private void TestRunAll(World w)
        {
            Say(" -- [A] run all (dry-run and Enter-only: nothing may change)");
            E.SetTarget(w.RecLan);
            E.RssCpu = 4; E.RssProfile = "Closest";
            string lan = KeyDump(w.LanKey), ndis = KeyDump(R.NdisKey);
            int calls = Stub.Calls.Count, backups = E.BackupList().Count;
            E.DryRun = true;
            Answers("y", "A", "y", "y", "y", "y", "y", "y");
            Q(() => E.RunAll());
            E.DryRun = false;
            Check("[A] dry-run with Y everywhere: registry, RSS, adapter and backups untouched; ends with Verify",
                KeyDump(w.LanKey) == lan && KeyDump(R.NdisKey) == ndis && Stub.Calls.Count == calls && E.BackupList().Count == backups && Quiet.Contains("Verify result:"), Quiet);
            Answers();
            Q(() => E.RunAll());
            Check("[A] Enter at every question: only the step-4 backup (default Yes) is made, nothing else changes",
                KeyDump(w.LanKey) == lan && KeyDump(R.NdisKey) == ndis && Stub.Calls.Count == calls && E.BackupList().Count == backups + 1, Quiet);
            E.RssCpu = 0;
        }
    }
}
