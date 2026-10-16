using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GrobPC.NetTune.App
{
    // "For AerialOS users only": grobPC NetTune runs ONLY on AerialOS. Unlike grobyq there is no "anyos" license
    // override - this tool is bound to the AerialOS build (ZERO_WAKE & mitzu LLC). Same detection rules as grobyq 3.6.3+:
    //  - the marker key HKLM\SOFTWARE\AerialOS counts only with Product = "AerialOS" (REG_SZ) and, when present, a
    //    Version >= 6.0; read from the 64-bit view, then WOW6432Node;
    //  - OEM / registration strings count only as the exact word "AerialOS" (not the substring "aerial"), the support
    //    URL only with the AerialOS site.
    // Any offline marker can be copied by hand - this is a distribution gate, not a security boundary.
    public static class AerialOs
    {
        public const int MinMajor = 6;
        public sealed class Check { public string Where = "", Value = "", Note = ""; public bool Hit; }

        private static readonly Regex Word = new Regex(@"(?<![A-Za-z0-9])AerialOS(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly object _gate = new object();
        private static bool? _cached;

        public static bool Detected()
        {
            lock (_gate)
            {
                if (!_cached.HasValue)
                {
                    bool hit = false;
                    foreach (var c in Checks()) if (c.Hit) { hit = true; break; }
                    _cached = hit;
                }
                return _cached.Value;
            }
        }

        // ---- pure rules (the selftest calls these) ----
        public static bool MarkerOk(object product, object version, out string note)
        {
            note = "";
            var ps = product as string;
            if (ps == null) { note = product == null ? "no Product value" : "Product is not a text value"; return false; }
            if (!string.Equals(ps.Trim(), "AerialOS", StringComparison.OrdinalIgnoreCase)) { note = "Product must be AerialOS"; return false; }
            if (version == null) return true;
            var vs = version as string;
            Version v;
            if (vs == null || !Version.TryParse(vs.Trim(), out v)) { note = "Version is not a version number"; return false; }
            if (v.Major < MinMajor) { note = "AerialOS {0}.0 or newer is required"; return false; }
            return true;
        }

        public static bool NameOk(string v) { return !string.IsNullOrEmpty(v) && Word.IsMatch(v); }

        public static bool UrlOk(string v)
        {
            return !string.IsNullOrEmpty(v) && (v.IndexOf("aerialos.vercel.app", StringComparison.OrdinalIgnoreCase) >= 0 || v.IndexOf("dsc.gg/aerialos", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static List<Check> Checks()
        {
            var res = new List<Check>();
            var k = new Check { Where = @"HKLM\SOFTWARE\AerialOS" };
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var a = hk.OpenSubKey(@"SOFTWARE\AerialOS", false))
                    {
                        if (a == null) continue;
                        object prod = a.GetValue("Product"), ver = a.GetValue("Version");
                        string note;
                        bool ok = MarkerOk(prod, ver, out note);
                        k.Value = Convert.ToString(prod ?? "") + (ver != null ? " " + Convert.ToString(ver) : "") + (view == RegistryView.Registry32 ? "  (WOW6432Node)" : "");
                        k.Note = note;
                        if (ok) { k.Hit = true; break; }
                    }
                }
                catch { }
            }
            res.Add(k);

            RegistryKey hklm = null;
            try { hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64); } catch { }
            if (hklm == null) return res;
            using (hklm)
            {
                Add(res, hklm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation", "Manufacturer", false);
                Add(res, hklm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation", "Model", false);
                Add(res, hklm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation", "SupportURL", true);
                Add(res, hklm, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "RegisteredOrganization", false);
                Add(res, hklm, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "RegisteredOwner", false);
                Add(res, hklm, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName", false);
            }
            return res;
        }

        private static void Add(List<Check> res, RegistryKey hklm, string path, string name, bool url)
        {
            var c = new Check { Where = @"HKLM\" + path + @"\" + name };
            try { using (var k = hklm.OpenSubKey(path, false)) if (k != null) c.Value = Convert.ToString(k.GetValue(name) ?? ""); }
            catch { }
            c.Hit = url ? UrlOk(c.Value) : NameOk(c.Value);
            res.Add(c);
        }

        // grobPC-NetTune.exe /aerial : what the check sees on this PC, and what makes it pass.
        public static string Report()
        {
            bool det = Detected();
            var lines = new List<string>();
            lines.Add(det ? "AerialOS detected." : "AerialOS not detected.");
            lines.Add("");
            foreach (var c in Checks())
                lines.Add((c.Hit ? "[+]  " : " ·   ") + c.Where + " = " + (c.Value.Length == 0 ? "—" : c.Value) + (c.Note.Length > 0 ? "   (" + string.Format(c.Note, MinMajor) + ")" : ""));
            if (!det)
            {
                lines.Add("");
                lines.Add(string.Format("Needed: HKLM\\SOFTWARE\\AerialOS with Product = AerialOS and Version {0}.0 or newer (AerialOS-marker.reg, run as administrator).", MinMajor));
            }
            return string.Join("\n", lines);
        }
    }
}
