using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GrobPC.NetTune.App
{
    // IUi for the window. The engine runs flows on a worker thread; Say / Table marshal to the output pane, and Answer
    // blocks the worker while a modal question dialog runs on the UI thread (console semantics, GUI surface).
    public sealed class WpfUi : IUi
    {
        private readonly Dispatcher _disp;
        private readonly Action<string, Tone> _sink;     // append one monospace line on the UI thread
        public Window Owner;

        public WpfUi(Dispatcher disp, Action<string, Tone> sink) { _disp = disp; _sink = sink; }

        public void Say(string text, Tone tone)
        {
            if (_disp.CheckAccess()) _sink(text ?? "", tone);
            else _disp.Invoke((Action)(() => _sink(text ?? "", tone)));
        }

        public void Table(string[] headers, List<string[]> rows, int[] maxWidths)
        {
            foreach (string l in TableText.Render(headers, rows, maxWidths)) Say(l, Tone.Dim);
        }

        public string Answer(Question q)
        {
            if (_disp.CheckAccess()) return QuestionDialog.Show(Owner, q);
            return (string)_disp.Invoke((Func<string>)(() => QuestionDialog.Show(Owner, q)));
        }
    }

    // The engine's question as a dark modal: Y/N, a choice row, or a number box. Returns the raw answer
    // ("" = Enter / cancel = the engine's default; "y"/"n" for Y/N; the chosen key for a choice).
    public static class QuestionDialog
    {
        public static string Show(Window owner, Question q)
        {
            var w = new Window
            {
                Title = Brand.Short, Width = 560, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = owner == null, WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Background = Ui.B("Bg"), Foreground = Ui.B("Fg"), FontFamily = new FontFamily("Segoe UI"), UseLayoutRounding = true,
            };
            TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
            if (owner != null && owner.IsVisible) w.Owner = owner;
            WinChrome.Dark(w);

            var dock = new DockPanel();
            var top = new Border { Height = 4, Background = Ui.B("Acc") };
            DockPanel.SetDock(top, Dock.Top);
            dock.Children.Add(top);
            var body = new StackPanel { Margin = new Thickness(22, 20, 22, 20) };
            body.Children.Add(Ui.Semi(q.Prompt ?? "", 11, "Fg", true));

            string result = "";
            Action<string> close = v => { result = v; w.Close(); };

            TextBox box = null;
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };

            if (q.YesNo)
            {
                var yes = Btn(q.DefaultYes ? "BtnAccent" : "BtnNormal", "Yes", () => close("y"));
                var no = Btn(q.DefaultYes ? "BtnNormal" : "BtnAccent", "No", () => close("n"));
                row.Children.Add(yes); row.Children.Add(no);
            }
            else if (q.Options != null && q.Options.Length > 0)
            {
                foreach (var o in q.Options)
                {
                    var hint = Ui.Text(o.Label, 8.75, "Dim", false);
                    hint.Margin = new Thickness(0, 6, 0, 0);
                    var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                    var b = Btn(o.Key.Equals(q.Default, StringComparison.OrdinalIgnoreCase) ? "BtnAccent" : "BtnNormal", "[" + o.Key + "]", () => close(o.Key));
                    b.MinWidth = 54;
                    line.Children.Add(b);
                    hint.Margin = new Thickness(10, 7, 0, 0);
                    line.Children.Add(hint);
                    body.Children.Add(line);
                }
            }
            else
            {
                box = new TextBox { Style = Ui.S("Box"), Text = q.Default ?? "", Width = 160, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
                body.Children.Add(box);
                var ok = Btn("BtnAccent", "OK", () => close(box.Text ?? ""));
                row.Children.Add(ok);
            }

            var cancel = Btn("BtnGhost", "Cancel", () => close(""));
            row.Children.Add(cancel);
            body.Children.Add(row);
            dock.Children.Add(body);
            w.Content = dock;

            w.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { e.Handled = true; close(""); }
                else if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    if (q.YesNo) close(q.DefaultYes ? "y" : "n");
                    else if (box != null) close(box.Text ?? "");
                    else close(q.Default ?? "");
                }
            };
            w.Loaded += (s, e) => { if (box != null) { box.Focus(); box.SelectAll(); } };
            try { w.ShowDialog(); } catch { }
            return result;
        }

        private static Button Btn(string style, string text, Action onClick)
        {
            var b = Ui.Btn(text, style, 72);
            b.Height = 34; b.Margin = new Thickness(8, 0, 0, 0);
            b.Click += (s, e) => onClick();
            return b;
        }
    }
}
