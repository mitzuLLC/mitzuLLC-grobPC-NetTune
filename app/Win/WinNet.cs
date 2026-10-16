using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace GrobPC.NetTune.App
{
    // INetSystem on Windows: the NetAdapter cmdlets through Windows PowerShell 5.1 (always present), netsh for the MTU,
    // reg.exe for the export. Every cmdlet runs in its own powershell.exe with -EncodedCommand (no quoting pitfalls),
    // objects come back as compact JSON. Text results follow the interface: "" = ok, otherwise the error line.
    public sealed class WinNet : INetSystem
    {
        public Action<string> Log;

        // ---- PowerShell runner ----
        private sealed class PsResult { public int Code; public string Out = "", Err = ""; }

        private static string Lit(string s) { return "'" + (s ?? "").Replace("'", "''") + "'"; }

        private PsResult Ps(string script)
        {
            var r = new PsResult();
            try
            {
                string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + enc,
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                };
                using (var p = Process.Start(psi))
                {
                    r.Out = p.StandardOutput.ReadToEnd();
                    r.Err = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    r.Code = p.ExitCode;
                }
            }
            catch (Exception ex) { r.Code = -1; r.Err = ex.Message; }
            return r;
        }

        // first non-empty error line (PowerShell error records are multi-line)
        private static string Err1(PsResult r)
        {
            string e = (r.Err ?? "").Trim();
            if (e.Length == 0) return r.Code != 0 ? "exit code " + r.Code : "";
            foreach (string l in e.Split('\n')) { string t = l.Trim(); if (t.Length > 0) return t; }
            return e;
        }

        // a mutating cmdlet: "" on success, the error line otherwise
        private string Run(string label, string cmd)
        {
            var r = Ps("try { " + cmd + "; exit 0 } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }");
            string err = r.Code == 0 ? "" : Err1(r);
            if (Log != null) Log(label + " -> " + (err.Length == 0 ? "ok" : err));
            return err;
        }

        private static List<JsonObject> Objects(string json)
        {
            var list = new List<JsonObject>();
            string s = (json ?? "").Trim();
            if (s.Length == 0) return list;
            JsonNode n;
            try { n = JsonNode.Parse(s); } catch { return list; }
            var arr = n as JsonArray;
            if (arr != null) { foreach (var e in arr) if (e is JsonObject o) list.Add(o); }
            else if (n is JsonObject one) list.Add(one);
            return list;
        }

        private static string Str(JsonObject o, string name)
        {
            if (o == null) return "";
            JsonNode v; if (!o.TryGetPropertyValue(name, out v) || v == null) return "";
            var jv = v as JsonValue;
            if (jv != null && jv.TryGetValue(out string sv)) return sv;
            if (jv != null && jv.TryGetValue(out bool bv)) return bv ? "True" : "False";
            return v.ToString();
        }

        private static string[] StrArr(JsonObject o, string name)
        {
            JsonNode v; if (o == null || !o.TryGetPropertyValue(name, out v) || v == null) return new string[0];
            var a = v as JsonArray;
            if (a == null) { var s = v.ToString(); return s.Length > 0 ? new[] { s } : new string[0]; }
            return a.Where(x => x != null).Select(x => { var jv = x as JsonValue; string sv; return jv != null && jv.TryGetValue(out sv) ? sv : x.ToString(); }).ToArray();
        }

        // ---- adapters ----
        public AdapterRecord[] GetAdapters()
        {
            var r = Ps(@"Get-NetAdapter -Physical -ErrorAction Stop | ForEach-Object { [pscustomobject]@{
  Alias = '' + $_.Name; Description = '' + $_.InterfaceDescription; Status = '' + $_.Status; LinkSpeed = '' + $_.LinkSpeed;
  MediaText = '' + $_.PhysicalMediaType; Medium = $(if ($null -ne $_.NdisPhysicalMedium) { [int]$_.NdisPhysicalMedium } else { -1 });
  Guid = '' + $_.InterfaceGuid; PnpId = '' + $_.PnPDeviceID; DriverProvider = '' + $_.DriverProvider;
  DriverVersion = '' + $_.DriverVersionString; IfIndex = [int]$_.ifIndex } } | ConvertTo-Json -Compress -Depth 3");
            if (r.Code != 0 && Objects(r.Out).Count == 0) throw new InvalidOperationException(Err1(r));
            var list = new List<AdapterRecord>();
            foreach (var o in Objects(r.Out))
                list.Add(new AdapterRecord
                {
                    Alias = Str(o, "Alias"), Description = Str(o, "Description"), Status = Str(o, "Status"), LinkSpeed = Str(o, "LinkSpeed"),
                    MediaText = Str(o, "MediaText"), Medium = ParseInt(Str(o, "Medium"), -1), Guid = Str(o, "Guid"), PnpId = Str(o, "PnpId"),
                    DriverProvider = Str(o, "DriverProvider"), DriverVersion = Str(o, "DriverVersion"), IfIndex = ParseInt(Str(o, "IfIndex"), -1),
                });
            return list.ToArray();
        }

        public RssRecord GetRss(string alias)
        {
            var r = Ps(@"try { $r = Get-NetAdapterRss -Name " + Lit(alias) + @" -ErrorAction Stop
  $tbl = @(); foreach ($e in $r.IndirectionTable) { $tbl += ('' + $e.ProcessorGroup + ':' + $e.ProcessorNumber) }
  $pa = @(); foreach ($e in $r.RssProcessorArray) { $pa += ('' + $e.ProcessorGroup + ':' + $e.ProcessorNumber) }
  [pscustomobject]@{ Enabled = [bool]$r.Enabled; Profile = '' + $r.Profile; BaseGroup = '' + $r.BaseProcessorGroup; BaseNumber = '' + $r.BaseProcessorNumber;
    MaxGroup = '' + $r.MaxProcessorGroup; MaxNumber = '' + $r.MaxProcessorNumber; MaxProcessors = '' + $r.MaxProcessors; Queues = '' + $r.NumberOfReceiveQueues;
    Table = $tbl; ProcArray = $pa } | ConvertTo-Json -Compress -Depth 4 } catch { }");
            var o = Objects(r.Out).FirstOrDefault();
            if (o == null) return null;
            return new RssRecord
            {
                Enabled = Str(o, "Enabled").Equals("True", StringComparison.OrdinalIgnoreCase) || Str(o, "Enabled") == "1",
                Profile = Str(o, "Profile"), BaseGroup = Str(o, "BaseGroup"), BaseNumber = Str(o, "BaseNumber"),
                MaxGroup = Str(o, "MaxGroup"), MaxNumber = Str(o, "MaxNumber"), MaxProcessors = Str(o, "MaxProcessors"), Queues = Str(o, "Queues"),
                Table = StrArr(o, "Table"), ProcArray = StrArr(o, "ProcArray"),
            };
        }

        public string EnableRss(string alias)
        {
            return Run("Enable-NetAdapterRss " + alias, "Enable-NetAdapterRss -Name " + Lit(alias) + " -NoRestart -ErrorAction Stop");
        }

        public string SetRss(string alias, RssParams p)
        {
            var sb = new StringBuilder("Set-NetAdapterRss -Name " + Lit(alias));
            sb.Append(" -BaseProcessorNumber ").Append(p.BaseProcessorNumber.ToString(CultureInfo.InvariantCulture));
            sb.Append(" -MaxProcessorNumber ").Append(p.MaxProcessorNumber.ToString(CultureInfo.InvariantCulture));
            sb.Append(" -MaxProcessors ").Append(p.MaxProcessors.ToString(CultureInfo.InvariantCulture));
            if (p.NumberOfReceiveQueues.HasValue) sb.Append(" -NumberOfReceiveQueues ").Append(p.NumberOfReceiveQueues.Value.ToString(CultureInfo.InvariantCulture));
            if (p.BaseProcessorGroup.HasValue) sb.Append(" -BaseProcessorGroup ").Append(p.BaseProcessorGroup.Value.ToString(CultureInfo.InvariantCulture));
            if (p.MaxProcessorGroup.HasValue) sb.Append(" -MaxProcessorGroup ").Append(p.MaxProcessorGroup.Value.ToString(CultureInfo.InvariantCulture));
            sb.Append(" -Profile ").Append(p.Profile).Append(" -NoRestart -ErrorAction Stop");
            return Run("Set-NetAdapterRss " + alias, sb.ToString());
        }

        public AdvancedRecord[] GetAdvanced(string alias)
        {
            var r = Ps(@"try { Get-NetAdapterAdvancedProperty -Name " + Lit(alias) + @" -AllProperties -ErrorAction Stop | ForEach-Object { [pscustomobject]@{
  Keyword = '' + $_.RegistryKeyword; Value = ('' + ($_.RegistryValue -join ',')); Display = '' + $_.DisplayName; DisplayValue = '' + $_.DisplayValue;
  DataType = '' + $_.RegistryDataType } } | ConvertTo-Json -Compress -Depth 3 } catch { }");
            return Objects(r.Out).Select(o => new AdvancedRecord
            {
                Keyword = Str(o, "Keyword"), Value = Str(o, "Value"), Display = Str(o, "Display"), DisplayValue = Str(o, "DisplayValue"), DataType = Str(o, "DataType"),
            }).ToArray();
        }

        public string RestartAdapter(string alias)
        {
            return Run("Restart-NetAdapter " + alias, "Restart-NetAdapter -Name " + Lit(alias) + " -ErrorAction Stop");
        }

        public string GetAdapterStatus(string alias)
        {
            var r = Ps("'' + (Get-NetAdapter -Name " + Lit(alias) + " -ErrorAction SilentlyContinue).Status");
            return (r.Out ?? "").Trim();
        }

        public string GetGateway(int ifIndex)
        {
            var r = Ps("$g = Get-NetRoute -InterfaceIndex " + ifIndex.ToString(CultureInfo.InvariantCulture) +
                " -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object RouteMetric | Select-Object -First 1; if ($g) { '' + $g.NextHop }");
            return (r.Out ?? "").Trim();
        }

        public int GetNlMtu(int ifIndex, string family)
        {
            var r = Ps("'' + (Get-NetIPInterface -InterfaceIndex " + ifIndex.ToString(CultureInfo.InvariantCulture) +
                " -AddressFamily " + (family == "IPv6" ? "IPv6" : "IPv4") + " -ErrorAction SilentlyContinue | Select-Object -First 1).NlMtu");
            return ParseInt((r.Out ?? "").Trim(), 0);
        }

        public NativeResult SetMtu(string alias, int mtu, string family)
        {
            var res = new NativeResult();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = string.Format(CultureInfo.InvariantCulture, "interface {0} set subinterface \"{1}\" mtu={2} store=persistent",
                        family == "IPv6" ? "ipv6" : "ipv4", alias, mtu),
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    res.Code = p.ExitCode;
                    res.Text = ((o + " " + e).Trim());
                    if (res.Text.Length == 0) res.Text = res.Code == 0 ? "Ok." : "netsh exit " + res.Code;
                }
            }
            catch (Exception ex) { res.Code = -1; res.Text = ex.Message; }
            return res;
        }

        public int RegExport(string keyPath, string file)
        {
            try
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
                var psi = new ProcessStartInfo
                {
                    FileName = "reg.exe",
                    Arguments = "export \"" + RegPath.ToRegExe(keyPath) + "\" \"" + file + "\" /y",
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                };
                using (var p = Process.Start(psi)) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(); return p.ExitCode; }
            }
            catch { return -1; }
        }

        private static int ParseInt(string s, int fallback)
        {
            int i; return int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i) ? i : fallback;
        }
    }
}
