using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GarminAgent;

public sealed class ActivityTools(string root, string runDirectory, bool demo, bool send, string timezone, CancellationToken cancellationToken = default, bool useSavedCredentials = true) : ITools, IAsyncDisposable
{
    private GarminPortal? portal;
    private int state;
    private List<ActivityIdentity> observed = [];
    private ActivityIdentity? selected;
    private string? zip;
    private JsonObject? payload;
    public bool Finished { get; private set; }
    private static ToolSpec Empty(string name, string description) => new(name, description + " This tool takes NO arguments. Always call with {}.",
        new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["required"] = new JsonArray(), ["additionalProperties"] = false });
    public IReadOnlyList<ToolSpec> Available => state switch
    {
        0 => [Empty("abrir_garmin", "Open Garmin Connect in the browser and reuse or establish the local web session.")],
        1 => [Empty("listar_actividades", "List recent activities in portal order. The first is the latest.")],
        2 => [new("descargar_original", "Download the original ZIP for the latest listed activity. Use its exact string activityId.", new JsonObject
        {
            ["type"] = "object", ["properties"] = new JsonObject { ["activityId"] = new JsonObject { ["type"] = "string", ["pattern"] = "^[1-9][0-9]{0,19}$" } },
            ["required"] = new JsonArray("activityId"), ["additionalProperties"] = false
        })],
        3 => [Empty("convertir_zip", "Decode the downloaded FIT with the official Garmin SDK and validate the API JSON. Do not generate measurements yourself.")],
        4 when send || demo => [Empty("publicar_actividades", demo ? "Simulate the POST to /sync/activities. Demo never contacts the real API." : "POST the validated JSON to the configured personal API. Credentials are held by code, not the model.")],
        4 or 5 => [Empty("finalizar", state == 5 ? "Finish after a verified import receipt." : "Finish preparation mode. JSON is validated but has not been posted.")],
        _ => []
    };
    public async Task<JsonObject> ExecuteAsync(string name, JsonObject arguments)
    {
        if (!Available.Any(tool => tool.Name == name)) throw new AgentException("La herramienta no está disponible en este estado.");
        var allowed = name == "descargar_original" ? new[] { "activityId" } : Array.Empty<string>();
        if (arguments.Any(pair => !allowed.Contains(pair.Key)) || arguments.Count != allowed.Length)
            throw new AgentException(allowed.Length == 0
                ? $"{name} no recibe argumentos: vuelve a llamar {name} con {{}}. El programa ya conserva el archivo y la actividad."
                : "descargar_original requiere solo activityId como cadena. Usa el ID de la primera actividad del listado.");
        switch (name)
        {
            case "abrir_garmin":
                if (!demo)
                {
                    if (portal is not null) await portal.DisposeAsync();
                    portal = new GarminPortal(root, useSavedCredentials);
                    await portal.OpenAsync(renewExpiredSession: true, cancellationToken: cancellationToken);
                }
                state = 1;
                return new() { ["connected"] = true, ["simulated"] = demo };
            case "listar_actividades":
                observed = demo ? [Fixture.Identity] : await portal!.ListAsync();
                if (observed.Count == 0) throw new AgentException("No hay actividades visibles en el portal.");
                state = 2;
                return new() { ["activities"] = JsonSerializer.SerializeToNode(observed.Select(item => new { activityId = item.Id, name = item.Name })), ["order"] = "latest_first" };
            case "descargar_original":
                string id;
                try { id = arguments["activityId"]!.GetValue<string>(); }
                catch { throw new AgentException("activityId debe ser una cadena, no un número."); }
                FitActivity.ValidateId(id);
                if (observed[0].Id != id) throw new AgentException("Solo se puede descargar la primera actividad observada, la más reciente.");
                if (demo) { selected = observed[0]; zip = Fixture.CreateZip(runDirectory); }
                else { (selected, zip) = await portal!.DownloadAsync(observed[0], runDirectory); }
                state = 3;
                return new() { ["activityId"] = id, ["downloaded"] = true, ["format"] = "zip", ["simulated"] = demo };
            case "convertir_zip":
                payload = FitActivity.ConvertZip(zip!, selected!, timezone);
                var json = payload.ToJsonString(JsonSettings.Pretty);
                if (Encoding.UTF8.GetByteCount(json) > 256 * 1024) throw new AgentException("JSON demasiado grande para la API.");
                await System.IO.File.WriteAllTextAsync(Path.Combine(runDirectory, "activities.json"), json);
                state = 4;
                return new() { ["validated"] = true, ["count"] = 1, ["activityId"] = selected!.Id, ["timeSource"] = FitActivity.LastTimeSource };
            case "publicar_actividades":
                if (demo) { state = 5; return new() { ["processed"] = 1, ["simulated"] = true }; }
                if (!send) throw new AgentException("El envío no está habilitado en esta ejecución.");
                var receipt = await PersonalApi.PostAsync(payload!, Secrets.SyncKey(root));
                state = 5;
                await System.IO.File.WriteAllTextAsync(Path.Combine(runDirectory, "receipt.json"), receipt.ToJsonString(JsonSettings.Pretty));
                return receipt;
            case "finalizar":
                Finished = true;
                return new() { ["finished"] = true, ["mode"] = demo ? "demo" : send ? "posted" : "prepared", ["activityId"] = selected!.Id };
            default: throw new AgentException("Herramienta desconocida.");
        }
    }
    public async ValueTask DisposeAsync() { if (portal is not null) await portal.DisposeAsync(); }
}

public static class Secrets
{
    public static string SyncKey(string root)
    {
        var file = Path.GetFullPath(Path.Combine(root, "..", "myGarming", ".tools", "garmin-sync-config.bin"));
        if (!System.IO.File.Exists(file)) throw new AgentException("No está la configuración local cifrada de la API.");
        var bytes = ProtectedData.Unprotect(System.IO.File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
        try
        {
            using var config = JsonDocument.Parse(bytes);
            if (config.RootElement.GetProperty("GARMIN_API_URL").GetString() != PersonalApi.Origin)
                throw new AgentException("La configuración no corresponde a nuestra API.");
            return config.RootElement.GetProperty("GARMIN_SYNC_KEY").GetString() ?? throw new AgentException("Falta la clave de sincronización.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

public static class PersonalApi
{
    public const string Origin = "https://my-garmin-api.pvasquez1992.workers.dev";
    public static async Task<JsonObject> PostAsync(JsonObject payload, string key, HttpMessageHandler? testHandler = null)
    {
        if (payload.Count != 1 || payload["activities"] is not JsonArray { Count: 1 } rows || rows[0] is not JsonObject row)
            throw new AgentException("Esta versión solo envía una actividad validada por ejecución.");
        FitActivity.ValidateRow(row);
        using var handler = testHandler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        using var request = new HttpRequestMessage(HttpMethod.Post, Origin + "/sync/activities")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new AgentException($"La API devolvió HTTP {(int)response.StatusCode}; no se confirma importación.");
        var result = await response.Content.ReadFromJsonAsync<JsonObject>();
        if (result?["data"]?["processed"]?.GetValue<int>() != rows.Count) throw new AgentException("El recibo de la API no coincide con lo enviado.");
        return new() { ["processed"] = rows.Count, ["simulated"] = false };
    }
}
