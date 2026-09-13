namespace Blix.Core;

/// <summary>
/// An application that draws its own interface.
/// </summary>
/// <remarks>
/// <b>The host used to decide this from a single type test.</b> `gameLoop is IDebuggable` determined
/// whether ImGui was created at all, and the only thing that could ever be drawn into it was the
/// diagnostics overlay — so an application wanting a panel of its own had two options, neither of them
/// good: pretend to be a diagnostics producer, or go without.
/// <para>
/// <b>No UI types in the signature, deliberately.</b> ImGui is an immediate-mode global, so an
/// implementation just calls it inside <see cref="DrawUi"/>, and this interface stays a bare hook that
/// Blix.Core can declare without taking a dependency on any UI library. Same discipline as
/// <see cref="ViewDeclaration"/> knowing a surface handle and never a renderer: the engine says WHERE
/// something happens, the application says what.
/// </para>
/// <para>
/// Implemented by the game loop, the way <see cref="IInputHandler"/> is. One source, because one is what
/// there is evidence for; a registry can come when something needs two.
/// </para>
/// </remarks>
public interface IUiSource
{
    /// <summary>Name for this source, for diagnostics and error messages.</summary>
    string UiName { get; }

    /// <summary>
    /// Draws this frame's interface. Called between the UI library's new-frame and render calls.
    /// </summary>
    void DrawUi();
}
