using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;

namespace WanMonitor;

public sealed record ProbeResult(DateTime Time, bool InternetOk, bool GatewayOk, long RttMs, string? GatewayIp);

public sealed class Outage
{
    public DateTime Start { get; init; }
    public DateTime? End { get; set; }
    public int Fails { get; set; }
    /// <summary>Cuántas de las sondas fallidas tampoco obtuvieron respuesta del gateway.</summary>
    public int GatewayFails { get; set; }
    /// <summary>Etiqueta libre de la configuración vigente (contenido de nota.txt), p. ej. "5G NSA" o "4G".</summary>
    public string Context { get; set; } = "";
    /// <summary>
    /// LAN solo si el gateway dejó de responder en la mayoría de las sondas del corte.
    /// Un fallo suelto del gateway es el CPE ocupado renegociando, no una caída de la red local.
    /// </summary>
    public bool GatewayFailed => Fails > 0 && GatewayFails * 2 >= Fails;
    public string Kind => GatewayFailed ? "LAN" : "WAN";
    public double Seconds => ((End ?? DateTime.Now) - Start).TotalSeconds;
}

/// <summary>
/// Sonda de conectividad: un ping por segundo al destino de Internet. Si falla, verifica
/// el gateway para clasificar el corte (WAN = el router responde pero no hay salida;
/// LAN = ni el router responde). Registra todo en %LOCALAPPDATA%\WanMonitor.
/// </summary>
public sealed class WanProbe : IDisposable
{
    public const int DefaultPeriodSeconds = 632;

    public string InternetTarget { get; set; } = "8.8.8.8";
    public int IntervalMs { get; set; } = 1000;
    /// <summary>
    /// 2 s: por encima de la latencia de un enlace congestionado (que responde lento pero responde)
    /// y muy por encima de la de uno sano. Evita contar la saturación como corte.
    /// </summary>
    public int TimeoutMs { get; set; } = 2000;
    /// <summary>Fallos consecutivos necesarios para declarar un corte.</summary>
    public int FailsToOpen { get; set; } = 2;
    /// <summary>Duración mínima para que un corte sirva de referencia de fase (los parpadeos por congestión no cuentan).</summary>
    public int AnchorMinSeconds { get; set; } = 5;

    public string LogDirectory { get; }
    public string OutagesCsvPath => Path.Combine(LogDirectory, "cortes.csv");

    public DateTime StartedAt { get; private set; } = DateTime.Now;
    public long TotalProbes { get; private set; }
    public long FailedProbes { get; private set; }
    public bool Paused { get; set; }

    public ProbeResult? Last { get; private set; }
    public Outage? Current { get; private set; }
    public List<Outage> Outages { get; } = new();

    public event Action<ProbeResult>? Probed;
    public event Action<Outage>? OutageOpened;
    public event Action<Outage>? OutageClosed;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _consecutiveFails;
    private DateTime? _firstFailAt;
    private int _gatewayFailCount;
    private string? _gateway;
    private DateTime _gatewayCheckedAt = DateTime.MinValue;
    private readonly object _logLock = new();
    /// <summary>Inicios de los cortes que forman la serie periódica; de aquí salen el periodo y la predicción.</summary>
    private readonly List<DateTime> _cadence = new();
    private string _context = "";
    private DateTime _contextStamp = DateTime.MinValue;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public WanProbe()
    {
        LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WanMonitor");
        Directory.CreateDirectory(LogDirectory);
        LoadTodayOutages();
    }

    public void Start()
    {
        if (_loop != null) return;
        StartedAt = DateTime.Now;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        AppendProbeLog($"{Stamp(DateTime.Now)} INICIO monitor (destino {InternetTarget}, intervalo {IntervalMs} ms, timeout {TimeoutMs} ms)");
    }

    public void ResetStats()
    {
        StartedAt = DateTime.Now;
        TotalProbes = 0;
        FailedProbes = 0;
        Outages.Clear();
    }

