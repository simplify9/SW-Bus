using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
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
    /// Registers all services required by the SW.Bus operations viewer dashboard.
    /// Call this in <c>Program.cs</c> / <c>Startup.ConfigureServices</c> after <c>AddBus()</c>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <param name="configure">Optional action to configure <see cref="ViewerOptions"/>.</param>
    /// <returns>The <see cref="IServiceCollection"/> for chaining.</returns>
    /// <example>
    /// <code>
    /// // Decoupled auth — host owns the auth scheme
    /// services.AddBusViewer(o => o.RequirePolicy("OpsOnly"));
    ///
    /// // Built-in Basic auth from configuration
    /// services.AddBusViewer(o => o.UseBasicAuth());
    ///
    /// // No auth (development only)
    /// services.AddBusViewer(o => o.AllowAnonymous());
    /// </code>
    /// </example>
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

        // Register Razor Pages and apply auth conventions for the BusViewer area
        services.AddRazorPages(o =>
        {
            switch (options.AuthMode)
            {
                case ViewerAuthMode.Policy when !string.IsNullOrWhiteSpace(options.PolicyName):
                    o.Conventions.AuthorizeAreaFolder("BusViewer", "/", options.PolicyName!);
                    break;

                case ViewerAuthMode.Basic:
                    // Enforced at page level by BasicAuthFilter (applied via convention below)
                    o.Conventions.AddAreaFolderApplicationModelConvention(
                        "BusViewer", "/",
                        model => model.Filters.Add(new BasicAuthFilter(options)));
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
    /// Call this in <c>Program.cs</c> <c>app.MapRazorPages()</c> is already present, this
    /// adds no extra mapping — the pages are discovered automatically from the RCL.
    /// Use this overload to document intent and ensure static files are served.
    /// </summary>
    /// <param name="app">The <see cref="IApplicationBuilder"/>.</param>
    /// <returns>The <see cref="IApplicationBuilder"/> for chaining.</returns>
    /// <example>
    /// <code>
    /// app.UseStaticFiles();   // Required — serves /_content/SimplyWorks.Bus.RabbitMqViewer/ assets
    /// app.UseRouting();
    /// app.UseAuthentication();
    /// app.UseAuthorization();
    /// app.UseBusViewer();     // Dashboard at /bus-viewer
    /// app.MapRazorPages();
    /// </code>
    /// </example>
    public static IApplicationBuilder UseBusViewer(this IApplicationBuilder app) => app;
}

