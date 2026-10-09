using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dynastream.Fit;

namespace GarminAgent;

public sealed record ActivityIdentity(string Id, string Name, string? Sport = null, string? LocalStart = null);

public static class FitActivity
{
    public const long MaxFitBytes = 32 * 1024 * 1024;
    public static void ValidateId(string id)
    {
        if (!Regex.IsMatch(id, "^[1-9][0-9]{0,19}$", RegexOptions.CultureInvariant))
            throw new AgentException("ID Garmin inválido; debe provenir de un enlace de actividad.");
    }
    public static JsonObject ConvertZip(string zipPath, ActivityIdentity identity, string fallbackTimezone)
    {
        ValidateId(identity.Id);
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > 100) throw new AgentException("El ZIP contiene demasiados archivos.");
        var candidates = zip.Entries.Where(e => e.FullName.EndsWith(".fit", StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count != 1) throw new AgentException("Esta primera versión requiere un ZIP con exactamente un FIT.");
        var entry = candidates[0];
        if (entry.Length <= 0 || entry.Length > MaxFitBytes) throw new AgentException("El FIT excede el límite de 32 MB o está vacío.");
        // Read into memory; never extract ZIP paths supplied by an external file.
        using var input = entry.Open();
        using var bytes = new MemoryStream();
        input.CopyTo(bytes);
        bytes.Position = 0;
        var decoder = new Decode();
        if (!decoder.CheckIntegrity(bytes)) throw new AgentException("FIT inválido: cabecera o CRC no válidos.");
        bytes.Position = 0;
        var sessions = new List<SessionMesg>();
        ActivityMesg? activity = null;
        int? lastLat = null, lastLon = null;
        decoder.MesgEvent += (_, e) =>
        {
            if (e.mesg.Num == MesgNum.Session) sessions.Add(new SessionMesg(e.mesg));
            if (e.mesg.Num == MesgNum.Activity) activity = new ActivityMesg(e.mesg);
            if (e.mesg.Num == MesgNum.Record)
            {
                var record = new RecordMesg(e.mesg);
                if (record.GetPositionLat() is int lat && record.GetPositionLong() is int lon) { lastLat = lat; lastLon = lon; }
            }
        };
        decoder.Read(bytes);
        if (sessions.Count != 1) throw new AgentException("FIT sin sesión o multideporte: esta versión no combina sesiones.");
        var session = sessions[0];
        var utc = session.GetStartTime()?.GetDateTime() ?? throw new AgentException("El FIT no tiene hora de inicio.");
        utc = System.DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var duration = session.GetTotalTimerTime() ?? throw new AgentException("El FIT no tiene duración.");
        var distance = session.GetTotalDistance() ?? throw new AgentException("El FIT no tiene distancia; no se inventará un valor.");
        int? offset = null;
        if (identity.LocalStart is not null && System.DateTime.TryParse(identity.LocalStart, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            var delta = (System.DateTime.SpecifyKind(local, DateTimeKind.Unspecified) - System.DateTime.SpecifyKind(utc, DateTimeKind.Unspecified)).TotalMinutes;
            if (Math.Abs(delta) <= 840 && delta == Math.Truncate(delta)) offset = (int)delta;
        }
        if (offset is null && activity?.GetTimestamp() is { } end && activity.GetLocalTimestamp() is { } localEnd)
        {
            var delta = ((long)localEnd - end.GetTimeStamp()) / 60d;
            if (Math.Abs(delta) <= 840 && delta == Math.Truncate(delta)) offset = (int)delta;
        }
        var timeSource = offset is null ? "configured_timezone" : "garmin";
        if (offset is null) offset = (int)TimeZoneInfo.FindSystemTimeZoneById(fallbackTimezone).GetUtcOffset(utc).TotalMinutes;
        var sport = identity.Sport ?? SportName(session.GetSport());
        var row = new JsonObject
        {
            ["id"] = identity.Id, ["name"] = identity.Name, ["sport"] = sport,
            ["startedAt"] = utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["localDate"] = utc.AddMinutes(offset.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["utcOffsetMinutes"] = offset, ["durationSeconds"] = (double)duration, ["distanceMeters"] = (double)distance
        };
        void Add(string key, double? value) { if (value is { } n && double.IsFinite(n) && n >= 0) row[key] = Math.Round(n, 3); }
        Add("elapsedSeconds", session.GetTotalElapsedTime());
        Add("averageSpeedMps", session.GetEnhancedAvgSpeed() ?? session.GetAvgSpeed());
        Add("maxSpeedMps", session.GetEnhancedMaxSpeed() ?? session.GetMaxSpeed());
        Add("caloriesKcal", session.GetTotalCalories());
        Add("averageHeartRateBpm", session.GetAvgHeartRate());
        Add("maxHeartRateBpm", session.GetMaxHeartRate());
        Add("averagePowerWatts", session.GetAvgPower());
        Add("maxPowerWatts", session.GetMaxPower());
        Add("elevationGainMeters", session.GetTotalAscent());
        Add("elevationLossMeters", session.GetTotalDescent());
        Add("lapCount", session.GetNumLaps());
        Add("aerobicTrainingEffect", session.GetTotalTrainingEffect());
        Add("anaerobicTrainingEffect", session.GetTotalAnaerobicTrainingEffect());
        void Coordinates(string prefix, int? latitude, int? longitude)
        {
            if (latitude is not { } lat || longitude is not { } lon) return;
            var degreesLat = lat * (180d / 2147483648d);
            var degreesLon = lon * (180d / 2147483648d);
            if (Math.Abs(degreesLat) > 90 || Math.Abs(degreesLon) > 180) return;
            row[prefix + "Latitude"] = Math.Round(degreesLat, 7);
            row[prefix + "Longitude"] = Math.Round(degreesLon, 7);
        }
        Coordinates("start", session.GetStartPositionLat(), session.GetStartPositionLong());
        Coordinates("end", lastLat, lastLon);
        ValidateRow(row);
        // This diagnostic is returned separately, never included in the strict API body.
        LastTimeSource = timeSource;
        return new JsonObject { ["activities"] = new JsonArray(row) };
    }
    public static string LastTimeSource { get; private set; } = "unknown";
    public static void ValidateRow(JsonObject row)
    {
        ValidateId(row["id"]!.GetValue<string>());
        var name = row["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 500) throw new AgentException("Nombre de actividad inválido.");
        if (!Regex.IsMatch(row["sport"]?.GetValue<string>() ?? "", "^[a-z][a-z0-9_]{0,63}$")) throw new AgentException("Tipo de deporte inválido.");
        if (!System.DateTime.TryParseExact(row["startedAt"]?.GetValue<string>(), "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new AgentException("Fecha UTC inválida.");
        if (!System.DateTime.TryParseExact(row["localDate"]?.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new AgentException("Fecha local inválida.");
        foreach (var key in new[] { "durationSeconds", "distanceMeters" })
        {
            var number = row[key]?.GetValue<double>() ?? double.NaN;
            if (!double.IsFinite(number) || number < 0 || number > 1e12) throw new AgentException($"Medición inválida: {key}.");
        }
    }
    private static string SportName(Sport? sport) => sport switch
    {
        Sport.Running => "running", Sport.Cycling => "cycling", Sport.Walking => "walking",
        Sport.Swimming => "swimming", Sport.Hiking => "hiking", _ => "other"
    };
}
