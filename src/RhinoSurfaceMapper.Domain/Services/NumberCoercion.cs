using RhinoSurfaceMapper.Domain.Exceptions;

namespace RhinoSurfaceMapper.Domain.Services;

/// <summary>
/// Internal numeric coercion mirroring Python's dynamically-typed <c>number()</c> helper inside
/// <c>MapperState.validate_map</c>, which calls the built-in <c>float()</c> on an arbitrary JSON
/// value and then requires the result to be finite.
/// </summary>
/// <remarks>
/// Kept as one shared helper (AGENTS.md's DRY rule) rather than re-implemented at each
/// <c>MapValidator</c> call site. All culture-sensitive parsing uses
/// <see cref="System.Globalization.CultureInfo.InvariantCulture"/>, matching the "numeric
/// fidelity" requirement that no locale-dependent formatting leak into domain logic.
/// </remarks>
internal static class NumberCoercion
{
    /// <summary>
    /// Coerces a raw, dynamically-typed value (as it would arrive straight from JSON) to a
    /// finite <see cref="double"/>, reproducing Python's <c>float(value)</c> plus
    /// <c>math.isfinite(result)</c> check.
    /// </summary>
    /// <param name="value">Raw value: expected to be a <see cref="double"/>, <see cref="int"/>,
    /// <see cref="long"/>, numeric <see cref="string"/>, or <see cref="bool"/> (Python's
    /// <c>bool</c> is an <c>int</c> subtype, so <c>float(True) == 1.0</c>; this method reproduces
    /// that specific nuance for parity, even though no field in this port currently relies on
    /// it).</param>
    /// <param name="fieldName">Field name used to build a descriptive failure message.</param>
    /// <returns>The finite <see cref="double"/> value.</returns>
    /// <exception cref="MapValidationException">
    /// The value is <see langword="null"/>, is not numeric, or is numeric but not finite (for
    /// example <c>NaN</c> or infinity) — matching Python's "número não finito" rule.
    /// </exception>
    public static double ToFiniteDouble(object? value, string fieldName)
    {
        double result = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            bool b => b ? 1.0 : 0.0,
            string s when double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            null => throw new MapValidationException($"{fieldName} is required."),
            _ => throw new MapValidationException($"{fieldName} is not a valid number."),
        };

        if (!double.IsFinite(result))
        {
            throw new MapValidationException($"{fieldName} contains a non-finite number.");
        }

        return result;
    }

    /// <summary>
    /// Coerces a raw value to a finite integral <see cref="int"/> within <paramref name="min"/>/
    /// <paramref name="max"/>, reproducing Python's single combined check
    /// <c>not value.is_integer() or not min &lt;= value &lt;= max</c> — one <paramref name="rangeMessage"/>
    /// covers both "not an integer" and "out of range" failures, exactly as the original raises
    /// only one <see cref="MapValidationException"/> for either cause.
    /// </summary>
    /// <param name="value">Raw value, as it would arrive straight from JSON.</param>
    /// <param name="fieldName">Field name used only for the separate "not finite/numeric" failure messages.</param>
    /// <param name="min">Inclusive lower bound.</param>
    /// <param name="max">Inclusive upper bound.</param>
    /// <param name="rangeMessage">Message raised for a non-integral value or one outside <paramref name="min"/>/<paramref name="max"/>.</param>
    /// <exception cref="MapValidationException">
    /// The value is not numeric or not finite (field-specific message), or is numeric but
    /// non-integral or outside the inclusive range (<paramref name="rangeMessage"/>).
    /// </exception>
    public static int ToIntegralValueInRange(object? value, string fieldName, int min, int max, string rangeMessage)
    {
        double number = ToFiniteDouble(value, fieldName);
        if (number != Math.Floor(number) || number < min || number > max)
        {
            throw new MapValidationException(rangeMessage);
        }

        return (int)number;
    }
}
