using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;

namespace SW.Bus.RabbitMqViewer.Auth;

/// <summary>
/// ASP.NET Core page filter that enforces HTTP Basic authentication for all
/// viewer dashboard Razor Pages when <see cref="ViewerAuthMode.Basic"/> is active.
/// Credentials are resolved from <see cref="IConfiguration"/> at request-time,
/// enabling rotation without restart.
/// </summary>
internal sealed class BasicAuthFilter : IAsyncPageFilter
{
    private readonly ViewerOptions options;

    public BasicAuthFilter(ViewerOptions options) => this.options = options;

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(
        PageHandlerExecutingContext context,
        PageHandlerExecutionDelegate next)
    {
        if (options.AuthMode != ViewerAuthMode.Basic)
        {
            await next();
            return;
        }

        var config   = context.HttpContext.RequestServices.GetService(typeof(IConfiguration)) as IConfiguration;
        var expected = GetExpected(config);

        var authHeader = context.HttpContext.Request.Headers.Authorization.ToString();

        if (IsAuthorized(authHeader, expected))
        {
            await next();
            return;
        }

        context.HttpContext.Response.Headers["WWW-Authenticate"] = "Basic realm=\"SW.Bus Viewer\"";
        context.Result = new UnauthorizedResult();
    }

    private (string Username, string Password) GetExpected(IConfiguration? config)
    {
        var user = config?[options.UsernameConfigKey] ?? string.Empty;
        var pass = config?[options.PasswordConfigKey] ?? string.Empty;
        return (user, pass);
    }

    private static bool IsAuthorized(string authHeader, (string Username, string Password) expected)
    {
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var encoded  = authHeader["Basic ".Length..].Trim();
            var decoded  = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var colonIdx = decoded.IndexOf(':');
            if (colonIdx <= 0) return false;

            var user = decoded[..colonIdx];
            var pass = decoded[(colonIdx + 1)..];

            // Constant-time comparison to avoid timing attacks
            return SlowEquals(user, expected.Username) && SlowEquals(pass, expected.Password);
        }
        catch
        {
            return false;
        }
    }

    private static bool SlowEquals(string a, string b)
    {
        var diff = a.Length ^ b.Length;
        for (var i = 0; i < a.Length && i < b.Length; i++)
            diff |= a[i] ^ b[i];
        return diff == 0;
    }
}

