using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PositionTracker.Api.Events;
using PositionTracker.Api.Persistence;
using PositionTracker.Core;
using PositionTracker.Core.Runtime;
using PositionTracker.Data;
using PositionTracker.Data.Entities;

namespace PositionTracker.Api.Operations;

public sealed record SplitLine(int Lap, int SegmentIndex, TimeSpan Time, string Colour);

/// <summary>One line of the results as the organizer sees it (the full name; the export decides what to publish).</summary>
public sealed record ResultLine(
    string ParticipantId, int? Rank, int? CategoryRank, int? GenderRank, string Bib, string Name, string? Nickname, string? Gender,
    string? Category, string? Wave, bool IsMinor, bool NameConsent, string Status, DateTimeOffset Start, DateTimeOffset? FinishTime,
    TimeSpan? NetTime, TimeSpan? GapToLeader, double DistanceM, int Laps, double AvgSpeedMps, double MaxSpeedMps, TimeSpan MovingTime,
    TimeSpan StoppedTime, double AscentM, double DescentM, IReadOnlyList<SplitLine> Splits, double StartObservedM = 0)
{
    /// <summary>The first position lay this far along the course (m) or more: the rider was not seen at the start.</summary>
    public const double StartMissedM = 500;

    public bool StartMissed => StartObservedM >= StartMissedM;
}

/// <param name="Provisional">True while the event is not finished: the lines are the standing now, not the results.</param>
public sealed record ResultsView(
    string EventId, string Name, string Location, DateTimeOffset StartTime, string Sport, string State, bool Provisional,
    DateTimeOffset? EndedAt, int Laps, int Finished, int Dnf, int Dns, IReadOnlyList<ResultLine> Rows);

