using Blix.Audio;
using Blix.Core;
using Blix.Graphics;

namespace Blix;

// Minimal base for an IGameLoop. Captures the IRenderHost and IGraphicsDevice once at
// load so subclasses don't have to thread them through their own fields, and exposes
// them as protected properties.
//
// It is an IFixedGameLoop, so the host runs its fixed steps: OnUpdate, then OnFixedUpdate
// once per whole FixedStep of simulation time, then OnRender (see IFixedGameLoop for the
// order and what input means in it). Subclasses override OnFixedUpdate to tick whatever
// they own that's IFixedUpdateable (physics is the canonical case).
public abstract class Game : IFixedGameLoop
{
    protected IRenderHost Host { get; private set; } = null!;

    protected IGraphicsDevice GraphicsDevice { get; private set; } = null!;

    // Captured from the host on load when it implements IAudioHost. Null on
    // hosts without audio (headless test harnesses, or runtimes that haven't
    // been wired with an audio backend yet) — subclasses that touch audio must
    // null-check before use.
    protected IAudioDevice? AudioDevice { get; private set; }

    // Fixed-update cadence. 60Hz by convention — matches most physics defaults. The host
    // reads it every frame, after OnUpdate, so an override is in effect from the first frame.
    protected virtual double FixedStep => 1.0 / 60.0;

    // Simulation seconds per second of frame time, chosen in OnUpdate: 1 is real time, 0 pauses,
    // above 1 fast-forwards (every step still runs; nothing is skipped).
    protected double FixedTimeScale { get; set; } = 1.0;

    double IFixedGameLoop.FixedStep => FixedStep;

    double IFixedGameLoop.FixedTimeScale => FixedTimeScale;

    void IGameLoop.OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        Host = host;
        GraphicsDevice = graphicsDevice;
        AudioDevice = (host as IAudioHost)?.AudioDevice;
        OnLoad();
    }

    protected virtual void OnLoad() { }

    public virtual void OnUpdate(Time time) { }

    public virtual void OnFixedUpdate(Time time) { }

    public virtual void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList) { }

    public virtual void OnResize(int width, int height) { }

    public virtual void OnUnload() { }
}
