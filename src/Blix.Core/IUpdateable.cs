namespace Blix;

// Universal "tick me each frame" contract. Game code that wants per-frame work in a
// uniform shape implements this and gets driven from a caller's update loop.
//
// Plain GameObject is intentionally NOT IUpdateable — objects that just sit in the
// scene don't pay for a virtual call or imply they have behavior. Subtypes that need
// behavior (AnimatedGameObject, future user-defined behaviors) opt in explicitly.
public interface IUpdateable
{
    void Update(Time time);
}
