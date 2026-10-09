using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace GarminAgent;

/// <summary>A normal installed browser, owned by this run, with a temporary profile and local CDP.</summary>
public sealed class NativeBrowser : IAsyncDisposable
{
    private readonly Process process;
    private readonly string profile;
    private readonly string localRoot;
    public Uri Endpoint { get; }
    private NativeBrowser(Process process, string profile, string localRoot, Uri endpoint)
        => (this.process, this.profile, this.localRoot, Endpoint) = (process, profile, localRoot, endpoint);

    public static async Task<NativeBrowser> StartAsync(string root, string initialUrl)
    {
        // Never open or modify the user's ordinary Chrome/Edge profile.
        var localRoot = Path.GetFullPath(Path.Combine(root, ".local"));
        var profile = Path.Combine(localRoot, "browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var info = new ProcessStartInfo(FindExecutable()) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--user-data-dir=" + profile);
        info.ArgumentList.Add("--remote-debugging-port=0");
        info.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        info.ArgumentList.Add("--no-first-run");
        info.ArgumentList.Add("--no-default-browser-check");
        info.ArgumentList.Add(initialUrl);
        // No credential arguments, automation flags, fingerprint scripts, or extensions.
        var process = Process.Start(info) ?? throw new AgentException("No se pudo abrir Chrome o Edge.");
        NativeBrowser? owned = null;
        try
        {
            var activePort = Path.Combine(profile, "DevToolsActivePort");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (process.HasExited) throw new AgentException("El navegador se cerró antes de habilitar la conexión local.");
                if (System.IO.File.Exists(activePort))
                {
                    string[] lines;
                    try { lines = await System.IO.File.ReadAllLinesAsync(activePort); }
                    catch (IOException) { await Task.Delay(200); continue; }
                    if (lines.Length > 0 && int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535)
                    {
                        owned = new(process, profile, localRoot, new Uri("http://127.0.0.1:" + port));
                        return owned;
                    }
                }
                await Task.Delay(200);
            }
            throw new AgentException("No apareció el puerto local del navegador. Una política de Chrome/Edge puede impedir CDP.");
        }
        catch
        {
            owned ??= new(process, profile, localRoot, new Uri("http://127.0.0.1"));
            await owned.DisposeAsync();
            throw;
        }
    }

    public async Task WaitForGarminLoginAsync(TimeSpan timeout)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited) throw new NeedsLoginException("Se cerró el navegador antes de completar el acceso a Garmin.");
            try
            {
                var targets = JsonNode.Parse(await http.GetStringAsync(new Uri(Endpoint, "/json/list"))) as JsonArray;
                if (targets?.Any(target => target?["type"]?.GetValue<string>() == "page"
                    && GarminRoutes.IsApp(target["url"]?.GetValue<string>() ?? "")) == true) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(500);
        }
        throw new NeedsLoginException("No se completó el acceso en el navegador normal. Ejecuta login otra vez.");
    }

    public async ValueTask DisposeAsync()
    {
        // Only the process created with our unique temporary profile is owned here.
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await process.WaitForExitAsync(timeout.Token);
            }
        }
        catch (InvalidOperationException) { }
        catch (OperationCanceledException) { }
        finally { process.Dispose(); }
        await Task.Delay(250);
        var resolved = Path.GetFullPath(profile);
        var boundary = Path.GetFullPath(localRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)
            || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(resolved), "^browser-[a-f0-9]{32}$"))
            throw new AgentException("No se eliminó el perfil temporal porque su ruta no pasó la validación.");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true); return; }
            catch (IOException) { await Task.Delay(300); }
            catch (UnauthorizedAccessException) { await Task.Delay(300); }
        }
        Console.WriteLine("El navegador dejó archivos temporales bloqueados dentro de .local; no se publican en Git.");
    }

    private static string FindExecutable()
    {
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) };
        foreach (var relative in new[] { "Google/Chrome/Application/chrome.exe", "Microsoft/Edge/Application/msedge.exe" })
            foreach (var directory in roots)
            {
                var candidate = Path.Combine(directory, relative);
                if (System.IO.File.Exists(candidate)) return candidate;
            }
        throw new AgentException("Instala Chrome o Edge para conectar Garmin.");
    }
}

public static class GarminRoutes
{
    public const string Activities = "https://connect.garmin.com/app/activities";
    public const string SignIn = "https://connect.garmin.com/signin/?service=https%3A%2F%2Fconnect.garmin.com%2Fapp";
    public const string ActivitySelector = "a[href*='/app/activity/'], a[href*='/modern/activity/']";
    public static bool IsApp(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "connect.garmin.com"
        && (uri.AbsolutePath is "/app" or "/modern" || uri.AbsolutePath.StartsWith("/app/", StringComparison.Ordinal)
            || uri.AbsolutePath.StartsWith("/modern/", StringComparison.Ordinal));
    public static string? ActivityId(string url)
    {
        if (!IsApp(url)) return null;
        var path = new Uri(url).AbsolutePath;
        var match = System.Text.RegularExpressions.Regex.Match(path, "^/(?:app|modern)/activity/([1-9][0-9]{0,19})/?$");
        return match.Success ? match.Groups[1].Value : null;
    }
    public static bool IsService(string url, string servicePath) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host is "connect.garmin.com" or "connectapi.garmin.com"
        && uri.AbsolutePath.Contains(servicePath, StringComparison.Ordinal);
}
