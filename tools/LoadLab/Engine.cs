using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace LoadLab;

public sealed record Runner(int Index, string ParticipantId, string Token, double SpeedMps, double OffsetM);

/// <summary>
/// Open-loop load generator. A dedicated scheduler thread integrates the target rate R(t) and dispatches one
/// position POST per unit of credit; latency is measured from the intended send time, so a stalled server cannot
/// hide its queueing (no coordinated omission). Every request, SignalR message and probe delivery is logged raw.
/// </summary>
public sealed class Engine : IAsyncDisposable
{
    private const double MinRunnerIntervalS = 1.05; // the app allows 60 requests a minute per token
    private const double RunnerPeriodS = 5;          // a realistic phone sends a fix every 5 s
    private const int ProbeCount = 10;
    private const int MaxInFlight = 4000;

    private readonly string _base;
    private readonly string _eventId;
    private readonly string _apiKey;
    private readonly string _outDir;
    private readonly Course _course;
    private readonly Runner[] _probes;
    private readonly Runner[] _load;
    private readonly long[] _lastSentTicks;
    private readonly HttpClient _http;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly long _unixStartUs;
    private readonly Channel<string> _reqLog = Channel.CreateUnbounded<string>(new() { SingleReader = true });
    private readonly Channel<string> _sigLog = Channel.CreateUnbounded<string>(new() { SingleReader = true });
    private readonly Channel<string> _e2eLog = Channel.CreateUnbounded<string>(new() { SingleReader = true });
    private readonly Channel<string> _rateLog = Channel.CreateUnbounded<string>(new() { SingleReader = true });
    private readonly Channel<string> _wifiLog = Channel.CreateUnbounded<string>(new() { SingleReader = true });
    private readonly StreamWriter _phaseLog;
    private readonly List<Task> _writers = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, long> _probeLastSendUs = new();
    private readonly ConcurrentDictionary<string, long> _probeLastSignalUs = new();
    private readonly List<HubConnection> _spectators = [];
    private HubConnection? _organizer;
    private int _inFlight;
    private int _consecutiveConnectFailures;
    private long _spectatorBytes;
    private long _spectatorMessages;
    private long _shed;
    private long _genLimited;

    // the scheduler's view of the current phase
    private volatile Func<double, double> _rate = _ => 0;
    private volatile int _poolOverride; // > 0: the number of runners the load rotates over (the active field)
    private long _orgMessages;
    private long _phaseStartTicks;
    private int _rr;
    private PhaseStats _stats = new();

    public bool Aborted { get; private set; }

