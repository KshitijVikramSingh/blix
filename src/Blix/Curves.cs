using System.Numerics;

namespace Blix;

// A pure function `time -> value`. Curves are the time-varying half of an animation;
// animations pair a curve with a target (a setter closure or typed reference). Curves
// know nothing about start times, lifecycle, or what they animate — they just answer
// "what's the value at this elapsed time?"
//
// Deliberately no Duration on this base interface. Infinite curves (constant, looped,
// procedural) don't have one; finite curves expose it via IFiniteCurve<T> so an
// animation can ask "are you finished?" without forcing every curve to lie about it.
public interface ICurve<T>
{
    T Evaluate(double time);
}

// A curve that has a definite end. Animations that consume curves use IFiniteCurve to
// detect when to self-remove — `is IFiniteCurve<T> f` + `local < f.Duration`. Past the
// duration, finite curves still evaluate (clamped to their end value); they just stop
// claiming to be alive.
public interface IFiniteCurve<T> : ICurve<T>
{
    double Duration { get; }
}

// Returns the same value at every t. Useful as a placeholder, a "hold" segment in a
// sequence, or the wrapped curve in a LoopCurve when the underlying value doesn't
// actually vary. Not finite — runs forever.
public sealed record ConstantCurve<T>(T Value) : ICurve<T>
{
    public T Evaluate(double time) => Value;
}

// Linear ramp from From to To across [0, Duration]. Times outside that range clamp to
// the endpoints — Evaluate(< 0) returns From, Evaluate(> Duration) returns To. Pair
// with LoopCurve if cyclic behaviour is wanted.
public sealed class LinearCurve : IFiniteCurve<float>
{
    public float From { get; init; }
    public float To { get; init; }
    public double Duration { get; init; } = 1.0;

    public float Evaluate(double time)
    {
        if (Duration <= 0.0) return To;
        var t = (float)Math.Clamp(time / Duration, 0.0, 1.0);
        return From + (To - From) * t;
    }
}

// Vector3 sibling of LinearCurve. Linear interpolation is correct for positions and
// scales — animating a sequence of waypoint positions, a sliding camera offset, etc.
// Uses Vector3.Lerp.
public sealed class LinearVector3Curve : IFiniteCurve<Vector3>
{
    public Vector3 From { get; init; }
    public Vector3 To { get; init; }
    public double Duration { get; init; } = 1.0;

    public Vector3 Evaluate(double time)
    {
        if (Duration <= 0.0) return To;
        var t = (float)Math.Clamp(time / Duration, 0.0, 1.0);
        return Vector3.Lerp(From, To, t);
    }
}

// Quaternion sibling. Uses spherical-linear interpolation (Quaternion.Slerp) rather
// than componentwise lerp — slerp interpolates along the great-circle arc on the unit
// 4-sphere, which is what "smooth rotation from A to B" actually means. Componentwise
// lerp produces non-unit intermediate quaternions and visually-wrong rotation rates.
public sealed class SlerpQuaternionCurve : IFiniteCurve<Quaternion>
{
    public Quaternion From { get; init; } = Quaternion.Identity;
    public Quaternion To { get; init; } = Quaternion.Identity;
    public double Duration { get; init; } = 1.0;

    public Quaternion Evaluate(double time)
    {
        if (Duration <= 0.0) return To;
        var t = (float)Math.Clamp(time / Duration, 0.0, 1.0);
        return Quaternion.Slerp(From, To, t);
    }
}

// One (Time, Value) sample in a keyframe-based curve. Lightweight value type so an
// array of 50+ keyframes packs contiguously with zero heap allocations per entry.
public readonly record struct Keyframe<T>(double Time, T Value);

// Generalised N-keyframe curve for Vector3 values, linearly interpolated between
// adjacent keyframes. Sibling to LinearVector3Curve in the same way KeyframeQuaternion
// sibling SlerpQuaternionCurve — both are the "list of authored samples" form that
// imported animations produce (glTF's translation/scale tracks land here directly).
//
// Construction validates: at least one keyframe, strictly ascending Times. Duration
// is the last keyframe's Time. Times before the first keyframe clamp to the first
// value; times past the last clamp to the last (same convention as LinearCurve).
public sealed class KeyframeVector3Curve : IFiniteCurve<Vector3>
{
    public Keyframe<Vector3>[] Keyframes { get; }
    public double Duration { get; }

