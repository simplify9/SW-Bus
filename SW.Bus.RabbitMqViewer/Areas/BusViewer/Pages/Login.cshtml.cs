using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;

namespace SW.Bus.RabbitMqViewer.Areas.BusViewer.Pages;

/// <summary>
/// Form-based login page for the Bus Viewer dashboard.
/// Validates credentials from IConfiguration and issues a short-lived, httpOnly session cookie.
/// </summary>
[AllowAnonymous]
public class LoginModel : PageModel
{
    [BindProperty]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; set; }

    public IActionResult OnGet()
    {
        if (HttpContext.User.Identity?.IsAuthenticated == true &&
            HttpContext.User.Identity.AuthenticationType == BusViewerExtensions.CookieSchemeName)
            return Redirect(SafeReturnUrl());

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(
        [FromServices] IConfiguration config,
        [FromServices] ViewerOptions options)
    {
        var expectedUser = config[options.UsernameConfigKey] ?? string.Empty;
        var expectedPass = config[options.PasswordConfigKey] ?? string.Empty;

        if (!SlowEquals(Username, expectedUser) || !SlowEquals(Password, expectedPass))
        {
            ErrorMessage = "Invalid username or password.";
            return Page();
        }

        var claims    = new[] { new Claim(ClaimTypes.Name, Username) };
        var identity  = new ClaimsIdentity(claims, BusViewerExtensions.CookieSchemeName);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            BusViewerExtensions.CookieSchemeName,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc   = DateTimeOffset.UtcNow.AddHours(8),
                AllowRefresh = false
            });

        return Redirect(SafeReturnUrl());
    }

    private string SafeReturnUrl() =>
        !string.IsNullOrWhiteSpace(ReturnUrl) &&
        ReturnUrl.StartsWith("/bus-viewer", StringComparison.OrdinalIgnoreCase) &&
        !ReturnUrl.StartsWith("/bus-viewer/login", StringComparison.OrdinalIgnoreCase)
            ? ReturnUrl
            : "/bus-viewer";

    private static bool SlowEquals(string a, string b)
    {
        var diff = a.Length ^ b.Length;
        for (var i = 0; i < a.Length && i < b.Length; i++)
            diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
