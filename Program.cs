using GarminAgent;
using System.Text.Json;

try
{
    var command = args.FirstOrDefault() ?? "help";
    string FindRoot()
    {
        foreach (var starting in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(starting); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "GarminAgent.csproj"))) return directory.FullName;
        throw new AgentException("No se encontró GarminAgent.csproj. Ejecuta el programa desde la carpeta del proyecto.");
    }
    var root = FindRoot();
    if (command == "self-test") return await Checks.RunAsync(root);
    if (command == "browser-check") return await BrowserChecks.RunAsync(root);
    if (command == "help")
    {
        Console.WriteLine("""
        Garmin Agent — .NET 10 + Microsoft Agent Framework + Foundry Local + Playwright
        dotnet run -- demo                Modelo real; portal y POST simulados; FIT ficticio real
        dotnet run -- login               Abre Garmin para conectar y cifrar la sesión web
        dotnet run -- run                 Descarga la última actividad y prepara JSON; sin POST
        dotnet run -- run --enviar        Descarga, convierte y envía a /sync/activities
        dotnet run -- self-test           Pruebas locales; no usan Garmin ni producción
        dotnet run -- browser-check       Prueba navegador/CDP/descarga con datos ficticios
        Opcional: --model qwen2.5-7b --timezone America/New_York
        """);
        return 0;
    }
    string Option(string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        if (i < 0) return fallback;
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
            throw new AgentException($"Falta el valor de {name}.");
        return args[i + 1];
    }
    if (command == "login")
    {
        await using var portal = new GarminPortal(root);
        await portal.OpenAsync(interactive: true);
        Console.WriteLine("Sesión web guardada cifrada para tu usuario de Windows. La ventana se cerrará ahora; puedes continuar con run.");
        return 0;
    }
    if (command is not ("run" or "demo")) throw new AgentException("Comando desconocido. Usa dotnet run -- help.");
    var demo = command == "demo";
    if (demo && args.Contains("--enviar")) throw new AgentException("Demo nunca publica en la API.");
    var run = Path.Combine(root, ".local", "runs", System.DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(run);
    using var trace = new TraceLog(Path.Combine(run, "trace.jsonl"));
    var connection = await Foundry.DiscoverAsync(Option("--model", "qwen2.5-7b"));
    Console.WriteLine($"Foundry Local: {connection.Model}. Modo: {(demo ? "demo" : args.Contains("--enviar") ? "envío real" : "preparar JSON")}.");
    await using var tools = new ActivityTools(root, run, demo, args.Contains("--enviar"), Option("--timezone", "America/New_York"));
    using var model = Foundry.CreateClient(connection);
    var agent = new ImportAgent(model, tools, trace);
    await agent.RunAsync();
    Console.WriteLine($"Archivos locales: {run}");
    return 0;
}
catch (NeedsLoginException ex)
{
    Console.Error.WriteLine("Estado: reauth_required. " + ex.Message);
    return 2;
}
catch (AgentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (Exception ex)
{
    // Browser/HTTP exceptions can contain page contents or credentials. Never dump them.
    Console.Error.WriteLine($"Ejecución detenida ({ex.GetType().Name}). Consulta README.md para diagnóstico.");
    return 1;
}

namespace GarminAgent
{
    public sealed class AgentException(string message) : Exception(message);
    public sealed class NeedsLoginException(string message) : Exception(message);
    public static class JsonSettings
    {
        public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    }
}
