using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace GarminAgent;

public sealed class GarminPortal(string root) : IAsyncDisposable
{
    private IPlaywright? playwright;
    private IBrowser? browser;
    private IBrowserContext? context;
    private IPage? page;
    private string SessionFile => Path.Combine(root, ".local", "browser-session.bin");
    private const string ActivitiesUrl = "https://connect.garmin.com/modern/activities";
    private static readonly Regex ActivityLink = new(@"^https://connect\.garmin\.com/modern/activity/([1-9][0-9]{0,19})(?:[/?#].*)?$", RegexOptions.CultureInvariant);

    public async Task OpenAsync()
    {
        playwright = await Playwright.CreateAsync();
        // Installed Edge avoids another browser download; this is an isolated browser context.
        browser = await playwright.Chromium.LaunchAsync(new() { Headless = false, Channel = "msedge" });
        string? storage = null;
        if (System.IO.File.Exists(SessionFile))
        {
            try
            {
                var bytes = ProtectedData.Unprotect(await System.IO.File.ReadAllBytesAsync(SessionFile), null, DataProtectionScope.CurrentUser);
                try { storage = Encoding.UTF8.GetString(bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            catch (CryptographicException) { throw new AgentException("No se puede abrir la sesión web con este usuario de Windows."); }
        }
        context = await browser.NewContextAsync(new() { AcceptDownloads = true, StorageState = storage });
        page = await context.NewPageAsync();
        page.SetDefaultTimeout(12000);
        await page.GotoAsync(ActivitiesUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
        Console.WriteLine("Si Garmin pide acceso, entra en la ventana de Edge y completa el MFA. El agente no recibe tu contraseña.");
        // Login success is an observable activity list, not just a redirect or a timer.
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (page.IsClosed) throw new AgentException("Se cerró el navegador antes de conectar Garmin.");
            if (new Uri(page.Url).Host == "connect.garmin.com" && await page.Locator("a[href*='/modern/activity/']").CountAsync() > 0)
            {
                await SaveSessionAsync();
                return;
            }
            await Task.Delay(1000);
        }
        throw new AgentException("No se observó la lista de actividades después de iniciar sesión. Ejecuta de nuevo login.");
    }

    public async Task<List<ActivityIdentity>> ListAsync()
    {
        var links = await page!.Locator("a[href*='/modern/activity/']").EvaluateAllAsync<Link[]>("els => els.map(e => ({href:e.href, text:(e.innerText || e.textContent || '').trim()}))");
        var result = new List<ActivityIdentity>();
        foreach (var link in links)
        {
            var match = ActivityLink.Match(link.href);
            if (!match.Success || result.Any(item => item.Id == match.Groups[1].Value)) continue;
            result.Add(new(match.Groups[1].Value, link.text.Length is > 0 and <= 500 ? link.text : "Actividad Garmin"));
            if (result.Count == 10) break;
        }
        return result;
    }

    public async Task<(ActivityIdentity Identity, string Zip)> DownloadAsync(ActivityIdentity identity, string runDirectory)
    {
        FitActivity.ValidateId(identity.Id);
        var metadataResponse = page!.WaitForResponseAsync(response => response.Status == 200
            && new Uri(response.Url).Host == "connect.garmin.com"
            && new Uri(response.Url).AbsolutePath.EndsWith("/activity-service/activity/" + identity.Id, StringComparison.Ordinal), new() { Timeout = 20000 });
        await page.GotoAsync("https://connect.garmin.com/modern/activity/" + identity.Id, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        try
        {
            // Observe the page's own authenticated metadata response. Never pass DOM, cookies,
            // headers or arbitrary webpage instructions to the model.
            var response = await metadataResponse;
            var value = JsonNode.Parse(await response.TextAsync());
            var name = value?["activityName"]?.GetValue<string>();
            var sport = value?["activityTypeDTO"]?["typeKey"]?.GetValue<string>();
            var local = value?["summaryDTO"]?["startTimeLocal"]?.GetValue<string>();
            if (name is { Length: > 0 and <= 500 }) identity = identity with { Name = name, Sport = sport, LocalStart = local };
        }
        catch (Microsoft.Playwright.TimeoutException) { Console.WriteLine("No se observó metadata del portal; se usará la identidad del listado y el FIT."); }
        catch (System.Text.Json.JsonException) { Console.WriteLine("Metadata no disponible; se usará el FIT."); }
        var export = page.GetByText(new Regex("^(Export Original|Exportar original)$", RegexOptions.IgnoreCase));
        if (!await export.First.IsVisibleAsync())
        {
            var selectors = new[]
            {
                "button[aria-label='More']", "button[aria-label='Más']", "button[title='More']", "button[title='Settings']",
                ".activity-settings .dropdown-toggle", ".activity-settings-icon", ".activity-actions .dropdown-toggle",
                "#activitySettingsMenu", "#activity-settings", ".activity-details .icon-gear", ".icon-gear"
            };
            var opened = false;
            foreach (var selector in selectors)
            {
                var candidates = page.Locator(selector);
                if (await candidates.CountAsync() != 1 || !await candidates.IsVisibleAsync()) continue;
                await candidates.ClickAsync();
                if (await export.CountAsync() == 1 && await export.IsVisibleAsync()) { opened = true; break; }
            }
            if (!opened)
            {
                var menu = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(More|Settings|Más|Configuración|Opciones|Actions|Acciones)$", RegexOptions.IgnoreCase) });
                if (await menu.CountAsync() == 1) await menu.ClickAsync();
            }
        }
        if (await export.CountAsync() != 1 || !await export.IsVisibleAsync())
            throw new AgentException("No se encontró Exportar original. El portal pudo cambiar; hay que ajustar los selectores de GarminPortal.cs.");
        var download = await page.RunAndWaitForDownloadAsync(() => export.ClickAsync(), new() { Timeout = 60000 });
        if (await download.FailureAsync() is not null) throw new AgentException("Garmin no completó la descarga.");
        var destination = Path.Combine(runDirectory, "original.zip");
        await download.SaveAsAsync(destination);
        if (new FileInfo(destination).Length > FitActivity.MaxFitBytes) throw new AgentException("ZIP descargado demasiado grande.");
        await SaveSessionAsync();
        return (identity, destination);
    }
    private async Task SaveSessionAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionFile)!);
        var bytes = Encoding.UTF8.GetBytes(await context!.StorageStateAsync(new() { IndexedDB = true }));
        try { await System.IO.File.WriteAllBytesAsync(SessionFile, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public async ValueTask DisposeAsync()
    {
        if (context is not null) await context.CloseAsync();
        if (browser is not null) await browser.CloseAsync();
        playwright?.Dispose();
    }
    private sealed record Link(string href, string text);
}
