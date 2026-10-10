using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;

namespace GarminAgent;

public sealed record FoundryConnection(Uri Endpoint, string Model);

// The SDK owns the native runtime, model and local web service in this process.
// The web service bridges the runtime to Agent Framework's existing IChatClient.
public static class Foundry
{
    public static async Task<FoundrySession> StartAsync(string root, string modelName, CancellationToken cancellationToken = default)
    {
        var appData = Path.Combine(root, ".local", "foundry");
        var modelCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".foundry", "cache", "models");
        var configuration = new Configuration
        {
            AppName = "garmin-agent",
            AppDataDir = appData,
            ModelCacheDir = modelCache,
            LogsDir = Path.Combine(appData, "logs"),
            LogLevel = Microsoft.AI.Foundry.Local.LogLevel.Error,
            Web = new Configuration.WebService { Urls = "http://127.0.0.1:0" }
        };
        Console.WriteLine("[foundry/sdk] iniciando el runtime local...");
        await FoundryLocalManager.CreateAsync(configuration, NullLogger.Instance, cancellationToken);
        var manager = FoundryLocalManager.Instance;
        var session = new FoundrySession(manager);
        try
        {
            Console.WriteLine("[foundry/sdk] preparando aceleración para este equipo...");
            var progress = new ProgressDisplay();
            await manager.DownloadAndRegisterEpsAsync(progress.ExecutionProvider, cancellationToken);
            var catalog = await manager.GetCatalogAsync(cancellationToken);
            var model = await catalog.GetModelAsync(modelName, cancellationToken)
                ?? await catalog.GetModelVariantAsync(modelName, cancellationToken)
                ?? throw new AgentException($"El modelo '{modelName}' no está disponible en el catálogo local.");
            if (model.Info.SupportsToolCalling != true)
                throw new AgentException("El modelo elegido no anuncia soporte de herramientas.");
            session.SetModel(model);
            Console.WriteLine($"[foundry/sdk] preparando {model.Id}; reutilizando la caché cuando esté disponible...");
            await model.DownloadAsync(progress.Model, cancellationToken);
            Console.WriteLine("[foundry/sdk] cargando el modelo...");
            await model.LoadAsync(cancellationToken);
            await manager.StartWebServiceAsync(cancellationToken);
            session.MarkWebStarted();
            var urls = manager.Urls?.ToArray();
            if (urls is not { Length: 1 } || !Uri.TryCreate(urls[0], UriKind.Absolute, out var endpoint))
                throw new AgentException("El SDK no devolvió un único endpoint local.");
            ValidateEndpoint(endpoint);
            session.Connection = new(endpoint, model.Id);
            Console.WriteLine("[foundry/sdk] modelo listo; endpoint privado en localhost.");
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
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

    private sealed class ProgressDisplay
    {
        private string? lastProvider;
        private int lastProviderStep = -1;
        private int lastModelStep = -1;
        public void ExecutionProvider(string name, double percent)
        {
            var step = (int)(percent / 10);
            if (lastProvider == name && step == lastProviderStep) return;
            lastProvider = name;
            lastProviderStep = step;
            Console.WriteLine($"[foundry/sdk] acelerador {name}: {percent:F0}%");
        }
        public void Model(float percent)
        {
            var step = (int)(percent / 10);
            if (step == lastModelStep) return;
            lastModelStep = step;
            Console.WriteLine($"[foundry/sdk] descarga del modelo: {percent:F0}%");
        }
    }
}

public sealed class FoundrySession(FoundryLocalManager manager) : IAsyncDisposable
{
    private Microsoft.AI.Foundry.Local.IModel? model;
    private bool webStarted;
    private bool disposed;
    public FoundryConnection Connection { get; internal set; } = null!;
    internal void SetModel(Microsoft.AI.Foundry.Local.IModel value) => model = value;
    internal void MarkWebStarted() => webStarted = true;

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { if (webStarted) await manager.StopWebServiceAsync(cleanup.Token); }
        catch (Exception ex) { Console.Error.WriteLine($"[foundry/sdk] cierre del endpoint: {ex.GetType().Name}."); }
        try
        {
            if (model is not null) await model.UnloadAsync(cleanup.Token);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[foundry/sdk] descarga de memoria: {ex.GetType().Name}."); }
        finally
        {
            manager.Dispose();
            Console.WriteLine("[foundry/sdk] runtime cerrado; la descarga permanece en caché.");
        }
    }
}
