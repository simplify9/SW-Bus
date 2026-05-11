using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace SW.Bus.RabbitMqViewer.Auth;

/// <summary>
/// Page filter that enforces form-based session authentication for the Bus Viewer dashboard.
/// Checks for a valid viewer session cookie (see <see cref="BusViewerExtensions.CookieSchemeName"/>).
/// Unauthenticated full-page requests are redirected to /bus-viewer/login.
/// Unauthenticated HTMX partial requests receive HX-Redirect so the whole page navigates to login.
/// All viewer responses are sent with Cache-Control: no-store to prevent credential caching.
/// </summary>
internal sealed class ViewerAuthFilter : IAsyncPageFilter
{
    private readonly ViewerOptions options;

    public ViewerAuthFilter(ViewerOptions options) => this.options = options;

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

        // Never apply auth to the login / logout pages — that would be an infinite redirect loop
        var path = context.HttpContext.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/bus-viewer/login",  StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/bus-viewer/logout", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        // Suppress all browser and proxy caching for viewer responses
        context.HttpContext.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.HttpContext.Response.Headers.Pragma       = "no-cache";

        // Validate the viewer session cookie
        var authResult = await context.HttpContext.AuthenticateAsync(BusViewerExtensions.CookieSchemeName);
        if (authResult.Succeeded)
        {
            await next();
            return;
        }

        // HTMX partial request — return HX-Redirect so the whole page navigates to login
        if (context.HttpContext.Request.Headers.ContainsKey("HX-Request"))
        {
            context.HttpContext.Response.Headers["HX-Redirect"] = "/bus-viewer/login";
            context.Result = new StatusCodeResult(401);
        }
        else
        {
            var returnUrl = Uri.EscapeDataString(path + context.HttpContext.Request.QueryString);
            context.Result = new RedirectResult($"/bus-viewer/login?returnUrl={returnUrl}");
        }
    }
}

