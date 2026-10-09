using GarminAgent;
using System.Text.Json;

try
{
    var command = args.FirstOrDefault() ?? "help";
    var root = Path.GetFullPath(Environment.CurrentDirectory);
    if (!File.Exists(Path.Combine(root, "GarminAgent.csproj")))
        throw new AgentException("Ejecuta el programa dentro de C:\\myGarming\\garmin-agent.");
    if (command == "self-test") return await Checks.RunAsync(root);
    if (command == "help")
    {
        Console.WriteLine("""
        Garmin Agent — laboratorio local .NET 10 + Foundry Local + Playwright
        dotnet run -- demo                Modelo real; portal y POST simulados; FIT ficticio real
        dotnet run -- login               Abre Garmin para conectar y cifrar la sesión web
        dotnet run -- run                 Descarga la última actividad y prepara JSON; sin POST
        dotnet run -- run --enviar        Descarga, convierte y envía a /sync/activities
        dotnet run -- self-test           Pruebas locales; no usan Garmin ni producción
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
        await portal.OpenAsync();
        Console.WriteLine("Sesión web guardada cifrada para tu usuario de Windows.");
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
    var agent = new ImportAgent(new Foundry(connection), tools, trace);
    await agent.RunAsync();
    Console.WriteLine($"Archivos locales: {run}");
    return 0;
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
    public static class JsonSettings
    {
        public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    }
}
