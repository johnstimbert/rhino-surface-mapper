using System.Diagnostics;
using RhinoSurfaceMapper.Domain.Interfaces;

namespace RhinoSurfaceMapper.Infrastructure.Time;

/// <summary>
/// Production <see cref="IClock"/> backed by the system clock and
/// <see cref="Stopwatch"/>'s high-resolution timestamp. See <see cref="IClock"/> for why
/// production code must never read time directly.
/// </summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public double MonotonicSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
