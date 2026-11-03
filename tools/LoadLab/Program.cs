using System.Globalization;
using System.Text.Json;
using LoadLab;

// LoadLab — load and performance measurement of PositionTracker on the home server (see perf/README.md).
//   seed <gpx> <runners> <outDir>
//   run  <baseUrl> <seedDir> <apiKey> <gpx> <outDir> [timeScale] [onlyPhases]
// timeScale shortens every phase (0.1 = a 10x shorter smoke run). onlyPhases is a comma list (E0,E1,...).

if (args.Length >= 4 && args[0] == "seed")
{
    Seed.Write(args[1], int.Parse(args[2], CultureInfo.InvariantCulture), args[3]);
    return 0;
}
if (args.Length < 6 || args[0] != "run")
{
    Console.Error.WriteLine("usage: seed <gpx> <runners> <outDir> | run <baseUrl> <seedDir> <apiKey> <gpx> <outDir> [timeScale] [onlyPhases]");
    return 2;
}

var baseUrl = args[1];
var seedDir = args[2];
var apiKey = args[3];
var gpx = args[4];
var outDir = args[5];
var scale = args.Length > 6 ? double.Parse(args[6], CultureInfo.InvariantCulture) : 1.0;
var only = args.Length > 7 ? args[7].Split(',').ToHashSet() : null;
bool Run(string phase) => only is null || only.Contains(phase);
double S(double seconds) => Math.Max(5, seconds * scale);
string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(seedDir, "meta.json"))).RootElement;
var eventId = meta.GetProperty("eventId").GetString()!;
var runners = meta.GetProperty("runners").GetInt32();
var field = runners - 10;          // the probes are not part of the load
var maxRate = field / 1.1;         // every token at most ~1 request per 1.05 s

await using var engine = new Engine(baseUrl, eventId, apiKey, Path.Combine(seedDir, "tokens.csv"), gpx, outDir);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; engine.Abort("Ctrl+C"); };
await engine.StartAsync();
engine.Mark("START", $"event {eventId}, runners {runners}, scale {scale}");

// E0 — noise floor: an empty field, only the 10 probe runners (0.2 req/s each) and one organizer view
if (Run("E0")) await engine.PhaseAsync("E0 noise floor", S(420), _ => 0);

// E1 — field size: more and more phones active, each sending every 5 s (the broadcast cost grows with the field)
if (Run("E1"))
    foreach (var n in new[] { 0.25, 0.5, 0.75, 1.0 }.Select(x => (int)Math.Round(field * x)).Distinct())
        await engine.PhaseAsync($"E1 field {n}", S(120), _ => n / 5.0, F($"{n} active runners, every 5 s ({n / 5.0:0} req/s)"), pool: n);

// E2 — step response at the full field: 0 -> the whole field every 5 s, then the step down
if (Run("E2"))
{
    await engine.PhaseAsync("E2 pre-step", S(120), _ => 0);
    var stepRps = Math.Round(field / 5.0);
    await engine.PhaseAsync("E2 step up", S(360), _ => stepRps, F($"{stepRps:0} req/s (the whole field every 5 s)"));
    await engine.PhaseAsync("E2 step down", S(180), _ => 0);
}

// E3 — staircase to saturation
var capacity = 150.0;
if (Run("E3"))
{
    double[] steps = [25, 50, 75, 100, 125, 150, 200, 250, 300, 350, 400, 500, 650, 800, 1000, 1200];
    var lastGood = 0.0;
    foreach (var rps in steps.Where(s => s <= maxRate))
    {
        var st = await engine.PhaseAsync($"E3 step {rps:0}", S(90), _ => rps, $"{rps:0} req/s");
        var saturated = st.Percentile(99) > 1500 || st.ErrorRate > 0.02 || st.AchievedRps < 0.9 * rps;
        if (saturated)
        {
            engine.Mark("E3 saturation", F($"at {rps:0} req/s: {st}"));
            break;
        }
        lastGood = rps;
        if (engine.Aborted) break;
    }
    capacity = Math.Max(50, lastGood);
    engine.Mark("E3 capacity", F($"{capacity:0} req/s"));
    await engine.PhaseAsync("E3 cooldown", S(180), _ => 0);
}

// E4 — logarithmic chirp around 45 % of capacity: 1/400 Hz .. 1/4 Hz in 20 minutes (system identification)
if (Run("E4"))
{
    var mean = 0.45 * capacity;
    var amp = 0.40 * capacity;
    double f0 = 1.0 / 400, f1 = 1.0 / 4, T = S(1200);
    var k = f1 / f0;
    await engine.PhaseAsync("E4 chirp", T,
        t => mean + amp * Math.Sin(2 * Math.PI * f0 * T / Math.Log(k) * (Math.Pow(k, t / T) - 1)),
        F($"mean {mean:0} amp {amp:0} req/s, f {f0:0.####}..{f1:0.##} Hz log, T {T:0} s"));
    await engine.PhaseAsync("E4 cooldown", S(180), _ => 0);
}

// E5 — impulse: 1000 runners (distinct tokens) within one second (a mass start), three times
if (Run("E5"))
{
    var burst = Math.Min(1000, field);
    for (var i = 1; i <= 3; i++)
        await engine.PhaseAsync($"E5 impulse {i}", S(180), t => t is >= 30 and < 31 ? burst : 0, $"{burst} requests in 1 s at t=30 s");
}

// E6 — spectator fan-out sweep at a constant 30 % ingest
var goodSpectators = 0;
if (Run("E6"))
{
    var ingest = 0.3 * capacity;
    foreach (var n in new[] { 0, 5, 10, 20, 35, 50, 75, 100 })
    {
        await engine.SetSpectatorsAsync(n);
        var (b0, m0) = engine.SpectatorCounters();
        var o0 = engine.OrganizerMessages;
        var seconds = S(120);
        await engine.PhaseAsync($"E6 spectators {n}", seconds, _ => ingest, F($"{n} spectators, ingest {ingest:0} req/s"));
        var (b1, m1) = engine.SpectatorCounters();
        var orgRate = (engine.OrganizerMessages - o0) / seconds;
        var bps = (b1 - b0) / seconds;
        var perConn = n == 0 ? 0 : (m1 - m0) / seconds / n;
        engine.Mark($"E6 measured {n}", F($"{bps / 1e6:0.00} MB/s, {perConn:0.000} msg/s per spectator, organizer {orgRate:0.000} msg/s"));
        if (n > 0) goodSpectators = n;
        if (n > 0 && (perConn < 0.8 * orgRate || bps > 15e6))
        {
            engine.Mark("E6 limit", F($"{n} spectators: {bps / 1e6:0.00} MB/s, {perConn:0.000} msg/s vs organizer {orgRate:0.000}"));
            break;
        }
    }
    await engine.SetSpectatorsAsync(0);
    await engine.PhaseAsync("E6 cooldown", S(120), _ => 0);
}

// E7 — soak: a realistic race for 30 minutes (the whole field every 5 s, or half the capacity) with spectators
if (Run("E7"))
{
    var race = Math.Min(field / 5.0, 0.5 * capacity);
    var spectators = Math.Clamp(goodSpectators / 2, 5, 25);
    await engine.SetSpectatorsAsync(spectators);
    await engine.PhaseAsync("E7 soak", S(1800), _ => race, F($"{race:0} req/s, {spectators} spectators"));
    await engine.SetSpectatorsAsync(0);
    await engine.PhaseAsync("E7 cooldown", S(180), _ => 0);
}

engine.Mark("END");
return 0;
