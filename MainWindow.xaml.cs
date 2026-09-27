using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WanMonitor;

public partial class MainWindow : Window
{
    private readonly WanProbe _probe = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CultureInfo _ci = CultureInfo.GetCultureInfo("es-CO");
    private Settings _settings = new();
    private IntPtr _hwnd;

    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
    private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(0xF3, 0x9C, 0x12));
    private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x7F, 0x8C, 0x8D));

    public MainWindow()
    {
        InitializeComponent();
        _settings = Settings.Load();
        Opacity = _settings.Opacity;
        if (_settings.Left.HasValue && _settings.Top.HasValue)
        {
            Left = _settings.Left.Value;
            Top = _settings.Top.Value;
        }
        else
        {
            Left = SystemParameters.WorkArea.Right - 300;
            Top = SystemParameters.WorkArea.Top + 20;
        }
        ApplyCompact(_settings.Compact);
        MiCompact.IsChecked = _settings.Compact;
        MiAutostart.IsChecked = Autostart.IsEnabled();

        _probe.Probed += _ => Dispatcher.InvokeAsync(Refresh);
        _probe.OutageOpened += _ => Dispatcher.InvokeAsync(Refresh);
        _probe.OutageClosed += _ => Dispatcher.InvokeAsync(Refresh);
        _uiTimer.Tick += (_, _) => Refresh();
        _topmostTimer.Tick += (_, _) => ReassertTopmost();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        // Ventana de herramienta: no aparece en Alt+Tab.
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
        EnsureOnScreen();
        _probe.Start();
        _uiTimer.Start();
        _topmostTimer.Start();
        Refresh();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
        _probe.Dispose();
    }

    // ---------- pintar estado ----------

    private void Refresh()
    {
        var last = _probe.Last;
        var current = _probe.Current;
        var now = DateTime.Now;

        if (_probe.Paused)
        {
            Dot.Fill = Gray;
            TxtStatus.Text = "Sonda en pausa";
            TxtLatency.Text = "";
        }
        else if (last == null)
        {
            Dot.Fill = Gray;
            TxtStatus.Text = "Iniciando…";
        }
        else if (current != null)
        {
            Dot.Fill = Red;
            TxtStatus.Text = current.GatewayFailed ? $"SIN RED ({current.Seconds:0} s)" : $"CAÍDO ({current.Seconds:0} s)";
            TxtLatency.Text = "";
        }
        else if (!last.InternetOk)
        {
            Dot.Fill = Amber;
            TxtStatus.Text = "Sin respuesta…";
            TxtLatency.Text = "";
        }
        else
        {
            Dot.Fill = Green;
            TxtStatus.Text = "En línea";
            TxtLatency.Text = $"{last.RttMs} ms";
        }

        // próximo corte
        var next = _probe.PredictedNextOutage;
        if (next.HasValue)
        {
            var remaining = (int)(next.Value - now).TotalSeconds;
            if (remaining >= 0)
            {
                TxtNext.Text = $"Próximo corte ~{next.Value:HH:mm:ss}  (en {remaining / 60}:{remaining % 60:00})";
                TxtNext.Foreground = current != null ? Red : remaining <= 30 ? Amber : (Brush)FindResource("Fg");
            }
            else
            {
                // el momento previsto pasó sin corte: se muestra tal cual, no se desplaza un periodo
                var late = -remaining;
                TxtNext.Text = $"Esperado ~{next.Value:HH:mm:ss}  (hace {late / 60}:{late % 60:00}, sin ocurrir)";
                TxtNext.Foreground = current != null ? Red : Amber;
            }
        }
        else
        {
            TxtNext.Text = "Próximo corte: sin datos todavía";
            TxtNext.Foreground = (Brush)FindResource("Muted");
        }

        if (Details.Visibility == Visibility.Visible)
        {
            var todayCount = _probe.TodayOutages.Count();
            var down = TimeSpan.FromSeconds(_probe.TodayDownSeconds);
            TxtToday.Text = $"Hoy: {todayCount} cortes · {(int)down.TotalMinutes}:{down.Seconds:00} caído · {_probe.AvailabilityPercent.ToString("0.00", _ci)} %";

            var lastOutage = _probe.Outages.OrderBy(o => o.Start).LastOrDefault();
            TxtLast.Text = lastOutage == null
                ? "Último: —"
                : $"Último: {lastOutage.Start:HH:mm:ss} · {lastOutage.Seconds:0} s ({lastOutage.Kind})";

            TxtPeriod.Text = $"Periodo: {_probe.EstimatedPeriodSeconds:0} s · gateway {last?.GatewayIp ?? "—"}";
        }
    }

    // ---------- siempre encima (incluso sobre PiP del navegador) ----------

    private void ReassertTopmost()
    {
        if (_hwnd == IntPtr.Zero) return;
        // Entre varias ventanas topmost gana la última en reafirmarlo; el PiP de Chrome/Edge también es topmost.
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // ---------- interacción ----------

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
            _settings.Left = Left; _settings.Top = Top; _settings.Save();
        }
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        MiCompact.IsChecked = !MiCompact.IsChecked;
        ApplyCompact(MiCompact.IsChecked);
    }

    private void OnToggleCompact(object sender, RoutedEventArgs e) => ApplyCompact(MiCompact.IsChecked);

    private void ApplyCompact(bool compact)
    {
        Details.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        _settings.Compact = compact;
        _settings.Save();
        Refresh();
    }

    private void OnTogglePause(object sender, RoutedEventArgs e)
    {
        _probe.Paused = MiPause.IsChecked;
        Refresh();
    }

    private void OnCopySummary(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_probe.BuildSummary()); } catch { }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", _probe.LogDirectory) { UseShellExecute = true }); } catch { }
    }

    private void OnOpacity(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            Opacity = v;
            _settings.Opacity = v;
            _settings.Save();
        }
    }

    private void OnToggleAutostart(object sender, RoutedEventArgs e)
    {
        Autostart.Set(MiAutostart.IsChecked);
        MiAutostart.IsChecked = Autostart.IsEnabled();
    }

    private void OnResetStats(object sender, RoutedEventArgs e)
    {
        _probe.ResetStats();
        Refresh();
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void EnsureOnScreen()
    {
        var wa = SystemParameters.VirtualScreenWidth;
        var ha = SystemParameters.VirtualScreenHeight;
        if (Left < SystemParameters.VirtualScreenLeft || Left > SystemParameters.VirtualScreenLeft + wa - 60) Left = SystemParameters.WorkArea.Right - 300;
        if (Top < SystemParameters.VirtualScreenTop || Top > SystemParameters.VirtualScreenTop + ha - 40) Top = SystemParameters.WorkArea.Top + 20;
    }

    // ---------- Win32 ----------

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}

// ---------- ajustes persistentes ----------

public sealed class Settings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Opacity { get; set; } = 0.9;
    public bool Compact { get; set; }

    private static string PathFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WanMonitor", "ajustes.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(PathFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathFile)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFile)!);
            File.WriteAllText(PathFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

// ---------- inicio con Windows (clave Run del usuario actual) ----------

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "WanMonitor";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return k?.GetValue(Name) is string;
        }
        catch { return false; }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                // Assembly.Location viene vacío en un exe de archivo único: usar la ruta del proceso.
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) exe = Path.Combine(AppContext.BaseDirectory, "WanMonitor.exe");
                k?.SetValue(Name, $"\"{exe}\"");
            }
            else k?.DeleteValue(Name, false);
        }
        catch { }
    }
}
