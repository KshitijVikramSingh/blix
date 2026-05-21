namespace Blix.Core;

// Edge-triggered input events from the host. Separated from the game loop so consumers
// can opt in without taking on update/render obligations. Blix.Runtime.OpenTK checks
// for this interface on the loop instance and forwards events when present.
public interface IInputHandler
{
    void OnKeyDown(Key key) { }

    void OnKeyUp(Key key) { }

    void OnMouseMove(float x, float y, float deltaX, float deltaY) { }

    void OnMouseDown(MouseButton button) { }

    void OnMouseUp(MouseButton button) { }

    void OnMouseWheel(float offsetX, float offsetY) { }
}
