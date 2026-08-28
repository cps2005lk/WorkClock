using System.Net;
using System.Text.Json;
using Microsoft.Playwright;

namespace WorkClock;

/// <summary>
/// Handles logging into your attendance portal's Azure AD sign-in page
/// (using a visible browser, so you can type your password / do MFA once),
/// then saves the resulting cookies so future runs can skip the browser entirely.
/// </summary>
public class SessionManager
{
    private readonly string _loginUrl;
    private readonly string _sessionFile;
    private readonly int _loginTimeoutMinutes;

    public SessionManager(
        string sessionFile,
        string baseUrl,
        int loginTimeoutMinutes = 3)
    {
        _sessionFile = sessionFile;
        _loginUrl = baseUrl;
        _loginTimeoutMinutes = loginTimeoutMinutes;
    }

    /// <summary>
    /// Opens a real browser window and waits for you to complete the Azure AD login.
    /// Saves cookies to disk when done. Call this once, or whenever the saved
    /// session has expired.
    /// </summary>
    public async Task LoginAsync()
    {
        Console.WriteLine("Opening browser for Azure AD sign-in...");
        Console.WriteLine($"Please log in (and complete MFA if asked). Waiting up to {_loginTimeoutMinutes} minutes.");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = false
        });

        var page = await browser.NewPageAsync();
        await page.GotoAsync(_loginUrl);

        // Wait until we're redirected back to the app (i.e. login succeeded).
        await page.WaitForURLAsync(url => url.StartsWith(_loginUrl),
            new PageWaitForURLOptions { Timeout = _loginTimeoutMinutes * 60_000 });

        // Give the app a moment to finish setting its session cookie.
        await page.WaitForTimeoutAsync(1500);

        await page.Context.StorageStateAsync(new BrowserContextStorageStateOptions
        {
            Path = _sessionFile
        });

        Console.WriteLine($"Login successful. Session saved to '{_sessionFile}'.");
    }

    /// <summary>
    /// True if we have a saved session file to try reusing.
    /// </summary>
    public bool HasSavedSession() => File.Exists(_sessionFile);

    /// <summary>
    /// Builds an HttpClient pre-loaded with the cookies from the saved session.
    /// </summary>
    public HttpClient CreateAuthenticatedClient()
    {
        if (!HasSavedSession())
            throw new InvalidOperationException("No saved session found. Call LoginAsync() first.");

        var container = new CookieContainer();
        using var doc = JsonDocument.Parse(File.ReadAllText(_sessionFile));

        foreach (var c in doc.RootElement.GetProperty("cookies").EnumerateArray())
        {
            var name = c.GetProperty("name").GetString()!;
            var value = c.GetProperty("value").GetString()!;
            var domain = c.GetProperty("domain").GetString()!.TrimStart('.');

            container.Add(new System.Net.Cookie(name, value, "/", domain));
        }

        var handler = new HttpClientHandler
        {
            CookieContainer = container,
            UseCookies = true
        };

        return new HttpClient(handler) { BaseAddress = new Uri(_loginUrl) };
    }
}
