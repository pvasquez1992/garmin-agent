using Microsoft.Playwright;
using System.Security.Cryptography;
using System.Text;

namespace GarminAgent;

public static class BrowserChecks
{
    public static async Task<int> RunAsync(string root)
    {
        var directory = Path.Combine(root, ".local", "browser-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var zip = Fixture.CreateZip(directory);
        var checks = 0;
        void Check(bool ok, string description)
        {
            if (!ok) throw new AgentException("Falló la prueba de navegador: " + description);
            checks++;
        }
        // These browser pages and cookies are fictitious; no real Garmin login is attempted.
        await using var native = await NativeBrowser.StartAsync(root, "about:blank");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.ConnectOverCDPAsync(native.Endpoint.ToString(), new() { Timeout = 15000, NoDefaults = true });
        Check(native.Endpoint.IsLoopback, "conexión CDP local");
        var context = await browser.NewContextAsync(new() { AcceptDownloads = true });
        var page = await context.NewPageAsync();
        Console.WriteLine("Diagnóstico de Chrome: navigator.webdriver=" + (await page.EvaluateAsync<bool>("navigator.webdriver")).ToString().ToLowerInvariant());
        await page.SetContentAsync("""
            <html><body>
            <a href="https://connect.garmin.com/app/activity/12345">Nueva ruta</a>
            <a href="https://connect.garmin.com/modern/activity/67890">Ruta anterior</a>
            <a id="export" href="https://example.com/fixture.zip">Exportar original</a>
            </body></html>
            """);
        Check(await page.Locator(GarminRoutes.ActivitySelector).CountAsync() == 2, "selector de ambas rutas");
        var urls = await page.Locator(GarminRoutes.ActivitySelector).EvaluateAllAsync<string[]>("els => els.map(e => e.href)");
        Check(urls.Select(GarminRoutes.ActivityId).SequenceEqual(new[] { "12345", "67890" }), "IDs de enlaces del DOM");
        var bytes = await System.IO.File.ReadAllBytesAsync(zip);
        await context.RouteAsync("https://example.com/fixture.zip", route => route.FulfillAsync(new()
        {
            Status = 200, BodyBytes = bytes,
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/zip", ["Content-Disposition"] = "attachment; filename=fixture.zip" }
        }));
        var download = await page.RunAndWaitForDownloadAsync(() => page.Locator("#export").ClickAsync(), new() { Timeout = 15000 });
        var saved = Path.Combine(directory, "downloaded.zip");
        await download.SaveAsAsync(saved);
        Check(System.IO.File.Exists(saved), "descarga guardada mediante CDP");
        var payload = FitActivity.ConvertZip(saved, Fixture.Identity, "America/New_York");
        Check(payload["activities"]?[0]?["distanceMeters"]?.GetValue<double>() == 5000, "descarga ZIP → FIT → JSON");
        await context.AddCookiesAsync([new Cookie { Name = "fixture", Value = "not-a-real-session", Domain = "example.com", Path = "/", Secure = true }]);
        var state = Encoding.UTF8.GetBytes(await context.StorageStateAsync());
        var cipher = ProtectedData.Protect(state, null, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(state);
        var decoded = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
        try
        {
            var restored = await browser.NewContextAsync(new() { StorageState = Encoding.UTF8.GetString(decoded) });
            Check((await restored.CookiesAsync("https://example.com")).Any(cookie => cookie.Name == "fixture"), "sesión DPAPI restaurada en navegador");
            await restored.CloseAsync();
        }
        finally { CryptographicOperations.ZeroMemory(decoded); }
        await context.CloseAsync();
        Console.WriteLine($"Navegador: {checks} comprobaciones correctas. No se inició sesión en Garmin ni se llamó a producción.");
        return 0;
    }
}
