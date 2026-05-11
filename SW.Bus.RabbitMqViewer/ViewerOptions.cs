using System;

namespace SW.Bus.RabbitMqViewer;

/// <summary>
/// Authentication mode for the bus operations viewer dashboard.
/// </summary>
public enum ViewerAuthMode
{
    /// <summary>
    /// No authentication. All requests are allowed.
    /// <para>
    /// <b>Warning:</b> If the bus environment is <c>Production</c>, using this mode
    /// will throw an <see cref="InvalidOperationException"/> at startup unless
    /// <see cref="ViewerOptions.AllowAnonymousInProduction"/> is explicitly set to <c>true</c>.
    /// </para>
    /// </summary>
    None,

    /// <summary>
    /// Delegates authentication to the host application's authorization pipeline.
    /// The viewer applies <see cref="ViewerOptions.PolicyName"/> via
    /// <c>RequireAuthorization(policyName)</c> on the viewer's route group.
    /// The user is responsible for configuring the authentication scheme (JWT, cookie, OIDC, etc.).
    /// This is the recommended production mode.
    /// </summary>
    Policy,

    /// <summary>
    /// Built-in HTTP Basic authentication.
    /// Credentials are read from <c>IConfiguration</c> at <b>request time</b> (not startup),
    /// enabling secret rotation without application restart.
    /// Returns <c>401 WWW-Authenticate: Basic realm="SW.Bus Viewer"</c> on failure.
    /// </summary>
    Basic
}

/// <summary>
/// Configuration options for the SW.Bus operations viewer dashboard.
/// Configure via <c>services.AddBusViewer(o => ...)</c>.
/// </summary>
public sealed class ViewerOptions
{
    /// <summary>
    /// Gets the active authentication mode. Set via <see cref="RequirePolicy"/>,
    /// <see cref="UseBasicAuth"/>, or <see cref="AllowAnonymous"/>.
    /// Default: <see cref="ViewerAuthMode.None"/>.
    /// </summary>
    public ViewerAuthMode AuthMode { get; private set; } = ViewerAuthMode.None;

    /// <summary>
    /// Gets the authorization policy name used when <see cref="AuthMode"/> is <see cref="ViewerAuthMode.Policy"/>.
    /// </summary>
    public string? PolicyName { get; private set; }

    /// <summary>
    /// Gets the <c>IConfiguration</c> key for the Basic auth username.
    /// </summary>
    public string UsernameConfigKey { get; private set; } = "BusViewer:Username";

    /// <summary>
    /// Gets the <c>IConfiguration</c> key for the Basic auth password.
    /// </summary>
    public string PasswordConfigKey { get; private set; } = "BusViewer:Password";

    /// <summary>
    /// Gets or sets whether anonymous access is explicitly allowed in a Production environment.
    /// Only respected when <see cref="AuthMode"/> is <see cref="ViewerAuthMode.None"/>.
    /// Default: <c>false</c>.
    /// </summary>
    public bool AllowAnonymousInProduction { get; set; }

    /// <summary>
    /// Gets or sets the display title shown in the dashboard header.
    /// Default: <c>"SW.Bus Operations"</c>.
    /// </summary>
    public string Title { get; set; } = "SW.Bus Operations";

    // ─── Fluent configuration ────────────────────────────────────────────────

    /// <summary>
    /// Configures the viewer to delegate authentication to the host's authorization pipeline.
    /// The specified policy must be registered in the host's DI container via
    /// <c>services.AddAuthorization(o => o.AddPolicy(...))</c>.
    /// </summary>
    /// <param name="policyName">The authorization policy name to enforce on all viewer routes.</param>
    /// <example>
    /// <code>
    /// services.AddAuthorization(o => o.AddPolicy("OpsOnly", p => p.RequireRole("Ops")));
    /// services.AddBusViewer(o => o.RequirePolicy("OpsOnly"));
    /// </code>
    /// </example>
    public ViewerOptions RequirePolicy(string policyName)
    {
        AuthMode   = ViewerAuthMode.Policy;
        PolicyName = policyName ?? throw new ArgumentNullException(nameof(policyName));
        return this;
    }

    /// <summary>
    /// Configures built-in HTTP Basic authentication for the viewer dashboard.
    /// Credentials are resolved from <c>IConfiguration</c> at request-time, enabling
    /// secret rotation via environment variables or a secrets manager without restart.
    /// </summary>
    /// <param name="usernameConfigKey">Configuration key for the username. Default: <c>"BusViewer:Username"</c>.</param>
    /// <param name="passwordConfigKey">Configuration key for the password. Default: <c>"BusViewer:Password"</c>.</param>
    /// <example>
    /// <code>
    /// // appsettings.json / environment variable BusViewer__Username / BusViewer__Password
    /// services.AddBusViewer(o => o.UseBasicAuth());
    /// </code>
    /// </example>
    public ViewerOptions UseBasicAuth(
        string usernameConfigKey = "BusViewer:Username",
        string passwordConfigKey = "BusViewer:Password")
    {
        AuthMode          = ViewerAuthMode.Basic;
        UsernameConfigKey = usernameConfigKey ?? throw new ArgumentNullException(nameof(usernameConfigKey));
        PasswordConfigKey = passwordConfigKey ?? throw new ArgumentNullException(nameof(passwordConfigKey));
        return this;
    }

    /// <summary>
    /// Disables all authentication for the viewer dashboard.
    /// <para>
    /// <b>Not recommended for production.</b> Set <see cref="AllowAnonymousInProduction"/> to
    /// <c>true</c> only if the route is already protected by an upstream proxy or network policy.
    /// </para>
    /// </summary>
    public ViewerOptions AllowAnonymous()
    {
        AuthMode = ViewerAuthMode.None;
        return this;
    }
}

