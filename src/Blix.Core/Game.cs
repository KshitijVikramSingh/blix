using Blix.Audio;
using Blix.Core;
using Blix.Graphics;

namespace Blix;

// Minimal base for an IGameLoop. Captures the IRenderHost and IGraphicsDevice once at
// load so subclasses don't have to thread them through their own fields, and exposes
// them as protected properties.
//
// Also owns the fixed-step clock: each variable-rate OnUpdate accumulates frame Δt
// and dispatches OnFixedUpdate one or more times at the FixedStep cadence. Subclasses
// override OnFixedUpdate to tick whatever they own that's IFixedUpdateable (physics
// is the canonical case).
public abstract class Game : IGameLoop
{
    // Lazy so the subclass's FixedStep override is in effect by the time it's read —
    // virtual-call-in-constructor would otherwise see only the base value because the
    // subclass's fields and v-table aren't fully wired during base construction.
    private FixedStepClock? fixedClock;

    protected IRenderHost Host { get; private set; } = null!;

    protected IGraphicsDevice GraphicsDevice { get; private set; } = null!;

    // Captured from the host on load when it implements IAudioHost. Null on
    // hosts without audio (headless test harnesses, or runtimes that haven't
    // been wired with an audio backend yet) — subclasses that touch audio must
    // null-check before use.
    protected IAudioDevice? AudioDevice { get; private set; }

    // Fixed-update cadence. 60Hz by convention — matches most physics defaults and
    // avoids the awkward fractional accumulator residues that, say, 30Hz produces
    // against a 60Hz render. Override in the subclass constructor's `: base(...)` if
    // a different step is needed; the override needs to land before the base
    // constructor's clock is constructed, so this is a virtual property read once.
    protected virtual double FixedStep => 1.0 / 60.0;

    void IGameLoop.OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        Host = host;
        GraphicsDevice = graphicsDevice;
        AudioDevice = (host as IAudioHost)?.AudioDevice;
        OnLoad();
    }

    void IGameLoop.OnUpdate(Time time)
    {
        // Variable-rate first: animations, input, anything time-sensitive that should
        // sample at the display refresh.
        OnUpdate(time);

        // Then fixed-rate: physics integration, collision resolution, anything that
        // wants stable Δt. The clock returns 0..MaxStepsPerFrame for this frame.
        fixedClock ??= new FixedStepClock(FixedStep);
        var steps = fixedClock.Accumulate(time);
        for (var i = 0; i < steps; i++)
        {
            // Δ is the fixed step; Total stays as-is (no in-loop interpolation yet —
            // physics doesn't read Total for integration).
            OnFixedUpdate(new Time(time.Total, FixedStep));
        }
    }

    protected virtual void OnLoad() { }

    public virtual void OnUpdate(Time time) { }

    public virtual void OnFixedUpdate(Time time) { }

    public virtual void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList) { }

    public virtual void OnResize(int width, int height) { }

    public virtual void OnUnload() { }
}
