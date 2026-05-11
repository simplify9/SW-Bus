using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace SW.Bus.RabbitMqViewer.Auth;

/// <summary>
/// Inserts the Bus Viewer authentication middleware at the very beginning of the ASP.NET
/// pipeline — before UseAuthentication / UseAuthorization — so unauthenticated requests
/// are redirected to /bus-viewer/login before the authorization middleware can return 403.
///
/// Active only when ViewerAuthMode.Basic is configured.
/// </summary>
internal sealed class BusViewerStartupFilter : IStartupFilter
{
    private readonly ViewerOptions options;

    public BusViewerStartupFilter(ViewerOptions options) => this.options = options;

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            if (options.AuthMode == ViewerAuthMode.Basic)
            {
                app.Use(async (context, nextDelegate) =>
                {
                    var path = context.Request.Path.Value ?? string.Empty;

                    // Only intercept /bus-viewer/* paths
                    if (!path.StartsWith("/bus-viewer", StringComparison.OrdinalIgnoreCase))
                    {
                        await nextDelegate();
                        return;
                    }

                    // Login and logout are always reachable without a session
                    if (path.StartsWith("/bus-viewer/login",  StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("/bus-viewer/logout", StringComparison.OrdinalIgnoreCase))
                    {
                        await nextDelegate();
                        return;
                    }

                    // Validate the viewer session cookie directly (does not require
                    // UseAuthentication to have run — calls the cookie handler directly via DI)
                    var authResult = await context.AuthenticateAsync(BusViewerExtensions.CookieSchemeName);

                    if (!authResult.Succeeded)
                    {
                        // No valid session — redirect to login before the rest of the
                        // pipeline (incl. UseAuthorization) has a chance to run
                        context.Response.Headers.CacheControl = "no-store";

                        if (context.Request.Headers.ContainsKey("HX-Request"))
                        {
                            // HTMX partial request — tell the browser to navigate to login
                            context.Response.Headers["HX-Redirect"] = "/bus-viewer/login";
                            context.Response.StatusCode = 200;
                        }
                        else
                        {
                            var returnUrl = Uri.EscapeDataString(path + context.Request.QueryString);
                            context.Response.Redirect($"/bus-viewer/login?returnUrl={returnUrl}");
                        }
                        return; // Short-circuit — never reaches UseAuthorization
                    }

                    // Valid session — expose the viewer principal so UseAuthorization
                    // sees an authenticated user (satisfies any host fallback policy)
                    context.User = authResult.Principal!;
                    context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";

                    await nextDelegate();
                });
            }

            // Invoke the application's own Configure()/Program.cs middleware setup
            next(app);
        };
    }
}

