using System;
using System.Reflection;

namespace GrobPC.NetTune.App
{
    // Brand identity (names, links). Single source of truth for the app shell.
    public static class Brand
    {
        public const string Name = "grobPC NetTune";
        public const string Short = "NetTune";
        public const string Tagline = "One RSS queue, one CPU - network DPC where you put it";
        public const string Company = "mitzu LLC";
        public const string Credit = "by mitzu LLC  ·  grobPC";
        public const string XUrl = "https://x.com/grobPC";
        public const string SiteUrl = "https://grobpc.com";
        public const string Mutex = "Global\\grobPC-NetTune-mitzu";

        // Distribution gate: NetTune runs only on AerialOS (no "anyos" override - see Win/AerialGate.cs).
        public const string AerialUrl = "https://aerialos.vercel.app";
        public const string AerialDiscord = "https://dsc.gg/aerialos";

        public static string Version
        {
            get { try { return Assembly.GetExecutingAssembly().GetName().Version.ToString(3); } catch { return Engine.Version; } }
        }

        // Numbering mirrors grobyq: 2.0.0 ... then alpha -> beta -> release.
        public const string Stage = "alpha";
        public static string VersionText { get { return Version + (Stage.Length > 0 ? " " + Stage : ""); } }
    }
}
