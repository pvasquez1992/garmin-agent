using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Dynastream.Fit;
using Microsoft.Extensions.AI;

namespace GarminAgent;

public static class Fixture
{
    public static readonly ActivityIdentity Identity = new("99999999999999999999", "Demo ficticia — carrera de 5 km", "running");
    public static string CreateZip(string directory)
    {
        var path = Path.Combine(directory, "original.zip");
        using var fit = new MemoryStream();
        var encoder = new Encode(ProtocolVersion.V20);
        encoder.Open(fit);
        var start = new System.DateTime(2026, 10, 9, 1, 0, 0, DateTimeKind.Utc);
        var file = new FileIdMesg();
        file.SetType(Dynastream.Fit.File.Activity);
        file.SetTimeCreated(new Dynastream.Fit.DateTime(start));
        encoder.Write(file);
        var session = new SessionMesg();
        session.SetSport(Sport.Running);
        session.SetStartTime(new Dynastream.Fit.DateTime(start));
        session.SetTimestamp(new Dynastream.Fit.DateTime(start.AddMinutes(30)));
        session.SetTotalTimerTime(1800);
        session.SetTotalElapsedTime(1830);
        session.SetTotalDistance(5000);
        session.SetAvgHeartRate(140);
        session.SetTotalCalories(350);
        session.SetStartPositionLat(0);
        session.SetStartPositionLong(0);
        encoder.Write(session);
        var activity = new ActivityMesg();
        var end = new Dynastream.Fit.DateTime(start.AddMinutes(30));
        activity.SetTimestamp(end);
        activity.SetLocalTimestamp(end.GetTimeStamp() - 4 * 3600);
        encoder.Write(activity);
        encoder.Close();
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var output = zip.CreateEntry("demo.fit").Open();
        fit.Position = 0;
        fit.CopyTo(output);
        return path;
    }
}

