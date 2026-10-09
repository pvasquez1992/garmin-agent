using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OpenAI;

namespace GarminAgent;

public sealed record FoundryConnection(Uri Endpoint, string Model);

// Keep the existing CLI for model loading. The client connects Agent Framework
// to the loopback OpenAI-compatible API; it never calls a cloud model provider.
public static class Foundry
{
    public static async Task<FoundryConnection> DiscoverAsync(string alias)
    {
        async Task<JsonNode> Command(params string[] arguments)
        {
            var info = new ProcessStartInfo("foundry.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in arguments) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new AgentException("No se pudo ejecutar Foundry Local.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(deadline.Token);
            var value = await stdout;
            await stderr;
            if (process.ExitCode != 0) throw new AgentException("Foundry Local no respondió. Revisa foundry status.");
            return JsonNode.Parse(value) ?? throw new AgentException("Respuesta de Foundry Local inválida.");
        }
        var status = await Command("status", "--output", "json");
        if (status["service"]?["ready"]?.GetValue<bool>() != true) throw new AgentException("Foundry no está listo. Inicia tu modelo con foundry run qwen2.5-7b.");
        var endpoint = new Uri(status["service"]?["webUrls"]?[0]?.GetValue<string>() ?? throw new AgentException("Foundry no expone un endpoint local."));
        ValidateEndpoint(endpoint);
        var model = (await Command("model", "info", alias, "--output", "json"))["model"];
        if (model?["supportsToolCalling"]?.GetValue<bool>() != true) throw new AgentException("El modelo elegido no anuncia soporte de herramientas.");
        if (model?["cached"]?.GetValue<bool>() != true) throw new AgentException("Descarga y abre primero el modelo en Foundry; el agente no descarga modelos automáticamente.");
        return new(endpoint, model["id"]!.GetValue<string>());
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || !endpoint.IsLoopback || endpoint.Scheme != "http")
            throw new AgentException("Solo se permite inferencia en localhost.");
    }

    public static IChatClient CreateClient(FoundryConnection connection)
    {
        ValidateEndpoint(connection.Endpoint);
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            Timeout = TimeSpan.FromMinutes(4)
        };
        var client = new OpenAIClient(new ApiKeyCredential("local-not-a-secret"), new OpenAIClientOptions
        {
            Endpoint = new Uri(connection.Endpoint, "/v1"),
            Transport = new HttpClientPipelineTransport(http),
            NetworkTimeout = TimeSpan.FromMinutes(4),
            RetryPolicy = new ClientRetryPolicy(0)
        }).GetChatClient(connection.Model).AsIChatClient();
        return new OwnedChatClient(client, http);
    }

    private sealed class OwnedChatClient(IChatClient inner, HttpClient http) : DelegatingChatClient(inner)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) http.Dispose();
        }
    }
}
