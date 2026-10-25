using System.Globalization;

namespace Lattice.Api;

/// <summary>A colour with opacity, one byte per channel.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
/// <param name="A">Opacity: 0 clear, 255 opaque.</param>
public readonly record struct Rgba(byte R, byte G, byte B, byte A);

/// <summary>A fraction from 0 to 1 (an opacity, a dim factor, a strength, a duty). <c>default</c> is 0.</summary>
public readonly record struct Fraction
{
    private Fraction(float value) => Value = value;

    /// <summary>The fraction, 0 to 1.</summary>
    public float Value { get; }

    /// <summary>0.</summary>
    public static Fraction Zero => default;

    /// <summary>1.</summary>
    public static Fraction One => new(1f);

    /// <summary>Refuses a value outside 0 to 1, and NaN.</summary>
    public static Result<Fraction, Reason> Create(float value) =>
        value is >= 0f and <= 1f
            ? Result<Fraction, Reason>.Ok(new Fraction(value))
            : Result<Fraction, Reason>.Error(Measure.Invalid(nameof(Fraction), value, "0-to-1"));
}

/// <summary>A length of time in seconds, zero or more (an ease, a fade, a highlight's life). <c>default</c> is zero: at once.</summary>
public readonly record struct Duration
{
    private Duration(float seconds) => Seconds = seconds;

    /// <summary>The seconds, zero or more.</summary>
    public float Seconds { get; }

    /// <summary>Zero: at once.</summary>
    public static Duration Zero => default;

    /// <summary>Refuses a negative or non-finite value.</summary>
    public static Result<Duration, Reason> Create(float seconds) =>
        Measure.IsFinite(seconds) && seconds >= 0f
            ? Result<Duration, Reason>.Ok(new Duration(seconds))
            : Result<Duration, Reason>.Error(Measure.Invalid(nameof(Duration), seconds, "not-negative"));
}

/// <summary>A repeat period in seconds, more than zero (a pulse, a blink, a flash). <c>default</c> is zero: no period, drawn steady.</summary>
public readonly record struct Period
{
    private Period(float seconds) => Seconds = seconds;

    /// <summary>The seconds, more than zero.</summary>
    public float Seconds { get; }

    /// <summary>Refuses a value of zero or less, and non-finite values.</summary>
    public static Result<Period, Reason> Create(float seconds) =>
        Measure.IsFinite(seconds) && seconds > 0f
            ? Result<Period, Reason>.Ok(new Period(seconds))
            : Result<Period, Reason>.Error(Measure.Invalid(nameof(Period), seconds, "positive"));
}

/// <summary>How many times something plays, at least once. <c>default</c> is zero: it does not play.</summary>
public readonly record struct Repeats
{
    private Repeats(int count) => Count = count;

    /// <summary>The count, at least one.</summary>
    public int Count { get; }

    /// <summary>Refuses a count below one.</summary>
    public static Result<Repeats, Reason> Create(int count) =>
        count >= 1
            ? Result<Repeats, Reason>.Ok(new Repeats(count))
            : Result<Repeats, Reason>.Error(Reason.InvalidValue(nameof(Repeats), count.ToString(CultureInfo.InvariantCulture), "at-least-one"));
}

/// <summary>A distance in metres, zero or more (a label's range, a near limit). <c>default</c> is zero.</summary>
public readonly record struct Metres
{
    private Metres(float value) => Value = value;

    /// <summary>The metres, zero or more.</summary>
    public float Value { get; }

    /// <summary>Refuses a negative or non-finite value.</summary>
    public static Result<Metres, Reason> Create(float value) =>
        Measure.IsFinite(value) && value >= 0f
            ? Result<Metres, Reason>.Ok(new Metres(value))
            : Result<Metres, Reason>.Error(Measure.Invalid(nameof(Metres), value, "not-negative"));
}

/// <summary>A line width in screen pixels, more than zero. <c>default</c> is zero: no line.</summary>
public readonly record struct Pixels
{
    private Pixels(float value) => Value = value;

    /// <summary>The pixels, more than zero.</summary>
    public float Value { get; }

    /// <summary>Refuses a value of zero or less, and non-finite values.</summary>
    public static Result<Pixels, Reason> Create(float value) =>
        Measure.IsFinite(value) && value > 0f
            ? Result<Pixels, Reason>.Ok(new Pixels(value))
            : Result<Pixels, Reason>.Error(Measure.Invalid(nameof(Pixels), value, "positive"));
}

/// <summary>
/// A distance fade: fully drawn up to <see cref="Start"/>, gone from <see cref="End"/> on, eased between, measured from
/// the camera. <c>default</c> is zero to zero: gone at any distance.
/// </summary>
public readonly record struct FadeRange
{
    private FadeRange(Metres start, Metres end)
    {
        Start = start;
        End = end;
    }

    /// <summary>Where the fade begins.</summary>
    public Metres Start { get; }

    /// <summary>Where the thing is gone, at or past <see cref="Start"/>.</summary>
    public Metres End { get; }

    /// <summary>Refuses an end before the start.</summary>
    public static Result<FadeRange, Reason> Create(Metres start, Metres end) =>
        end.Value >= start.Value
            ? Result<FadeRange, Reason>.Ok(new FadeRange(start, end))
            : Result<FadeRange, Reason>.Error(Measure.Invalid(nameof(FadeRange), end.Value, "end-not-before-start"));
}

/// <summary>A point in the game's world space, in metres.</summary>
/// <param name="X">X.</param>
/// <param name="Y">Y (up).</param>
/// <param name="Z">Z.</param>
public readonly record struct WorldPoint(float X, float Y, float Z);

/// <summary>A direction or offset in the game's world space, in metres.</summary>
/// <param name="X">X.</param>
/// <param name="Y">Y (up).</param>
/// <param name="Z">Z.</param>
public readonly record struct WorldVector(float X, float Y, float Z);

internal static class Measure
{
    public static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    public static bool IsFinite(WorldPoint p) => IsFinite(p.X) && IsFinite(p.Y) && IsFinite(p.Z);

    public static bool IsFinite(WorldVector v) => IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);

    public static bool IsZero(WorldVector v) => v.X == 0f && v.Y == 0f && v.Z == 0f;

    public static Reason Invalid(string type, float value, string rule) =>
        Reason.InvalidValue(type, value.ToString(CultureInfo.InvariantCulture), rule);
}
