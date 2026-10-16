using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GrobPC.NetTune.App
{
    // Small UI helpers (brushes, text, buttons) over the App.xaml resource dictionary - the grobyq idiom.
    public static class Ui
    {
        public static Brush B(string key) { return (Brush)Application.Current.Resources[key]; }
        public static Style S(string key) { return (Style)Application.Current.Resources[key]; }
        public static double Pt(double pt) { return Math.Round(pt * 96.0 / 72.0, 2); }

        public static TextBlock Text(string text, double pt, string brush = "Fg", bool wrap = true)
        {
            var t = new TextBlock { Text = text ?? "", FontSize = Pt(pt), Foreground = B(brush) };
            t.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
            if (!wrap) t.TextTrimming = TextTrimming.CharacterEllipsis;
            return t;
        }
        public static TextBlock Semi(string text, double pt, string brush = "Fg", bool wrap = false) { var t = Text(text, pt, brush, wrap); t.FontWeight = FontWeights.SemiBold; return t; }
        public static TextBlock Bold(string text, double pt, string brush = "Fg", bool wrap = false) { var t = Text(text, pt, brush, wrap); t.FontWeight = FontWeights.Bold; return t; }

        public static Button Btn(string text, string style = "BtnNormal", double minWidth = 0)
        {
            var b = new Button { Content = text, Style = S(style) };
            if (minWidth > 0) b.MinWidth = minWidth;
            return b;
        }

        private static readonly Dictionary<string, BitmapImage> _img = new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);
        public static BitmapImage Img(string name)
        {
            BitmapImage b;
            if (_img.TryGetValue(name, out b)) return b;
            try
            {
                b = new BitmapImage();
                b.BeginInit();
                b.UriSource = new Uri("pack://application:,,,/Assets/" + name, UriKind.Absolute);
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.EndInit();
                b.Freeze();
            }
            catch { b = null; }
            _img[name] = b;
            return b;
        }

        public static Image Picture(string name, double height)
        {
            var i = new Image { Source = Img(name), Height = height, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality);
            return i;
        }

        public static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
        }

        public static Brush ToneBrush(Tone t)
        {
            switch (t)
            {
                case Tone.Good: return B("Ok");
                case Tone.Warn: return B("Warn");
                case Tone.Bad: return B("Bad");
                case Tone.Info: return B("Acc");
                case Tone.Title: return B("Fg");
                case Tone.Dim: return B("Dim");
            }
            return B("Fg");
        }
    }

    // Native dark chrome (Win10 1809+ / Win11): dark caption, matching border. No-op on older Windows.
    public static class WinChrome
    {
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        public static void Dark(Window w)
        {
            if (w == null) return;
            var h = new WindowInteropHelper(w).Handle;
            if (h != IntPtr.Zero) { Apply(h); return; }
            EventHandler apply = null;
            apply = (s, e) => { w.SourceInitialized -= apply; Apply(new WindowInteropHelper(w).Handle); };
            w.SourceInitialized += apply;
        }

        public static void Apply(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
                int cap = 0x00060606; DwmSetWindowAttribute(hwnd, 35, ref cap, 4);
                int txt = 0x00EDEDED; DwmSetWindowAttribute(hwnd, 36, ref txt, 4);
                int brd = 0x00221C1C; DwmSetWindowAttribute(hwnd, 34, ref brd, 4);
            }
            catch { }
        }
    }

    // Dark message box (Info / Ask), the grobyq Dlg idiom.
    public static class Dlg
    {
        public static void Info(Window owner, string text) { Show(owner, text, false, "OK"); }
        public static bool Ask(Window owner, string text, bool danger = false) { return Show(owner, text, danger, "Yes", "No") == 0; }

        public static int Show(Window owner, string text, bool danger, params string[] buttons)
        {
            var w = new Window
            {
                Title = Brand.Name,
                Width = 560, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = owner == null,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Background = Ui.B("Bg"), Foreground = Ui.B("Fg"), FontFamily = new FontFamily("Segoe UI"), UseLayoutRounding = true,
            };
            TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
            if (owner != null) w.Owner = owner;
            WinChrome.Dark(w);
            var dock = new DockPanel();
            var bar = new Border { Height = 4, Background = Ui.B(danger ? "Bad" : "Acc") };
            DockPanel.SetDock(bar, Dock.Top);
            dock.Children.Add(bar);
            var body = new StackPanel { Margin = new Thickness(22) };
            body.Children.Add(Ui.Text(text ?? "", 10, "Fg"));
            var strip = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            int result = -1; Button first = null;
            for (int i = 0; i < buttons.Length; i++)
            {
                int idx = i;
                var b = Ui.Btn(buttons[i], i == 0 ? (danger ? "BtnDanger" : "BtnAccent") : "BtnNormal", 96);
                b.Height = 34; b.Margin = new Thickness(i == 0 ? 0 : 10, 0, 0, 0);
                b.Click += (s, e) => { result = idx; w.Close(); };
                if (i == 0) first = b;
                strip.Children.Add(b);
            }
            body.Children.Add(strip);
            dock.Children.Add(body);
            w.Content = dock;
            w.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { result = buttons.Length - 1; e.Handled = true; w.Close(); }
                else if (e.Key == Key.Enter) { result = 0; e.Handled = true; w.Close(); }
            };
            w.Loaded += (s, e) => { if (first != null) first.Focus(); };
            try { w.ShowDialog(); } catch { }
            return result;
        }
    }
}
