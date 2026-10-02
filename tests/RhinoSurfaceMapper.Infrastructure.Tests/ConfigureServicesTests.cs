using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RhinoSurfaceMapper.Infrastructure.Logging;

namespace RhinoSurfaceMapper.Infrastructure.Tests;

/// <summary>
/// Covers <see cref="ConfigureServices.AddInfrastructure"/> wired into a real generic host —
/// a regression test for a dependency cycle discovered during Phase 0 development:
/// <see cref="RollingFileLoggerProvider"/> must never depend (directly or indirectly) on a
/// service that itself depends on <see cref="ILoggerFactory"/>/<see cref="IEnumerable{T}"/>
/// of <see cref="ILoggerProvider"/>, or <c>HostApplicationBuilder.Build()</c> hangs.
/// </summary>
public sealed class ConfigureServicesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "RhinoSurfaceMapperTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task AddInfrastructure_builds_and_starts_a_generic_host_without_hanging_or_throwing()
    {
        // Runs on a background task with a bounded wait: if the dependency-cycle regression
        // this test guards against were ever reintroduced, HostApplicationBuilder.Build()
        // would hang forever rather than throw, so a plain synchronous call could not be
        // asserted on safely. Timing out and failing is the correct outcome for that case.
        var buildAndStart = Task.Run(async () =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration["Logging:File:Directory"] = _directory;
            builder.Services.AddInfrastructure(builder.Configuration);

            using var host = builder.Build();
            await host.StartAsync();
            await host.StopAsync();
        });

        var completed = await Task.WhenAny(buildAndStart, Task.Delay(TimeSpan.FromSeconds(10)));

        completed.Should().Be(buildAndStart, "HostApplicationBuilder.Build() must not hang when the logging provider is registered");
        await buildAndStart; // Re-observes/rethrows any exception captured by the task.
    }

    [Fact]
    public void AddInfrastructure_returns_the_same_service_collection_for_chaining()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        var result = services.AddInfrastructure(configuration);

        result.Should().BeSameAs(services);
    }

    [Fact]
    public void RollingFileLoggerProvider_is_registered_under_two_service_types_resolving_the_same_singleton_instance()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:File:Directory"] = _directory })
            .Build();
        services.AddInfrastructure(configuration);

        using var provider = services.BuildServiceProvider();

        var concrete = provider.GetRequiredService<RollingFileLoggerProvider>();
        var asLoggerProvider = provider.GetServices<ILoggerProvider>().OfType<RollingFileLoggerProvider>().Single();

        asLoggerProvider.Should().BeSameAs(concrete,
            "both registrations must resolve to the one singleton instance, which is what makes double-disposal possible");
    }

    [Fact]
    public void Disposing_the_root_service_provider_does_not_throw_even_though_the_logging_provider_is_disposed_through_two_registrations()
    {
        // Regression test for the exact scenario Dispose()'s XML remarks describe: the
        // built-in container tracks each resolved service-type registration for disposal
        // independently, so it calls Dispose() on this one singleton instance twice during
        // shutdown. RollingFileLoggerProvider.Dispose() must tolerate that without throwing.
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:File:Directory"] = _directory })
            .Build();
        services.AddInfrastructure(configuration);

        var provider = services.BuildServiceProvider();
        // Resolving both forces the container to track both registrations against the
        // same singleton instance.
        _ = provider.GetRequiredService<RollingFileLoggerProvider>();
        _ = provider.GetServices<ILoggerProvider>().ToList();

        var act = () => provider.Dispose();

        act.Should().NotThrow();
    }
}
