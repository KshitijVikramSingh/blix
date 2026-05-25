namespace Blix.Diagnostics;

// Implemented by the runtime so demos can reach the diagnostics surface.
// CurrentDebug is the legacy hook for push-style writes during render
// (used by ShaderLab's draw-list extensions); System is the full surface
// for contributor registration, freeze control, and sink wiring.
//
// Both are nullable: a runtime that wasn't built with diagnostics, or a
// game loop that isn't IDebuggable, may return null. Consumers must
// guard accordingly.
public interface IDebugHost
{
    DebugContext? CurrentDebug { get; }

    DebugSystem? System { get; }
}
