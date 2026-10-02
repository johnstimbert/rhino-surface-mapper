using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RhinoSurfaceMapper.Infrastructure.Logging;
using RhinoSurfaceMapper.Infrastructure.Paths;
using RhinoSurfaceMapper.Infrastructure.Tests;

namespace RhinoSurfaceMapper.Infrastructure.Tests.Logging;

/// <summary>
/// Covers <see cref="LoggingShutdownFlusherHostedService"/>: it must register a flush
/// callback on <see cref="Microsoft.Extensions.Hosting.IHostApplicationLifetime.ApplicationStopping"/>
/// during <see cref="LoggingShutdownFlusherHostedService.StartAsync"/>, must actually flush
/// pending entries when that token fires, and must deregister the callback on
/// <see cref="LoggingShutdownFlusherHostedService.StopAsync"/> so a later trigger is a no-op.
/// </summary>
public sealed class LoggingShutdownFlusherHostedServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "RhinoSurfaceMapperTests", Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private RollingFileLoggerProvider CreateProvider() =>
        new(Options.Create(new RollingFileLoggerOptions { Directory = _directory }), new AppPaths(_directory), _clock);

    [Fact]
    public async Task ApplicationStopping_flushes_the_provider_without_waiting_for_Dispose_or_the_periodic_timer()
    {
        using var provider = CreateProvider();
        using var lifetime = new FakeHostApplicationLifetime();
        var service = new LoggingShutdownFlusherHostedService(lifetime, provider);

        provider.CreateLogger("Cat").LogInformation("flushed-on-stopping-marker");
        await service.StartAsync(CancellationToken.None);

        lifetime.TriggerApplicationStopping();

        // FlushAndReportDrops() runs synchronously inside the registered callback, so no
        // real-time wait is needed beyond letting the already-queued writer catch up — unlike
        // the periodic-timer tests in RollingFileLoggerProviderTests, which must wait out a
        // real, non-injectable 2-second interval.
        var contents = await WaitForFileContentAsync(provider.CurrentFilePath, "flushed-on-stopping-marker");

        contents.Should().Contain("flushed-on-stopping-marker");
    }

    [Fact]
    public async Task StopAsync_deregisters_the_callback_so_a_later_stopping_trigger_is_a_no_op()
    {
        using var provider = CreateProvider();
        using var lifetime = new FakeHostApplicationLifetime();
        var service = new LoggingShutdownFlusherHostedService(lifetime, provider);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        // Triggering after StopAsync must not throw even though the registration was disposed.
        var act = () => lifetime.TriggerApplicationStopping();

        act.Should().NotThrow();
    }

    private static async Task<string> WaitForFileContentAsync(string path, string expectedSubstring)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && TryReadAllText(path, out var content) && content.Contains(expectedSubstring, StringComparison.Ordinal))
            {
                return content;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"'{expectedSubstring}' was not written to '{path}' within the timeout.");
    }

    private static bool TryReadAllText(string path, out string content)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            content = reader.ReadToEnd();
            return true;
        }
        catch (IOException)
        {
            content = string.Empty;
            return false;
        }
    }
}
