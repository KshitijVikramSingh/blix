# Blix

The layer game code targets. Owns the loop contract, scene composition, per-object pose, cameras + lights, animation + skeletal animation, physics, geometry + collision, audio, and the glTF importer. Everything below it (`Blix.Core`, `Blix.Graphics`, `Blix.Graphics.Images`, `Blix.Render`, `Blix.Assets`, `Blix.Diagnostics`, `Blix.Geometry`) is platform/renderer plumbing.

This doc is the reference for the `Blix` namespace. For the layers below, see [`architecture.md`](architecture.md) (project graph, host contracts, the Vulkan binding model) and [`renderer.md`](renderer.md) (render graph, shaders, rendering techniques).

## Overview

The deliberate framing: this layer is small and explicit. It does **not** introduce an ECS, a constraint-solver physics system, a scripting boundary, an editor, or a scenes-as-assets format. It introduces the smallest set of types a frame of update + render needs — a loop contract, transforms, cameras, time, cooked models resident on the device, skeletal animation, time-driven animation, kinematic physics + collision, and audio — and lets game code keep its own lists.

Anything richer (a general scene graph, render queues, animation graphs, multi-body solver, navmesh, editor) is a follow-on that lands when there's a real consumer. `Transform3D` already supports explicit parent/child pose composition without introducing an engine-owned scene, and a cooked model brings its own node hierarchy (`Model`).

## Getting started

```csharp
using Blix;
using Blix.Core;
using Blix.Graphics;
using Blix.Runtime.Silk;

var options = WindowOptions.FromArgs(args, WindowOptions.Default with
{
    Title = "My Game",
    Width = 960,
    Height = 540,
});

using var window = new Window(new MyGame(), options);
window.Run();

internal sealed class MyGame : Game
{
    protected override void OnLoad()
    {
        // Host and GraphicsDevice are now available.
    }

    public override void OnUpdate(Time time)
    {
        // Input is read, not delivered, and is fixed for this update.
        if (Host.Input[Key.Escape].Pressed)
            Host.RequestClose();
    }

    public override void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        commandList.Pass(
            "clear",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new(0.03f, 0.06f, 0.12f, 1f) },
                ClearDepth: false),
            _ => { });
    }

    public override void OnUnload()
    {
        // The GPU is idle here: free what OnLoad made. Under --validate a leftover fails the run.
    }
}
```

This is the same shaderless application boundary `src/Demos/Blix.Demos.Chassis/Program.cs` proves. For a model on screen, `examples/hello-blix-3d` stands a cooked character on Studio's lit stage and plays its clips; a game's own path is `ModelData.Load` → `device.CreateModel` → draw the `Model`'s parts, as Runner and VulkanLit do. `src/Demos/Blix.Demos.VulkanLit/Program.cs` is the lit/skinned/PBR reference (directional + spot + point shadows, IBL, bloom, a skinned model through `Model`). Vulkan Sponza is a heavy-scene demo where higher-end rendering is researched and measured; its feature set belongs in [Renderer](renderer.md#vulkan-sponza-research-renderer). For a playable title — `Transform3D`, `PhysicsHost3D` (gravity/jump), `CollisionWorld3D.Overlap`, a skinned character through `Model` + `ClipPlayer`, `AudioSource` and `IDebuggable` diagnostics — see `src/Demos/Blix.Demos.Runner/Program.cs` (a 3D endless runner).

## Project dependencies

`Blix` references:
- `Blix.Core` — `IRenderHost`, `IAudioHost`, `InputState`, `Key`, `MouseButton`, `RenderFrameContext`, `IRuntimeDiagnosticsSink`
- `Blix.Geometry` — `Bounds3`/`Bounds2`, `BoundingSphere`, `Ray`, `Plane`, `Triangle`, `Capsule`, `OrientedBounds3`, `TriangleMesh3D`, `Circle`, `Capsule2D`, `OrientedBounds2`, `LineMesh2D`, `Segment2D`, `Intersection`/`Intersection2D`, `CollisionHit`, `CollisionResponse`
- `Blix.Graphics` — `IGraphicsDevice`, `RenderCommandList`, matrix helpers
- `Blix.Render` — `Mesh`, the uploaded geometry a `Model`'s parts hold
- `Blix.Assets` — `MeshData` and the cooked `.blixmesh` reader

No glTF parser: the engine reads cooked models only (`ModelData.Load` takes a `.blixmesh`). Parsing glTF sources is `Blix.Import`'s, which the cook and the tools use and a game does not ship.

Nothing references `Blix` from below. No transitive dependency on `Blix.Runtime.Silk` or `Blix.Diagnostics` — the layer is platform-free.

## Public API index

Every public type built by `Blix.csproj` and `Blix.Core.csproj`, one line each.

**Loop + time:** `IGameLoop`, `IFixedGameLoop`, `Game`, `Time`, `IUpdateable`, `IFixedUpdateable`, `FixedStepClock`. These are in the `Blix` namespace but built by `Blix.Core`, so a program can use them without referencing this library.

**Transforms + cameras:** `Transform3D`, `Transform2D`, `Camera3D`, `Camera2D`, `CameraController`

**Lights:** `DirectionalLight`, `PointLight`, `SpotLight` — runtime light descriptions; a cooked file's lights are facts on `ModelData.Light`.

**Cooked models, resident:** `ModelData`, `ModelNeeds`, `Model`, `ResidencyExtensions` (`device.CreateModel`), `BoneBuffers`, `JointHierarchy`, `PbrMaterial`, `PbrMaterialExtensions`, `AlphaMode`, `TextureData`, `MaterialTextureLoader`, `MaterialTextures`, `TextureRegistry`, `UnreadAttribute`

**Time-driven animation:** `IAnimation`, `AnimationHost`, `FloatAnimation`, `Transform3DAnimation`, `CallbackAnimation`, and the curves: `ICurve<T>`, `IFiniteCurve<T>`, `ConstantCurve<T>`, `LinearCurve`, `LinearVector3Curve`, `SlerpQuaternionCurve`, `LoopCurve<T>`, `Keyframe<T>`, `KeyframeVector3Curve`, `KeyframeQuaternionCurve`, `Interpolation`

**Skeletal animation:** `BoneTransform`, `Bone`, `Skeleton`, `SkinBinding`, `Pose`, `BonePalette`, `BonePaletteSet`, `BoneMask`, `AnimationClip`, `BoneTrack`, `ClipPlayer`, `PoseStack`, `PoseLayer`, `PoseLayerMode`, `PoseLayerFinish`, `PoseBlend`, `PoseDelta`, `RootMotion`, `SkinningAnalysis`

**Physics:** `PhysicsHost3D`, `PhysicsHost2D`

**Collision world:** `CollisionWorld3D<T>`, `CollisionWorld2D<T>`, `CollisionContact3D<T>`, `CollisionContact2D<T>`, `CollisionLayer`, `CollisionMask`

**Audio:** `AudioListener`, `AudioSource`

**Picking + views:** `ViewTable`, `ViewDeclaration`, `ViewId`, `ViewPicking`

## Loop + time

### IGameLoop

The contract a game implements. The runtime (`Blix.Runtime.Silk.Window`) drives it.

```csharp
public interface IGameLoop
{
    void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice) { }
    void OnUpdate(Time time) { }
    void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList);
    void OnResize(int width, int height) { }
    void OnUnload() { }
}
```

Three deliberate separations:

- **Time vs render dims.** `Time` is its own value type. `RenderFrameContext` carries the default framebuffer's `(Width, Height)` and the frame's `FixedAlpha`. Splitting lets render-side and update-side state diverge cleanly (fixed-step physics, time scale, pause).
- **Input vs loop.** Input is not on `IGameLoop` and is not delivered by callback. The host computes a frame-stable `InputState` once per update and a game reads `Host.Input` during `OnUpdate`: `Down` for a hold, `Pressed`/`Released` for the transitions, `MouseDelta` for motion. Deriving those from platform events is mechanism the engine owes; deciding that Space means jump is the game's.
- **Diagnostics vs loop.** `IDebuggable` (in `Blix.Diagnostics`) is a separate opt-in surface.

### Game

