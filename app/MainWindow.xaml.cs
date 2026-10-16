using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace GrobPC.NetTune.App
{
    // The shell: a sidebar of actions (the .cmd menu as buttons) and a console-style output pane. Each action runs the
    // matching engine flow on a worker thread; the engine's Say / Table stream into the pane and its questions pop a
    // modal dialog. The engine is the single source of truth - this window only drives it and shows what it prints.
    public partial class MainWindow : Window
    {
        private Engine _eng;
        private WpfUi _ui;
        private StackPanel _out;
        private ScrollViewer _scroll;
        private TextBlock _header;
        private CheckBox _dry;
        private readonly List<Button> _actions = new List<Button>();
        private bool _busy;
        private readonly bool _startDry;

        public MainWindow(bool dryRun)
        {
            _startDry = dryRun;
            InitializeComponent();
            WinChrome.Dark(this);
            BuildUi();
            Loaded += (s, e) => Start();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            WinChrome.Apply(new WindowInteropHelper(this).Handle);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try { Application.Current.Shutdown(0); } catch { }
        }

        // ----------------------------------------------------------------- build
        private void BuildUi()
        {
            SideHost.Child = BuildSidebar();
            MainHost.Children.Add(BuildMain());
        }

        private FrameworkElement BuildSidebar()
        {
            var dock = new DockPanel { LastChildFill = true };

            var head = new Grid { Height = 64 };
            var glyph = Ui.Picture("aerialos_40.png", 30);
            glyph.HorizontalAlignment = HorizontalAlignment.Left; glyph.VerticalAlignment = VerticalAlignment.Center; glyph.Margin = new Thickness(18, 0, 0, 0);
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(58, 0, 0, 0) };
            name.Children.Add(Ui.Semi(Brand.Short, 13, "Fg"));
            name.Children.Add(Ui.Text("by mitzu LLC", 8, "Dim", false));
            head.Children.Add(glyph); head.Children.Add(name);
            DockPanel.SetDock(head, Dock.Top);
            dock.Children.Add(head);

            // footer: dry-run toggle
            var foot = new Border { BorderBrush = Ui.B("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 10, 12, 12) };
            var fg = new Grid();
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var dl = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            dl.Children.Add(Ui.Semi("Dry-run", 10, "Fg"));
            dl.Children.Add(Ui.Text("preview only, nothing is written", 8.5, "Dim", false));
            fg.Children.Add(dl);
            _dry = new CheckBox { Style = Ui.S("Switch"), IsChecked = _startDry, VerticalAlignment = VerticalAlignment.Center };
            _dry.Checked += (s, e) => { if (_eng != null) _eng.DryRun = true; RefreshHeader(); };
            _dry.Unchecked += (s, e) => { if (_eng != null) _eng.DryRun = false; RefreshHeader(); };
            Grid.SetColumn(_dry, 1);
            fg.Children.Add(_dry);
            foot.Child = fg;
            DockPanel.SetDock(foot, Dock.Bottom);
            dock.Children.Add(foot);

            var list = new StackPanel { Margin = new Thickness(10, 6, 10, 0) };
            list.Children.Add(SideLabel("DETECT", true));
            list.Children.Add(Action("1  Detect", "Re-read CPU, adapters and the selected driver key", () => _eng.Detect()));
            list.Children.Add(Action("2  Select adapter", "Pick which physical adapter to tune", () => _eng.InvokeSelectAdapter()));
            list.Children.Add(Action("3  Choose RSS CPU", "The logical processor the queue lands on", () => _eng.ChooseCpu()));
            list.Children.Add(SideLabel("APPLY", false));
            list.Children.Add(Action("4  Backup", "Snapshot every value this tool can change", () => _eng.NewBackup("manual")));
            list.Children.Add(Action("5  Registry profile: LAN", "Import the LAN RSS .reg profile", () => _eng.WriteProfile("L", "")));
            list.Children.Add(Action("5  Registry profile: Wi-Fi", "Import the Wi-Fi RSS .reg profile", () => _eng.WriteProfile("W", "")));
            list.Children.Add(Action("5  Registry profile: Wi-Fi buffers", "Rx/Tx descriptor counts", () => _eng.WriteProfile("B", "")));
            list.Children.Add(Action("5  Registry profile: NDIS", "Global NDIS\\Parameters (needs a reboot)", () => _eng.WriteProfile("N", "")));
            list.Children.Add(Action("6  Apply RSS", "Enable-NetAdapterRss + Set-NetAdapterRss", () => _eng.ApplyRss()));
            list.Children.Add(Action("7  Restart adapter", "Apply driver-key changes (link drops briefly)", () => _eng.RestartTarget(false)));
            list.Children.Add(SideLabel("CHECK", false));
            list.Children.Add(Action("8  Verify", "RSS state, keywords and registry vs the profile", () => _eng.Verify()));
            list.Children.Add(Action("R  Restore", "Put a backup back (exact)", () => _eng.InvokeRestore()));
            list.Children.Add(Action("A  Run all", "Backup -> profile -> RSS -> restart -> verify", () => _eng.RunAll()));
            list.Children.Add(SideLabel("TOOLS", false));
            list.Children.Add(Action("Self-test", "82 checks on an in-memory registry (nothing real touched)", RunSelfTest));
            dock.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false });
            return dock;
        }

        private FrameworkElement BuildMain()
        {
            var dock = new DockPanel { LastChildFill = true };

            var top = new Border { Background = Ui.B("Card"), BorderBrush = Ui.B("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(18, 10, 18, 10) };
            var tg = new Grid();
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _header = Ui.Text("", 10, "Dim", false);
            _header.VerticalAlignment = VerticalAlignment.Center;
            tg.Children.Add(_header);
            var clear = Ui.Btn("Clear", "BtnGhost", 74);
            clear.Click += (s, e) => { _out.Children.Clear(); };
            Grid.SetColumn(clear, 1);
            tg.Children.Add(clear);
            top.Child = tg;
            DockPanel.SetDock(top, Dock.Top);
            dock.Children.Add(top);

            var links = new Border { Background = Ui.B("Side"), BorderBrush = Ui.B("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 7, 18, 7) };
            var lr = new StackPanel { Orientation = Orientation.Horizontal };
            lr.Children.Add(Ui.Text(Brand.Tagline + "   ", 8.75, "Faint", false));
            lr.Children.Add(Link("aerialos.vercel.app", Brand.AerialUrl));
            lr.Children.Add(Link("grobpc.com", Brand.SiteUrl));
            lr.Children.Add(Link("x.com/grobPC", Brand.XUrl));
            links.Child = lr;
            DockPanel.SetDock(links, Dock.Bottom);
            dock.Children.Add(links);

            _out = new StackPanel { Margin = new Thickness(16, 10, 16, 12) };
            _scroll = new ScrollViewer { Content = _out, Background = Ui.B("LogBg"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
            dock.Children.Add(_scroll);
            return dock;
        }

        private TextBlock SideLabel(string text, bool first)
        {
            var t = Ui.Semi(text, 8.25, "Faint");
            t.Margin = new Thickness(8, first ? 6 : 14, 0, 4);
            return t;
        }

        private Button Action(string text, string tip, Action flow)
        {
            var b = new Button { Style = Ui.S("BtnGhost"), Content = text, HorizontalContentAlignment = HorizontalAlignment.Left, Height = 32, Margin = new Thickness(0, 1, 0, 1), ToolTip = tip };
            b.Click += (s, e) => RunFlow(flow);
            _actions.Add(b);
            return b;
        }

        private TextBlock Link(string text, string url)
        {
            var t = Ui.Text(text + "   ", 8.75, "Acc", false);
            t.Cursor = System.Windows.Input.Cursors.Hand;
            t.MouseLeftButtonUp += (s, e) => Ui.Open(url);
            return t;
        }

        // ----------------------------------------------------------------- engine wiring
        private void Start()
        {
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "grobPC NetTune");
            try { Directory.CreateDirectory(baseDir); } catch { }
            _ui = new WpfUi(Dispatcher, Append) { Owner = this };
            _eng = new Engine(new WinRegistry(), new RegRoots(), new WinNet { Log = s => LogFile(baseDir, s) }, new WinCpu(), _ui, baseDir)
            {
                DryRun = _startDry,
                LogSink = s => LogFile(baseDir, s),
            };
            Append(" " + Brand.Name + " " + Brand.VersionText + "   " + Brand.Credit, Tone.Title);
            Append(" AerialOS edition - RSS queue -> one CPU, registry profile, verify, exact restore.", Tone.Dim);
            RunFlow(() => { _eng.InitializeSession(); _eng.Detect(); });
        }

        private static void LogFile(string baseDir, string line)
        {
            try
            {
                string dir = Path.Combine(baseDir, "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "nettune-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log"), line + Environment.NewLine);
            }
            catch { }
        }

        // one flow at a time; the sidebar is disabled while it runs
        private void RunFlow(Action flow)
        {
            if (_busy || _eng == null) return;
            _busy = true;
            SetActionsEnabled(false);
            Task.Run(() =>
            {
                try { flow(); }
                catch (Exception ex) { _ui.Say(Status.Line(Status.Fail, ex.GetType().Name + ": " + ex.Message), Tone.Bad); }
                finally { Dispatcher.Invoke(() => { _busy = false; SetActionsEnabled(true); RefreshHeader(); }); }
            });
        }

        private void RunSelfTest()
        {
            var reg = new MockRegistry();
            var st = new SelfTest(reg, "Mock", reg.Clear, _ui);
            st.Run();
        }

        private void SetActionsEnabled(bool on) { foreach (var b in _actions) b.IsEnabled = on; }

        private void RefreshHeader()
        {
            if (_eng == null) return;
            string mode = _eng.DryRun ? "DRY-RUN" : "LIVE";
            string body;
            if (_eng.Ctx == null) body = "No adapter selected";
            else body = string.Format(CultureInfo.InvariantCulture, "Adapter: {0} [{1}]    RSS CPU {2} ({3})    {4}",
                _eng.Ctx.Alias, _eng.Ctx.Media, _eng.RssCpu, _eng.RssProfile, mode);
            _header.Text = " " + body;
            _header.Foreground = Ui.ToneBrush(_eng.DryRun ? Tone.Warn : Tone.Dim);
            if (_dry != null && _dry.IsChecked != _eng.DryRun) _dry.IsChecked = _eng.DryRun;
        }

        // ----------------------------------------------------------------- output pane
        private const int MaxLines = 4000;
        private void Append(string text, Tone tone)
        {
            var t = new TextBlock
            {
                Text = text ?? "",
                FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                FontSize = 12.5,
                Foreground = Ui.ToneBrush(tone),
                TextWrapping = TextWrapping.NoWrap,
            };
            if (tone == Tone.Title) t.FontWeight = FontWeights.SemiBold;
            _out.Children.Add(t);
            while (_out.Children.Count > MaxLines) _out.Children.RemoveAt(0);
            _scroll.ScrollToEnd();
        }
    }
}
