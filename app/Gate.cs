using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GrobPC.NetTune.App
{
    // Shown instead of the app on a PC that is not AerialOS. NetTune is AerialOS-only: there is no license override.
    internal static class AerialOnlyWindow
    {
        public static void ShowGate()
        {
            var w = Build();
            try { w.ShowDialog(); } catch { }
        }

        private static Window Build()
        {
            var w = new Window
            {
                Title = Brand.Name + " - AerialOS", Width = 560, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = true,
                Background = Ui.B("Bg"), Foreground = Ui.B("Fg"), FontFamily = new FontFamily("Segoe UI"), UseLayoutRounding = true,
            };
            TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
            WinChrome.Dark(w);

            var dock = new DockPanel { LastChildFill = true };
            var head = new Border { Background = Ui.B("Card"), BorderBrush = Ui.B("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Height = 70 };
            var hg = new Grid { Margin = new Thickness(18, 0, 18, 0) };
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var glyph = Ui.Picture("aerialos_40.png", 40);
            glyph.VerticalAlignment = VerticalAlignment.Center; glyph.Margin = new Thickness(0, 0, 12, 0);
            hg.Children.Add(glyph);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(Ui.Bold(Brand.Name, 15, "Fg"));
            var sub = Ui.Bold("AERIALOS EDITION", 7.5, "Acc"); sub.Margin = new Thickness(1, 1, 0, 0);
            names.Children.Add(sub);
            Grid.SetColumn(names, 1); hg.Children.Add(names);
            head.Child = hg;
            DockPanel.SetDock(head, Dock.Top);
            dock.Children.Add(head);

            var body = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
            body.Children.Add(Ui.Bold("for AerialOS users only", 15, "Fg"));
            var t = Ui.Text(Brand.Name + " runs only on AerialOS - the gaming Windows build by ZERO_WAKE & mitzu LLC. This PC is not running AerialOS.\n" +
                            "Install AerialOS to use it. Questions: the AerialOS Discord.", 9.5, "Dim");
            t.Margin = new Thickness(1, 8, 0, 0);
            body.Children.Add(t);

            var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(1, 12, 0, 0) };
            links.Children.Add(Link("aerialos.vercel.app", Brand.AerialUrl));
            links.Children.Add(Link("dsc.gg/aerialos", Brand.AerialDiscord));
            body.Children.Add(links);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
            var close = Ui.Btn("Close", "BtnAccent", 110); close.Height = 34;
            close.Click += (s, e) => w.Close();
            row.Children.Add(close);
            var why = Ui.Btn("Why?", "BtnGhost", 90); why.Height = 34; why.Margin = new Thickness(10, 0, 0, 0);
            why.Click += (s, e) => Dlg.Info(w, AerialOs.Report());
            row.Children.Add(why);
            body.Children.Add(row);
            dock.Children.Add(body);
            w.Content = dock;

            w.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; w.Close(); } };
            w.Loaded += (s, e) => close.Focus();
            return w;
        }

        private static TextBlock Link(string text, string url)
        {
            var t = Ui.Text(text, 9, "Acc", false);
            t.Cursor = Cursors.Hand; t.Margin = new Thickness(0, 0, 18, 0); t.VerticalAlignment = VerticalAlignment.Center;
            t.MouseEnter += (s, e) => t.TextDecorations = TextDecorations.Underline;
            t.MouseLeave += (s, e) => t.TextDecorations = null;
            t.MouseLeftButtonUp += (s, e) => Ui.Open(url);
            return t;
        }
    }
}
