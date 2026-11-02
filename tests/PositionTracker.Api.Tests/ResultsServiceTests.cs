using PositionTracker.Api.Operations;
using PositionTracker.Core;
using PositionTracker.Core.Runtime;
using PositionTracker.Data.Entities;

namespace PositionTracker.Api.Tests;

public class ResultsServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);

    private static ResultRow Row(string id, string bib, string category, ParticipantState state, double netMin, double distanceM = 37_500, string name = "Name") =>
        new(id, bib, name, null, category, false, null, state, T0, state == ParticipantState.Finished ? T0.AddMinutes(netMin) : null,
            state == ParticipantState.Finished ? TimeSpan.FromMinutes(netMin) : null, null, distanceM, 3, 8, 12, TimeSpan.FromMinutes(netMin), TimeSpan.Zero, 400, 400, []);

    private static ParticipantEntity Person(string id, string gender, string name = "Name", bool consent = true) =>
        new() { Id = Guid.Parse(id), Bib = "1", Name = name, Email = "a@b.c", EmailNormalized = "a@b.c", Gender = gender, Language = "sk", ShowNameToSpectators = consent };

    private const string A = "00000000-0000-0000-0000-00000000000a";
    private const string B = "00000000-0000-0000-0000-00000000000b";
    private const string C = "00000000-0000-0000-0000-00000000000c";
    private const string D = "00000000-0000-0000-0000-00000000000d";
    private const string E = "00000000-0000-0000-0000-00000000000e";

    [Fact]
    public void Ranks_go_to_finishers_only_overall_per_category_and_per_gender_and_the_others_are_dnf_or_dns()
    {
        var rows = new[]
        {
            Row(A, "1", "Open", ParticipantState.Finished, 60), Row(B, "2", "Women", ParticipantState.Finished, 65),
            Row(C, "3", "Open", ParticipantState.Finished, 70), Row(D, "4", "Open", ParticipantState.Dnf, 0, 20_000),
            Row(E, "5", "Open", ParticipantState.NotStarted, 0, 0),
        };
        var people = new Dictionary<string, ParticipantEntity>
        {
            [A] = Person(A, "male"), [B] = Person(B, "female"), [C] = Person(C, "female"), [D] = Person(D, "male"), [E] = Person(E, "male"),
        };

        var lines = ResultsService.Build(rows, people, new Dictionary<Guid, string>());

        Assert.Equal([1, 2, 3, null, null], lines.Select(l => l.Rank));
        Assert.Equal([1, 1, 2, null, null], lines.Select(l => l.CategoryRank)); // Open: A then C; Women: B
        Assert.Equal([1, 1, 2, null, null], lines.Select(l => l.GenderRank)); // male: A; female: B then C
        Assert.Equal(["finished", "finished", "finished", "dnf", "dns"], lines.Select(l => l.Status));
    }

    private static ResultsView View(params ResultLine[] rows) => View(false, rows);

    private static ResultsView View(bool pointToPoint, params ResultLine[] rows) =>
        new("e", "Race", "Place", T0, "cycling", "finished", false, T0.AddHours(2), pointToPoint ? 1 : 3, pointToPoint, rows.Length, 0, 0, rows);

    private static ResultLine Line(string bib, string name, bool consent, bool minor = false, string? nickname = null) =>
        new(A, 1, 1, 1, bib, name, nickname, "male", "Open", null, minor, consent, "finished", T0, T0.AddHours(1), TimeSpan.FromMinutes(75),
            TimeSpan.Zero, 37_500, 3, 8, 12, TimeSpan.FromMinutes(70), TimeSpan.FromMinutes(5), 400, 400,
            [new SplitLine(1, 0, TimeSpan.FromMinutes(10), "green")]);

    [Fact]
    public void The_csv_has_a_byte_order_mark_semicolons_and_comma_decimals_and_hides_names_without_consent_by_default()
    {
        var csv = ResultsService.ToCsv(View(Line("7", "Jana Nováková", consent: false), Line("8", "Peter Mladý", consent: true, minor: true, nickname: "Rocket"),
            Line("9", "Eva Veľká", consent: true)), namesOnlyWithConsent: true);

        Assert.StartsWith("﻿Poradie;", csv);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.Contains(";#7;", lines[1]);            // no consent: the number
        Assert.DoesNotContain("Nováková", csv);
        Assert.Contains(";Rocket;", lines[2]);        // a minor: the nickname, never the name
        Assert.DoesNotContain("Mladý", csv);
        Assert.Contains(";Eva Veľká;", lines[3]);
        Assert.Contains("1:15:00", lines[1]);         // the net time
        Assert.Contains("37,50", lines[1]);           // a decimal comma
        Assert.Contains(";0:10:00", lines[1]);        // the sector time column
        Assert.Contains("Nováková", ResultsService.ToCsv(View(Line("7", "Jana Nováková", false)), namesOnlyWithConsent: false));
    }

    [Fact]
    public void A_point_to_point_csv_has_no_laps_column_and_names_the_splits_by_section_only()
    {
        var laps = ResultsService.ToCsv(View(Line("7", "Jana", consent: true)), namesOnlyWithConsent: false).Split("\r\n");
        var p2p = ResultsService.ToCsv(View(true, Line("7", "Jana", consent: true)), namesOnlyWithConsent: false).Split("\r\n");

        Assert.Contains(";Kolá;", laps[0]);
        Assert.EndsWith(";Kolo 1, úsek 1", laps[0]);
        Assert.DoesNotContain("Kolá", p2p[0]);
        Assert.DoesNotContain("Kolo", p2p[0]);
        Assert.EndsWith(";Úsek 1", p2p[0]);
        Assert.Equal(laps[0].Split(';').Length - 1, p2p[0].Split(';').Length);
        Assert.Equal(p2p[0].Split(';').Length, p2p[1].Split(';').Length);
        Assert.Contains(";37,50;28,8;", p2p[1]); // the distance is followed by the average speed, the laps cell is gone
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("-2", "'-2")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("a;b", "\"a;b\"")]
    [InlineData("plain", "plain")]
    public void A_cell_that_a_spreadsheet_could_run_as_a_formula_is_neutralised_and_separators_are_quoted(string input, string expected) =>
        Assert.Equal(expected, ResultsService.Cell(input));
}
