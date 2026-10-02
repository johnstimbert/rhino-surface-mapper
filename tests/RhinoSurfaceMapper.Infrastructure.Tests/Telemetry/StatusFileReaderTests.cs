using System.Text.Json;
using FluentAssertions;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Infrastructure.Telemetry;

namespace RhinoSurfaceMapper.Infrastructure.Tests.Telemetry;

/// <summary>
/// Covers <see cref="StatusFileReader"/> against <c>python/tests/test_elite_dangerous_status.py</c>'s
/// acceptance spec: an unchanged modification time suppresses a re-read, a changed one (or
/// <c>force</c>) triggers one, and malformed/missing files surface their exception unwrapped.
/// </summary>
public sealed class StatusFileReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RhinoSurfaceMapperTests", Guid.NewGuid().ToString("N"));
    private static readonly DateTime FirstWriteTimeUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SecondWriteTimeUtc = FirstWriteTimeUtc.AddSeconds(10);

    public StatusFileReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TryReadIfChanged_returns_the_parsed_document_on_the_first_read()
    {
        string path = Path.Combine(_root, "Status.json");
        File.WriteAllText(path, """{ "event": "Status", "Flags": 0 }""");
        var reader = new StatusFileReader();

        using var result = reader.TryReadIfChanged(path, previousMtimeTicks: null);

        result.Should().NotBeNull();
        result!.Document.RootElement.GetProperty("event").GetString().Should().Be("Status");
    }

    [Fact]
    public void TryReadIfChanged_returns_null_when_the_mtime_token_is_unchanged()
    {
        string path = Path.Combine(_root, "Status.json");
        File.WriteAllText(path, """{ "event": "Status" }""");
        File.SetLastWriteTimeUtc(path, FirstWriteTimeUtc);
        var reader = new StatusFileReader();
        using var first = reader.TryReadIfChanged(path, previousMtimeTicks: null)!;

        var second = reader.TryReadIfChanged(path, previousMtimeTicks: first.MtimeTicks);

        second.Should().BeNull();
    }

    [Fact]
    public void TryReadIfChanged_Should_SuppressAReparse_When_ContentChangesButLastWriteTimeIsRestored()
    {
        string path = Path.Combine(_root, "Status.json");
        File.WriteAllText(path, """{ "event": "Status", "Flags": 1 }""");
        File.SetLastWriteTimeUtc(path, FirstWriteTimeUtc);
        var reader = new StatusFileReader();
        using var first = reader.TryReadIfChanged(path, previousMtimeTicks: null)!;

        File.WriteAllText(path, "{ not json");
        File.SetLastWriteTimeUtc(path, FirstWriteTimeUtc);

        StatusReadResult? second = null;
        var act = () => second = reader.TryReadIfChanged(path, previousMtimeTicks: first.MtimeTicks);

        act.Should().NotThrow();
        second.Should().BeNull();
    }

    [Fact]
    public void TryReadIfChanged_rereads_once_the_mtime_token_changes()
    {
        string path = Path.Combine(_root, "Status.json");
        File.WriteAllText(path, """{ "event": "Status" }""");
        File.SetLastWriteTimeUtc(path, FirstWriteTimeUtc);
        var reader = new StatusFileReader();
        using var first = reader.TryReadIfChanged(path, previousMtimeTicks: null)!;

        File.WriteAllText(path, """{ "event": "StatusChanged" }""");
        File.SetLastWriteTimeUtc(path, SecondWriteTimeUtc);

        using var second = reader.TryReadIfChanged(path, previousMtimeTicks: first.MtimeTicks);

        second.Should().NotBeNull();
        second!.Document.RootElement.GetProperty("event").GetString().Should().Be("StatusChanged");
    }

    [Fact]
    public void TryReadIfChanged_Should_ObserveANewJsonFailure_When_LastWriteTimeChanges()
    {
        string path = Path.Combine(_root, "Status.json");
        File.WriteAllText(path, """{ "event": "Status", "Flags": 1 }""");
        File.SetLastWriteTimeUtc(path, FirstWriteTimeUtc);
        var reader = new StatusFileReader();
        using var first = reader.TryReadIfChanged(path, previousMtimeTicks: null)!;

        File.WriteAllText(path, "{ not json");
        File.SetLastWriteTimeUtc(path, SecondWriteTimeUtc);

        var act = () => reader.TryReadIfChanged(path, previousMtimeTicks: first.MtimeTicks);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void TryReadIfChanged_rereads_unconditionally_when_force_is_true_even_with_an_unchanged_mtime()
    {
        string path = Path.Combine(_root, "Status.json");
        File.WriteAllText(path, """{ "event": "Status" }""");
        File.SetLastWriteTimeUtc(path, FirstWriteTimeUtc);
        var reader = new StatusFileReader();
        using var first = reader.TryReadIfChanged(path, previousMtimeTicks: null)!;

        using var second = reader.TryReadIfChanged(path, previousMtimeTicks: first.MtimeTicks, force: true);

        second.Should().NotBeNull();
    }

    [Fact]
    public void TryReadIfChanged_throws_FileNotFoundException_for_a_missing_file()
    {
        var reader = new StatusFileReader();

        var act = () => reader.TryReadIfChanged(Path.Combine(_root, "missing.json"), previousMtimeTicks: null);

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void TryReadIfChanged_throws_JsonException_for_malformed_JSON()
    {
        string path = Path.Combine(_root, "Status.json");
        File.WriteAllText(path, "{ not json");
        var reader = new StatusFileReader();

        var act = () => reader.TryReadIfChanged(path, previousMtimeTicks: null);

        act.Should().Throw<JsonException>();
    }
}