/// <summary>
/// The results of an event for its organizer. While the event runs they are the standing computed from the runtime; when the
/// event finishes the lines are stored (<see cref="SnapshotAsync"/>) so they stay after the runtime and the positions are gone.
/// Ranks (overall, category, gender) are given to finishers only, in net-time order.
/// </summary>
public sealed class ResultsService(AppDbContext db, EventRegistry registry)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The lines for the finish of <paramref name="ev"/>, built from its runtime; the caller adds them to the context.</summary>
    public static async Task<List<ResultRowEntity>> SnapshotAsync(AppDbContext db, EventEntity ev, EventRuntime runtime, CancellationToken ct)
    {
        var participants = await db.Participants.AsNoTracking().Where(p => p.EventId == ev.Id).ToListAsync(ct);
        var waves = await db.Waves.AsNoTracking().Where(w => w.EventId == ev.Id).ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        var byId = participants.ToDictionary(p => p.Id.ToString("D"), StringComparer.Ordinal);
        var lines = Build(runtime.BuildResults(), byId, waves);
        return lines.Select(l => new ResultRowEntity
        {
            Id = Guid.CreateVersion7(), EventId = ev.Id, ParticipantId = Guid.ParseExact(l.ParticipantId, "D"), Bib = l.Bib, Name = l.Name,
            Nickname = l.Nickname, Gender = l.Gender, Category = l.Category, Wave = l.Wave, IsMinor = l.IsMinor, NameConsent = l.NameConsent,
            Status = l.Status, Rank = l.Rank, CategoryRank = l.CategoryRank, GenderRank = l.GenderRank, Start = l.Start, FinishTime = l.FinishTime,
            NetTime = l.NetTime, GapToLeader = l.GapToLeader, DistanceM = l.DistanceM, Laps = l.Laps, AvgSpeedMps = l.AvgSpeedMps,
            MaxSpeedMps = l.MaxSpeedMps, MovingTime = l.MovingTime, StoppedTime = l.StoppedTime, AscentM = l.AscentM, DescentM = l.DescentM,
            StartObservedM = l.StartObservedM, SplitsJson = JsonSerializer.Serialize(l.Splits, Json),
        }).ToList();
    }

    internal static List<ResultLine> Build(IReadOnlyList<ResultRow> rows, IReadOnlyDictionary<string, ParticipantEntity> people,
        IReadOnlyDictionary<Guid, string> waves)
    {
        var lines = new List<ResultLine>();
        var categoryCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var genderCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var overall = 0;
        foreach (var r in rows)
        {
            people.TryGetValue(r.ParticipantId, out var person);
            var finished = r.State == ParticipantState.Finished && r.NetTime is not null;
            var status = finished ? "finished" : r.State == ParticipantState.NotStarted && r.TotalDistanceM <= 0 ? "dns" : "dnf";
            int? rank = null, categoryRank = null, genderRank = null;
            if (finished)
            {
                rank = ++overall;
                categoryRank = categoryCount[r.Category] = categoryCount.GetValueOrDefault(r.Category) + 1;
                var gender = person?.Gender ?? "";
                genderRank = genderCount[gender] = genderCount.GetValueOrDefault(gender) + 1;
            }
            lines.Add(new ResultLine(
                r.ParticipantId, rank, categoryRank, genderRank, r.Bib, person?.Name ?? r.Name, r.Nickname, person?.Gender, r.Category,
                person?.WaveId is { } w && waves.TryGetValue(w, out var wave) ? wave : null, r.IsMinor, person?.ShowNameToSpectators ?? false,
                status, r.Start, r.FinishTime, r.NetTime, r.GapToLeader, r.TotalDistanceM, r.Laps, r.AvgSpeedMps, r.MaxSpeedMps,
                r.MovingTime, r.StoppedTime, r.AscentM, r.DescentM,
                r.Segments.Select(s => new SplitLine(s.Lap, s.SegmentIndex, s.Time, s.Colour.ToString().ToLowerInvariant())).ToList(), r.StartObservedM));
        }
        return lines;
    }

    public async Task<EventResult<ResultsView>> GetAsync(Guid organizerId, string eventId, CancellationToken ct)
    {
        if (!EventService.TryParseId(eventId, out var id)) return EventErrors.NotFound;
        var ev = await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id && e.OrganizerId == organizerId, ct);
        if (ev is null) return EventErrors.NotFound;

        List<ResultLine> lines = [];
        var stored = ev.State == EventState.Finished ? await db.ResultRows.AsNoTracking().Where(r => r.EventId == ev.Id).ToListAsync(ct) : [];
        if (stored.Count > 0)
        {
            lines = stored
                .OrderBy(r => r.Rank ?? int.MaxValue).ThenBy(r => r.Status == "dnf" ? 0 : 1).ThenByDescending(r => r.DistanceM)
                .ThenBy(r => r.Bib, StringComparer.OrdinalIgnoreCase)
                .Select(r => new ResultLine(
                    r.ParticipantId.ToString("D"), r.Rank, r.CategoryRank, r.GenderRank, r.Bib, r.Name, r.Nickname, r.Gender, r.Category, r.Wave,
                    r.IsMinor, r.NameConsent, r.Status, r.Start, r.FinishTime, r.NetTime, r.GapToLeader, r.DistanceM, r.Laps, r.AvgSpeedMps,
                    r.MaxSpeedMps, r.MovingTime, r.StoppedTime, r.AscentM, r.DescentM,
                    JsonSerializer.Deserialize<List<SplitLine>>(r.SplitsJson, Json) ?? [], r.StartObservedM))
                .ToList();
        }
        else if (registry.Get(RuntimeFactory.RuntimeId(ev.Id)) is { } runtime)
        {
            var participants = await db.Participants.AsNoTracking().Where(p => p.EventId == ev.Id).ToListAsync(ct);
            var waves = await db.Waves.AsNoTracking().Where(w => w.EventId == ev.Id).ToDictionaryAsync(w => w.Id, w => w.Name, ct);
            lines = Build(runtime.BuildResults(), participants.ToDictionary(p => p.Id.ToString("D"), StringComparer.Ordinal), waves);
        }

        return new EventResult<ResultsView>(new ResultsView(
            ev.Id.ToString("D"), ev.Name, ev.Location, ev.StartTime, ev.Sport.ToString().ToLowerInvariant(), ev.State.ToString().ToLowerInvariant(),
            ev.State != EventState.Finished, ev.EndedAt, ev.Laps,
            lines.Count(l => l.Status == "finished"), lines.Count(l => l.Status == "dnf"), lines.Count(l => l.Status == "dns"), lines), null);
    }

    /// <summary>
    /// The results as CSV for the organizer to publish: <c>;</c> separated with a comma as the decimal mark (the Slovak spreadsheet
    /// default), UTF-8 with a byte order mark. With <paramref name="namesOnlyWithConsent"/> a name appears only where the participant
    /// agreed to show it (and is not a minor), the others are listed by nickname or number. A cell that could be read as a formula is
    /// made harmless.
    /// </summary>
    public static string ToCsv(ResultsView view, bool namesOnlyWithConsent)
    {
        var splitKeys = view.Rows.SelectMany(r => r.Splits).Select(s => (s.Lap, s.SegmentIndex)).Distinct().OrderBy(k => k.Lap).ThenBy(k => k.SegmentIndex).ToList();
        var c = CultureInfo.GetCultureInfo("sk-SK");
        var sb = new StringBuilder("﻿");
        void Row(IEnumerable<string?> cells) => sb.Append(string.Join(';', cells.Select(Cell))).Append("\r\n");
        Row(new[]
        {
            "Poradie", "Poradie v kategórii", "Poradie podľa pohlavia", "Číslo", "Meno", "Kategória", "Vlna", "Pohlavie", "Stav", "Štart", "Cieľ",
            "Čistý čas", "Strata", "Vzdialenosť (km)", "Kolá", "Priemerná rýchlosť (km/h)", "Najvyššia rýchlosť (km/h)", "Čas v pohybe",
            "Čas státia", "Stúpanie (m)", "Klesanie (m)", "Poznámka",
        }.Concat(splitKeys.Select(k => $"Kolo {k.Lap}, úsek {k.SegmentIndex + 1}")));
        foreach (var r in view.Rows)
        {
            var name = !namesOnlyWithConsent || (r.NameConsent && !r.IsMinor) ? r.Name : (string.IsNullOrWhiteSpace(r.Nickname) ? "#" + r.Bib : r.Nickname);
            var status = r.Status switch { "finished" => "Dokončil", "dnf" => "DNF", _ => "DNS" };
            Row(new[]
            {
                r.Rank?.ToString(c), r.CategoryRank?.ToString(c), r.GenderRank?.ToString(c), r.Bib, name, r.Category, r.Wave,
                r.Gender switch { "male" => "M", "female" => "Ž", _ => r.Gender }, status,
                r.Start.ToString("d. M. yyyy H:mm:ss", c), r.FinishTime?.ToString("d. M. yyyy H:mm:ss", c), Span(r.NetTime), Span(r.GapToLeader),
                (r.DistanceM / 1000).ToString("0.00", c), r.Laps.ToString(c), (r.AvgSpeedMps * 3.6).ToString("0.0", c),
                (r.MaxSpeedMps * 3.6).ToString("0.0", c), Span(r.MovingTime), Span(r.StoppedTime), r.AscentM.ToString("0", c), r.DescentM.ToString("0", c),
                r.StartMissed ? $"Štart nezaznamenaný (prvá poloha na {(r.StartObservedM / 1000).ToString("0.0", c)} km)" : null,
            }.Concat(splitKeys.Select(k => Span(r.Splits.FirstOrDefault(s => s.Lap == k.Lap && s.SegmentIndex == k.SegmentIndex)?.Time))));
        }
        return sb.ToString();
    }

    private static string? Span(TimeSpan? t) =>
        t is { } s ? $"{(int)s.TotalHours}:{s.Minutes:00}:{s.Seconds:00}" : null;

    /// <summary>One CSV cell: quoted when needed, and a leading <c>= + - @</c> (a formula in a spreadsheet) is neutralised with an apostrophe.</summary>
    internal static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([';', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
