using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using Microsoft.Playwright;

namespace GarminAgent;

public sealed class GarminPortal(string root) : IAsyncDisposable
{
    private IPlaywright? playwright;
    private IBrowser? browser;
    private IBrowserContext? context;
    private IPage? page;
    private IResponse? recentList;
    private NativeBrowser? native;
    private string SessionFile => Path.Combine(root, ".local", "browser-session.bin");
    private const string ActivitiesUrl = GarminRoutes.Activities;

    public async Task OpenAsync(bool interactive = false)
    {
        if (!interactive && !System.IO.File.Exists(SessionFile))
            throw new NeedsLoginException("Primero conecta la sesión web con dotnet run -- login; después repite run.");
        string? storage = null;
        if (!interactive && System.IO.File.Exists(SessionFile))
        {
            try
            {
                var bytes = ProtectedData.Unprotect(await System.IO.File.ReadAllBytesAsync(SessionFile), null, DataProtectionScope.CurrentUser);
                try { storage = Encoding.UTF8.GetString(bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            catch (CryptographicException) { throw new NeedsLoginException("No se puede abrir la sesión web con este usuario de Windows. Revisa la configuración local."); }
        }
        native = await NativeBrowser.StartAsync(root, interactive ? GarminRoutes.SignIn : "about:blank");
        if (interactive)
        {
            Console.WriteLine("Inicia sesión en la ventana normal de Chrome/Edge y completa el MFA si aparece. El agente se conectará después del acceso.");
            await native.WaitForGarminLoginAsync(TimeSpan.FromMinutes(10));
        }
        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.ConnectOverCDPAsync(native.Endpoint.ToString(), new() { NoDefaults = interactive, Timeout = 15000 });
        context = interactive ? browser.Contexts.First() : await browser.NewContextAsync(new() { AcceptDownloads = true, StorageState = storage });
        page = interactive ? context.Pages.FirstOrDefault() ?? await context.NewPageAsync() : await context.NewPageAsync();
        page.Response += (_, response) =>
        {
            if (response.Status == 200 && GarminRoutes.IsService(response.Url, "/activitylist-service/activities/search/activities"))
                recentList = response;
        };
        page.SetDefaultTimeout(12000);
        await page.GotoAsync(ActivitiesUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
        // Login success is an observable activity list, not just a redirect or a timer.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (page.IsClosed) throw new AgentException("Se cerró el navegador antes de conectar Garmin.");
            if (GarminRoutes.IsApp(page.Url) && await page.Locator(GarminRoutes.ActivitySelector).CountAsync() > 0)
            {
                await SaveSessionAsync();
                return;
            }
            await Task.Delay(1000);
        }
        throw new NeedsLoginException("No se observó una sesión válida. Ejecuta dotnet run -- login para autenticarte y después repite run.");
    }

    public async Task<List<ActivityIdentity>> ListAsync()
    {
        var stage = "abrir listado";
        try
        {
            recentList = null;
            await page!.GotoAsync(ActivitiesUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            EnsureConnectedUrl();
            stage = "esperar enlaces";
            await page.Locator(GarminRoutes.ActivitySelector).First.WaitForAsync(new() { Timeout = 30000 });
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (recentList is null && DateTimeOffset.UtcNow < deadline) await Task.Delay(200);
            stage = "leer enlaces";
            var links = await ReadLinksAsync(page!);
            var visible = new Dictionary<string, string>();
            foreach (var link in links)
            {
                var id = GarminRoutes.ActivityId(link.href);
                if (id is not null) visible.TryAdd(id, link.text);
            }
            if (recentList is null) throw new AgentException("No se observó la lista de datos del portal; no se puede comprobar cuál actividad es la más reciente.");
            stage = "leer respuesta de actividades";
            var raw = JsonNode.Parse(await recentList.TextAsync()) as JsonArray
                ?? throw new AgentException("Formato de listado Garmin inesperado.");
            var dated = new List<(ActivityIdentity Identity, System.DateTime Utc)>();
            foreach (var item in raw)
            {
                var id = item?["activityId"]?.ToString() ?? "";
                if (!visible.ContainsKey(id)) continue;
                FitActivity.ValidateId(id);
                var start = item?["startTimeGMT"]?.GetValue<string>();
                if (!System.DateTime.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)) continue;
                var name = item?["activityName"]?.GetValue<string>();
                var sport = item?["activityType"]?["typeKey"]?.GetValue<string>();
                var local = item?["startTimeLocal"]?.GetValue<string>();
                if (name is not { Length: > 0 and <= 500 }) continue;
                dated.Add((new(id, name, sport, local), utc));
            }
            return dated.OrderByDescending(item => item.Utc).Take(10).Select(item => item.Identity).ToList();
        }
        catch (PlaywrightException ex)
        {
            // Classify known failures without logging URLs, response bodies or browser state.
            var reason = ex.Message.Contains("No resource with given identifier", StringComparison.OrdinalIgnoreCase) ? "respuesta ya no disponible"
                : ex.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase) ? "la página cambió durante la lectura"
                : ex.Message.Contains("net::ERR_ABORTED", StringComparison.OrdinalIgnoreCase) ? "navegación interrumpida"
                : ex.Message.Contains("Target page, context or browser has been closed", StringComparison.OrdinalIgnoreCase) ? "navegador cerrado"
                : ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ? "tiempo de espera agotado"
                : ex.Message.Contains("Unsupported type", StringComparison.OrdinalIgnoreCase) ? "tipo de resultado no admitido"
                : ex.Message.Contains("deserialize", StringComparison.OrdinalIgnoreCase) ? "conversión del resultado"
                : ex.Message.Contains("EvalError", StringComparison.OrdinalIgnoreCase) ? "evaluación rechazada"
                : ex.Message.Contains("TypeError", StringComparison.OrdinalIgnoreCase) ? "tipo incorrecto en JavaScript"
                : ex.Message.Contains("ReferenceError", StringComparison.OrdinalIgnoreCase) ? "referencia de JavaScript inexistente"
                : ex.Message.Contains("constructor", StringComparison.OrdinalIgnoreCase) ? "constructor del resultado"
                : ex.Message.Contains("JSON", StringComparison.OrdinalIgnoreCase) ? "conversión JSON"
                : "error de navegador";
            throw new AgentException($"No se pudo leer Garmin en el paso '{stage}': {reason}.");
        }
    }

    public async Task<(ActivityIdentity Identity, string Zip)> DownloadAsync(ActivityIdentity identity, string runDirectory)
    {
        FitActivity.ValidateId(identity.Id);
        var metadataResponse = page!.WaitForResponseAsync(response => response.Status == 200
            && GarminRoutes.IsService(response.Url, "/activity-service/activity/" + identity.Id)
            && new Uri(response.Url).AbsolutePath.EndsWith("/activity-service/activity/" + identity.Id, StringComparison.Ordinal), new() { Timeout = 20000 });
        await page.GotoAsync("https://connect.garmin.com/app/activity/" + identity.Id, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        EnsureConnectedUrl();
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
        catch (System.TimeoutException) { Console.WriteLine("No se observó metadata del portal; se usará la identidad del listado y el FIT."); }
        catch (System.Text.Json.JsonException) { Console.WriteLine("Metadata no disponible; se usará el FIT."); }
        // The metadata response can arrive before React has rendered the toolbar.
        try { await page.Locator("[class*='ActivitySettingsMenu_menuContainer'] > button").WaitForAsync(new() { Timeout = 15000 }); }
        catch (System.TimeoutException) { } // Older portal versions use the fallback selectors below.
        var export = page.GetByText(new Regex("^(Export Original|Export File|Exportar original|Exportar archivo)$", RegexOptions.IgnoreCase));
        if (!await export.First.IsVisibleAsync())
        {
            var selectors = new[]
            {
                "[class*='ActivitySettingsMenu_menuContainer'] > button",
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
                try { await export.First.WaitForAsync(new() { Timeout = 5000 }); }
                catch (System.TimeoutException) { }
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
    private void EnsureConnectedUrl()
    {
        if (!GarminRoutes.IsApp(page!.Url))
            throw new NeedsLoginException("Garmin pidió volver a iniciar sesión. Ejecuta dotnet run -- login y repite run.");
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (context is not null) await context.CloseAsync();
            if (browser is not null) await browser.CloseAsync();
        }
        finally
        {
            playwright?.Dispose();
            if (native is not null) await native.DisposeAsync();
        }
    }
    internal static async Task<Link[]> ReadLinksAsync(IPage page)
    {
        // Serialize in the browser and deserialize in .NET, rather than asking CDP
        // to materialize a custom record array directly.
        var json = await page.Locator(GarminRoutes.ActivitySelector).EvaluateAllAsync<string>(
            "els => JSON.stringify(els.map(e => ({href:e.href, text:(e.innerText || e.textContent || '').trim()})))");
        return JsonSerializer.Deserialize<Link[]>(json)
            ?? throw new AgentException("No se pudo interpretar el listado de enlaces de Garmin.");
    }
    internal sealed record Link(string href, string text);
}
