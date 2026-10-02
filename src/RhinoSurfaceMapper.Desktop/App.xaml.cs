using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RhinoSurfaceMapper.Desktop.Logging;
using RhinoSurfaceMapper.Domain.Interfaces;
using RhinoSurfaceMapper.Infrastructure;

namespace RhinoSurfaceMapper.Desktop;

/// <summary>
/// WPF composition root. Builds the generic host (configuration, DI, logging), installs the
/// three global exception handlers required by the design's "Cross-cutting capture" section,
/// and shows the main window — or, when started with <c>--selftest</c>, logs a startup/shutdown
/// record and exits immediately without showing any window. <c>--selftest</c> is retained from
/// Phase 0 because the WebView2 Runtime is still not installed on this development machine (see
/// the design's R1 risk entry), so it remains the only way to prove the host/logging pipeline
/// non-interactively in this environment even though <see cref="MainWindow"/> now hosts a real
/// <c>BlazorWebView</c>.
/// </summary>
public partial class App : System.Windows.Application
{
    private IHost? _host;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Installed before the host is built so even a failure during startup is captured.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _host = BuildHost(e.Args);

        // Run on a thread-pool thread rather than calling the blocking Start() extension
        // directly: IHost.StartAsync()'s internal awaits can resume on the
        // DispatcherSynchronizationContext that WPF installs on this thread before OnStartup
        // runs, which would deadlock if awaited while this same (UI) thread blocks on the result.
        Task.Run(() => _host.StartAsync()).GetAwaiter().GetResult();

        var logger = _host.Services.GetRequiredService<ILogger<App>>();
        var appPaths = _host.Services.GetRequiredService<IAppPaths>();
        var assemblyName = typeof(App).Assembly.GetName();

        logger.ApplicationStarting(assemblyName.Name ?? "RhinoSurfaceMapper.Desktop", assemblyName.Version?.ToString() ?? "unknown", Environment.Version.ToString());
        logger.ApplicationStarted(appPaths.BaseDirectory, appPaths.MapsDirectory, appPaths.LogsDirectory, appPaths.OptionsPath);

        if (e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            // The WebView2 Runtime is still not installed on this development machine (design
            // risk R1): self-test proves the host/logging pipeline without ever creating the
            // BlazorWebView control, which would throw at this point if the runtime were missing.
            //
            // Deliberately bypasses Application.Shutdown()/OnExit() here: at this point the
            // WPF dispatcher's message loop (started by Application.Run(), which is still
            // further down the call stack) has never actually begun pumping messages, because
            // no window has been shown. Calling Shutdown() in that state was observed, during
            // Phase 0 development, to let an internal WPF dispatcher exception surface *after*
            // this method returns, which would then re-enter OnDispatcherUnhandledException
            // against an already-disposed host. Stopping the host and exiting the process
            // directly avoids the WPF shutdown path entirely, which is safe here because
            // nothing else (no window, no message loop) depends on it running down cleanly.
            logger.ApplicationStopping();
            Task.Run(() => _host.StopAsync(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();
            logger.ApplicationStopped();
            _host.Dispose();
            Environment.Exit(0);
            return;
        }

        // MainWindow is DI-resolved (not `new`-ed) so its BlazorWebView's RootComponents can
        // receive the same IServiceProvider the rest of the app uses (AddWpfBlazorWebView()
        // registers a WebViewManager that resolves JS interop and component services from it).
        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            var logger = _host.Services.GetRequiredService<ILogger<App>>();
            logger.ApplicationStopping();

            // Run on a thread-pool thread rather than blocking the dispatcher thread directly:
            // IHost.StopAsync()'s internal awaits can resume on the captured
            // DispatcherSynchronizationContext, which would deadlock if awaited while this
            // same (UI) thread is synchronously blocked on the result.
            Task.Run(() => _host.StopAsync(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();

            logger.ApplicationStopped();
            _host.Dispose();
        }

        base.OnExit(e);
    }

    /// <summary>
    /// Builds the generic host: <c>appsettings.json</c> configuration, the Application and
    /// Infrastructure DI registrations, and the rolling-file/debug logging providers.
    /// </summary>
    private static IHost BuildHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

        // Called as an ordinary static method (not extension syntax): the
        // RhinoSurfaceMapper.Application namespace cannot be brought into scope with `using`
        // here because its leaf segment "Application" collides with System.Windows.Application,
        // this class's own base type.
        RhinoSurfaceMapper.Application.ConfigureServices.AddApplication(builder.Services);
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddWpfShell(builder.Configuration);

        return builder.Build();
    }

    /// <summary>Handles an exception that reached the WPF dispatcher without being caught.</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCriticalAndFlush(e.Exception, nameof(DispatcherUnhandledException));
        e.Handled = true;
        Shutdown(-1);
    }

    /// <summary>Handles an exception that unwound an entire AppDomain without being caught.</summary>
    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            LogCriticalAndFlush(exception, nameof(AppDomain.UnhandledException));
        }
    }

    /// <summary>Handles a faulted <see cref="Task"/> whose exception was never observed.</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCriticalAndFlush(e.Exception, nameof(TaskScheduler.UnobservedTaskException));
        e.SetObserved();
    }

    /// <summary>
    /// Logs the exception at <see cref="LogLevel.Critical"/> and flushes the rolling-file
    /// provider synchronously, so the record survives even if the process terminates
    /// immediately afterwards (design: "each logs Critical, flushes the sink synchronously").
    /// </summary>
    /// <remarks>
    /// <para>
    /// Guards against <see cref="ObjectDisposedException"/> specifically: a global exception
    /// handler can fire after the host has already been disposed (for example, during process
    /// teardown), and a logger that throws while reporting a crash would mask the original
    /// exception entirely.
    /// </para>
    /// <para>
    /// No explicit flush/wait call follows <c>logger.UnhandledException(...)</c> here: that
    /// call is itself synchronous and, because <c>UnhandledException</c> is logged at
    /// <see cref="LogLevel.Critical"/>, <c>RollingFileLogger.Log</c> already blocks this
    /// calling thread — with a bounded timeout
    /// (<c>RollingFileLoggerProvider.CriticalFlushTimeout</c>), so a wedged writer can never
    /// hang this handler — until the entry has been durably written and flushed, or that
    /// timeout has elapsed. An earlier version of this method instead followed the log call
    /// with a fixed <c>Thread.Sleep(250)</c> as a guess at "probably enough time"; that guess
    /// was wrong in practice (a lone Critical entry was observed to still only become visible
    /// on the 2-second periodic tick), which is exactly what prompted moving the actual
    /// guarantee into the logging call itself instead of leaving it to the caller to guess.
    /// </para>
    /// </remarks>
    private void LogCriticalAndFlush(Exception exception, string source)
    {
        if (_host is null)
        {
            return;
        }

        ILogger<App> logger;
        try
        {
            logger = _host.Services.GetRequiredService<ILogger<App>>();
        }
        catch (ObjectDisposedException)
        {
            // The host's service provider is already torn down; there is no sink left to
            // write to, so there is nothing more this handler can safely do.
            return;
        }

        logger.UnhandledException(exception, source);
    }
}