The minimum useful base. Captures `IRenderHost` and `IGraphicsDevice` once at load, exposes them as `Host` and `GraphicsDevice`. Also auto-captures `AudioDevice` from `Host as IAudioHost`. It is an `IFixedGameLoop`, so the host runs its `OnFixedUpdate` steps (see [Fixed steps](#fixed-steps)).

```csharp
internal sealed class MyGame : Game, IDebuggable
{
    protected override void OnLoad()
    {
        // Host, GraphicsDevice, and AudioDevice are already set.
        Host.SetTitle("My Game");
    }

    public override void OnUpdate(Time time) { /* per-render-frame tick */ }
    public override void OnFixedUpdate(Time time) { /* fixed-cadence tick (default 60Hz) */ }
    public override void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList) { }
}
```

`OnLoad` is the only base-class virtual that's intercepted: `Game` implements `IGameLoop.OnLoad` explicitly, captures the parameters, then calls `protected virtual void OnLoad()` with no args. Everything else is plain `virtual` no-op.

Protected members:
- `Host` (`IRenderHost`) — runtime knobs.
- `GraphicsDevice` (`IGraphicsDevice`) — graphics resources.
- `AudioDevice` (`IAudioDevice?`) — captured from `Host as IAudioHost`. Null on hosts without audio; audio-aware code null-checks.

Virtuals:
- `OnLoad()` — game-side load hook.
- `OnUpdate(Time)` — variable-rate (per render frame).
- `OnFixedUpdate(Time)` — fixed-rate (default 60Hz, see `FixedStep` virtual).
- `OnRender(Time, RenderFrameContext, RenderCommandList)` — must override.
- `OnResize(int, int)` — framebuffer resize hook.
- `OnUnload()` — game-side unload hook.

Override `protected virtual double FixedStep => 1.0 / 60.0` to change the fixed cadence, and set `FixedTimeScale` (1 real time, 0 paused) in `OnUpdate`; the host reads both after it.

### Time

```csharp
public readonly record struct Time(double Total, double Delta);
```

A passed-by-value struct. `Total` is seconds since startup (monotonically increasing); `Delta` is seconds since the previous frame. The host's `FrameClock` takes **one sample per frame**, which `OnUpdate` and `OnRender` both see; no platform clock leaks across the boundary.

**`--step <seconds>` means the same in either host:** every frame advances by exactly that much, whatever the wall clock did, so a bounded run tells its loop the same times every run. Without it a window takes the display's measured frame time; a headless run always has a step (default 1/60 s). It makes the *application's* time deterministic, not execution: work budgeted by a stopwatch (`MaterialTextureLoader.Drain(ms)`, an async load) still runs on the wall clock and can land on a different frame.

`Time` and `RenderFrameContext` are separate because `Time` is a layer-spanning fact ("what frame is the engine on, how long since the last one"), while `RenderFrameContext` is render-side info that only matters when there's a swapchain.

#### Deliberate limits

- No `Time.Now` singleton. Passing explicitly avoids hidden global state and keeps tests trivial.

### IUpdateable + IFixedUpdateable

Opt-in update contracts: a thing that only sits in a scene implements neither, so it pays no virtual call and claims no behaviour it lacks. The host ticks neither: a loop calls `Update` from `OnUpdate` and `FixedUpdate` from `OnFixedUpdate` on what it owns, so it decides what runs and in which order.

```csharp
public interface IUpdateable    { void Update(Time time); }
public interface IFixedUpdateable { void FixedUpdate(Time time); }
```

Variable-rate (`Update`) fires once per render frame at whatever the display does (typically 60–144Hz). Fixed-rate (`FixedUpdate`) fires at a fixed cadence (default 60Hz) — zero, one, or more times per frame depending on how much delta has accumulated. The two interfaces have different method names so a single class can implement both with independent method bodies.

### Fixed steps

A loop that is an `IFixedGameLoop` (`FixedStep`, `FixedTimeScale`, `OnFixedUpdate`) has its simulation scheduled by the host, which owns a `FixedStepClock`. Both hosts run every frame in one order:

1. input held still for the frame;
2. `OnUpdate(time)`: read `Host.Input`, turn it into intents, choose `FixedTimeScale`;
3. `OnFixedUpdate(step)` once per whole step of accumulated time: zero, one or several;
4. `OnRender(time, frame, ...)`, where `frame.FixedAlpha` in [0, 1) is how far time is toward the next step.

- **Input is the frame's.** A step does not see a press of its own, because a frame can hold no step or several. Record the press as an intent in `OnUpdate` and consume it in a step: it reaches exactly one step, and waits through frames that have none.
- **Simulation time is its own clock.** A step's `Time.Total` advances one step per step: it runs at the time scale and stands still while paused. `Host.ResetFixedClock(total)` sets it and clears the residual, for a restart, a load or a scrub.
- **The cap is in seconds.** A frame contributes at most `FixedStepClock.MaxFrameDelta` (0.25 s) before the scale, so a stall drops time instead of replaying it, and a fast-forward still runs every step it asked for: 30 Hz at 6× is 45 steps in a long frame. There is no step cap, which would make the scale lie.
- **Presentation that reads the simulation goes in `OnRender`.** `OnUpdate` runs before this frame's steps, so a camera placed there from simulation state lags a step. There is no late-update hook.

## Scene primitives

### Transform3D

Mutable class. `Position` / `Rotation` / `Scale` are bare auto-properties; pose lives directly on the instance and game code mutates it in place.

```csharp
var t = new Transform3D
{
    Position = new Vector3(0.65f, 0.35f, 1.25f),
    Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4.0f),
    Scale = new Vector3(1.15f, 1.15f, 1.15f),
};

var model = t.ToMatrix();           // GraphicsMatrices.CreateModel
var forward = t.Forward;            // Local -Z transformed by Rotation
t.LookAt(new Vector3(0, 0, 0), Vector3.UnitY);
```

`Forward` is the rotation applied to `-Z`. Identity rotation looks down `-Z`, matching the default camera convention. `LookAt(target, up)` solves the rotation that aligns local `-Z` with the target direction.

#### Parenting

`Position` / `Rotation` / `Scale` are **local**; an optional `Parent` composes them up a hierarchy. `WorldMatrix` is the local matrix composed with the parent chain (`world = local * parentWorld`, matching `Skeleton`'s hierarchy walk), and `WorldPosition` / `WorldRotation` read the world pose for consumers outside the hierarchy (a chase camera, a muzzle spawn point). Assigning a `Parent` that would form a cycle throws.

```csharp
var hull = new Transform3D { Position = new Vector3(0, 0.5f, 0) };
var turret = new Transform3D { Parent = hull };                  // rides the hull, yaws on its own
var barrel = new Transform3D { Position = new Vector3(0, 0, -1.4f), Parent = turret };
// driving the hull carries turret + barrel; turret.Rotation aims independently.

var worldMuzzle = barrel.WorldPosition;   // composed down hull -> turret -> barrel
```

`SetParent(newParent, keepWorldPose)` re-parents; with `keepWorldPose: true` the local TRS is recomputed so the **world** pose is unchanged across the switch — e.g. `shell.SetParent(null, keepWorldPose: true)` detaches a shell from a moving barrel at its current muzzle pose so it flies straight instead of snapping to the barrel's local frame. `WorldMatrix` is a `System.Numerics` model matrix (translation in the last row), fed straight to a `model * v` shader / `InstanceData.Model` with no transpose. `Blix.Demos.TankArena` is the working reference (its hull → turret → barrel tanks are parenting hierarchies); the composition + render convention are pinned by `Blix.Test.Graphics` Section AH.

#### Fitting an articulated model onto a rig

Mapping a rigged glTF (a tank with a separate turret/gun, a mech, a crane) onto a `Transform3D` rig works without screenshots if you **measure the asset instead of guessing pivots**:

1. **Inspect it** — `./blix inspect <model.glb>` prints the node hierarchy and, for each mesh-bearing node, its composed-world `scale` / `translation` and assembled `bounds`. A rigged part's **node translation is its rotation pivot** (authors place the node origin at the hinge); the bounds give the model's size and forward axis.
2. **Import nodes, not a fused blob** — `GltfStaticImporter.ImportNodes` keeps every node in its own local space (vs `Import`, which bakes world transforms into one static mesh). Compose each node's world transform by walking parents (`world = local * parentWorld`).
3. **Bake each part to its pivot** — transform a part's primitives by `nodeWorld * Translate(-pivot)` so its pivot sits at the mesh origin, upload as a `Mesh`, and give each part its own `InstancedBatch` (reusing one world/caster pipeline — same vertex layout).
4. **Drive from the rig** — the per-frame instance matrix is `Scale(s) * RotateY(yawFix) * rigPart.WorldMatrix * Translate(0, lift, 0)`, and the rig's child positions (turret-on-hull, gun-on-turret) are the **measured** node offsets mapped through the same `RotateY(yawFix) * s` — so the meshes and the gameplay rig (aim, muzzle, recoil) stay locked and a single scale/yaw knob can't desync them.
5. **Expose only the residuals** as live `[Tune]` knobs — global scale, a forward-axis `yawFix` (model `-X` → engine `-Z` is `-90°`), and a vertical `lift`. Because the pivots came from measurement, sensible defaults land the fit with no dialing. `Blix.Demos.TankArena.LoadTankParts` / `SeatRig` / `PartModel` are the worked reference.

#### Deliberate limits

- Class, not struct. Game code regularly hands the same transform to multiple consumers; reference semantics are the right default.
- No engine-owned object container — parenting is pose-level on `Transform3D`. Game code wires `Transform.Parent` and keeps its own object lists; there's no automatic scene graph that owns children, draw order, or lifetimes. (A cooked model's own node hierarchy is `Model.Nodes`.)
- No dirty-flag caching for `ToMatrix()` / `WorldMatrix`. Matrix composition is a few small multiplies and hierarchies here are shallow; `WorldMatrix` re-walks the parent chain on each access (add caching when a deep rig needs it). Cycle-checking happens once, on `Parent` assignment.
- Non-uniform parent scale combined with a child rotation can shear the child (the standard TRS-hierarchy limitation; content keeps non-uniform scale off shared parents, mirroring the rigid-bone skinning assumption — `TankArena` applies a single **uniform** model scale in the per-part instance matrix, not in the pose hierarchy).
- No `Origin` / `Pivot`. Mesh-side authored offsets are normalised at import time (`ObjImporter.RecenterToOrigin`, default true); game code uses plain `Transform.Position`.

### Transform2D

The 2D sibling. Same shape; scalar `Rotation` around Z; 2D `Position` and `Scale`. `ToMatrix()` returns `Matrix4x4` (not `Matrix3x2`) so 2D content composes with the same shader pipelines and view/projection plumbing as 3D content — Z row stays identity and 2D objects sit on `z = 0`.

`Right` and `Up` are the two natural 2D facing conventions — pick whichever matches the sprite art. `Forward` is intentionally absent (no canonical "forward" in 2D).

#### Deliberate limits

- No `LookAt`. Collapses to `Rotation = MathF.Atan2(target.Y - Position.Y, target.X - Position.X)` (modulo facing convention).
- `ToMatrix()` returns `Matrix4x4`, not `Matrix3x2`, to share the same `uModel` pipeline meshes use.

## Cameras

`Camera3D` and `Camera2D` both **compose** a `Transform` rather than embedding pose fields — pose is the universal primitive across cameras, GameObjects, and lights, so it lives in one place and is mutated through one type.

```csharp
var camera = new Camera3D
{
    Transform = new Transform3D
    {
        Position = new Vector3(0, 1, 3),
        Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.25f),
    },
    VerticalFieldOfView = MathF.PI / 3.0f,
    NearPlane = 0.1f,
    FarPlane = 100.0f,
};

camera.Transform.Position += camera.Transform.Forward * speed * dt;
var view = camera.GetView();
var projection = camera.GetProjection(aspectRatio);
var viewProjection = camera.GetViewProjection(aspectRatio);
```

`Camera3D` adds `VerticalFieldOfView`, `NearPlane`, `FarPlane`, and the matrix helpers. `GetView()` builds the view matrix from `Transform.Position` and `Transform.Rotation` only — `Transform.Scale` is ignored for cameras (field is still there from `Transform3D`'s shared shape, just unused).

`Camera2D` has `Zoom`, `NearDepth`, `FarDepth`, and the same matrix helpers (with `GetProjection(viewportWidth, viewportHeight)` taking pixel-space dimensions because the orthographic frustum sizes by absolute width/height).

`Transform` is `{ get; init; }` on both cameras: settable once during object initialization, then immutable as a reference. The transform's *fields* are still freely mutable through `camera.Transform.Position = ...`. This shape blocks accidental "I swapped the camera's transform and now the old reference is stale" bugs while leaving the common-case mutation path open.

### Why composition, not inline pose

Every type with a pose composes a `Transform`: cameras, the physics hosts, the audio listener, and whatever a game holds. The win is that **animation targets the Transform**, not the owning type: `Transform3DAnimation` drives a camera's `Transform` and a prop's the same way, with no branching by target type.

### Deliberate limits

- No `ICamera` interface. Each camera class stays concrete until a real consumer needs the polymorphism.
- `Camera3D` and `Camera2D` stay separate. A unified `Camera { Mode: 2D/3D, ... }` was considered and rejected — the API split (FoV vs Zoom, perspective vs ortho) is cleaner than the mode branch every consumer would need.
- `Camera2D` is platform-ready but unused by the current demos. The 2D layer (Transform2D, Camera2D, the 2D geometry/collision primitives) exists in full ready for the first 2D game.

## Lights

Three light types, structurally distinct rather than a common `Light` base — their per-fragment evaluation differs (distance falloff, cone falloff, world-space direction-only), and shaders bind them as type-specific uniform arrays.

### DirectionalLight

```csharp
public sealed class DirectionalLight
{
    public Vector3 Direction { get; set; } = -Vector3.UnitY;
    public float Intensity { get; set; } = 1.0f;
}
```

A single directional source: parallel rays from a fixed world-space direction, like the sun. `Direction` points **from** a surface **to** the light (Lambert convention), so a light directly above the scene has `Direction = +Y`.

No `Transform3D` — directional rays have no position; the source is at infinity. Forcing a `Transform3D` here would lie about what the type is.

The demo uses a single `mainLight` field and threads `Direction` + `Intensity` into per-frame uniforms. There's no light **scene container** yet — adding one is deferred until a second consumer wants the same iteration shape.

### PointLight

```csharp
public sealed class PointLight
{
    public Vector3 Position { get; set; }
    public Vector3 Color { get; set; } = Vector3.One;
    public float Intensity { get; set; } = 1.0f;
    public float Range { get; set; } = 10.0f;
    public bool CastsShadow { get; set; } = false;
}
```

Omnidirectional point source with distance falloff (smooth-cutoff inverse-square: `saturate(1 − (d/range)⁴)² / d²`). Up to 4 active per scene (lit shader sizes its uniform array to 4); the count beyond 4 is silently capped at the C# binding site.

`CastsShadow = true` reserves a slot in the engine's point-shadow cubemap array. Up to 2 shadow-casting point lights are supported (engine + shader cap); shadow-casting lights should be placed at the front of the point-light array because the shader's `uPointShadowCubes[i]` indexing convention pairs index `i` with the `i`-th shadow-casting point light.

### SpotLight

```csharp
public sealed class SpotLight
{
    public Vector3 Position { get; set; }
    public Vector3 Direction { get; set; } = -Vector3.UnitZ;
    public Vector3 Color { get; set; } = Vector3.One;
    public float Intensity { get; set; } = 1.0f;
    public float Range { get; set; } = 10.0f;
    public float InnerConeAngle { get; set; } = MathF.PI / 8.0f;   // 22.5 degrees
    public float OuterConeAngle { get; set; } = MathF.PI / 6.0f;   // 30 degrees
    public bool CastsShadow { get; set; } = false;
}
```

Cone-attenuated point source. Cosines of inner/outer cone angles are computed C#-side and passed as uniforms so the shader doesn't recompute per-fragment. Up to 4 per scene; up to 4 shadow casters (engine + shader cap).

### Deliberate limits

- No light scene container. Demo code holds `mainLight`, `pointLights`, `spotLights` fields and threads uniforms manually.
- No emissive material → light conversion. Lights are explicit objects.

## Animation

An animation is a thing that mutates state as a function of time. The engine factors this into a small surface: an animation host, individual animations, and (optionally) typed curves. Skeletal clips have their own player (`ClipPlayer`, under *Skeletal animation*).

### Contracts

```csharp
public interface IAnimation  { bool Advance(double delta); }
public interface ICurve<T>   { T Evaluate(double time); }
public interface IFiniteCurve<T> : ICurve<T> { double Duration { get; } }
```

- **`IAnimation.Advance`** moves the animation on by `delta` seconds on **its own clock** and returns `true` while running, `false` to be removed. Nothing reads an absolute total: pausing is not advancing it, rate is a scaled delta, and restarting or scrubbing is setting its `Elapsed`. Infinite animations return `true` forever; finite ones return `false` once their curves have all finished, and the host drops them in the same tick.
- **`ICurve<T>`** is a pure `time → value` function. Deliberately no `Duration` on the base interface — infinite curves (`ConstantCurve`, `LoopCurve`) don't have one.
- **`IFiniteCurve<T>`** extends with a `Duration` property. Animations test for this interface to know when to self-remove: `curve is IFiniteCurve<T> finite && local >= finite.Duration` means done. Past `Duration`, finite curves still evaluate (clamped at the end value); they just stop claiming to be alive.

### Built-in curves

```csharp
// Infinite.
new ConstantCurve<float>(1.0f);

// Finite. Clamp at endpoints; report done after Duration.
new LinearCurve         { From = 0,            To = 1,            Duration = 0.3 };
new LinearVector3Curve  { From = Vector3.Zero, To = pos,          Duration = 1.5 };
new SlerpQuaternionCurve{ From = qA,           To = qB,           Duration = 2.0 };

// N-keyframe finite. Strictly-ascending times; clamps to first/last.
new KeyframeVector3Curve(new[] {
    new Keyframe<Vector3>(0.0, Vector3.Zero),
    new Keyframe<Vector3>(0.5, Vector3.UnitY),
});
new KeyframeQuaternionCurve(/* ... */);

// Wrap any curve so its time axis loops over [0, Period). Infinite by definition.
new LoopCurve<float>(inner, period: 2.0);
```

`SlerpQuaternionCurve` and `KeyframeQuaternionCurve` use `Quaternion.Slerp` because rotation values need spherical-linear interpolation along the unit-4-sphere arc. Componentwise lerp on quaternions produces non-unit intermediates and wrong rotation rates.

`LoopCurve` is a curve wrapper, not an animation wrapper — looping is *how time is read*, not *how state is written*, so it belongs at the curve layer. Loop is intentionally not `IFiniteCurve` (a loop runs forever by definition); the underlying curve can be finite or infinite.

### Built-in animations

```csharp
// Curve-driven generic for float-valued state (material uniforms, FoV, intensity).
new FloatAnimation
{
    Curve = new LinearCurve { From = 0, To = 1, Duration = 0.5 },
    Setter = v => fade = v,
    Delay = 0.25,           // holds the curve's start value this long first
};

// Drives Position / Rotation / Scale on a Transform3D from independent curves.
new Transform3DAnimation
{
    Target = door.Transform,
    Position = new LinearVector3Curve { /* ... */ },
    Rotation = null,   // null = leave channel alone
    Scale    = null,
};

// Escape hatch for one-offs: handed the delta, returns "still running?".
new CallbackAnimation(delta => { /* mutate state */; return continueRunning; });

host.AddAnimation(fadeIn);
host.Advance(time.Delta);   // in the order added; the finished are dropped this tick
```

Typed animations are the goal — concrete classes with named fields, refactorable, IDE-navigable. `CallbackAnimation` is the bridge while a one-off lives in just one place. If the same callback shape appears twice, promote to a typed class.

Demo-specific animations stay in the demo. A spin animation like `EulerRotationAnimation` (closed-form, takes a `Vector3 RadiansPerSecond` and sets `Target.Rotation` from `Time.Total × per-axis rate`) is a demo-shaped pattern, not engine-shaped, so it doesn't get promoted until a second consumer needs it.

### AnimationHost

The animation-list + iterate-and-remove machinery lives on a reusable `AnimationHost`: `AddAnimation`, and `Advance(delta)`, which advances every animation in the order added and drops the ones that report done, keeping the survivors' order. It owns no time; it hands each animation the delta it was given.

```csharp
public sealed class AnimationHost : IUpdateable { /* ... */ }
```

The structural commit: **animations attach to whatever owns them, not to a central system**. There's no `AnimationSystem` singleton or service: a game composes a host where it has animations to run, and ticks it. `PhysicsHost3D` is the sibling pattern for motion integration; a type that wants both composes both hosts.

### Targeting

Animations reference their targets via **typed fields**, not property paths or reflection. `Transform3DAnimation.Target` is a `Transform3D` — assigned at construction, the animation mutates it directly. Refactoring, jump-to-definition, and type-checking all work.

`FloatAnimation` uses a `Setter` closure because float-valued state lives in too many different places (`Camera3D.VerticalFieldOfView`, `DirectionalLight.Intensity`, a material parameter, a shader push-constant field) to make each one grow an "animatable" abstraction. The closure captures whatever needs to be written.

### Deliberate limits

- **No `AnimationSystem` singleton.** Animations attach to hosts; hosts tick themselves.
- **No sequence / parallel composition primitives.** `Sequence(fadeIn, hold, fadeOut)` and friends compose trivially over `IAnimation` when needed.
- **No easing functions yet.** Quadratic / cubic / elastic / etc. land when a use case appears.
- **Time is each animation's own.** Pause, rate, restart and scrub are the caller's, through the delta it passes and the `Elapsed` it can set; there is no global time scale.
- **No serialised animation assets.** When Material assets demonstrated the value, JSON-defined animations might follow.

## Skeletal animation

Vertices moving *relative to each other* within a single mesh, driven by a hierarchy of bones. A `ClipPlayer` samples clips into a `Pose`; `PoseBlend`, `PoseDelta` and `BoneMask` compose poses; a `Model` turns the pose into each skin's palette.

### Data primitives

```csharp
public readonly record struct BoneTransform(Vector3 Translation, Quaternion Rotation, Vector3 Scale);

public readonly record struct Bone(string Name, int ParentIndex, BoneTransform Rest, Matrix4x4? Offset = null);

public sealed class Skeleton                       // a pose hierarchy: what clips, stacks and masks index
{
    public Bone[] Bones { get; }
    public int BoneCount { get; }
    public Pose CreateRestPose();                  // each bone's Rest
    public void ComputeBoneWorlds(Pose pose, Matrix4x4[] outWorlds);
}

public sealed class SkinBinding                    // a skin: which bones are its joints, and their binds
{
    public SkinBinding(Skeleton skeleton, IReadOnlyList<int> bones, IReadOnlyList<Matrix4x4> inverseBinds);
    public static SkinBinding Direct(Skeleton skeleton, IReadOnlyList<Matrix4x4> inverseBinds);
    public Skeleton Skeleton { get; }
    public IReadOnlyList<int> Bones { get; }       // joint j -> bone of Skeleton
    public IReadOnlyList<Matrix4x4> InverseBinds { get; }
    public int JointCount { get; }
    public void ComputePalette(IReadOnlyList<Matrix4x4> boneWorlds, Matrix4x4 post, Span<Matrix4x4> palette);
    public void ComputePalette(Pose pose, BonePalette palette, Matrix4x4[]? outBoneWorlds = null);
    public int[] JointParents();
    public Matrix4x4[] JointBindWorlds();
}

public sealed class Pose
{
    public BoneTransform[] Locals { get; }
    public int BoneCount { get; }
    public Pose(int boneCount);
    public Pose(BoneTransform[] locals);
    public void CopyFrom(Pose other);
}

public sealed class BonePalette
{
    public Matrix4x4[] Matrices { get; }
    public int BoneCount { get; }
    public BonePalette(int boneCount);
}
```

`BoneTransform` is the struct form of `Transform3D` — same TRS semantics, value type so a `Pose`'s array of N bone locals doesn't allocate N heap objects. Uses `Translation` (not `Position`) to mark the bone-local domain; reading code disambiguates "this is a bone-local transform inside a skeletal pose" from "this is an object's world position."

**A skeleton is not a skin.** glTF animates nodes; a skin names some of them as its joints and says, per joint, where a vertex sat relative to it at bind. Two skins can share joints with different binds, and a node a clip moves need be no skin's joint. So `Skeleton` is the pose hierarchy alone, and each skin's joints and inverse binds are a `SkinBinding` over it, which keeps the `Skeleton` it indexes: its joint indices mean bones of that hierarchy and no other. A palette is always a binding operation, `inverseBind[j] · world[bones[j]] · post`; a skeleton cannot compute one, because it cannot say which skin's binds to apply.

`Bone` is name, hierarchy via `ParentIndex` (flat-array index, `-1` for root), `Rest` (what it holds when no track moves it: glTF's joint node transform, required) and `Offset` (the non-joint nodes between it and its parent; null is identity). A hand-built skeleton states its rest; nothing reconstructs it from binds, because a file's rest need not be its bind.

`Skeleton` enforces a **hierarchy-order invariant** at construction: each bone's `ParentIndex` is either `-1` or strictly less than its own index. `ComputeBoneWorlds` walks the array once forward with no recursion and no sorting — every parent's world matrix is already filled when a child reads it.

`Pose` carries no reference to its owning `Skeleton`. Animations produce poses; skeletons hold the hierarchy; `ComputeBoneWorlds` joins them with a bone-count validation at the boundary, and a binding's `ComputePalette` refuses worlds that are not one per bone of its skeleton.

`BonePalette` is a typed wrapper around `Matrix4x4[]` — the GPU-ready output, one matrix per joint. `BonePaletteSet` packs many bodies' palettes for one skin at a stride of its joint count; `Model.PackPalettes` fills one set per skin from poses of `Model.Skeleton`.

### Matrix convention (F-016)

Skeletal math uses the same convention as the rest of the engine: **`System.Numerics` row-vector form** — translation in `M41/M42/M43`, a vertex flows left-to-right (`v_row * M`, and `M = A * B` applies `A` first). `GraphicsMatrices.CreateModel` (and therefore `BoneTransform.ToMatrix`) is `Scale * Rotation * Translation` in that form. There are **no transposes** in the skeletal path: `Matrix4x4.Decompose` reads `System.Numerics` matrices directly, so `BoneTransform.FromMatrix` decomposes the matrix as-is, and `Skeleton.ComputeBoneWorlds` composes `child = local * parentWorld` straight (the same walk `Transform3D.WorldMatrix` uses). See [`architecture.md` → Matrices](architecture.md#conventions) for the full convention and the upload→GLSL story; `Blix.Test.Graphics` Section AH pins it.

A manually built inverse-bind matrix is just `GraphicsMatrices.CreateModel(bindPos, bindRot, bindScale)` inverted — no transpose. The cook doesn't transpose either: SharpGLTF already hands back IBMs in this row-vector form, and the `.blixmesh` stores them as read.

### Clips

```csharp
public readonly record struct Keyframe<T>(double Time, T Value);

public sealed class BoneTrack
{
    public int BoneIndex { get; init; }
    public IFiniteCurve<Vector3>?    Translation { get; init; }
    public IFiniteCurve<Quaternion>? Rotation    { get; init; }
    public IFiniteCurve<Vector3>?    Scale       { get; init; }
}

public sealed class AnimationClip
{
    public string Name { get; }
    public BoneTrack[] Tracks { get; }
    public double Duration { get; }
    public void Sample(double time, Pose outPose);
}
```

**`BoneTrack` channels are independent and all-optional.** Mirrors glTF's structure: each glTF animation channel targets one of `{translation, rotation, scale, weights}` on one node, so a single bone may be rotation-animated only, or PRS-animated, or any subset.

**`AnimationClip.Sample(time, outPose)` is partial-write semantics.** Only the bone indices and channels each track explicitly defines are written to `outPose`. Other bones, and the other channels of a track's bone, keep whatever values `outPose` already holds. This is what makes partial clips useful: an "upper body wave" clip can be sampled into a pose that an "idle loop" already populated, and only the upper-body bones change.

For full-pose clips (every bone tracked), the same semantics works — every bone gets overwritten so prior values don't matter. For partial clips, the caller resets `outPose` to a base (typically the cached rest pose) before sampling.

### ClipPlayer

A clip, its own clock, and the reset every hand-rolled consumer got wrong:

```csharp
var player = new ClipPlayer(model.Skeleton, model.Clip("Running_A"));
player.Rate = speed / baseSpeed;   // negative plays backwards; 0 holds
player.Advance(time.Delta);        // false once a one-shot has finished
// player.Pose is the sampled pose; player.RootDelta the root's travel this advance
```

`Advance` starts every sample from the rest pose — `AnimationClip.Sample` writes only the channels a clip has tracks for, so without it untracked bones keep last frame's values. It also guards zero-duration "pose" clips, reports `Finished` in the direction of travel (a one-shot played backwards ends at 0), and measures `RootDelta` across the loop seam piece by piece rather than as one subtraction that jumps backwards every cycle. `ScrubTo` jumps without accruing travel. `Phase` is where the clip sits in its cycle, in [0, 1].

Deliberately not here: the palette (whoever draws builds it, from whichever pose won), and any state machine, blend tree, event track or scheduler.

### Composition: blend, additive, masks

```csharp
PoseBlend.Lerp(a.Pose, b.Pose, weight, outPose);                // crossfade
PoseBlend.Lerp(a.Pose, b.Pose, weight, mask, outPose);          // per bone: weight × mask[i]
outPose.Locals[i] = PoseDelta.LayerOnto(basePose, rest, clipPose, weight);   // additive, per bone
var upper = BoneMask.Subtree(skeleton, "spine", weight: 1f, falloff: 2);
```

- **`PoseBlend.Lerp`** interpolates per bone: translation and scale linearly, rotation by slerp. The masked overload multiplies the weight by the bone's mask, so a mask of 0 leaves that bone exactly as pose A had it.
- **`PoseDelta.LayerOnto`** applies a clip's offset from rest on top of a base: translation adds, rotation composes, scale multiplies, each scaled by the weight.
- **`BoneMask`** names bones by a subtree root, not by indices (an asset's numbering must not leak into game code), with an optional falloff up the chain so a boundary does not kink. An unknown bone name throws, naming the bones that exist.

Weights only: a crossfade is the caller moving a weight.

### PoseStack

The composition in reusable form: an ordered stack of layers, each applied onto the result of the layers below it, starting from the rest pose.

```csharp
var stack = new PoseStack(model.Skeleton);
stack.Add(walk);                                                    // a ClipPlayer; Blend at weight 1 by default
var punch = stack.Add(punchPlayer, PoseLayerMode.Blend, 1f, BoneMask.Subtree(model.Skeleton, "spine", falloff: 2));
punch.OnFinish = PoseLayerFinish.Remove;                            // the default is Hold: keep the last frame
stack.Advance(time.Delta);                                          // advances every layer's player, then evaluates
// stack.Pose is the composed pose
```

- A layer is a source (a `ClipPlayer`, which belongs to that one layer), a mode (`Blend` toward its pose, or `Additive` offset from rest), a weight and an optional `BoneMask`. Each mode is exactly the `PoseBlend` / `PoseDelta` call it stands for.
- **A flat list, not a blend tree:** no nodes, parameters, states, transitions or durations.
- `Evaluate()` recomposes without moving any clock, after a weight, mask or mode change. Root motion is the caller's to take from whichever layer drives the body (`ClipPlayer.RootDelta`).
- It is an `IAnimation` that never finishes, so a stack can sit inside an `AnimationHost` as one entry. It is separate from the host because its layers have an order a host's animations do not.

Studio's `RigAnimation` is a two-layer stack: the subject, and the secondary as a blend, additive or masked layer.

### Skinned models

A skinned model is a `Model` whose parts a skin deforms (see *Cooked models*). The pose is of `Model.Skeleton`, the model's animated hierarchy: every skin's joints and every node a clip moves. Each skin gathers its own palette from that one pose:

```csharp
var palettes = model.CreatePaletteSets(instances: 1);
var bones = model.CreateBoneBuffers(skinnedProgram, maxInstances: 1);
model.PackPalettes(poses, placements, palettes);   // world-space: SkeletonPlacement × placement
for (var s = 0; s < palettes.Length; s++) bones.Upload(s, palettes[s]);
// draw each model.SkinnedParts entry with bones.For(part.SkinIndex)
```

The shader side is `skinning.glsl` (set 3, binding 0, unsized): each skin's buffer is sized for its bones × bodies, so there is no bone cap beyond what the device binds. A crowd is N bodies in one set, read at `gl_InstanceIndex × boneCount` (Bulwark).

### Cooked models

The engine reads cooked models only. `ModelData.Load(path, needs, scene)` reads a `.blixmesh` — glTF's scene graph as the cook wrote it: nodes, meshes placed by them, skins as joint nodes, clips on nodes, materials and the scene level (scenes, visibility, instances, cameras, lights, material variants). `ModelNeeds` declares the vertex layout the pipelines read. `device.CreateModel(data, textureLoader, name)` makes it resident: one uploaded `Mesh` per primitive with its material and textures, and the skins and clips above.

A tool opens glTF sources by cooking them first (`Blix.Recipes.CookCache.Resolve`); `Blix.Import` holds the source importers the cook uses.

### Deliberate limits (skeletal)

- **Rigid-bone normal assumption.** Skinning the normal with the full skin matrix is exact for rotation + uniform scale, approximate for non-uniform per-bone scale.
- **No CPU skinning fallback.** GPU only.
- **No state machine or animation graph.** What plays when is game code's; the engine learns weights.
- **A skin mirrored at some joints and not others** within one primitive draws with one front face (Studio judges it by one weighted bone).
- **`Pose` is mutable shared state.** A pose is written by one player per frame; a consumer that blends reads players' poses into a pose of its own.

## Physics

`PhysicsHost3D` is the motion-integration sibling of `AnimationHost`. Anything that wants velocity-driven motion composes one and points its `Target` at a `Transform3D`:

```csharp
public sealed class PhysicsHost3D : IFixedUpdateable
{
    public Transform3D Target { get; init; }
    public Vector3 Velocity { get; set; }
    public float GravityScale { get; set; } = 1.0f;       // 0 disables gravity
    public Vector3 Gravity { get; set; } = new(0, -9.81f, 0);

    public void FixedUpdate(Time time)
    {
        var dt = (float)time.Delta;
        Velocity      += Gravity * GravityScale * dt;
        Target.Position += Velocity * dt;
    }
}
```

A game composes a host per body and points it at that body's `Transform3D`; something wanting animation too composes an `AnimationHost` beside it.

`PhysicsHost2D` mirrors `PhysicsHost3D` for 2D: `Transform2D` target, `Vector2` velocity, `Gravity` defaults `(0, 9.81f)` (Y-down to match screen-space ortho where origin is top-left). Nothing wires it yet: Pong, the 2D demo, keeps its own pose. `Blix.Test.Physics2D` covers the 2D collision it would pair with (`CollisionWorld2D`, `Intersection2D`).

### Engine vs game-code split

Motion integration lives in the engine (`PhysicsHost*.FixedUpdate`). Collision detection lives in the engine (`Intersection.Test` / `.Sweep` / `.Raycast`, queried via `CollisionWorld3D<T>` for static world geometry). Response math helpers live in `Blix.Geometry.CollisionResponse` (`ReflectVelocity`, `RemoveNormalComponent`, `ReflectVelocities`). **Choosing the response is the game's call.**

The five canonical response modes are doc'd as vocabulary on `CollisionResponse`, not as an enum the engine branches on:

| Mode | What it does | Helper |
| --- | --- | --- |
| **None** | Ignore the hit | — |
| **Trigger** | Fire an event, don't touch state | — |
| **Stop** | `Velocity = Vector3.Zero` | — |
| **Slide** | Kill into-surface velocity, preserve tangential | `RemoveNormalComponent` |
| **Bounce** | Reflect into-surface velocity with restitution | `ReflectVelocity`, `ReflectVelocities` |

### NormalisedInverseMass

Two-body collision response needs a per-body knob: how readily does this body get pushed when something hits it? The engine names that **NormalisedInverseMass** — mathematically equivalent to `1 / mass`, scaled so `1.0` is the baseline unit-mobile body and `0.0` is anchored (doesn't move at all).

| Value | Meaning |
| --- | --- |
| `0.0` | Anchored — body doesn't move on impact, partner absorbs the full impulse |
| `1.0` | Baseline unit-mobile dynamic body |
| `< 1` | Heavier than baseline (harder to push) |
| `> 1` | Lighter than baseline (easier to push) |

The term is deliberately verbose to avoid `Mass`, which would invite `ApplyForce` / `ApplyImpulse` / F=ma vocabulary the engine doesn't want yet. NormalisedInverseMass expresses the kinematic concept (responsiveness) without committing to integrated dynamics.

**Not on `PhysicsHost3D`.** The value lives wherever the game wants it (a constant for uniform-mass cubes, a per-body field on a game-side class, a Dictionary keyed on body reference, an asset). When the response helper needs it, the game passes the value at the call site.

**World vs dynamic pairs are structurally distinct.** `CollisionWorld3D<T>` holds *static* colliders only — its concerns are layer masks, broadphase optimisation (eventually), and integration with graph/grid/navmesh structures for AI (eventually). Dynamic-vs-dynamic collision is a different problem — pair iteration with two-body response math, no broadphase needed at small scales. The engine keeps these as separate APIs rather than papering them over with "static = NormalisedInverseMass 0 in the same container."

### Deliberate limits

- **No `Mass`.** Even though `NormalisedInverseMass` is mathematically `1 / mass`, the engine deliberately doesn't expose `Mass` as a separate field.
- **No `ApplyForce` / `ApplyImpulse` / springs / damping.** Same reason — adding force-style API would pull the engine into integrated dynamics it doesn't model yet.
- **No friction / drag.** Velocity decay over time and tangential velocity damping at contact are response-side concerns.
- **No constraint solver.** Each `PhysicsHost3D` integrates independently; there is no system that resolves multi-body contacts simultaneously. Adequate for "things fall and bounce" demos; not adequate for a stack of crates that needs to settle.
- **No dynamic colliders in `CollisionWorld3D<T>`.** The world is static-only by design.
- **Single-pass depenetration.** A body in contact with multiple colliders may need multiple resolution passes to settle.

## Geometry + collision

Geometric primitives and intersection tests live in **`Blix.Geometry`** — a dependency-free project that sits below `Blix.Assets`, `Blix.Render`, `Blix`, and the demo. Everything above can use these types without inheriting any other engine concept.

### 3D primitives

```csharp
public readonly record struct Bounds3(Vector3 Min, Vector3 Max);
public readonly record struct BoundingSphere(Vector3 Center, float Radius);
public readonly record struct Ray(Vector3 Origin, Vector3 Direction);
public readonly record struct Plane(Vector3 Normal, float Offset);
public readonly record struct Triangle(Vector3 V0, Vector3 V1, Vector3 V2);
public readonly record struct Capsule(Vector3 PointA, Vector3 PointB, float Radius);
public readonly record struct OrientedBounds3(Vector3 Center, Quaternion Orientation, Vector3 HalfExtents);
public sealed class TriangleMesh3D;
```

`Plane` is the boundary of an infinite halfspace: the solid region is on the negative-Normal side (`SignedDistance(P) < 0`), free space on the positive side. A floor with `Normal = +Y` has its solid region below.

`Triangle` is the atomic primitive of mesh colliders. `NormalRaw` is the unnormalised cross product (cheap); `Normal` is the unit normal; `Centroid` is the arithmetic mean.

`TriangleMesh3D` is a static triangle-soup collider — vertices baked in **world space** at construction, paired with a cached `Bounds` AABB so every intersection query can early-out against a single AABB before touching individual triangles. World-space-only removes any per-mesh transform from the inner loop. A mesh that moves (rotating platform, animated door) needs to be rebuilt on transform change; a future `TransformedTriangleMesh` storing local triangles + a `Transform3D` reference can land alongside the first moving-collider use case.

`OrientedBounds3` is the tilted axis-aligned box: bridges the AABB/triangle-mesh gap for rotated-prop content where AABB is too loose and a triangle mesh is overkill.

`Blix.Assets` adds an extension method bridging authored content into the collider format: `MeshData.ToTriangleMesh(Matrix4x4? worldTransform = null)` reads the position component from each vertex, optionally transforms it, and packs into a `TriangleMesh3D`.

### 2D primitives

```csharp
public readonly record struct Bounds2(Vector2 Min, Vector2 Max);
public readonly record struct Circle(Vector2 Center, float Radius);
public readonly record struct Capsule2D(Vector2 PointA, Vector2 PointB, float Radius);
public readonly record struct OrientedBounds2(Vector2 Center, float Rotation, Vector2 HalfExtents);
public readonly record struct Segment2D(Vector2 A, Vector2 B);
public sealed class LineMesh2D;
```

Sibling shapes to the 3D ones. `Plane` is intentionally absent in 2D — half-spaces are rare; line segments + OBBs cover the same content with less indirection.

### Intersection results

All discrete and sweep tests return a shared `CollisionHit?` (or `CollisionHit2D?` for the 2D path):

```csharp
public readonly record struct CollisionHit
{
    public float Time   { get; init; }  // [0,1] for sweep, 0 for discrete
    public Vector3 Point  { get; init; }  // world-space contact
    public Vector3 Normal { get; init; }  // points from B into A (push direction)
    public float Depth  { get; init; }  // positive overlap for discrete; 0 for sweep
}
```

The same struct serves both: discrete tests fill `Time = 0` and `Depth = overlap`; sweep tests fill `Time = TOI` and `Depth = 0`. `Hit.Normal * Hit.Depth` depenetrates for discrete; `Hit.Time` interpolates position for sweep.

### Discrete tests

```csharp
public static class Intersection
{
    // Pair tests (3D). Every shape vs every other.
    public static CollisionHit? Test(BoundingSphere a, BoundingSphere b);
    public static CollisionHit? Test(BoundingSphere s, Bounds3 b);
    public static CollisionHit? Test(Bounds3 a, Bounds3 b);
    public static CollisionHit? Test(BoundingSphere s, Plane p);
    public static CollisionHit? Test(Bounds3 b, Plane p);
    public static CollisionHit? Test(BoundingSphere s, TriangleMesh3D m);
    public static CollisionHit? Test(Bounds3 b, TriangleMesh3D m);
    public static CollisionHit? Test(Capsule a, Capsule b);
    public static CollisionHit? Test(BoundingSphere s, Capsule c);
    public static CollisionHit? Test(Capsule c, Bounds3 b);
    public static CollisionHit? Test(Capsule c, Plane p);
    public static CollisionHit? Test(Capsule c, TriangleMesh3D m);
    public static CollisionHit? Test(OrientedBounds3 a, OrientedBounds3 b);
    public static CollisionHit? Test(OrientedBounds3 o, Bounds3 b);
    public static CollisionHit? Test(OrientedBounds3 o, BoundingSphere s);
    public static CollisionHit? Test(OrientedBounds3 o, Plane p);
    public static CollisionHit? Test(OrientedBounds3 o, Capsule c);
    public static CollisionHit? Test(OrientedBounds3 o, TriangleMesh3D m);
}
```

Each returns null on disjoint; on overlap, computes the minimum-penetration separating axis, the contact normal (pointing from B into A, so `A.Position += Normal * Depth` separates them), and a sensible contact point. Concentric / centre-inside-AABB degenerate cases pick a stable normal rather than returning `NaN`.

Triangle-mesh tests iterate every triangle (after a single mesh-bounds early-out) and return the deepest contact. Sphere-Triangle uses the closest-point-on-triangle formulation (Ericson §5.1.5). AABB-Triangle uses Akenine-Möller's 13-axis SAT. Capsule-Triangle computes the minimum distance between the capsule's segment and the triangle by checking 8 candidate pairs.

OBB-OBB uses 15-axis SAT. Other OBB-vs-X tests transform the query into OBB-local space, run the AABB version, transform the contact data back.

### Sweep tests (continuous collision)

```csharp
public static CollisionHit? Sweep(BoundingSphere a, Vector3 motionA, BoundingSphere b, Vector3 motionB);
public static CollisionHit? Sweep(BoundingSphere s, Vector3 sMotion, Bounds3 b, Vector3 bMotion);
public static CollisionHit? Sweep(Bounds3 a, Vector3 motionA, Bounds3 b, Vector3 motionB);
public static CollisionHit? Sweep(BoundingSphere s, Vector3 sMotion, Plane p);
public static CollisionHit? Sweep(Bounds3 b, Vector3 bMotion, Plane p);
```

Per-shape per-step displacement. Returns `CollisionHit?` with `Time ∈ [0, 1]` for the fraction of the step at which contact first occurs. Sphere-Sphere is the exact quadratic; AABB-AABB uses the slab method on relative motion; Sphere-AABB is conservative (ray-vs-AABB-inflated-by-radius). Already-overlapping pairs degrade to the discrete `Test` result so callers see `Depth` for depenetration. Plane has no motion argument — planes are infinite static surfaces by convention.

### Raycasts

```csharp
public static CollisionHit? Raycast(Ray r, BoundingSphere s, float maxDistance = float.PositiveInfinity);
public static CollisionHit? Raycast(Ray r, Bounds3 b,        float maxDistance = float.PositiveInfinity);
public static CollisionHit? Raycast(Ray r, Plane p,          float maxDistance = float.PositiveInfinity);
public static CollisionHit? Raycast(Ray r, TriangleMesh3D m, float maxDistance = float.PositiveInfinity);
public static CollisionHit? Raycast(Ray r, Capsule c,        float maxDistance = float.PositiveInfinity);
public static CollisionHit? Raycast(Ray r, OrientedBounds3 o, float maxDistance = float.PositiveInfinity);
```

`CollisionHit.Time` is the parametric distance along the ray (`Point = ray.Origin + ray.Direction * Time`) — this departs from sweep's `[0, 1]` convention because rays have no natural "step length." `Ray.Direction` is expected to be unit length.

The mesh raycast does a single AABB early-out, then Möller-Trumbore per triangle (two-sided). The capsule raycast decomposes into infinite-cylinder + two hemispherical caps. Ray-origin-inside cases clamp `Time = 0` and return the entry direction as the normal.

### Intersection2D

Sibling static class with all-pairs tests + per-primitive raycasts for the 2D primitives. Mirrors the 3D pattern. Pressure-tested by the `Blix.Test.Physics2D` CLI harness (43 cases). The 3D original it mirrors had **no suite at all** until the character arc; it has one now (`Blix.Test.Physics3D`), and the asymmetry is worth remembering — the mirror was tested for years while the thing it mirrored was not.

### CollisionWorld3D + CollisionWorld2D

```csharp
public sealed class CollisionWorld3D<T> where T : notnull
{
    public void Add(T owner, Bounds3 bounds,        CollisionLayer layer = default);
    public void Add(T owner, BoundingSphere sphere, CollisionLayer layer = default);
    public void Add(T owner, Plane plane,           CollisionLayer layer = default);
    public void Add(T owner, TriangleMesh3D mesh,   CollisionLayer layer = default);
    public void Add(T owner, Capsule capsule,       CollisionLayer layer = default);
    public void Add(T owner, OrientedBounds3 obb,   CollisionLayer layer = default);

    public void Remove(T owner);
    public void Clear();

    public CollisionContact3D<T>? Raycast(Ray ray, float maxDistance = ∞);
    public CollisionContact3D<T>? Raycast(Ray ray, CollisionMask mask, float maxDistance = ∞);
    public void Overlap(Bounds3 query,        List<CollisionContact3D<T>> results);
    public void Overlap(Bounds3 query,        CollisionMask mask, List<CollisionContact3D<T>> results);
    public void Overlap(BoundingSphere query, List<CollisionContact3D<T>> results);
    public void Overlap(BoundingSphere query, CollisionMask mask, List<CollisionContact3D<T>> results);
}

public readonly record struct CollisionContact3D<T>(T Owner, CollisionLayer Layer, CollisionHit Hit);
```

`Raycast` returns the closest hit (one `CollisionContact3D<T>?`). `Overlap` takes a caller-provided list so the per-frame collision loop can reuse a single buffer with zero allocations.

No broadphase yet — internals are plain typed lists, queries iterate everything. When n² becomes a profiling problem, swap the internal storage to BVH / grid / sweep-and-prune; the public API is shape-pair-driven, so the change stays internal.

`CollisionWorld2D<T>` is the 2D analogue with the same shape, taking 2D primitives and returning `CollisionContact2D<T>`.

### Layers + masks

Two typed wrappers around a 32-bit field, semantically distinct:

```csharp
public readonly record struct CollisionLayer(uint Bits);
public readonly record struct CollisionMask(uint Bits);
```

A collider has a **layer** (typically a single bit — "what kind of thing am I"). A query takes a **mask** (any combination of bits — "what kinds do I care about"). A query matches a collider when `mask.Matches(layer)`.

| Method | Returns | Use |
| --- | --- | --- |
| `CollisionLayer.Default` | bit 0 set | Default for colliders added without an explicit layer |
| `CollisionLayer.FromIndex(int)` | the bit at `index` | Pick a specific slot 0..31 |
| `CollisionMask.All` | every bit | Default for queries without a filter |
| `CollisionMask.None` | no bits | Sentinel — query that matches nothing |
| `CollisionMask.FromLayer(layer)` | one layer's bits | "Only this one kind" |
| `CollisionMask.FromLayers(a, b, c)` | union of bits | "Any of these kinds" |

Defaults are **backward-compatible**: a collider added without an explicit layer goes into `CollisionLayer.Default` (bit 0); a query without an explicit mask uses `CollisionMask.All`.

Bidirectional matching ("A wants to hit B AND B wants to be hit by A") isn't modelled — query-side only. If needed, callers run two queries and combine.

### Deliberate limits (geometry + collision)

- **No spatial acceleration** (BVH, octree, grid, sweep-and-prune). Pairwise iteration is fine at the demo's scale.
- **Convex shapes are AABB, Sphere, Plane, Capsule, OBB.** Convex hull deferred until a real consumer needs it.
- **`Capsule`-vs-`AABB`** is approximate (closest segment point to AABB centre, then sphere-vs-AABB). Exact for "character outside the wall," conservative when the segment skims the AABB at a steep angle.
- **OBB-OBB contact point** is approximated as A's centre offset by half the penetration depth along the contact normal. Adequate for kinematic depenetration; a proper contact-manifold solver isn't built.
- **No OBB sweep tests.** Only discrete + raycast.
- **Sweep tests not yet defined for 2D primitives.**
- **No collision-response in the engine.** `CollisionHit` carries enough info (`Normal`, `Depth`, `Point`) for the caller to depenetrate, bounce, slide, or trigger an event — the engine has no opinion about which.

## Picking

Picking — "the user clicked a screen pixel; what world-space object did they hit?" — composes from primitives already in the engine: `Camera3D` knows how to unproject, `Intersection.Raycast` knows how to hit-test, `CollisionWorld3D` knows how to iterate static colliders.

```csharp
public Ray Camera3D.ScreenPointToRay(
    float screenX, float screenY,
    float viewportWidth, float viewportHeight);
```

Takes a cursor position in **screen pixels** (top-left origin, Y growing downward — the window-system convention) and a viewport size in the **same coordinate system**, returns a world-space `Ray` whose origin sits on the near plane and whose direction points away from the camera through that screen pixel.

### Coordinate-system gotcha — HDPI / Retina

`RenderFrameContext.Width/Height` is the framebuffer in **physical pixels** (2× larger on Retina). Mouse events fire in **logical pixels**. Mixing them — passing framebuffer dimensions with mouse coords — silently shifts NDC by ~2×, sending the ray into the wrong corner of the scene. `IRenderHost.LogicalSize` returns the window's client area in logical pixels and is the right value for `ScreenPointToRay`'s viewport parameters when the caller is using mouse coords.

```csharp
var (logicalW, logicalH) = Host.LogicalSize;
var ray = camera.ScreenPointToRay(mouseX, mouseY, logicalW, logicalH);
var hit = collisionWorld.Raycast(ray);
```

### Picking through a named view

`ScreenPointToRay` takes a viewport size and **recomputes** the view-projection from
its aspect ratio. That quietly assumes two things: the view fills the window, and its
matrix is the one the camera would derive. Neither holds for a picture drawn into a
panel — an inspector viewport, a split screen, a second camera on the same scene —
which is exactly what first-class views exist to allow.

```csharp
public static Ray? ViewPicking.RayThrough(in ViewDeclaration view, Vector2 pointer);
```

Reads the view's **own** matrix and **own** rectangle, so a picture drawn anywhere can
be picked anywhere, including one rendered to an off-screen texture. `Blix.Tools.View`
is the proving consumer: Studio renders a second camera into an off-screen target,
`ViewportPanel` presents that texture inside ImGui, and selection casts through the
letterboxed image rectangle. `RayThrough` returns `null` when the pointer is outside
the view — which is also how *"which view is the cursor over?"* gets answered: ask
each declared view, and for a non-overlapping layout at most one says yes. Overlapping
panels are an ordering question, which is the caller's.

For a view that does fill the window it returns **exactly** what `ScreenPointToRay`
returns; `Blix.Test.Graphics` Section **AN** pins that equality, because a picking
routine that disagrees with the camera is worse than none.

`pointer` is in logical coordinates and is compared against
`ViewDeclaration.LogicalViewport`. A view also records a physical rectangle, but the
view does not schedule a scene pass or automatically install that rectangle as a Vulkan
viewport/scissor. The embedded Studio viewport explicitly renders a pass that fills its
own target; its logical rectangle describes where the letterboxed image appears for
input. See [Architecture](architecture.md#views-and-the-frame-that-is-not-one-picture).

**What's pickable is what the game chooses to iterate.** Picking has no engine-level "is pickable" concept — `CollisionWorld3D` holds whatever the game registers, and the per-object loop is game code. A future editor would probably want a separate `PickableRegistry<T>` (similar to `CollisionWorld3D`, distinct semantics — "click-targetable" vs "physically present"), but that doesn't exist yet and bolting it onto colliders would conflate two unrelated questions. `ViewPicking` deliberately stops at the ray for the same reason: it answers where a click points, not what is there. What exists in a world, how it is stored, and what selecting something means stay with the application — and the two applications here that pick things already disagree about all three.

## Audio

3D positional audio. The structure mirrors graphics: an abstract assembly (`Blix.Audio`) + a backend (`Blix.Audio.OpenAL`) + game-layer wrappers (in `Blix`) + an asset importer (in `Blix.Assets`).

### Architecture

- **`Blix.Audio`** — abstract: `IAudioDevice`, opaque `AudioClipHandle` / `AudioSourceHandle`, state records `AudioListenerState` + `AudioSourceState`, CPU-side `AudioClipData` (PCM bytes + sample format).
- **`Blix.Audio.OpenAL`** — `OpenALAudioDevice` via the OpenTK.Audio.OpenAL NuGet. Opens the system default device, creates one context, sets the global distance model to `AL_INVERSE_DISTANCE_CLAMPED`. Per-source state diffing avoids redundant `alSource*` calls when nothing changed frame-to-frame.
- **`Blix.Assets.WavImporter`** — RIFF/WAVE parser. Accepts 8-bit unsigned and 16-bit signed PCM, mono or stereo, any sample rate. Tolerates LIST/JUNK/INFO chunks. Rejects float/24-bit/ADPCM/μ-law with a clear error. Validates chunk sizes against file length before reading.
- **`Blix.Core.IAudioHost`** — companion facet to `IRenderHost`. `Window` implements both; `Game.AudioDevice` is auto-captured from the host on load.

### AudioListener + AudioSource

```csharp
public sealed class AudioListener
{
    public Transform3D Transform { get; init; }
    public float Gain { get; set; } = 1.0f;
    public void Sync(IAudioDevice device);
}

public sealed class AudioSource : IDisposable
{
    public AudioSourceHandle Handle { get; }
    public Vector3 Position { get; set; }
    public float Gain { get; set; } = 1.0f;
    public float Pitch { get; set; } = 1.0f;
    public bool IsLooping { get; set; } = false;
    public float ReferenceDistance { get; set; } = 1.0f;
    public float MaxDistance { get; set; } = 100.0f;

    public AudioSource(IAudioDevice device, AudioClipHandle clip, string? name = null);
    public void Sync(IAudioDevice device);
    public void Play(IAudioDevice device);
    public void Pause(IAudioDevice device);
    public void Stop(IAudioDevice device);
    public void Dispose(IAudioDevice device);
}
```

Listener holds a `Transform3D` reference and `Sync(device)` pushes position + Forward/Up + master gain. Source holds the backend handle and mutable per-frame parameters; `Sync(device)` pushes state, lifecycle methods cover playback.

### Update model

State-based push, once per frame. Game code calls `audioListener.Sync(device)` and `audioSource.Sync(device)` after updating positions (e.g. at the end of `OnUpdate`). The device's per-source state diff keeps the AL-call count proportional to actual change rate.

### Deliberate limits

- **PCM only.** 8-bit unsigned and 16-bit signed PCM, mono or stereo, any sample rate. No float WAVs, no 24/32-bit, no ADPCM/μ-law/A-law. No OGG/MP3 — promote to NVorbis when real music content motivates it.
- **No streaming.** Every clip loads fully into an OpenAL buffer.
- **Mono-only positional.** OpenAL's 3D math only applies to mono sources. Stereo sources play as-is, unpositionalised.
- **No Doppler.** State records have no velocity field.
- **No directional cones.** Sources are omnidirectional.
- **No source pooling.** Each `AudioSource` owns one OpenAL source for its full lifetime.
- **One global distance model.** `AL_INVERSE_DISTANCE_CLAMPED`, with per-source `ReferenceDistance` + `MaxDistance` exposed.
- **No reverb / DSP effects.** OpenAL Soft supports EFX via AL_EXT_EFX; out of scope for v0.
- **macOS requires OpenAL Soft** (`brew install openal-soft`). Apple's bundled framework is deprecated since 10.15 and silently no-ops. `OpenALAudioDevice` probes the standard Homebrew prefixes for `libopenal.dylib`; otherwise it logs a warning and falls back (which will likely be silent).

## Cross-references

- **Mesh / materials / shaders / render passes** — game code reads `obj.Mesh` and `obj.Material` (a `MaterialHandle`) and records them into the Vulkan `RenderGraph`. See `src/Demos/Blix.Demos.VulkanLit/` and `src/Demos/Blix.Demos.VulkanSponza/` for application-owned render setup, [Renderer](renderer.md) for the ownership split, and [Architecture](architecture.md#the-vulkan-binding-model) for the backend's binding model.
- **`IDebuggable` / `DebugContext` / debug draw** — game code implements `IDebuggable` to contribute UI/values/draw commands; the diagnostics system lives in `Blix.Diagnostics`.
- **2D physics test harness** — `Blix.Test.Physics2D` is a 43-case CLI test runner exercising every `Intersection2D` overload. Pressure-tests the 2D primitives without a visual demo.
- **3D physics test harness** — `Blix.Test.Physics3D`, its twin, opened by the character arc because the 3D math had never had one. Covers the capsule: the nine-candidate closest pair (including the impaled case the original eight could not see), the exact plane sweep, and the converging triangle/mesh sweep checked against it — plus tunnelling at 100 m/s with the discrete test as its control.

## Roadmap

In approximate priority order. Each item is a feature direction, not a structural commitment — concrete shapes get designed when their feature gets built.

### Pending

- **Editor layer.** Scene authoring, material tweaking, save/load. Substantially larger than other items here; picking, embedded viewports, and diagnostics UI are meaningful pieces, not yet an editor model.
- **Broadphase** (BVH / grid / SAP). Premature until profiling shows pairwise n² in `CollisionWorld3D` is a problem. Mesh-internal BVH first (when triangle counts pass hundreds), world-level second.

### Skipped (explicitly deferred)

- **Forces / impulses / mass + multi-body solver.** Real physics-gameplay. Kinematic depenetration covers the demo; full N-body iterative resolution is a multi-week commitment that isn't justified by current content.
- **Engine-owned pathfinding.** Bulwark and external game consumers already carry domain-specific navigation. No repeated generic contract yet justifies moving one representation or policy into `Blix`.
- **Convex hull collider.** No specific content needs it; OBB covers the tilted-prop case.
- **Hot-reload / asset cache.** The cooked-asset pipeline (`.blixtex` / `.blixprobe` / `.blixmesh`) skips the slow import paths for VulkanSponza, but there's no in-memory asset cache or hot-reload; uncooked loads re-import every time. Each becomes a follow-up when iteration speed becomes a bottleneck.
