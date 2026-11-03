using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LoadLab;

/// <summary>
/// Writes the SQL that seeds a fresh load-test database with one live event (a closed GPX loop, many laps), its
/// confirmed runners and a device token per runner. Only token hashes go into the SQL (as the app stores them);
/// the raw tokens go to <c>tokens.csv</c> for the generator. Runs against the throwaway load-test stack only.
/// </summary>
public static class Seed
{
    public static void Write(string gpxPath, int runners, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var course = Course.Load(gpxPath);
        var gpx = Convert.ToBase64String(File.ReadAllBytes(gpxPath));
        var organizerId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        var rng = new Random(463);
        var sql = new StringBuilder();
        var csv = new StringBuilder("index,participantId,token,speedMps,offsetM\n");
        sql.AppendLine("BEGIN;");
        sql.AppendLine($"""
            INSERT INTO "Organizers" ("Id","CreatedAt","Email","EmailConfirmedAt","EmailNormalized","Language","Name")
            VALUES ('{organizerId}', now(), 'loadtest@lab463.invalid', now(), 'loadtest@lab463.invalid', 'sk', 'Load test');
            INSERT INTO "Events" ("Id","OrganizerId","Name","Location","StartTime","Sport","State","Laps","PointToPoint","SpectatorDelaySeconds","GpxBytes","CreatedAt","Version")
            VALUES ('{eventId}', '{organizerId}', 'NUC load test', 'Žilina', now() - interval '10 minutes', 'Running', 'Live', 400, false, 30,
                    decode('{gpx}', 'base64'), now(), gen_random_uuid());
            INSERT INTO "Courses" ("EventId","LapLengthM","AscentM","DescentM","PointCount","IsClosed","UploadedAt")
            VALUES ('{eventId}', {course.LengthM.ToString("0.0", CultureInfo.InvariantCulture)}, 0, 0, 0, true, now());
            """);
        for (var i = 0; i < runners; i++)
        {
            var pid = Guid.CreateVersion7();
            var token = Base64Url(RandomNumberGenerator.GetBytes(32));
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
            var speed = 2.4 + rng.NextDouble() * 2.2; // 2.4..4.6 m/s: 3:37..6:57 min/km
            var offset = rng.NextDouble() * 300;
            var gender = i % 3 == 0 ? "female" : "male";
            sql.AppendLine($"""
                INSERT INTO "Participants" ("Id","EventId","Bib","Name","Email","EmailNormalized","Gender","Language","Status","IsMinor","ShowNameToSpectators","NicknameHidden","CreatedAt")
                VALUES ('{pid}', '{eventId}', '{i + 1}', 'Runner {i + 1:0000}', 'r{i}@lab463.invalid', 'r{i}@lab463.invalid', '{gender}', 'sk', 'Confirmed', false, true, false, now() + interval '{i} milliseconds');
                INSERT INTO "DeviceTokens" ("Id","EventId","ParticipantId","TokenHash","IssuedAt") VALUES ('{Guid.CreateVersion7()}', '{eventId}', '{pid}', '\x{hash}'::bytea, now());
                """);
            csv.Append(CultureInfo.InvariantCulture, $"{i},{pid},{token},{speed:0.000},{offset:0.0}\n");
        }
        sql.AppendLine("COMMIT;");
        File.WriteAllText(Path.Combine(outDir, "seed.sql"), sql.ToString());
        File.WriteAllText(Path.Combine(outDir, "tokens.csv"), csv.ToString());
        File.WriteAllText(Path.Combine(outDir, "meta.json"), JsonSerializer.Serialize(new { eventId = eventId.ToString("D"), runners, lapM = course.LengthM }));
        Console.WriteLine($"Seeded {runners} runners for event {eventId:D} into {outDir}");
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
