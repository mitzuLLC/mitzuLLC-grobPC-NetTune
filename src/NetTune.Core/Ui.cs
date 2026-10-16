using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GrobPC.NetTune
{
    public enum Tone { Normal, Title, Dim, Good, Warn, Bad, Info }

    public sealed class Choice
    {
        public readonly string Key, Label;
        public Choice(string key, string label) { Key = key; Label = label; }
    }

    // One question of the engine. Options == null: free text (a number). YesNo: a [y/N] question.
    // Text is the console / log form of the question (the selftest compares prompts by it).
    public sealed class Question
    {
        public string Prompt = "", Default = "";
        public Choice[] Options;
        public bool YesNo, DefaultYes;

        public string Text
        {
            get
            {
                if (YesNo) return Prompt + (DefaultYes ? " [Y/n]" : " [y/N]");
                if (Options == null || Options.Length == 0) return Prompt;
                return Prompt + ": " + string.Join("  ", Options.Select(o => "[" + o.Key + "] " + o.Label));
            }
        }
    }

    // Front end of the engine: the WPF window, or the capture used by the tests. Answer() returns the raw answer;
    // the engine trims it and applies the default ("" = Enter).
    public interface IUi
    {
        void Say(string text, Tone tone);
        void Table(string[] headers, List<string[]> rows, int[] maxWidths);
        string Answer(Question q);
    }

    public static class Status
    {
        public const string Pass = "PASS", Warn = "WARN", Fail = "FAIL", Info = "INFO", Skip = "SKIP";

        public static Tone ToneOf(string status)
        {
            switch (status)
            {
                case Pass: return Tone.Good;
                case Warn: return Tone.Warn;
                case Fail: return Tone.Bad;
                case Info: return Tone.Info;
                case Skip: return Tone.Dim;
            }
            return Tone.Normal;
        }

        public static string Line(string status, string text) { return string.Format(" [{0,-4}] {1}", status, text); }
    }

    // Text rendering of a table (log, report, capture) - same layout as Say-Table of the PowerShell tool.
    public static class TableText
    {
        private static string Pad(string t, int w)
        {
            t = t ?? "";
            if (t.Length > w) return w <= 2 ? t.Substring(0, w) : t.Substring(0, w - 2) + "..";
            return t.PadRight(w);
        }

        public static List<string> Render(string[] headers, List<string[]> rows, int[] maxWidths)
        {
            int n = headers.Length;
            var w = new int[n];
            for (int i = 0; i < n; i++) w[i] = (headers[i] ?? "").Length;
            foreach (var r in rows)
                for (int i = 0; i < n; i++)
                {
                    int len = i < r.Length && r[i] != null ? r[i].Length : 0;
                    if (len > w[i]) w[i] = len;
                }
            if (maxWidths != null)
                for (int i = 0; i < n && i < maxWidths.Length; i++) if (maxWidths[i] > 0 && w[i] > maxWidths[i]) w[i] = maxWidths[i];
            var lines = new List<string>();
            var sb = new StringBuilder("  ");
            for (int i = 0; i < n; i++) sb.Append(Pad(headers[i], w[i])).Append("  ");
            lines.Add(sb.ToString().TrimEnd());
            sb.Clear().Append("  ");
            for (int i = 0; i < n; i++) sb.Append(new string('-', w[i])).Append("  ");
            lines.Add(sb.ToString().TrimEnd());
            foreach (var r in rows)
            {
                sb.Clear().Append("  ");
                for (int i = 0; i < n; i++) sb.Append(Pad(i < r.Length ? r[i] : "", w[i])).Append("  ");
                lines.Add(sb.ToString().TrimEnd());
            }
            return lines;
        }
    }

    // Collects everything the engine prints and answers questions from a queue (tests, /selftest, state.txt).
    public sealed class CaptureUi : IUi
    {
        public readonly List<string> Lines = new List<string>();
        public readonly Queue<string> Answers = new Queue<string>();
        public int AskCount;
        public string LastPrompt = "";
        public IUi Echo;                                      // optional: also show it there

        public void Say(string text, Tone tone) { Lines.Add(text); if (Echo != null) Echo.Say(text, tone); }

        public void Table(string[] headers, List<string[]> rows, int[] maxWidths)
        {
            foreach (string l in TableText.Render(headers, rows, maxWidths)) Lines.Add(l);
            if (Echo != null) Echo.Table(headers, rows, maxWidths);
        }

        public string Answer(Question q)
        {
            AskCount++;
            LastPrompt = q.Text;
            return Answers.Count > 0 ? Answers.Dequeue() : "";
        }

        public void SetAnswers(params string[] answers) { Answers.Clear(); foreach (string a in answers) Answers.Enqueue(a); }
        public string Text { get { return string.Join("\n", Lines); } }
        public void Clear() { Lines.Clear(); }
    }
}
