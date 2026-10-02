using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RhinoSurfaceMapper.Desktop.Hosting;

namespace RhinoSurfaceMapper.Desktop;

/// <summary>
/// Composition-root extension for the WPF shell itself, mirroring the reference app's
/// <c>AddApplication()</c>/<c>AddInfrastructure()</c> convention (one <c>AddXxx()</c> per
/// project) applied to <c>Desktop</c>: the <c>BlazorWebView</c> WPF integration, the main
/// window, the game-process check, and the telemetry hosted service.
/// </summary>
/// <remarks>
/// <see cref="RhinoSurfaceMapper.Application.Interfaces.IMapSessionStore"/> and
/// <see cref="RhinoSurfaceMapper.Application.Interfaces.IMapSessionNotifier"/> are deliberately
/// <b>not</b> registered here even though the task description suggested doing so: the design's
/// "Application services and interfaces" table places both interfaces (and, implicitly, their
/// default registrations) in <c>Application</c>, alongside <c>IStatusTelemetryReader</c> and
/// <c>IJournalIdentityReader</c>, which are registered by <c>Application.ConfigureServices</c>
/// today. Registering the session store in two different places depending on which host calls
/// it would risk a future second host (for example, a CLI diagnostics tool) picking up a
/// different singleton than the Desktop shell uses — keeping the registration beside its
/// interface in <c>Application.ConfigureServices.AddApplication()</c> avoids that split.
/// </remarks>
public static class ConfigureServices
{
    /// <summary>
    /// Registers everything the WPF shell owns on top of <c>AddApplication()</c> and
    /// <c>AddInfrastructure()</c>: the <c>BlazorWebView</c> WPF services, <see cref="MainWindow"/>,
    /// <see cref="IGameProcessCheck"/>, and <see cref="TelemetryHostedService"/>.
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    /// <param name="configuration">
    /// Root configuration, used to bind the optional <c>Telemetry</c> section (currently only
    /// <see cref="TelemetryOptions.StatusPath"/>) — mirrors <c>AddInfrastructure(configuration)</c>'s signature.
    /// </param>
    public static IServiceCollection AddWpfShell(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddWpfBlazorWebView();
        services.Configure<TelemetryOptions>(configuration.GetSection("Telemetry"));

        services.AddSingleton<MainWindow>();
        services.AddSingleton<IGameProcessCheck, EliteDangerousProcessCheck>();
        services.AddHostedService<TelemetryHostedService>();

        return services;
    }
}
