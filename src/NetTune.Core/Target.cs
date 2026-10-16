using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GrobPC.NetTune
{
    public sealed class ClassCandidate { public string Index = "", Path = "", NetCfgInstanceId = "", DriverDesc = ""; }

    public sealed class ClassKeyResult
    {
        public bool Ok;
        public string Path = "", Index = "", Mode = "", Warn = "", Reason = "", KeyGuid = "", KeyDesc = "";
        public List<ClassCandidate> Candidates = new List<ClassCandidate>();
    }

    // The selected adapter + its resolved driver key.
    public sealed class TargetContext
    {
        public string Alias = "", Description = "", Status = "", LinkSpeed = "", Media = "", Guid = "", PnpId = "";
        public string DriverProvider = "", DriverVersion = "";
        public int IfIndex;
        public string ClassPath = "", ClassIndex = "", ClassMode = "", ClassWarn = "", ClassError = "", EnumDriver = "";
        public bool? EnumAgrees;
        public List<ClassCandidate> Candidates = new List<ClassCandidate>();
        public string[] NdiParams = new string[0];
        public bool RssSupported;
        public AdapterRecord Record;
    }

    public static class ClassKeys
    {
        // Resolve-ClassKey (R3): driver key Class\000X of one adapter.
        //   primary  : NetCfgInstanceId == InterfaceGuid (DriverDesc is compared and reported)
        //   fallback : DriverDesc == InterfaceDescription, only when exactly one key matches
        public static ClassKeyResult Resolve(IRegistry reg, string classRoot, string guid, string description)
        {
            var res = new ClassKeyResult();
            string want = (guid ?? "").Trim().Trim('{', '}');
            string desc = (description ?? "").Trim();
            string descPlain = Regex.Replace(desc, @"\s+#\d+$", "").Trim();
            var all = new List<ClassCandidate>();
            foreach (string s in reg.GetSubKeyNames(classRoot))
            {
                if (!Regex.IsMatch(s, @"^\d{4}$")) continue;
                string p = classRoot + "\\" + s;
                string g = "", d = "";
                var v = reg.GetValue(p, "NetCfgInstanceId");
                if (v != null) g = v.Data;
                v = reg.GetValue(p, "DriverDesc");
                if (v != null) d = v.Data;
                all.Add(new ClassCandidate { Index = s, Path = p, NetCfgInstanceId = g, DriverDesc = d });
            }
            Func<string, bool> descMatch = d => d.Equals(desc, StringComparison.OrdinalIgnoreCase) || d.Equals(descPlain, StringComparison.OrdinalIgnoreCase);
            var byGuid = all.Where(k => want.Length > 0 && k.NetCfgInstanceId.Trim().Trim('{', '}').Equals(want, StringComparison.OrdinalIgnoreCase)).ToList();
            var byDesc = all.Where(k => desc.Length > 0 && k.DriverDesc.Length > 0 && descMatch(k.DriverDesc)).ToList();
            if (byGuid.Count == 1)
            {
                var k = byGuid[0];
                res.Ok = true; res.Path = k.Path; res.Index = k.Index; res.Mode = "NetCfgInstanceId";
                res.KeyGuid = k.NetCfgInstanceId; res.KeyDesc = k.DriverDesc;
                if (!descMatch(k.DriverDesc)) res.Warn = string.Format("DriverDesc \"{0}\" differs from InterfaceDescription \"{1}\"", k.DriverDesc, desc);
                return res;
            }
            if (byGuid.Count > 1)
            {
                res.Reason = string.Format("NetCfgInstanceId matches {0} keys - ambiguous, nothing will be written", byGuid.Count);
                res.Candidates = byGuid;
                return res;
            }
            if (byDesc.Count == 1)
            {
                var k = byDesc[0];
                res.Ok = true; res.Path = k.Path; res.Index = k.Index; res.Mode = "DriverDesc";
                res.KeyGuid = k.NetCfgInstanceId; res.KeyDesc = k.DriverDesc;
                res.Warn = "NetCfgInstanceId not found: matched by DriverDesc (fallback)";
                return res;
            }
            if (byDesc.Count > 1)
            {
                res.Reason = string.Format("no NetCfgInstanceId match and DriverDesc matches {0} keys - ambiguous, nothing will be written", byDesc.Count);
                res.Candidates = byDesc;
                return res;
            }
            res.Reason = "no Class\\000X key matches this adapter (NetCfgInstanceId / DriverDesc) - nothing will be written";
            res.Candidates = all.Where(k => k.NetCfgInstanceId.Length > 0).ToList();
            return res;
        }

        // Get-NdiParams: keyword names the driver INF declares under Ndi\Params
        public static string[] NdiParams(IRegistry reg, string classPath)
        {
            if (string.IsNullOrEmpty(classPath)) return new string[0];
            return reg.GetSubKeyNames(classPath + "\\Ndi\\Params");
        }

        // New-TargetContext: resolve the driver key and cross-check it with Enum\<InstanceId>\Driver (R3 / R17:
        // never write into a key that may belong to another adapter)
        public static TargetContext NewContext(AdapterRecord rec, IRegistry reg, RegRoots roots, INetSystem net)
        {
            var ctx = new TargetContext
            {
                Record = rec, Alias = rec.Alias ?? "", Description = rec.Description ?? "", Status = rec.Status ?? "", LinkSpeed = rec.LinkSpeed ?? "",
                Media = NetUtil.MediaKind(rec), Guid = rec.Guid ?? "", PnpId = rec.PnpId ?? "", IfIndex = rec.IfIndex,
                DriverProvider = rec.DriverProvider ?? "", DriverVersion = rec.DriverVersion ?? "",
            };
            var rk = Resolve(reg, roots.ClassRoot, ctx.Guid, ctx.Description);
            if (rk.Ok)
            {
                ctx.ClassPath = rk.Path; ctx.ClassIndex = rk.Index; ctx.ClassMode = rk.Mode; ctx.ClassWarn = rk.Warn;
                ctx.NdiParams = NdiParams(reg, rk.Path);
            }
            else { ctx.ClassError = rk.Reason; ctx.Candidates = rk.Candidates; }
            if (!string.IsNullOrEmpty(ctx.PnpId))
            {
                var v = reg.GetValue(roots.EnumRoot + "\\" + ctx.PnpId, "Driver");
                if (v != null)
                {
                    ctx.EnumDriver = v.Data;
                    var m = Regex.Match(ctx.EnumDriver, @"\\(\d{4})$");
                    if (rk.Ok && m.Success) ctx.EnumAgrees = m.Groups[1].Value == rk.Index;
                }
            }
            if (rk.Ok)
            {
                string conflict = "";
                if (ctx.EnumAgrees == false)
                    conflict = string.Format("Enum\\..\\Driver = {0} does not agree with Class\\{1} (matched by {2})", ctx.EnumDriver, rk.Index, rk.Mode);
                else if (rk.Mode == "DriverDesc" && rk.KeyGuid.Length > 0 && ctx.EnumAgrees != true)
                    conflict = string.Format("Class\\{0} matches by DriverDesc only, holds the NetCfgInstanceId of another interface ({1}) and Enum\\..\\Driver does not confirm it", rk.Index, rk.KeyGuid);
                if (conflict.Length > 0)
                {
                    ctx.Candidates = new List<ClassCandidate> { new ClassCandidate { Index = rk.Index, NetCfgInstanceId = rk.KeyGuid, DriverDesc = rk.KeyDesc } };
                    ctx.ClassPath = ""; ctx.ClassIndex = ""; ctx.ClassMode = ""; ctx.ClassWarn = ""; ctx.NdiParams = new string[0];
                    ctx.ClassError = conflict + " - nothing will be written";
                }
            }
            ctx.RssSupported = net.GetRss(ctx.Alias) != null;
            return ctx;
        }
    }
}
