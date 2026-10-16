using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;

namespace GrobPC.NetTune.App
{
    // Startup: /selftest (headless, no gate, nothing applied) runs first; then /aerial (report) and /dryrun. The normal
    // path is single-instance -> AerialOS gate (no license override: AerialOS only) -> admin check -> the window.
    public partial class App : Application
    {
        private static Mutex _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var args = (e.Args ?? new string[0]).Select(a => (a ?? "").TrimStart('/', '-').ToLowerInvariant()).ToList();
            if (args.Contains("selftest")) { Shutdown(SelfTest()); return; }
            if (args.Contains("aerial")) { Dlg.Info(null, AerialOs.Report()); Shutdown(0); return; }
            bool dryRun = args.Contains("dryrun");
            Run(dryRun);
        }

        private void Run(bool dryRun)
        {
            try
            {
                bool created;
                _mutex = new Mutex(true, Brand.Mutex, out created);
                if (!created) { Dlg.Info(null, Brand.Name + " is already running."); Shutdown(0); return; }

                if (!AerialOs.Detected()) { AerialOnlyWindow.ShowGate(); Shutdown(2); return; }
                if (!Admin.IsAdmin())
                {
                    Dlg.Info(null, Brand.Name + " needs to run as administrator (it writes driver keys and runs Set-NetAdapterRss / netsh). Right-click -> Run as administrator.");
                    Shutdown(1); return;
                }

                var win = new MainWindow(dryRun);
                MainWindow = win;
                win.Show();
                try { win.Activate(); } catch { }
            }
            catch (Exception ex)
            {
                try { Dlg.Info(null, Brand.Name + ": " + ex.GetType().Name + ": " + ex.Message); } catch { }
                Shutdown(1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { if (_mutex != null) { _mutex.ReleaseMutex(); _mutex.Dispose(); } } catch { }
            base.OnExit(e);
        }

        // Headless smoke test (CI runs it on the protected build): the engine's 82 self-test checks on the in-memory
        // registry, the AerialOS pure rules, and a JIT pass over every method (an obfuscation layer that emits invalid
        // IL fails here, at build time). Writes %TEMP%\grobPC-NetTune-selftest.txt; exit 0 = pass, 3 = a check failed.
        private int SelfTest()
        {
            var log = new List<string> { Brand.Name + " " + Brand.VersionText + " selftest" };
            int fails = 0;

            // 1. engine self-test (sections A..E + restore flow) on MockRegistry
            try
            {
                var reg = new MockRegistry();
                var sink = new ListUi(log);
                var st = new SelfTest(reg, "Mock", reg.Clear, sink);
                int f = st.Run();
                fails += f;
            }
            catch (Exception ex) { fails++; log.Add("FAIL  engine selftest threw: " + ex.GetType().Name + ": " + ex.Message); log.Add(ex.StackTrace ?? ""); }

            // 2. AerialOS pure rules (same assertions as grobyq)
            try
            {
                string n;
                bool ok = AerialOs.MarkerOk("AerialOS", "6.1", out n) && AerialOs.MarkerOk("AerialOS", null, out n) &&
                    !AerialOs.MarkerOk("AerialOS", "5.9", out n) && !AerialOs.MarkerOk("Windows", null, out n) && !AerialOs.MarkerOk(null, null, out n) &&
                    !AerialOs.MarkerOk(6, null, out n) && !AerialOs.MarkerOk("AerialOS", "six", out n) &&
                    AerialOs.NameOk("AerialOS 6") && AerialOs.NameOk("Windows 11 AerialOS") && !AerialOs.NameOk("Aerial Photography LLC") && !AerialOs.NameOk("AerialOSX") &&
                    AerialOs.UrlOk("https://aerialos.vercel.app/") && AerialOs.UrlOk("https://dsc.gg/aerialos") && !AerialOs.UrlOk("https://example.com/aerial");
                log.Add((ok ? "OK    " : "FAIL  ") + "aerial rules");
                if (!ok) fails++;
                log.Add("      aerial: " + (AerialOs.Detected() ? "detected" : "not detected") + " (" + AerialOs.Checks().Count + " markers read)");
            }
            catch (Exception ex) { fails++; log.Add("FAIL  aerial rules: " + ex.Message); }

            // 3. JIT every method of the app + core assemblies (catches an obfuscator that emits invalid IL)
            try
            {
                int n = 0, bad = 0;
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                foreach (var asm in new[] { typeof(App).Assembly, typeof(Engine).Assembly })
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.ContainsGenericParameters) continue;
                        var methods = new List<MethodBase>();
                        methods.AddRange(type.GetMethods(all));
                        methods.AddRange(type.GetConstructors(all));
                        foreach (var m in methods)
                        {
                            if (m.IsAbstract || m.ContainsGenericParameters) continue;
                            if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0) continue;
                            var impl = m.MethodImplementationFlags;
                            if ((impl & MethodImplAttributes.Runtime) != 0 || (impl & MethodImplAttributes.InternalCall) != 0) continue;
                            try { RuntimeHelpers.PrepareMethod(m.MethodHandle); n++; }
                            catch (Exception ex) { bad++; if (bad <= 5) log.Add("      jit " + type.FullName + "." + m.Name + ": " + ex.GetType().Name + ": " + ex.Message); }
                        }
                    }
                log.Add((bad == 0 ? "OK    " : "FAIL  ") + "jit: " + n + " methods compiled, " + bad + " failed");
                if (bad != 0) fails++;
            }
            catch (Exception ex) { fails++; log.Add("FAIL  jit pass: " + ex.Message); }

            log.Add(fails == 0 ? "PASS" : "FAILED: " + fails);
            try { File.WriteAllLines(Path.Combine(Path.GetTempPath(), "grobPC-NetTune-selftest.txt"), log); } catch { }
            return fails == 0 ? 0 : 3;
        }

        // IUi that appends to a plain string list (headless selftest / CI log).
        private sealed class ListUi : IUi
        {
            private readonly List<string> _log;
            public ListUi(List<string> log) { _log = log; }
            public void Say(string text, Tone tone) { _log.Add(text ?? ""); }
            public void Table(string[] headers, List<string[]> rows, int[] maxWidths) { foreach (string l in TableText.Render(headers, rows, maxWidths)) _log.Add(l); }
            public string Answer(Question q) { return ""; }
        }
    }
}
