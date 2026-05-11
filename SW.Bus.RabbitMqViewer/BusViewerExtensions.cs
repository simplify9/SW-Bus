using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SW.Bus.RabbitMqExtensions;
using SW.Bus.RabbitMqViewer.Auth;

namespace SW.Bus.RabbitMqViewer;

/// <summary>
/// Extension methods for registering and mapping the SW.Bus operations viewer dashboard.
/// </summary>
public static class BusViewerExtensions
{
    /// <summary>
    /// The authentication scheme name used by the viewer's own session cookie.
    /// Separate from the host application's auth scheme so the two never interfere.
    /// </summary>
    public const string CookieSchemeName = "BusViewerCookie";

    /// <summary>
    /// Registers all services required by the SW.Bus operations viewer dashboard.
    /// Call this in <c>Program.cs</c> / <c>Startup.ConfigureServices</c> after <c>AddBus()</c>.
    /// </summary>
    public static IServiceCollection AddBusViewer(
        this IServiceCollection services,
        Action<ViewerOptions>? configure = null)
    {
        var options = new ViewerOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Guard: prevent silent anonymous exposure in Production
        var sp = services.BuildServiceProvider();
        var env = sp.GetService<IHostEnvironment>();
        if (env?.IsProduction() == true &&
            options.AuthMode == ViewerAuthMode.None &&
            !options.AllowAnonymousInProduction)
        {
            throw new InvalidOperationException(
                "SW.Bus Viewer is configured with no authentication in a Production environment. " +
                "Call options.RequirePolicy(...) or options.UseBasicAuth(...), " +
                "or explicitly set options.AllowAnonymousInProduction = true to suppress this check.");
        }

        // Register the viewer's own session cookie scheme — isolated from the host app's auth
        if (options.AuthMode == ViewerAuthMode.Basic)
        {
            // BusViewerStartupFilter inserts our middleware at the very front of the ASP.NET
            // pipeline (before UseAuthentication / UseAuthorization) so unauthenticated
            // /bus-viewer requests are redirected to login before anything can return 403.
            services.AddSingleton<IStartupFilter>(new BusViewerStartupFilter(options));

            services.AddAuthentication()
                .AddCookie(CookieSchemeName, o =>
                {
                    o.Cookie.Name     = ".BusViewerAuth";
                    o.Cookie.Path     = "/bus-viewer";
                    o.Cookie.HttpOnly = true;
                    o.Cookie.SameSite = SameSiteMode.Strict;
                    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    // Session-only cookie: no persistent storage, 8-hour absolute limit
                    o.ExpireTimeSpan    = TimeSpan.FromHours(8);
                    o.SlidingExpiration = false;
                    // Suppress the default redirect behaviour — BusViewerStartupFilter handles navigation
                    o.Events.OnRedirectToLogin        = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
                    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
                });
        }

        // Register Razor Pages and apply auth conventions for the BusViewer area
        services.AddRazorPages(o =>
        {
            switch (options.AuthMode)
            {
                case ViewerAuthMode.Policy when !string.IsNullOrWhiteSpace(options.PolicyName):
                    o.Conventions.AuthorizeAreaFolder("BusViewer", "/", options.PolicyName!);
                    break;

                case ViewerAuthMode.Basic:
                    // ViewerAuthFilter does the cookie check and redirects to /bus-viewer/login
                    o.Conventions.AddAreaFolderApplicationModelConvention(
                        "BusViewer", "/",
                        model => model.Filters.Add(new ViewerAuthFilter(options)));
                    o.Conventions.AllowAnonymousToAreaFolder("BusViewer", "/");
                    break;

                default:
                    o.Conventions.AllowAnonymousToAreaFolder("BusViewer", "/");
                    break;
            }
        });

        return services;
    }

    /// <summary>
    /// Maps the SW.Bus operations viewer dashboard Razor Pages.
    /// </summary>
    public static IApplicationBuilder UseBusViewer(this IApplicationBuilder app) => app;
}