    public Engine(string baseUrl, string eventId, string apiKey, string tokensCsv, string gpx, string outDir)
    {
        _base = baseUrl.TrimEnd('/');
        _eventId = eventId;
        _apiKey = apiKey;
        _outDir = outDir;
        Directory.CreateDirectory(outDir);
        _course = Course.Load(gpx);
        var runners = File.ReadLines(tokensCsv).Skip(1).Select(l => l.Split(','))
            .Select(p => new Runner(int.Parse(p[0], CultureInfo.InvariantCulture), p[1], p[2],
                double.Parse(p[3], CultureInfo.InvariantCulture), double.Parse(p[4], CultureInfo.InvariantCulture)))
            .ToArray();
        _probes = runners[..ProbeCount];
        _load = runners[ProbeCount..];
        _lastSentTicks = new long[runners.Length];
        _unixStartUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        _http = new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 768,
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            UseCookies = false,
        })
        { Timeout = TimeSpan.FromSeconds(30) };
        _phaseLog = new StreamWriter(Path.Combine(outDir, "phases.csv")) { AutoFlush = true };
        _phaseLog.WriteLine("phase,start_us,end_us,params");
        _writers.Add(WriteAsync(_reqLog, "requests.csv", "kind,runner,intended_us,send_delay_us,latency_us,service_us,status,server_ms,bytes"));
        _writers.Add(WriteAsync(_sigLog, "signalr.csv", "kind,conn,recv_us,at_ms,bytes,participants"));
        _writers.Add(WriteAsync(_e2eLog, "e2e.csv", "probe,send_us,recv_us,at_ms,signal_ago_ms"));
        _writers.Add(WriteAsync(_rateLog, "rate.csv", "t_us,target_rps,dispatched,inflight,spectators"));
        _writers.Add(WriteAsync(_wifiLog, "wifi.csv", "t_us,rtt_us"));
    }

    private long NowUs() => _unixStartUs + _clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;

    private long UsOfTicks(long ticks) => _unixStartUs + ticks * 1_000_000 / Stopwatch.Frequency;

    // ---------------------------------------------------------------- lifecycle

    public async Task StartAsync()
    {
        if (OperatingSystem.IsWindows()) _ = TimeBeginPeriod(1);
        _organizer = BuildHub();
        _organizer.On<JsonElement>("LiveUpdate", OnOrganizerUpdate);
        await _organizer.StartAsync();
        await _organizer.InvokeAsync("JoinOrganizer", _eventId, _apiKey);
        new Thread(SchedulerLoop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "scheduler" }.Start();
        _ = Task.Run(WifiProbeLoop);
        _ = Task.Run(StatusLoop);
    }

    public async ValueTask DisposeAsync()
    {
        _rate = _ => 0;
        await SetSpectatorsAsync(0);
        if (_organizer is not null) await _organizer.DisposeAsync();
        await Task.Delay(3000); // let the last requests finish
        _stop.Cancel();
        foreach (var c in new[] { _reqLog, _sigLog, _e2eLog, _rateLog, _wifiLog }) c.Writer.TryComplete();
        await Task.WhenAll(_writers);
        _phaseLog.Dispose();
        _http.Dispose();
    }

    // ---------------------------------------------------------------- phases

    /// <summary>Runs <paramref name="rate"/> (requests per second as a function of seconds since the phase start) for <paramref name="seconds"/>.</summary>
    public async Task<PhaseStats> PhaseAsync(string name, double seconds, Func<double, double> rate, string parameters = "", int pool = 0)
    {
        if (Aborted) return new PhaseStats();
        _poolOverride = pool;
        var stats = new PhaseStats();
        Interlocked.Exchange(ref _stats, stats);
        Interlocked.Exchange(ref _phaseStartTicks, _clock.ElapsedTicks);
        _rate = rate;
        var start = NowUs();
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} ▶ {name} ({seconds:0} s) {parameters}");
        var end = _clock.Elapsed + TimeSpan.FromSeconds(seconds);
        while (_clock.Elapsed < end && !Aborted) await Task.Delay(200);
        _rate = _ => 0;
        await Task.Delay(1000); // stragglers count to their phase
        _phaseLog.WriteLine($"{name},{start},{NowUs()},\"{parameters}\"");
        stats.DurationS = seconds;
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} ■ {name}: {stats}");
        return stats;
    }

    public void Mark(string name, string parameters = "") => _phaseLog.WriteLine($"{name},{NowUs()},{NowUs()},\"{parameters}\"");

    public async Task SetSpectatorsAsync(int count)
    {
        while (_spectators.Count < count && !Aborted)
        {
            var batch = new List<Task>();
            for (var i = 0; i < 10 && _spectators.Count < count; i++)
            {
                var conn = BuildHub();
                var id = _spectators.Count;
                conn.On<JsonElement>("PublicUpdate", view => OnSpectatorUpdate(id, view));
                _spectators.Add(conn);
                batch.Add(StartSpectatorAsync(conn));
            }
            await Task.WhenAll(batch);
        }
        while (_spectators.Count > count)
        {
            var conn = _spectators[^1];
            _spectators.RemoveAt(_spectators.Count - 1);
            await conn.DisposeAsync();
        }
    }

    public (long Bytes, long Messages) SpectatorCounters() => (Interlocked.Read(ref _spectatorBytes), Interlocked.Read(ref _spectatorMessages));

    public long OrganizerMessages => Interlocked.Read(ref _orgMessages);

    public int Spectators => _spectators.Count;

    private async Task StartSpectatorAsync(HubConnection conn)
    {
        try
        {
            await conn.StartAsync();
            await conn.InvokeAsync("JoinSpectator", _eventId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"spectator failed: {ex.GetType().Name} {ex.Message}");
        }
    }

    private HubConnection BuildHub() => new HubConnectionBuilder()
        .WithUrl(_base + "/hubs/live", o =>
        {
            o.SkipNegotiation = true;
            o.Transports = HttpTransportType.WebSockets;
        })
        .WithAutomaticReconnect()
        .Build();

    // ---------------------------------------------------------------- SignalR handlers

    private void OnOrganizerUpdate(JsonElement view)
    {
        var recv = NowUs();
        var at = view.GetProperty("at").GetDateTimeOffset().ToUnixTimeMilliseconds();
        var participants = view.GetProperty("participants");
        var bytes = view.GetRawText().Length;
        Interlocked.Increment(ref _orgMessages);
        _sigLog.Writer.TryWrite($"org,0,{recv},{at},{bytes},{participants.GetArrayLength()}");
        foreach (var p in participants.EnumerateArray())
        {
            var pid = p.GetProperty("participantId").GetString()!;
            if (!_probeLastSendUs.TryGetValue(pid, out var sendUs)) continue;
            if (!p.TryGetProperty("lastSignalAgo", out var agoEl) || agoEl.ValueKind != JsonValueKind.String) continue;
            var agoMs = (long)TimeSpan.Parse(agoEl.GetString()!, CultureInfo.InvariantCulture).TotalMilliseconds;
            var signalMs = at - agoMs;
            var last = _probeLastSignalUs.GetValueOrDefault(pid);
            if (signalMs > last + 500)
            {
                _probeLastSignalUs[pid] = signalMs;
                if (last != 0) _e2eLog.Writer.TryWrite($"{pid},{sendUs},{recv},{at},{agoMs}");
            }
        }
    }

    private void OnSpectatorUpdate(int id, JsonElement view)
    {
        var recv = NowUs();
        var bytes = view.GetRawText().Length;
        Interlocked.Add(ref _spectatorBytes, bytes);
        Interlocked.Increment(ref _spectatorMessages);
        var at = view.TryGetProperty("at", out var a) ? a.GetDateTimeOffset().ToUnixTimeMilliseconds() : 0;
        var n = view.TryGetProperty("participants", out var ps) && ps.ValueKind == JsonValueKind.Array ? ps.GetArrayLength() : -1;
        _sigLog.Writer.TryWrite($"spec,{id},{recv},{at},{bytes},{n}");
    }

    // ---------------------------------------------------------------- scheduler

    private void SchedulerLoop()
    {
        var credit = 0.0;
        var last = _clock.ElapsedTicks;
        var nextRateLog = last;
        var dispatchedSinceLog = 0;
        var tick100ms = Stopwatch.Frequency / 10;
        var probeNext = new long[ProbeCount];
        for (var i = 0; i < ProbeCount; i++) probeNext[i] = last + (long)(Stopwatch.Frequency * (1 + i * 0.5));
        while (!_stop.IsCancellationRequested)
        {
            var now = _clock.ElapsedTicks;
            var dt = (now - last) / (double)Stopwatch.Frequency;
            last = now;
            var t = (now - Interlocked.Read(ref _phaseStartTicks)) / (double)Stopwatch.Frequency;
            var r = Math.Max(0, _rate(t));
            credit += r * dt;
            if (r == 0 && credit < 1) credit = 0;
            var pool = _poolOverride > 0
                ? Math.Min(_poolOverride, _load.Length)
                : (int)Math.Clamp(Math.Ceiling(r * RunnerPeriodS), 1, _load.Length);
            while (credit >= 1)
            {
                credit -= 1;
                // the intended time of this request: spread evenly inside the last loop interval
                var intended = now - (long)(credit / Math.Max(r, 1e-9) * Stopwatch.Frequency);
                Dispatch(pool, Math.Min(intended, now), "load");
                dispatchedSinceLog++;
            }
            for (var i = 0; i < ProbeCount; i++)
            {
                if (now < probeNext[i]) continue;
                probeNext[i] += (long)(Stopwatch.Frequency * RunnerPeriodS);
                Send(_probes[i], now, "probe");
            }
            if (now >= nextRateLog)
            {
                _rateLog.Writer.TryWrite(string.Create(CultureInfo.InvariantCulture,
                    $"{UsOfTicks(now)},{r:0.###},{dispatchedSinceLog},{Volatile.Read(ref _inFlight)},{_spectators.Count}"));
                dispatchedSinceLog = 0;
                nextRateLog += tick100ms;
            }
            if (credit < 0.5 && r < 2000) Thread.Sleep(1); else Thread.SpinWait(50);
        }
    }

    private void Dispatch(int pool, long intendedTicks, string kind)
    {
        var minGap = (long)(MinRunnerIntervalS * Stopwatch.Frequency);
        for (var tries = 0; tries < pool; tries++)
        {
            var idx = (int)((uint)Interlocked.Increment(ref _rr) % (uint)pool);
            var runner = _load[idx];
            if (intendedTicks - Volatile.Read(ref _lastSentTicks[runner.Index]) < minGap) continue;
            Send(runner, intendedTicks, kind);
            return;
        }
        Interlocked.Increment(ref _genLimited);
        _stats.GenLimited++;
    }

    private void Send(Runner runner, long intendedTicks, string kind)
    {
        if (Volatile.Read(ref _inFlight) >= MaxInFlight)
        {
            Interlocked.Increment(ref _shed);
            _reqLog.Writer.TryWrite($"{kind},{runner.Index},{UsOfTicks(intendedTicks)},0,0,0,-2,0,0");
            _stats.Record(-2, 0);
            return;
        }
        Volatile.Write(ref _lastSentTicks[runner.Index], intendedTicks);
        Interlocked.Increment(ref _inFlight);
        _ = SendAsync(runner, intendedTicks, kind, _stats);
    }

    private async Task SendAsync(Runner runner, long intendedTicks, string kind, PhaseStats stats)
    {
        var sendTicks = _clock.ElapsedTicks;
        var status = 0;
        long serverMs = 0;
        long bytes = 0;
        try
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var elapsedS = (nowUtc.ToUnixTimeMilliseconds() - _unixStartUs / 1000) / 1000.0;
            var (lat, lon) = _course.At(runner.OffsetM + runner.SpeedMps * elapsedS);
            var body = string.Create(CultureInfo.InvariantCulture,
                $"{{\"fixes\":[{{\"t\":\"{nowUtc:yyyy-MM-ddTHH:mm:ss.fffZ}\",\"lat\":{lat:0.0000000},\"lon\":{lon:0.0000000},\"acc\":5}}],\"status\":{{\"battery\":{80 - runner.Index % 50},\"charging\":false,\"queue\":0,\"foreground\":true}}}}");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/events/{_eventId}/positions")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runner.Token);
            if (kind == "probe") _probeLastSendUs[runner.ParticipantId] = UsOfTicks(sendTicks);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, _stop.Token);
            status = (int)response.StatusCode;
            bytes = response.Content.Headers.ContentLength ?? 0;
            if (response.Headers.TryGetValues("X-T", out var xt))
                serverMs = (long)(double.Parse(xt.First(), CultureInfo.InvariantCulture) * 1000);
            Interlocked.Exchange(ref _consecutiveConnectFailures, 0);
        }
        catch (HttpRequestException)
        {
            status = -1;
            if (Interlocked.Increment(ref _consecutiveConnectFailures) > 200) Abort("200 consecutive connection failures (kill switch?)");
        }
        catch (TaskCanceledException)
        {
            status = -3;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
        var done = _clock.ElapsedTicks;
        var latencyUs = (done - intendedTicks) * 1_000_000 / Stopwatch.Frequency;
        var serviceUs = (done - sendTicks) * 1_000_000 / Stopwatch.Frequency;
        var delayUs = (sendTicks - intendedTicks) * 1_000_000 / Stopwatch.Frequency;
        _reqLog.Writer.TryWrite($"{kind},{runner.Index},{UsOfTicks(intendedTicks)},{delayUs},{latencyUs},{serviceUs},{status},{serverMs},{bytes}");
        if (kind == "load") stats.Record(status, latencyUs / 1000.0);
    }

    public void Abort(string reason)
    {
        if (Aborted) return;
        Aborted = true;
        _rate = _ => 0;
        Console.WriteLine($"ABORT: {reason}");
        _phaseLog.WriteLine($"ABORT,{NowUs()},{NowUs()},\"{reason}\"");
    }

    // ---------------------------------------------------------------- background loops

    private async Task WifiProbeLoop()
    {
        using var ping = new Ping();
        var host = new Uri(_base).Host;
        while (!_stop.IsCancellationRequested)
        {
            var t = NowUs();
            long rtt = -1;
            try
            {
                var sw = Stopwatch.StartNew();
                var reply = await ping.SendPingAsync(host, 1000);
                if (reply.Status == IPStatus.Success) rtt = sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;
            }
            catch (PingException)
            {
            }
            _wifiLog.Writer.TryWrite($"{t},{rtt}");
            var wait = 100 - (int)((NowUs() - t) / 1000);
            if (wait > 0) await Task.Delay(wait);
        }
    }

    private async Task StatusLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            await Task.Delay(10_000);
            Console.WriteLine($"  · inflight {Volatile.Read(ref _inFlight)}, shed {Interlocked.Read(ref _shed)}, gen-limited {Interlocked.Read(ref _genLimited)}, spectators {_spectators.Count}, phase {_stats}");
        }
    }

    private async Task WriteAsync(Channel<string> channel, string file, string header)
    {
        await using var w = new StreamWriter(Path.Combine(_outDir, file), false, new UTF8Encoding(false), 1 << 20);
        await w.WriteLineAsync(header);
        var lastFlush = Stopwatch.StartNew();
        await foreach (var line in channel.Reader.ReadAllAsync())
        {
            await w.WriteLineAsync(line);
            if (lastFlush.ElapsedMilliseconds > 2000)
            {
                await w.FlushAsync();
                lastFlush.Restart();
            }
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);
}

/// <summary>Running statistics of one phase, for the adaptive decisions (saturation, fan-out limit).</summary>
public sealed class PhaseStats
{
    private readonly object _lock = new();
    private readonly List<double> _latenciesMs = [];
    public long Ok;
    public long Errors;
    public long GenLimited;
    public double DurationS;

    public void Record(int status, double latencyMs)
    {
        lock (_lock)
        {
            if (status is >= 200 and < 300)
            {
                Ok++;
                _latenciesMs.Add(latencyMs);
            }
            else Errors++;
        }
    }

    public double Percentile(double p)
    {
        lock (_lock)
        {
            if (_latenciesMs.Count == 0) return double.NaN;
            var sorted = _latenciesMs.ToArray();
            Array.Sort(sorted);
            return sorted[Math.Min(sorted.Length - 1, (int)(p / 100 * sorted.Length))];
        }
    }

    public double ErrorRate => Ok + Errors == 0 ? 0 : Errors / (double)(Ok + Errors);

    public double AchievedRps => DurationS <= 0 ? 0 : Ok / DurationS;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"ok {Ok}, err {Errors} ({ErrorRate:P1}), p50 {Percentile(50):0.0} ms, p99 {Percentile(99):0.0} ms, gen-limited {GenLimited}");
}