public static class Checks
{
    public static async Task<int> RunAsync(string root)
    {
        var directory = Path.Combine(root, ".local", "checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var assertions = 0;
        void Check(bool success, string description)
        {
            if (!success) throw new AgentException("Prueba falló: " + description);
            assertions++;
        }
        void Reject(Action action, string description)
        {
            try { action(); } catch (AgentException) { assertions++; return; }
            throw new AgentException("Debió rechazar: " + description);
        }
        var zip = Fixture.CreateZip(directory);
        var payload = FitActivity.ConvertZip(zip, Fixture.Identity, "America/New_York");
        var row = payload["activities"]![0]!;
        Check(row["distanceMeters"]!.GetValue<double>() == 5000 && row["durationSeconds"]!.GetValue<double>() == 1800, "unidades FIT y campos requeridos");
        Check(row["localDate"]!.GetValue<string>() == "2026-10-08" && row["utcOffsetMinutes"]!.GetValue<int>() == -240, "medianoche y offset del FIT");
        Check(row["startedAt"]!.GetValue<string>() == "2026-10-09T01:00:00.000Z", "UTC en formato estricto");
        Check(row["startLatitude"]!.GetValue<double>() == 0 && row["startLongitude"]!.GetValue<double>() == 0, "coordenadas cero válidas");
        Check(row["id"]!.GetValue<string>() == Fixture.Identity.Id, "ID Garmin separado del FIT");
        Check(GarminRoutes.ActivityId("https://connect.garmin.com/app/activity/12345") == "12345", "ruta Garmin app actual");
        Check(GarminRoutes.ActivityId("https://connect.garmin.com/modern/activity/12345?x=1") == "12345", "ruta Garmin modern anterior");
        Check(GarminRoutes.ActivityId("https://evil.example/app/activity/12345") is null, "rechaza dominio ajeno");
        Check(GarminRoutes.ActivityId("http://connect.garmin.com/app/activity/12345") is null, "rechaza HTTP");
        Check(!GarminRoutes.IsApp("https://sso.garmin.com/portal/sso/en-US/sign-in"), "SSO no significa sesión de actividades");
        Check(GarminRoutes.IsService("https://connectapi.garmin.com/activitylist-service/activities/search/activities", "/activitylist-service/"), "host API de Garmin");
        Check(!GarminRoutes.IsService("https://evil.example/activitylist-service/activities", "/activitylist-service/"), "rechaza respuestas de servicio ajenas");
        Reject(() => FitActivity.ValidateId("../x"), "ID malicioso");
        Reject(() => FitActivity.ValidateId("0"), "ID cero");
        var bad = Path.Combine(directory, "bad.zip");
        using (var archive = ZipFile.Open(bad, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("bad.fit").Open())) writer.Write("not FIT");
        Reject(() => FitActivity.ConvertZip(bad, Fixture.Identity, "America/New_York"), "FIT corrupto");
        var wrongReceipt = new FakeHttp(HttpStatusCode.OK, "{\"data\":{\"processed\":0}}");
        try { await PersonalApi.PostAsync(payload, "test-only", wrongReceipt); throw new AgentException("Recibo falso aceptado."); }
        catch (AgentException ex) when (ex.Message.Contains("recibo")) { assertions++; }
        var http = new FakeHttp(HttpStatusCode.OK, "{\"data\":{\"processed\":1}}");
        var receipt = await PersonalApi.PostAsync(payload, "test-only", http);
        Check(receipt["processed"]!.GetValue<int>() == 1 && http.CorrectRequest, "POST exacto con bearer y body activities");
        await using var tools = new ActivityTools(root, Path.Combine(directory, "flow"), true, false, "America/New_York");
        Directory.CreateDirectory(Path.Combine(directory, "flow"));
        try { await tools.ExecuteAsync("publicar_actividades", new()); throw new AgentException("Publicó antes de validar."); }
        catch (AgentException ex) when (ex.Message.Contains("estado")) { assertions++; }
        using var trace = new TraceLog(Path.Combine(directory, "trace.jsonl"));
        var model = new FakeModel();
        await new ImportAgent(model, tools, trace).RunAsync();
        Check(tools.Finished && model.Calls == 6, "ciclo modelo → herramienta → resultado con límite");
        var expiredModel = new FakeModel();
        try
        {
            await new ImportAgent(expiredModel, new ExpiredTools(), trace).RunAsync();
            throw new AgentException("Sesión vencida no detuvo el agente.");
        }
        catch (NeedsLoginException) { assertions++; }
        Check(expiredModel.Calls == 1, "sesión vencida detiene sin repetir login ni publicar");
        Reject(() => Foundry.CreateClient(new(new Uri("https://remote.example"), "test")), "modelo remoto rechazado antes de conectar");
        foreach (var fault in new[] { "parallel", "unknown", "text", "extra", "numeric" })
        {
            var flow = Path.Combine(directory, fault);
            Directory.CreateDirectory(flow);
            await using var checkedTools = new ActivityTools(root, flow, true, false, "America/New_York");
            var faultyModel = new FakeModel(fault);
            await new ImportAgent(faultyModel, checkedTools, trace).RunAsync();
            Check(checkedTools.Finished && faultyModel.Calls == 7, "framework rechaza y recupera: " + fault);
            Check(faultyModel.SawToolResults, "framework devuelve resultados a la siguiente consulta: " + fault);
            if (fault is "extra" or "numeric")
                Check(faultyModel.SawToolError, "argumentos inválidos no se descartan silenciosamente: " + fault);
        }
        var stuckModel = new FakeModel("always-text");
        try
        {
            await new ImportAgent(stuckModel, new ExpiredTools(), trace).RunAsync();
            throw new AgentException("Texto sin herramientas marcó éxito.");
        }
        catch (AgentException ex) when (ex.Message.Contains("12 turnos")) { assertions++; }
        Check(stuckModel.Calls == 12, "límite global de consultas también sin tool calling");
        Console.WriteLine($"Pruebas: {assertions} comprobaciones correctas. Solo datos ficticios y HTTP simulado.");
        return 0;
    }
    private sealed class ExpiredTools : ITools
    {
        public IReadOnlyList<ToolSpec> Available => [new("abrir_garmin", "login", new JsonObject())];
        public bool Finished => false;
        public Task<JsonObject> ExecuteAsync(string name, JsonObject arguments) =>
            Task.FromException<JsonObject>(new NeedsLoginException("Sesión vencida de prueba."));
    }
    private sealed class FakeModel(string? fault = null) : IChatClient
    {
        public int Calls;
        public bool SawToolResults;
        public bool SawToolError;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var results = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
            SawToolResults |= results.Length > 0;
            SawToolError |= results.Any(result => result.Result?.ToString()?.Contains("error") == true);
            var tool = options!.Tools!.Single();
            Dictionary<string, object?> arguments = tool.Name == "descargar_original"
                ? new() { ["activityId"] = Fixture.Identity.Id } : [];
            if (fault == "always-text" || (fault == "text" && Calls == 1))
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "He terminado sin usar herramientas.")));
            var name = fault == "unknown" && Calls == 1 ? "publicar_actividades" : tool.Name;
            if (fault == "extra" && Calls == 1) arguments["unexpected"] = "do not discard";
            if (fault == "numeric" && Calls == 3) arguments["activityId"] = 12345;
            List<AIContent> contents = [new FunctionCallContent("call_" + Calls, name, arguments)];
            if (fault == "parallel" && Calls == 1)
                contents.Add(new FunctionCallContent("parallel", "publicar_actividades", new Dictionary<string, object?>()));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
    private sealed class FakeHttp(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public bool CorrectRequest;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            CorrectRequest = request.Method == HttpMethod.Post && request.RequestUri!.ToString() == PersonalApi.Origin + "/sync/activities"
                && request.Headers.Authorization?.Scheme == "Bearer" && json?["activities"] is JsonArray { Count: 1 };
            return new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
