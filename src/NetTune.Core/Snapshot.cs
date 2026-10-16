using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GrobPC.NetTune
{
    public sealed class SnapItem
    {
        public string Scope, Path, Name;
        public SnapItem(string scope, string path, string name) { Scope = scope; Path = path; Name = name; }
    }

    public sealed class RestoreAction
    {
        public string Op, Path, Name, Now, Will;      // Op: set | delete
        public RegValue Value;                        // for set
    }

    // ----------------------------------------------------------------------------------------------------------------
    //  Snapshot / restore. Restore is exact: kind + data are put back and values that did not exist are deleted
    //  ("reg import" cannot do the second part). Same JSON as the PowerShell tool, so its backups restore here too:
    //  { tool, version, tag, created, computer, values: [ {scope, path, name, existed, kind, data} ], adapter, mtu, rss }
    // ----------------------------------------------------------------------------------------------------------------
    public sealed class Snapshot
    {
        public const string Tool = "grobPC-NetTune";
        public JsonObject Root;

        public static Snapshot New(IRegistry reg, IEnumerable<SnapItem> items, string tag, string version, string computer, DateTime now)
        {
            var vals = new JsonArray();
            foreach (var it in items)
            {
                var cur = reg.GetValue(it.Path, it.Name);
                var o = new JsonObject
                {
                    ["scope"] = it.Scope, ["path"] = RegPath.Normal(it.Path), ["name"] = it.Name,
                    ["existed"] = cur != null, ["kind"] = cur == null ? "" : cur.Kind,
                };
                if (cur != null && cur.Kind == RegKind.MultiString) o["data"] = new JsonArray(cur.Lines.Select(l => (JsonNode)JsonValue.Create(l)).ToArray());
                else o["data"] = cur == null ? "" : cur.Data;
                vals.Add(o);
            }
            var root = new JsonObject
            {
                ["tool"] = Tool, ["version"] = version, ["tag"] = tag,
                ["created"] = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ["computer"] = computer ?? "", ["values"] = vals,
            };
            return new Snapshot { Root = root };
        }

        public string Save(string dir)
        {
            string file = System.IO.Path.Combine(dir, "snapshot.json");
            File.WriteAllText(file, Root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            return file;
        }

        public static Snapshot Parse(string json, string sourceName)
        {
            JsonNode n;
            try { n = JsonNode.Parse(json); } catch (Exception ex) { throw new InvalidDataException("Not a grobPC-NetTune snapshot: " + sourceName + " (" + ex.Message + ")"); }
            var o = n as JsonObject;
            if (o == null || Str(o["tool"]) != Tool) throw new InvalidDataException("Not a grobPC-NetTune snapshot: " + sourceName);
            return new Snapshot { Root = o };
        }

        public static Snapshot Read(string file) { return Parse(File.ReadAllText(file), file); }

        public JsonObject Obj(string name) { return Root[name] as JsonObject; }

        // ---- JSON helpers (tolerant of the Windows PowerShell 5.1 forms) ----
        public static string Str(JsonNode n)
        {
            if (n == null) return null;
            var v = n as JsonValue;
            if (v == null) return n.ToJsonString();
            if (v.TryGetValue(out string s)) return s;
            if (v.TryGetValue(out bool b)) return b ? "True" : "False";
            return v.ToJsonString().Trim('"');
        }

        public static bool Bool(JsonNode n)
        {
            var v = n as JsonValue;
            if (v == null) return false;
            if (v.TryGetValue(out bool b)) return b;
            string s = Str(n);
            return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        }

        // Get-JsonArray: an array, a single item, or the { "value": [...], "Count": n } wrapper of PowerShell 5.1
        public static List<JsonNode> Arr(JsonNode n)
        {
            var list = new List<JsonNode>();
            if (n == null) return list;
            var o = n as JsonObject;
            if (o != null && o.ContainsKey("Count") && o.ContainsKey("value")) n = o["value"];
            var a = n as JsonArray;
            if (a != null) list.AddRange(a);
            else if (n != null) list.Add(n);
            return list;
        }

        public static int Int(JsonNode n, int fallback)
        {
            int i;
            return int.TryParse(Str(n), NumberStyles.Integer, CultureInfo.InvariantCulture, out i) ? i : fallback;
        }

        // Get-RestoreActions: compares the snapshot with the registry now. classPathNow = current driver key of the
        // adapter (its 000X index can change after a driver reinstall); "" = not resolved: class values are left alone.
        // The key comes from the scope, never from the path text in the file.
        public List<RestoreAction> GetRestoreActions(IRegistry reg, RegRoots roots, string classPathNow)
        {
            var acts = new List<RestoreAction>();
            foreach (var node in Arr(Root["values"]))
            {
                var v = node as JsonObject;
                if (v == null) continue;
                string name = Str(v["name"]), scope = Str(v["scope"]);
                if (string.IsNullOrEmpty(name)) continue;
                string path;
                if (scope == Profiles.Class) { if (string.IsNullOrEmpty(classPathNow)) continue; path = RegPath.Normal(classPathNow); }
                else if (scope == Profiles.Ndis) path = RegPath.Normal(roots.NdisKey);
                else continue;
                var cur = reg.GetValue(path, name);
                if (Bool(v["existed"]))
                {
                    string kind = Str(v["kind"]) ?? "";
                    RegValue want = kind == RegKind.MultiString
                        ? RegValue.Multi(Arr(v["data"]).Where(x => x != null).Select(Str).ToArray())
                        : new RegValue(kind, Str(v["data"]) ?? "");
                    if (RegValue.Equal(cur, want)) continue;
                    acts.Add(new RestoreAction { Op = "set", Path = path, Name = name, Value = want, Now = RegValue.Format(cur), Will = RegValue.Format(want) });
                }
                else
                {
                    if (cur == null) continue;
                    acts.Add(new RestoreAction { Op = "delete", Path = path, Name = name, Now = RegValue.Format(cur), Will = "(absent)" });
                }
            }
            return acts;
        }
    }
}
