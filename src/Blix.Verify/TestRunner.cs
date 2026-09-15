using System.Numerics;
using System.Reflection;

namespace Blix.Verify;

/// <summary>
/// A tally of claims: prints GREEN OK or RED FAIL per case and counts what failed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The helper is shared; the convention is not codified.</b> Seven independent things in this
/// tree agree that a headless judge prints OK/FAIL and exits non-zero, and they agree without
/// sharing a line — which is the argument for leaving the convention alone. There is no base class
/// here, no attribute, no discovery, no lifecycle, and nothing that decides what a suite IS. A
/// caller still writes its own <c>Main</c>, chooses its own cases, and returns its own exit code.
/// </para>
/// <para>
/// <b>What was not working was the copy.</b> Six hand-written runners had four vocabularies between
/// them — <c>Expect</c> against <c>ExpectTrue</c>, OK/FAIL against PASS — and in one three-hour
/// stretch the same <c>ExpectThrows</c> was written twice, by hand, into two files that could not
/// see each other. A convention that seven things arrive at independently is working; a helper that
/// gets rewritten every time someone needs one more assertion is not.
/// </para>
/// <para>
/// <b>It learns no domain.</b> Hit-and-miss against a collision result, a pose fingerprint, a
/// reachable bone — those stay with the suites that mean them, expressed in terms of
/// <see cref="Expect"/>. A runner that knew what a raycast was would be the framework this is
/// deliberately not.
/// </para>
/// </remarks>
public sealed class TestRunner
{
    private int passed;

    /// <summary>How many claims failed. The caller turns this into an exit code; that is the convention.</summary>
    public int Failed { get; private set; }

    /// <summary>How many passed, for a caller that reports its own totals.</summary>
    public int Passed => passed;

    /// <summary>The claim must hold.</summary>
    public void Expect(string label, bool condition, string? detail = null)
    {
        if (condition) Pass(label);
        else Fail(label, detail ?? "predicate was false");
    }

    /// <summary>The claim must hold. The older spelling, kept because four suites already use it.</summary>
    public void ExpectTrue(string label, bool condition, string detail = "predicate was false") =>
        Expect(label, condition, detail);

    /// <summary>Two numbers must agree within a tolerance.</summary>
    public void ExpectClose(string label, float actual, float expected, float tolerance = 1e-3f)
    {
        if (MathF.Abs(actual - expected) > tolerance)
        {
            Fail(label, $"{actual:0.00000} != expected {expected:0.00000} (tol {tolerance})");
            return;
        }

        Pass(label);
    }

    /// <summary>Two vectors must agree within a tolerance, compared by the length of their difference.</summary>
    public void ExpectClose(string label, Vector4 actual, Vector4 expected, float tolerance = 1e-3f)
    {
        if ((actual - expected).Length() > tolerance)
        {
            Fail(label,
                $"expected ({expected.X},{expected.Y},{expected.Z},{expected.W}) " +
                $"got ({actual.X},{actual.Y},{actual.Z},{actual.W})");
            return;
        }

        Pass(label);
    }

    /// <summary>
    /// The action must refuse. A test that only ever asserts success is not a test.
    /// </summary>
    /// <param name="mustMention">
    /// When given, the message must contain this. A throw is not enough on its own: an error that
    /// does not say what IS available is how a mis-declared thing stays invisible, which is usually
    /// the failure the check exists for.
    /// </param>
    public void ExpectThrows(string label, Action action, string? mustMention = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            action();
        }
        catch (Exception thrown)
        {
            // Reflection wraps whatever the target threw, and the wrapper's message says nothing
            // about the thing being tested.
            var message = thrown is TargetInvocationException { InnerException: { } inner }
                ? inner.Message
                : thrown.Message;

            if (mustMention is null || message.Contains(mustMention, StringComparison.Ordinal)) Pass(label);
            else Fail(label, $"threw, but never mentioned '{mustMention}': {message}");
            return;
        }

        Fail(label, "it did not throw");
    }

    /// <summary>Record a pass directly, for a suite expressing its own vocabulary on top of this.</summary>
    public void Pass(string label)
    {
        Console.WriteLine($"  OK   {label}");
        passed++;
    }

    /// <summary>Record a failure directly, with what went wrong.</summary>
    public void Fail(string label, string detail)
    {
        Console.WriteLine($"  FAIL {label} — {detail}");
        Failed++;
    }

    public void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine($"{passed}/{passed + Failed} passed, {Failed} failed");
    }
}
