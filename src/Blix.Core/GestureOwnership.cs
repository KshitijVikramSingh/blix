namespace Blix.Core;

/// <summary>
/// Remembers who a press went to, so its release goes to the same place.
/// </summary>
/// <remarks>
/// <b>Two bugs, mirror images of each other, and this is the statement that resolves both.</b>
/// <para>
/// Routing a release by asking who wants input <em>now</em> drops it whenever focus moves mid-gesture: a
/// drag begun in the world and finished over a panel left the application believing the button was still
/// down, so a selection marquee stayed live and followed a cursor that had long left it. Reported from the
/// chair as the mouse not lining up with the screen, which is nothing like what it was.
/// </para>
/// <para>
/// Delivering every release unconditionally fixes that and creates the reflection of it: a press the UI
/// owned still hands the application a release it never had a press for. Harmless when a handler is
/// strictly "remove this from the held set", and not harmless at all when the release <em>means</em>
/// something — a shot fired on release, a charge committed, a context menu opened.
/// </para>
/// <para>
/// Neither approximation is needed. "A press decides who owns the gesture" was already written down as the
/// reasoning; this implements it literally, which costs one set.
/// </para>
/// </remarks>
public sealed class GestureOwnership
{
    private readonly HashSet<int> heldByApplication = new();

    /// <summary>How many presses the application currently owns. For instruments and tests.</summary>
    public int HeldCount => heldByApplication.Count;

    /// <summary>
    /// Records a press and answers whether the application should hear it.
    /// </summary>
    /// <remarks>
    /// A press the UI takes is not recorded, so the matching release is not delivered either. A press the
    /// host itself consumed — a dump key, an overlay toggle — never reaches here at all, which gives the
    /// same outcome for the same reason.
    /// </remarks>
    public bool Press(int code, bool uiWantsInput)
    {
        if (uiWantsInput)
        {
            heldByApplication.Remove(code);
            return false;
        }

        heldByApplication.Add(code);
        return true;
    }

    /// <summary>
    /// Answers whether the application should hear this release, and forgets the press.
    /// </summary>
    /// <remarks>
    /// Deliberately does not consult the UI. Whether a panel has focus at the moment a button comes up
    /// says nothing about who was holding it down — that was settled when it went down.
    /// </remarks>
    public bool Release(int code) => heldByApplication.Remove(code);

    /// <summary>
    /// Forgets every held press.
    /// </summary>
    /// <remarks>
    /// For a window losing focus with keys down: the releases will never arrive, and an application left
    /// believing a key is held is the original bug wearing a different hat.
    /// </remarks>
    public void Clear() => heldByApplication.Clear();
}
