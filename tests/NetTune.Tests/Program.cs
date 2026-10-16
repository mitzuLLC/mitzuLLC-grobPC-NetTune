using System;
using System.Collections.Generic;
using GrobPC.NetTune;

namespace GrobPC.NetTune.Tests
{
    // Console runner of the self-test on the in-memory registry (Linux, CI). Exit code = number of failed checks.
    // -v also prints what the engine printed for the failed checks' context is in the FAIL detail.
    internal sealed class ConsoleUi : IUi
    {
        public void Say(string text, Tone tone)
        {
            var keep = Console.ForegroundColor;
            switch (tone)
            {
                case Tone.Good: Console.ForegroundColor = ConsoleColor.Green; break;
                case Tone.Bad: Console.ForegroundColor = ConsoleColor.Red; break;
                case Tone.Warn: Console.ForegroundColor = ConsoleColor.Yellow; break;
                case Tone.Dim: Console.ForegroundColor = ConsoleColor.DarkGray; break;
                case Tone.Title: Console.ForegroundColor = ConsoleColor.Cyan; break;
            }
            Console.WriteLine(text);
            Console.ForegroundColor = keep;
        }

        public void Table(string[] headers, List<string[]> rows, int[] maxWidths)
        {
            foreach (string l in TableText.Render(headers, rows, maxWidths)) Console.WriteLine(l);
        }

        public string Answer(Question q) { return ""; }
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            var reg = new MockRegistry();
            var t = new SelfTest(reg, "Mock", reg.Clear, new ConsoleUi());
            return t.Run();
        }
    }
}
