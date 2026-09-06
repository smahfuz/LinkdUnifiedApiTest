using PuppeteerSharp;
using Xunit;
using Xunit.Abstractions;

namespace LinkdUnified.Tests;

public class LiveVoyagerTest
{
    private readonly ITestOutputHelper _output;

    public LiveVoyagerTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task TestRealPuppeteerLoginDiagnostics()
    {
        var browserFetcher = new BrowserFetcher();
        await browserFetcher.DownloadAsync();

        var launchOptions = new LaunchOptions
        {
            Headless = true,
            Args = new[]
            {
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage",
                "--disable-blink-features=AutomationControlled",
                "--disable-infobars",
                "--window-size=1280,800"
            }
        };

        using var browser = await Puppeteer.LaunchAsync(launchOptions);
        using var page = await browser.NewPageAsync();

        await page.EvaluateFunctionOnNewDocumentAsync(@"() => {
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
            window.chrome = { runtime: {} };
        }");

        await page.SetUserAgentAsync("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        await page.SetViewportAsync(new ViewPortOptions { Width = 1280, Height = 800 });

        _output.WriteLine("Navigating to https://www.linkedin.com/login");
        await page.GoToAsync("https://www.linkedin.com/login", new NavigationOptions
        {
            WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Networkidle2 },
            Timeout = 30000
        });

        _output.WriteLine($"Initial URL: {page.Url}, Title: {await page.GetTitleAsync()}");

        // Type username and password
        await page.WaitForFunctionAsync(@"() => {
            const u = document.querySelector('#username') || document.querySelector('#session_key') || document.querySelector('input[name=""session_key""]') || document.querySelector('input[type=""email""]') || document.querySelector('input[name=""username""]');
            const p = document.querySelector('#password') || document.querySelector('#session_password') || document.querySelector('input[name=""session_password""]') || document.querySelector('input[type=""password""]');
            return u !== null && p !== null;
        }", new WaitForFunctionOptions { Timeout = 15000 });

        await page.EvaluateFunctionAsync(@"() => {
            const u = document.querySelector('#username') || document.querySelector('#session_key') || document.querySelector('input[name=""session_key""]') || document.querySelector('input[type=""email""]') || document.querySelector('input[name=""username""]');
            if (u) { u.focus(); u.value = ''; }
        }");
        await page.Keyboard.TypeAsync("mahfuz.duetcse@gmail.com", new PuppeteerSharp.Input.TypeOptions { Delay = 15 });

        await page.EvaluateFunctionAsync(@"() => {
            const p = document.querySelector('#password') || document.querySelector('#session_password') || document.querySelector('input[name=""session_password""]') || document.querySelector('input[type=""password""]');
            if (p) { p.focus(); p.value = ''; }
        }");
        await page.Keyboard.TypeAsync("mahfuz1227", new PuppeteerSharp.Input.TypeOptions { Delay = 15 });

        _output.WriteLine("Submitting form...");
        var navTask = page.WaitForNavigationAsync(new NavigationOptions
        {
            WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
            Timeout = 30000
        });

        await page.EvaluateFunctionAsync(@"() => {
            const btn = document.querySelector('button[type=""submit""]') || document.querySelector('.btn__primary--large') || document.querySelector('button[data-litms-control-urn=""login-submit""]');
            if (btn) btn.click();
            else {
                const f = document.querySelector('form');
                if (f) f.submit();
            }
        }");

        try { await navTask; } catch (Exception ex) { _output.WriteLine($"Nav error/timeout: {ex.Message}"); }

        await Task.Delay(4000);

        _output.WriteLine($"Post-login URL: {page.Url}");
        _output.WriteLine($"Post-login Title: {await page.GetTitleAsync()}");

        var cookies = await page.GetCookiesAsync();
        foreach (var c in cookies)
        {
            _output.WriteLine($"Cookie: {c.Name} = {(c.Value.Length > 20 ? c.Value.Substring(0, 20) + "..." : c.Value)} (Domain: {c.Domain})");
        }

        var content = await page.GetContentAsync();
        _output.WriteLine($"HTML length: {content.Length}");
        _output.WriteLine($"HTML snippet (first 1000): {content.Substring(0, Math.Min(1000, content.Length))}");
    }
}
