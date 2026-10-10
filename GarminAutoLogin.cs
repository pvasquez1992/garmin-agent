using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace GarminAgent;

public static class GarminAutoLogin
{
    public static bool IsTrustedLoginOrigin(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host is "sso.garmin.com" or "connect.garmin.com";

    // Return false when the form cannot be safely identified; leave manual login available.
    // Submit only once. MFA, CAPTCHA and incorrect credentials remain with the user.
    public static async Task<bool> TrySubmitAsync(IBrowser browser, GarminCredentials credentials,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(30));
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var page in browser.Contexts.SelectMany(context => context.Pages))
            {
                if (!IsTrustedLoginOrigin(page.Url)) continue;
                foreach (var frame in page.Frames)
                {
                    if (!IsTrustedLoginOrigin(frame.Url)) continue;
                    var password = frame.Locator("input[type='password']:visible");
                    var email = frame.Locator("input[type='email']:visible, input[name='email']:visible, input[name='username']:visible, input[id='email']:visible, input[id='username']:visible, input[autocomplete='username']:visible");
                    var submit = frame.GetByRole(AriaRole.Button, new()
                    {
                        NameRegex = new Regex("^(Sign\\s*In|Log\\s*In|Iniciar sesión|Acceder|Entrar)$", RegexOptions.IgnoreCase)
                    });
                    if (await password.CountAsync() != 1 || await email.CountAsync() != 1
                        || await submit.CountAsync() != 1 || !await submit.IsVisibleAsync()) continue;
                    // Check the origin again immediately before passing credentials to Playwright.
                    if (!IsTrustedLoginOrigin(page.Url) || !IsTrustedLoginOrigin(frame.Url)) continue;
                    await email.FillAsync(credentials.Email, new() { Timeout = 5000 });
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsTrustedLoginOrigin(frame.Url)) return false;
                    await password.FillAsync(credentials.Password, new() { Timeout = 5000 });
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsTrustedLoginOrigin(frame.Url)) return false;
                    await submit.ClickAsync(new() { Timeout = 5000 });
                    return true;
                }
            }
            await Task.Delay(250, cancellationToken);
        }
        return false;
    }
}
