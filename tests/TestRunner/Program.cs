using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PuppeteerSharp;

Console.WriteLine("=== STARTING REAL LINKEDIN HEADLESS BROWSER AUTH TEST ===");

var chromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
if (!File.Exists(chromePath)) chromePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";

Console.WriteLine($"Using Chrome at: {chromePath}");

var launchOptions = new LaunchOptions
{
    Headless = true,
    ExecutablePath = chromePath,
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

Console.WriteLine("Step 1: Navigating to https://www.linkedin.com/login");
await page.GoToAsync("https://www.linkedin.com/login", new NavigationOptions
{
    WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Networkidle2 },
    Timeout = 30000
});

Console.WriteLine($"Current URL: {page.Url}, Title: {await page.GetTitleAsync()}");

Console.WriteLine("Step 3: Finding visible inputs and setting values via React native setter...");
var fillSuccess = await page.EvaluateFunctionAsync<bool>(@"(user, pass) => {
    const userInput = Array.from(document.querySelectorAll('input')).find(i => (i.type === 'email' || i.type === 'text') && i.offsetParent !== null);
    const passInput = Array.from(document.querySelectorAll('input')).find(i => i.type === 'password' && i.offsetParent !== null);

    if (!userInput || !passInput) return false;

    // React native setter
    const nativeSetter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;

    userInput.focus();
    nativeSetter.call(userInput, user);
    userInput.dispatchEvent(new Event('input', { bubbles: true }));
    userInput.dispatchEvent(new Event('change', { bubbles: true }));

    passInput.focus();
    nativeSetter.call(passInput, pass);
    passInput.dispatchEvent(new Event('input', { bubbles: true }));
    passInput.dispatchEvent(new Event('change', { bubbles: true }));

    return true;
}", "mahfuz.duetcse@gmail.com", "mahfuz1227");

Console.WriteLine($"Fill success: {fillSuccess}");

Console.WriteLine("Step 4: Clicking exact LinkedIn Sign In button (ignoring Microsoft/Apple/Google)...");
var btnHandle = await page.EvaluateFunctionHandleAsync(@"() => {
    const btn = Array.from(document.querySelectorAll('button')).find(b => {
        if (!b || b.offsetParent === null) return false;
        const txt = b.innerText.trim();
        const isSocial = txt.includes('Microsoft') || txt.includes('Apple') || txt.includes('Google');
        if (isSocial) return false;

        return b.type === 'submit' || 
               txt === 'Sign in' || 
               txt === 'সাইন ইন করুন' || 
               txt === 'সাইন ইন' || 
               txt === 'Log in' || 
               txt === 'লগ ইন' ||
               b.getAttribute('data-litms-control-urn') === 'login-submit' ||
               b.className.includes('a22407db');
    });
    return btn;
}");

if (btnHandle is IElementHandle el && el != null)
{
    Console.WriteLine("Found EXACT LinkedIn Sign In button. Clicking...");
    await el.ClickAsync();
}
else
{
    Console.WriteLine("Button handle not found. Pressing Enter...");
    await page.Keyboard.PressAsync("Enter");
}

Console.WriteLine("Step 5: Waiting for post-login state resolution...");

for (int i = 0; i < 20; i++)
{
    await Task.Delay(1000);
    var url = page.Url;
    var cookies = await page.GetCookiesAsync();
    var liAt = cookies.FirstOrDefault(c => string.Equals(c.Name, "li_at", StringComparison.OrdinalIgnoreCase))?.Value;

    bool hasPinInput = false;
    string? visibleError = null;

    try
    {
        hasPinInput = await page.EvaluateFunctionAsync<bool>(@"() => {
            return document.querySelector('input[name=""pin""], input[name=""verificationCode""], #input__email_verification_pin, #pin, input[type=""tel""], input[type=""number""]') !== null;
        }");

        visibleError = await page.EvaluateFunctionAsync<string?>(@"() => {
            const errorEl = document.querySelector('#error-for-username, #error-for-password, .alert-content, .form__label--error, div[alert-type=""error""], .artdeco-inline-feedback--error, .error-for-password');
            return errorEl && errorEl.innerText.trim().length > 0 ? errorEl.innerText.trim() : null;
        }");
    }
    catch { }

    Console.WriteLine($"[{i}s] URL: {url} | li_at: {(string.IsNullOrEmpty(liAt) ? "No" : "YES")} | HasPIN: {hasPinInput} | Error: {visibleError ?? "None"}");

    if (!string.IsNullOrEmpty(liAt))
    {
        Console.WriteLine("\n🎉 SUCCESS: Logged in directly! li_at cookie extracted!");
        Console.WriteLine($"li_at = {liAt}");
        break;
    }

    if (hasPinInput || url.Contains("checkpoint") || url.Contains("challenge") || url.Contains("identity") || url.Contains("uas/consumer-email-challenge"))
    {
        Console.WriteLine("\n📬 2FA EMAIL VERIFICATION TRIGGERED!");
        Console.WriteLine($"URL: {url}");
        Console.WriteLine($"Title: {await page.GetTitleAsync()}");

        try
        {
            var textContent = await page.EvaluateFunctionAsync<string>(@"() => document.body.innerText");
            Console.WriteLine("\n--- Page Text Content Preview ---");
            Console.WriteLine(textContent.Length > 800 ? textContent.Substring(0, 800) : textContent);
            Console.WriteLine("---------------------------------\n");
        }
        catch { }

        Console.WriteLine("LinkedIn has dispatched the 6-digit verification code to your email!");
        break;
    }

    if (!string.IsNullOrEmpty(visibleError))
    {
        Console.WriteLine($"\n❌ ERROR: {visibleError}");
        break;
    }
}

Console.WriteLine("=== TEST RUN COMPLETE ===");