    public KeyframeVector3Curve(Keyframe<Vector3>[] keyframes)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        if (keyframes.Length == 0)
        {
            throw new ArgumentException("KeyframeVector3Curve requires at least one keyframe.", nameof(keyframes));
        }
        for (var i = 1; i < keyframes.Length; i++)
        {
            if (keyframes[i].Time <= keyframes[i - 1].Time)
            {
                throw new ArgumentException(
                    $"Keyframe times must be strictly ascending; keyframes[{i}].Time={keyframes[i].Time} <= keyframes[{i - 1}].Time={keyframes[i - 1].Time}.",
                    nameof(keyframes));
            }
        }
        Keyframes = keyframes;
        Duration = keyframes[^1].Time;
    }

    public Vector3 Evaluate(double time)
    {
        if (time <= Keyframes[0].Time) return Keyframes[0].Value;
        if (time >= Keyframes[^1].Time) return Keyframes[^1].Value;

        // Linear search. Binary search would be cheaper for long tracks but tracks
        // in shipped game animations rarely exceed ~30 keyframes; linear stays
        // simpler and cache-friendlier for that scale.
        for (var i = 0; i < Keyframes.Length - 1; i++)
        {
            var k0 = Keyframes[i];
            var k1 = Keyframes[i + 1];
            if (time >= k0.Time && time <= k1.Time)
            {
                var t = (float)((time - k0.Time) / (k1.Time - k0.Time));
                return Vector3.Lerp(k0.Value, k1.Value, t);
            }
        }
        return Keyframes[^1].Value;   // unreachable given the bounds checks above
    }
}

// Quaternion sibling. Slerp between adjacent keyframes — the great-circle arc on the
// unit 4-sphere, same reasoning as SlerpQuaternionCurve. Componentwise lerp would
// produce non-unit intermediates and wrong rotation rates.
public sealed class KeyframeQuaternionCurve : IFiniteCurve<Quaternion>
{
    public Keyframe<Quaternion>[] Keyframes { get; }
    public double Duration { get; }

    public KeyframeQuaternionCurve(Keyframe<Quaternion>[] keyframes)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        if (keyframes.Length == 0)
        {
            throw new ArgumentException("KeyframeQuaternionCurve requires at least one keyframe.", nameof(keyframes));
        }
        for (var i = 1; i < keyframes.Length; i++)
        {
            if (keyframes[i].Time <= keyframes[i - 1].Time)
            {
                throw new ArgumentException(
                    $"Keyframe times must be strictly ascending; keyframes[{i}].Time={keyframes[i].Time} <= keyframes[{i - 1}].Time={keyframes[i - 1].Time}.",
                    nameof(keyframes));
            }
        }
        Keyframes = keyframes;
        Duration = keyframes[^1].Time;
    }

    public Quaternion Evaluate(double time)
    {
        if (time <= Keyframes[0].Time) return Keyframes[0].Value;
        if (time >= Keyframes[^1].Time) return Keyframes[^1].Value;

        for (var i = 0; i < Keyframes.Length - 1; i++)
        {
            var k0 = Keyframes[i];
            var k1 = Keyframes[i + 1];
            if (time >= k0.Time && time <= k1.Time)
            {
                var t = (float)((time - k0.Time) / (k1.Time - k0.Time));
                return Quaternion.Slerp(k0.Value, k1.Value, t);
            }
        }
        return Keyframes[^1].Value;
    }
}

// Wraps any curve so its time axis loops over [0, Period). Composes with any ICurve<T>
// — looping is "how time is read," not "how state is written," so it belongs at the
// curve layer rather than as a separate animation wrapper.
//
// LoopCurve does NOT implement IFiniteCurve: a loop runs forever by definition. The
// underlying curve can be finite or infinite; the loop just keeps wrapping its t.
public sealed class LoopCurve<T> : ICurve<T>
{
    private readonly ICurve<T> inner;
    private readonly double period;

    public LoopCurve(ICurve<T> inner, double period)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (period <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(period), "LoopCurve period must be positive.");
        }
        this.inner = inner;
        this.period = period;
    }

    public T Evaluate(double time)
    {
        // `time % period` produces negative results for negative input in C#; shift
        // into [0, period) so callers passing pre-start times see a stable wrap.
        var wrapped = time % period;
        if (wrapped < 0.0) wrapped += period;
        return inner.Evaluate(wrapped);
    }
}
