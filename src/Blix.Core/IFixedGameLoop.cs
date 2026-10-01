namespace Blix;

/// <summary>A loop with a simulation on a fixed step, which the host schedules.</summary>
/// <remarks>
/// <para>
/// <b>A capability the host asks for, not a base class.</b> A host that is handed one runs a
/// <see cref="FixedStepClock"/> for it; every other loop is unaffected. Each frame is: input held still,
/// <see cref="IGameLoop.OnUpdate"/>, <see cref="OnFixedUpdate"/> once per whole step (zero or more), then
/// <see cref="IGameLoop.OnRender"/>, whose <see cref="Core.RenderFrameContext.FixedAlpha"/> says how far time
/// is toward the next step.
/// </para>
/// <para>
/// <b>Input is read in the update.</b> <c>Host.Input</c> is the frame's, and a step does not see a press
/// of its own: a frame can hold no step or several. Turn input into intents in <see cref="IGameLoop.OnUpdate"/>
/// and consume them in the step, so a press reaches exactly one step, waiting through frames that have none.
/// </para>
/// <para>
/// <b>Presentation that reads the simulation belongs in render.</b> The update runs before this frame's steps,
/// so a camera placed there from simulation state lags a step behind it. There is no late update: render is
/// where everything this frame changed has already happened.
/// </para>
/// </remarks>
public interface IFixedGameLoop : IGameLoop
{
    /// <summary>Seconds of simulation time per step. Read every frame, after the update.</summary>
    double FixedStep { get; }

    /// <summary>Simulation seconds per second of frame time: 1 is real time, 0 pauses. Read every frame, after the update.</summary>
    double FixedTimeScale => 1.0;

    /// <summary>One step. <see cref="Time.Delta"/> is the step; <see cref="Time.Total"/> is simulation time, not the frame's.</summary>
    void OnFixedUpdate(Time time);
}