    // ---------- estadísticas ----------

    public IEnumerable<Outage> TodayOutages => Outages.Where(o => o.Start.Date == DateTime.Today);

    public double TodayDownSeconds => TodayOutages.Sum(o => o.Seconds);

    public double AvailabilityPercent
    {
        get
        {
            var elapsed = (DateTime.Now - StartedAt).TotalSeconds;
            if (elapsed < 1) return 100;
            var down = Outages.Where(o => o.Start >= StartedAt).Sum(o => o.Seconds);
            return Math.Max(0, 100.0 * (1 - down / elapsed));
        }
    }

    /// <summary>Periodo estimado entre cortes: mediana de los últimos intervalos de la serie, o el valor por defecto.</summary>
    public double EstimatedPeriodSeconds
    {
        get
        {
            var intervals = new List<double>();
            for (int i = 1; i < _cadence.Count; i++)
            {
                var d = (_cadence[i] - _cadence[i - 1]).TotalSeconds;
                if (d >= 60 && d <= 3600) intervals.Add(d);
            }
            if (intervals.Count == 0) return DefaultPeriodSeconds;
            var recent = intervals.TakeLast(6).OrderBy(v => v).ToList();
            return recent[recent.Count / 2];
        }
    }

    /// <summary>
    /// Incorpora un corte a la serie periódica sólo si encaja en la cadencia.
    /// Un corte que llega mucho antes de lo esperado es un evento suelto —congestión, un parpadeo—
    /// y no debe mover la fase: si lo hiciera, el siguiente corte real llegaría "antes de tiempo"
    /// y la cuenta regresiva quedaría corriendo a mitad de camino.
    /// </summary>
    private void RegisterCadence(DateTime start)
    {
        if (_cadence.Count > 0 && (start - _cadence[^1]).TotalSeconds < EstimatedPeriodSeconds * 0.75) return;
        _cadence.Add(start);
        if (_cadence.Count > 60) _cadence.RemoveAt(0);
    }

    /// <summary>
    /// Momento esperado del próximo corte. Si ya pasó sin que ocurriera, devuelve ese instante
    /// (en el pasado) en lugar de saltar un periodo entero: que la cuenta quede en descubierto
    /// es información, taparla desplazándola no lo es.
    /// </summary>
    public DateTime? PredictedNextOutage
        => _cadence.Count == 0 ? null : _cadence[^1].AddSeconds(EstimatedPeriodSeconds);

