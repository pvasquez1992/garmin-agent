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
        Check(!await page.EvaluateAsync<bool>("navigator.webdriver"), "inicio de Chrome sin modo de automatización implícito");
        using (var cancelLogin = new CancellationTokenSource(TimeSpan.FromMilliseconds(750)))
        {
            try
            {
                await native.WaitForGarminLoginAsync(cancellationToken: cancelLogin.Token);
                throw new AgentException("La espera de login terminó sin acceso ni cancelación.");
            }
            catch (OperationCanceledException) when (cancelLogin.IsCancellationRequested)
            {
                Check(!page.IsClosed, "cancelar la espera de acceso no cierra el navegador por su cuenta");
            }
        }
        try
        {
            await native.WaitForGarminLoginAsync(TimeSpan.Zero);
            throw new AgentException("El límite explícito de acceso no se respetó.");
        }
        catch (NeedsLoginException) { Check(!page.IsClosed, "espera limitada conserva la ventana hasta que el dueño la cierre"); }

        var credentialRoot = Path.Combine(directory, "credential-fixture");
        Directory.CreateDirectory(Path.Combine(credentialRoot, ".local"));
        var credentialBytes = Encoding.UTF8.GetBytes("""
            {"version":1,"email":"fixture@example.com","password":"not-a-real-password"}
            """);
        var credentialCipher = ProtectedData.Protect(credentialBytes, null, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(credentialBytes);
        await File.WriteAllBytesAsync(GarminCredentials.FilePath(credentialRoot), credentialCipher);
        var credentials = GarminCredentials.Read(credentialRoot);
        Check(credentials.Email == "fixture@example.com" && credentials.Password == "not-a-real-password",
            "lectura de credenciales ficticias en el formato cifrado de PowerShell");
        Check(!Encoding.UTF8.GetString(credentialCipher).Contains("not-a-real-password"), "contraseña no almacenada en texto plano");
        Check(!GarminAutoLogin.IsTrustedLoginOrigin("https://sso.garmin.com.attacker.example/login")
            && !GarminAutoLogin.IsTrustedLoginOrigin("http://sso.garmin.com/login"), "rechazo de dominios parecidos y HTTP para credenciales");
        var authContext = await browser.NewContextAsync();
        const string loginHtml = """
            <html><body><form onsubmit="event.preventDefault(); document.body.dataset.submitted='yes';">
            <label>Email Address<input type="email" name="email"></label>
            <label>Password<input type="password"></label><button type="submit">Sign In</button>
            </form></body></html>
            """;
        // Intercept every request: even the trusted Garmin-looking fixture never contacts Garmin.
        await authContext.RouteAsync("**/*", route => route.FulfillAsync(new() { ContentType = "text/html", Body = loginHtml }));
        var authPage = await authContext.NewPageAsync();
        await authPage.GotoAsync("https://outside.example/login");
        Check(!await GarminAutoLogin.TrySubmitAsync(browser, credentials, timeout: TimeSpan.FromMilliseconds(300))
            && await authPage.Locator("input[type=password]").InputValueAsync() == "", "sin autocompletar credenciales fuera de Garmin");
        await authPage.GotoAsync("https://sso.garmin.com/portal/fixture");
        Check(await GarminAutoLogin.TrySubmitAsync(browser, credentials, timeout: TimeSpan.FromSeconds(3))
            && await authPage.Locator("input[type=email]").InputValueAsync() == credentials.Email
            && await authPage.Locator("input[type=password]").InputValueAsync() == credentials.Password
            && await authPage.Locator("body").GetAttributeAsync("data-submitted") == "yes", "autocompletado y envío del formulario ficticio una vez");
        await authContext.CloseAsync();

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
        var links = await GarminPortal.ReadLinksAsync(page);
        Check(links.Length == 2 && links[0].text == "Nueva ruta"
            && GarminRoutes.ActivityId(links[1].href) == "67890", "lectura de enlaces y nombres como objetos .NET");
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