    public string BuildSummary()
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.GetCultureInfo("es-CO");
        sb.AppendLine($"Monitor WAN — resumen {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", ci)}");
        sb.AppendLine($"Destino: {InternetTarget}  Gateway: {_gateway ?? "?"}  Sonda: 1/s, timeout {TimeoutMs} ms");
        if (CurrentContext.Length > 0) sb.AppendLine($"Configuración vigente: {CurrentContext}");
        sb.AppendLine($"Desde: {StartedAt:yyyy-MM-dd HH:mm:ss}  Sondas: {TotalProbes}  Fallidas: {FailedProbes}");
        sb.AppendLine($"Disponibilidad: {AvailabilityPercent.ToString("0.00", ci)} %");
        sb.AppendLine($"Periodo estimado entre cortes: {EstimatedPeriodSeconds:0} s");
        var today = TodayOutages.ToList();
        sb.AppendLine($"Hoy: {today.Count} cortes, {TimeSpan.FromSeconds(TodayDownSeconds):h\\:mm\\:ss} sin servicio");
        sb.AppendLine();
        sb.AppendLine("inicio;fin;segundos;tipo;fallos;contexto");
        foreach (var o in Outages.OrderBy(o => o.Start))
            sb.AppendLine($"{o.Start:yyyy-MM-dd HH:mm:ss};{(o.End.HasValue ? o.End.Value.ToString("HH:mm:ss") : "en curso")};{o.Seconds:0};{o.Kind};{o.Fails};{o.Context}");
        return sb.ToString();
    }

    // ---------- bucle ----------

    private async Task LoopAsync(CancellationToken ct)
    {
        using var ping = new Ping();
        using var gwPing = new Ping();
        while (!ct.IsCancellationRequested)
        {
            var tick = DateTime.Now;
            if (!Paused)
            {
                var r = await ProbeOnceAsync(ping, gwPing, ct);
                Process(r);
            }
            var elapsed = (int)(DateTime.Now - tick).TotalMilliseconds;
            var wait = Math.Max(50, IntervalMs - elapsed);
            try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { }
        }
    }

    private async Task<ProbeResult> ProbeOnceAsync(Ping ping, Ping gwPing, CancellationToken ct)
    {
        var now = DateTime.Now;
        bool ok = false; long rtt = -1;
        try
        {
            var reply = await ping.SendPingAsync(InternetTarget, TimeoutMs);
            ok = reply.Status == IPStatus.Success;
            rtt = ok ? reply.RoundtripTime : -1;
        }
        catch { ok = false; }

        bool gwOk = true;
        if (!ok)
        {
            var gw = GetGateway();
            if (gw != null)
            {
                try
                {
                    var greply = await gwPing.SendPingAsync(gw, Math.Min(TimeoutMs, 700));
                    gwOk = greply.Status == IPStatus.Success;
                }
                catch { gwOk = false; }
            }
        }
        return new ProbeResult(now, ok, gwOk, rtt, _gateway);
    }

    private void Process(ProbeResult r)
    {
        TotalProbes++;
        Last = r;
        if (r.InternetOk)
        {
            if (Current != null)
            {
                Current.End = r.Time;
                // demasiado corto para ser uno de la serie: se retira de la cadencia
                if (Current.Seconds < AnchorMinSeconds && _cadence.Count > 0 && _cadence[^1] == Current.Start)
                    _cadence.RemoveAt(_cadence.Count - 1);
                AppendOutageCsv(Current);
                AppendProbeLog($"{Stamp(r.Time)} FIN corte ({Current.Seconds:0} s, {Current.Kind}, {Current.Fails} fallos)");
                var closed = Current;
                Current = null;
                OutageClosed?.Invoke(closed);
            }
            else if (_consecutiveFails > 0)
            {
                AppendProbeLog($"{Stamp(r.Time)} OK tras {_consecutiveFails} fallo(s) aislado(s)");
            }
            _consecutiveFails = 0;
            _firstFailAt = null;
            _gatewayFailCount = 0;
            if (r.RttMs >= 800)
                AppendProbeLog($"{Stamp(r.Time)} LENTA {r.RttMs} ms (enlace congestionado, no es corte)");
            else if (TotalProbes % 60 == 0)
                AppendProbeLog($"{Stamp(r.Time)} ok {r.RttMs} ms");
        }
        else
        {
            FailedProbes++;
            _consecutiveFails++;
            _firstFailAt ??= r.Time;
            if (!r.GatewayOk) _gatewayFailCount++;
            AppendProbeLog($"{Stamp(r.Time)} FALLO #{_consecutiveFails} internet={(r.InternetOk ? "ok" : "sin respuesta")} gateway={(r.GatewayOk ? "ok" : "sin respuesta")}");

            if (Current == null && _consecutiveFails >= FailsToOpen)
            {
                Current = new Outage { Start = _firstFailAt.Value, Fails = _consecutiveFails, GatewayFails = _gatewayFailCount, Context = CurrentContext };
                Outages.Add(Current);
                RegisterCadence(Current.Start);
                AppendProbeLog($"{Stamp(r.Time)} INICIO corte (desde {Stamp(Current.Start)}){(Current.Context.Length > 0 ? $" [{Current.Context}]" : "")}");
                OutageOpened?.Invoke(Current);
            }
            else if (Current != null)
            {
                Current.Fails = _consecutiveFails;
                Current.GatewayFails = _gatewayFailCount;
            }
        }
        Probed?.Invoke(r);
    }

    /// <summary>
    /// Etiqueta de contexto para los cortes: primera línea de nota.txt en la carpeta de registros.
    /// Permite marcar qué configuración estaba vigente (4G, 5G NSA, equipo nuevo…) sin tocar la app.
    /// </summary>
    public string CurrentContext
    {
        get
        {
            try
            {
                var f = Path.Combine(LogDirectory, "nota.txt");
                if (!File.Exists(f)) return _context = "";
                var stamp = File.GetLastWriteTimeUtc(f);
                if (stamp != _contextStamp)
                {
                    _contextStamp = stamp;
                    var first = File.ReadLines(f, Encoding.UTF8).FirstOrDefault() ?? "";
                    _context = first.Trim().Replace(';', ',');
                    if (_context.Length > 40) _context = _context.Substring(0, 40);
                }
            }
            catch { }
            return _context;
        }
    }

    private string? GetGateway()
    {
        if ((DateTime.Now - _gatewayCheckedAt).TotalSeconds < 60 && _gateway != null) return _gateway;
        _gatewayCheckedAt = DateTime.Now;
        try
        {
            var gw = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            _gateway = gw?.ToString() ?? _gateway ?? "192.168.1.1";
        }
        catch { _gateway ??= "192.168.1.1"; }
        return _gateway;
    }

    // ---------- registros ----------

    private static string Stamp(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private string ProbeLogPath => Path.Combine(LogDirectory, $"sondas-{DateTime.Now:yyyy-MM-dd}.log");

    private void AppendProbeLog(string line)
    {
        try { lock (_logLock) File.AppendAllText(ProbeLogPath, line + Environment.NewLine, Utf8NoBom); } catch { }
    }

    private void AppendOutageCsv(Outage o)
    {
        try
        {
            lock (_logLock)
            {
                var exists = File.Exists(OutagesCsvPath);
                using var w = new StreamWriter(OutagesCsvPath, append: true, Utf8NoBom);
                if (!exists) w.WriteLine("fecha;inicio;fin;segundos;tipo;fallos;fallos_gateway;contexto");
                w.WriteLine($"{o.Start:yyyy-MM-dd};{o.Start:HH:mm:ss};{o.End:HH:mm:ss};{o.Seconds.ToString("0", CultureInfo.InvariantCulture)};{o.Kind};{o.Fails};{o.GatewayFails};{o.Context}");
            }
        }
        catch { }
    }

    /// <summary>Recupera los cortes de hoy del CSV para que las estadísticas sobrevivan a un reinicio de la app.</summary>
    private void LoadTodayOutages()
    {
        try
        {
            if (!File.Exists(OutagesCsvPath)) return;
            foreach (var line in File.ReadLines(OutagesCsvPath).Skip(1))
            {
                var p = line.Split(';');
                if (p.Length < 6) continue;
                if (!DateTime.TryParseExact(p[0] + " " + p[1], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)) continue;
                if (start.Date != DateTime.Today) continue;
                DateTime.TryParseExact(p[0] + " " + p[2], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end);
                if (end < start) end = end.AddDays(1);
                int.TryParse(p[5], out var fails);
                fails = Math.Max(fails, FailsToOpen);
                int gwFails;
                if (p.Length < 7 || !int.TryParse(p[6], out gwFails))
                    gwFails = p[4] == "LAN" ? fails : 0;   // formato viejo: sin conteo, se respeta la etiqueta guardada
                var o = new Outage { Start = start, End = end, Fails = fails, GatewayFails = gwFails, Context = p.Length > 7 ? p[7] : "" };
                Outages.Add(o);
                if (o.Seconds >= AnchorMinSeconds) RegisterCadence(o.Start);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _loop?.Wait(1500); } catch { }
        _cts?.Dispose();
    }
}
