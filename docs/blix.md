# Blix

The layer game code targets. Owns the loop contract, scene composition, per-object pose, cameras + lights, animation + skeletal animation, physics, geometry + collision, audio, and the glTF importer. Everything below it (`Blix.Core`, `Blix.Graphics`, `Blix.Graphics.Vulkan`, `Blix.Graphics.Images`, `Blix.Render`, `Blix.Assets`, `Blix.Diagnostics`, `Blix.Geometry`) is platform/renderer plumbing.

This doc is the reference for the `Blix` namespace. For the layers below, see [`architecture.md`](architecture.md) (project graph, host contracts, the Vulkan binding model) and [`renderer.md`](renderer.md) (render graph, shaders, rendering techniques).

## Overview

The deliberate framing: this layer is small and explicit. It does **not** introduce an ECS, a constraint-solver physics system, a scripting boundary, an editor, or a scenes-as-assets format. It introduces the smallest set of types a frame of update + render needs — a loop contract, transforms, objects, cameras, lights, time, animations (typed), skeletal animation, kinematic physics + collision, audio, and a glTF importer — and lets game code keep its own lists.

Anything richer (a scene graph, parenting, render queues, animation graphs, multi-body solver, navmesh, editor) is a follow-on that lands when there's a real consumer.

## Getting started

```csharp
using Blix;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Graphics.Vulkan;
using Blix.Runtime.Silk;

using var window = new Window(new MyGame());
window.Run();

internal sealed class MyGame : Game, IInputHandler, IDebuggable
{
    private readonly List<GameObject> objects = new();
    private Camera3D camera = null!;

    protected override void OnLoad()
    {
        Host.SetTitle("My Game");

        var mesh = GraphicsDevice.CreateMesh(/* ... */);

        // A GameObject just stores a backend-neutral MaterialHandle. Creating one
        // is a renderer concern: on the Vulkan device, CreateMaterial allocates a
        // MaterialBindings against a SPIR-V-reflected descriptor set; you write it
        // by name and take .Handle. (Full render setup lives in the demos.)
        var vk = (VulkanGraphicsDevice)GraphicsDevice;
        MaterialHandle material = vk.CreateMaterial(litShaderProgram, name: "hero.material")
            .SetUniform(binding: 0, "uTint", Vector4.One)
            .SetTexture(binding: 1, albedoTexture)
            .Handle;
        objects.Add(new GameObject("hero", mesh, material,
            new Transform3D { Position = new Vector3(0, 0, -2) }));

        camera = new Camera3D
        {
            Transform = new Transform3D { Position = new Vector3(0, 1, 3) },
            VerticalFieldOfView = MathF.PI / 3.0f,
            NearPlane = 0.1f,
            FarPlane = 100.0f,
        };
    }

    public override void OnUpdate(Time time)
    {
        foreach (var obj in objects)
            (obj as IUpdateable)?.Update(time);
    }

    public override void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        var view = camera.GetView();
        var projection = camera.GetProjection(frame.Width / (float)frame.Height);

        // The Vulkan backend drives a declarative RenderGraph: passes declare their
        // targets + Read edges, materials bind through MaterialBindings, and per-draw
        // data rides push constants / transient descriptor sets. See the demo programs
        // (src/Blix.Demos.VulkanLit, src/Blix.Demos.VulkanSponza) for the full render
        // setup; this doc focuses on the game-layer types above the renderer.
        foreach (var obj in objects)
        {
            // record obj.Mesh + obj.Material (a MaterialHandle) into the frame's graph
        }
    }

    public string DebugName => "MyGame";
    public void Debug(DebugContext debug) { /* opt-in */ }
    public void OnKeyDown(Key key) { /* opt-in */ }
    // ... other IInputHandler methods
}
```

See `Blix.Demos.VulkanLit/Program.cs` for a full game-layer reference — it exercises the lit/skinned/PBR path (directional + spot + point shadows, IBL, bloom, skinned glTF). For the higher-end scene path (3-cascade shadows, depth pre-pass, IBL, geometry LOD, optional froxel volumetric fog, ACES/AgX tonemap), see `Blix.Demos.VulkanSponza/Program.cs`. For the game layer driving an actual playable title — the fixed-step-ish update loop, `Transform3D`, `PhysicsHost3D` (gravity/jump), `CollisionWorld3D.Overlap`, skeletal animation (`SkinnedGameObject` path via clip → `Pose` → `BonePalette`), `AudioSource`, and `IDebuggable` diagnostics, all wired together — see `Blix.Demos.Runner/Program.cs` (a 3D endless runner).

## Project dependencies

`Blix` references:
- `Blix.Core` — `IRenderHost`, `IAudioHost`, `IInputHandler`, `Key`, `MouseButton`, `RenderFrameContext`, `IRuntimeDiagnosticsSink`
- `Blix.Geometry` — `Bounds3`/`Bounds2`, `BoundingSphere`, `Ray`, `Plane`, `Triangle`, `Capsule`, `OrientedBounds3`, `TriangleMesh3D`, `Circle`, `Capsule2D`, `OrientedBounds2`, `LineMesh2D`, `Segment2D`, `Intersection`/`Intersection2D`, `CollisionHit`, `CollisionResponse`
- `Blix.Graphics` — `IGraphicsDevice`, `RenderCommandList`, matrix helpers
- `Blix.Render` — `Mesh` (composed into `GameObject` / `Submesh`; the material slot is a backend-neutral `MaterialHandle` from `Blix.Graphics`)
- `Blix.Assets` — `IAssetImporter<T>`, `AssetImportContext`, `MeshData` (for the glTF importer)

Plus one NuGet dependency: **SharpGLTF.Toolkit**, used only by `GltfImporter` for parsing `.glb` / `.gltf` files. The glTF types are translated into engine types (`MeshData`, `Skeleton`, `AnimationClip`) at the format boundary so a future FBX or proprietary importer hits the same surface.

Nothing references `Blix` from below. No transitive dependency on `Blix.Runtime.Silk` or `Blix.Diagnostics` — the layer is platform-free.

## Public API index

Every public type in `Blix`, one-line each.

**Loop + time:** `IGameLoop`, `Game`, `Time`, `IUpdateable`, `IFixedUpdateable`, `FixedStepClock`

**Scene primitives:** `GameObject`, `Submesh`, `Transform3D`, `Transform2D`

**Cameras + lights:** `Camera3D`, `Camera2D`, `DirectionalLight`, `PointLight`, `SpotLight`

**Animation surface:** `IAnimated`, `IAnimation`, `ICurve<T>`, `IFiniteCurve<T>`, `AnimationHost`, `AnimatedGameObject`, `LinearCurve`, `LinearVector3Curve`, `SlerpQuaternionCurve`, `KeyframeVector3Curve`, `KeyframeQuaternionCurve`, `Keyframe<T>`, `LoopCurve<T>`, `ConstantCurve<T>`, `FloatAnimation`, `Transform3DAnimation`, `CallbackAnimation`, `Curves`

**Skeletal animation:** `BoneTransform`, `Bone`, `Skeleton`, `Pose`, `BonePalette`, `AnimationClip`, `BoneTrack`, `ClipAnimation`, `BlendedClipAnimation`, `AdditiveClipAnimation`, `PoseBlend`, `PoseDelta`, `SkinnedGameObject`

**glTF import:** `GltfImporter`, `GltfModel`, `GltfPrimitive`, `GltfMaterial`, `GltfTexture`

**Physics:** `PhysicsHost3D`, `PhysicsHost2D`, `PhysicsGameObject`

**Collision world:** `CollisionWorld3D<T>`, `CollisionWorld2D<T>`, `CollisionContact3D<T>`, `CollisionContact2D<T>`, `CollisionLayer`, `CollisionMask`

**Audio:** `AudioListener`, `AudioSource`

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

- **Time vs render dims.** `Time` is its own value type. `RenderFrameContext` carries only `(Width, Height)` of the default framebuffer. Splitting lets render-side and update-side state diverge cleanly (fixed-step physics, time scale, pause).
- **Input vs loop.** Input methods live on `Blix.Core.IInputHandler`, not on `IGameLoop`. Games opt in by implementing both; the runtime forwards events only when `IInputHandler` is present.
- **Diagnostics vs loop.** `IDebuggable` (in `Blix.Diagnostics`) is a separate opt-in surface.

### Game

The minimum useful base. Captures `IRenderHost` and `IGraphicsDevice` once at load, exposes them as `Host` and `GraphicsDevice`. Also auto-captures `AudioDevice` from `Host as IAudioHost` and routes `OnFixedUpdate` through the engine's `FixedStepClock`.

```csharp
internal sealed class MyGame : Game, IInputHandler, IDebuggable
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

Override `protected virtual double FixedStep => 1.0 / 60.0` to change the fixed cadence.

### Time

```csharp
public readonly record struct Time(double Total, double Delta);
```

A passed-by-value struct. `Total` is seconds since startup (monotonically increasing); `Delta` is seconds since the previous tick. The runtime accumulates `Total` from per-frame deltas; no platform clock leaks across the boundary.

`Time` and `RenderFrameContext` are separate because `Time` is a layer-spanning fact ("what frame is the engine on, how long since the last one"), while `RenderFrameContext` is render-side info that only matters when there's a swapchain.

#### Deliberate limits

- No `Time.Now` singleton. Passing explicitly avoids hidden global state and keeps tests trivial.

### IUpdateable + IFixedUpdateable

Universal opt-in update contracts. Plain `GameObject` is *not* `IUpdateable` — objects that just sit in the scene don't pay a virtual call or claim behaviour they don't have. Subtypes opt in.

```csharp
public interface IUpdateable    { void Update(Time time); }
public interface IFixedUpdateable { void FixedUpdate(Time time); }
```

Variable-rate (`Update`) fires once per render frame at whatever the display does (typically 60–144Hz). Fixed-rate (`FixedUpdate`) fires at a fixed cadence (default 60Hz) — zero, one, or more times per frame depending on how much delta has accumulated. The two interfaces have different method names so a single class can implement both with independent method bodies.

### FixedStepClock

`Game` owns one and drives it from `OnUpdate`:

```csharp
void IGameLoop.OnUpdate(Time time)
{
    OnUpdate(time);
    var steps = fixedClock.Accumulate(time);
    for (var i = 0; i < steps; i++)
        OnFixedUpdate(new Time(time.Total, FixedStep));
}
```

`FixedStepClock` caps `MaxStepsPerFrame` at 4 by default — if the renderer hitches (debugger pause, OS suspend) and seconds of delta accumulate, the catch-up bursts are bounded. Any leftover beyond the cap is dropped (slow-mo recovery) instead of running thousands of steps the moment the app resumes.

Iterate each scene list twice per outer frame: once with `(obj as IUpdateable)?.Update(time)` (variable, in `OnUpdate`), once with `(obj as IFixedUpdateable)?.FixedUpdate(time)` (fixed, in `OnFixedUpdate`).

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

1. **Inspect it** — `dotnet run --project src/Blix.Tools.Cook -- inspect <model.glb>` prints the node hierarchy and, for each mesh-bearing node, its composed-world `scale` / `translation` and assembled `bounds`. A rigged part's **node translation is its rotation pivot** (authors place the node origin at the hinge); the bounds give the model's size and forward axis.
2. **Import nodes, not a fused blob** — `GltfStaticImporter.ImportNodes` keeps every node in its own local space (vs `Import`, which bakes world transforms into one static mesh). Compose each node's world transform by walking parents (`world = local * parentWorld`).
3. **Bake each part to its pivot** — transform a part's primitives by `nodeWorld * Translate(-pivot)` so its pivot sits at the mesh origin, upload as a `Mesh`, and give each part its own `InstancedBatch` (reusing one world/caster pipeline — same vertex layout).
4. **Drive from the rig** — the per-frame instance matrix is `Scale(s) * RotateY(yawFix) * rigPart.WorldMatrix * Translate(0, lift, 0)`, and the rig's child positions (turret-on-hull, gun-on-turret) are the **measured** node offsets mapped through the same `RotateY(yawFix) * s` — so the meshes and the gameplay rig (aim, muzzle, recoil) stay locked and a single scale/yaw knob can't desync them.
5. **Expose only the residuals** as live `[Tune]` knobs — global scale, a forward-axis `yawFix` (model `-X` → engine `-Z` is `-90°`), and a vertical `lift`. Because the pivots came from measurement, sensible defaults land the fit with no dialing. `Blix.Demos.TankArena.LoadTankParts` / `SeatRig` / `PartModel` are the worked reference.

#### Deliberate limits

- Class, not struct. Game code regularly hands the same transform to multiple consumers; reference semantics are the right default.
- No parent/child container on `GameObject` — parenting is pose-level on `Transform3D`. Game code wires `Transform.Parent` and keeps its own object lists; there's no automatic scene-graph that owns children, draw order, or lifetimes.
- No dirty-flag caching for `ToMatrix()` / `WorldMatrix`. Matrix composition is a few small multiplies and hierarchies here are shallow; `WorldMatrix` re-walks the parent chain on each access (add caching when a deep rig needs it). Cycle-checking happens once, on `Parent` assignment.
- Non-uniform parent scale combined with a child rotation can shear the child (the standard TRS-hierarchy limitation; content keeps non-uniform scale off shared parents, mirroring the rigid-bone skinning assumption — `TankArena` applies a single **uniform** model scale in the per-part instance matrix, not in the pose hierarchy).
- No `Origin` / `Pivot`. Mesh-side authored offsets are normalised at import time (`ObjImporter.RecenterToOrigin`, default true); game code uses plain `Transform.Position`.

### Transform2D

The 2D sibling. Same shape; scalar `Rotation` around Z; 2D `Position` and `Scale`. `ToMatrix()` returns `Matrix4x4` (not `Matrix3x2`) so 2D content composes with the same shader pipelines and view/projection plumbing as 3D content — Z row stays identity and 2D objects sit on `z = 0`.

`Right` and `Up` are the two natural 2D facing conventions — pick whichever matches the sprite art. `Forward` is intentionally absent (no canonical "forward" in 2D).

#### Deliberate limits

- No `LookAt`. Collapses to `Rotation = MathF.Atan2(target.Y - Position.Y, target.X - Position.X)` (modulo facing convention).
- `ToMatrix()` returns `Matrix4x4`, not `Matrix3x2`, to share the renderer's `uModel` pipeline.

### GameObject

A plain container — `string Name`, `Mesh Mesh`, `MaterialHandle Material`, `Transform3D Transform`. No tags, no visibility flag, no component list, no render-queue field. The `Material` slot is the backend-neutral `MaterialHandle` (from `Blix.Graphics`), not a name-keyed uniform/texture bag.

```csharp
var heroCube = new GameObject("hero_cube", cubeMesh, heroCubeMaterial,
    new Transform3D { Position = new Vector3(-0.25f, -0.05f, -0.55f) });
```

Game code arranges `GameObject`s into whatever lists it needs:

```csharp
private readonly List<GameObject> opaqueObjects = new();
private readonly List<GameObject> glassObjects = new();
```

The demo splits by render pass: opaque list feeds shadow + scene passes; glass list feeds the refractive pass that runs after the scene snapshot. Pass routing is deliberately not the engine's concern — a future render-queue convention can land when the demo isn't the only consumer.

#### Deliberate limits

- No container abstraction. The demo's `List<GameObject>`s are the right size for the actual problem.
- No `Visible` flag. Add when something needs to hide without removing.
- No render-queue tag. Pass routing is the caller's problem until a second consumer wants the same routing decisions.

### Submesh

```csharp
public readonly record struct Submesh(Mesh Mesh, MaterialHandle Material);
```

The atomic draw unit for multi-part meshes (typically glTF characters split into body / hair / clothing). `SkinnedGameObject` carries a `Submesh[]`; the base `GameObject.Mesh` / `Material` reflect `Submeshes[0]` for compatibility with code that reads them generically.

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

Every type with a pose composes a `Transform`. Camera3D, GameObject, PointLight, SpotLight — all of them read and write pose through `.Transform`. The win is that **animation targets the Transform**, not the owning type: `() => camera.Transform.Position` and `() => obj.Transform.Position` are the same animation target shape. No branching by target type when animations land.

### Deliberate limits

- No `ICamera` interface. Each camera class stays concrete until a real consumer needs the polymorphism.
- `Camera3D` and `Camera2D` stay separate. A unified `Camera { Mode: 2D/3D, ... }` was considered and rejected — the API split (FoV vs Zoom, perspective vs ortho) is cleaner than the mode branch every consumer would need.
- `Camera2D` is platform-ready but unused by the current demos. The 2D layer (Transform2D, Camera2D, the 2D geometry/collision primitives) exists in full ready for the first 2D game.

## Lights

Three light types, structurally distinct rather than a common `Light` base — their per-fragment evaluation differs (distance falloff, cone falloff, world-space direction-only), and the renderer's shader binds them as type-specific uniform arrays.

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

An animation is a thing that mutates state as a function of time. The engine factors this into a small surface: an updateable, an animation host, individual animations, and (optionally) typed curves.

### Contracts

```csharp
public interface IAnimated   { void AddAnimation(IAnimation animation); }
public interface IAnimation  { bool Sample(Time time); }
public interface ICurve<T>   { T Evaluate(double time); }
public interface IFiniteCurve<T> : ICurve<T> { double Duration { get; } }
```

- **`IAnimated`** is "I host animations." Does *not* extend `IUpdateable` — a host typically also is updateable, but the interfaces stay independent so a future host could be driven externally.
- **`IAnimation.Sample`** returns `bool`: `true` while running, `false` to be removed. Infinite animations just return `true` forever. Finite animations check whether their curves are still running and return `false` once they've all finished; the host drops them in the same tick.
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
    Setter = v => mainLight.Intensity = v,
    StartTime = time.Total,
};

// Drives Position / Rotation / Scale on a Transform3D from independent curves.
new Transform3DAnimation
{
    Target = obj.Transform,
    Position = new LinearVector3Curve { /* ... */ },
    Rotation = null,   // null = leave channel alone
    Scale    = null,
    StartTime = time.Total,
};

// Escape hatch for one-offs.
new CallbackAnimation(time => { /* mutate state */; return continueRunning; });
```

Typed animations are the goal — concrete classes with named fields, refactorable, IDE-navigable. `CallbackAnimation` is the bridge while a one-off lives in just one place. If the same callback shape appears twice, promote to a typed class.

Demo-specific animations stay in the demo. A spin animation like `EulerRotationAnimation` (closed-form, takes a `Vector3 RadiansPerSecond` and sets `Target.Rotation` from `Time.Total × per-axis rate`) is a demo-shaped pattern, not engine-shaped, so it doesn't get promoted until a second consumer needs it.

### AnimationHost + AnimatedGameObject

The animation-list + iterate-and-remove machinery lives on a reusable `AnimationHost`. `AnimatedGameObject` is a thin delegate that composes one:

```csharp
public sealed class AnimationHost : IUpdateable, IAnimated { /* ... */ }

public sealed class AnimatedGameObject : GameObject, IUpdateable, IAnimated
{
    private readonly AnimationHost host = new();
    public void AddAnimation(IAnimation a) => host.AddAnimation(a);
    public void Update(Time time) => host.Update(time);
}
```

The structural commit: **animations attach to the thing being animated, not to a separate central system**. There's no `AnimationSystem` singleton or service. An `AnimatedGameObject` owns its animation list, ticks it as part of its own `Update`, and removes done animations in place. Game code's update loop just iterates objects and calls `Update`.

`AnimationHost` was extracted from `AnimatedGameObject` when physics arrived as a second composable behaviour (`PhysicsHost3D` is the sibling). Hosts are the engine's chosen composition unit: small, reusable, single-purpose. When game code wants animations *and* physics on the same object, it subclasses `GameObject` and composes both hosts directly — the engine doesn't ship a combinatorial `AnimatedPhysicsGameObject`.

### Targeting

Animations reference their targets via **typed fields**, not property paths or reflection. `Transform3DAnimation.Target` is a `Transform3D` — assigned at construction, the animation mutates it directly. Refactoring, jump-to-definition, and type-checking all work.

`FloatAnimation` uses a `Setter` closure because float-valued state lives in too many different places (`Camera3D.VerticalFieldOfView`, `DirectionalLight.Intensity`, a material parameter, a shader push-constant field) to make each one grow an "animatable" abstraction. The closure captures whatever needs to be written.

### Deliberate limits

- **No `AnimationSystem` singleton.** Animations attach to hosts; hosts tick themselves.
- **No sequence / parallel composition primitives.** `Sequence(fadeIn, hold, fadeOut)` and friends compose trivially over `IAnimation` when needed.
- **No easing functions yet.** Quadratic / cubic / elastic / etc. land when a use case appears.
- **No `Start(Time)` helper.** Callers set `StartTime` manually at construction.
- **No pause / resume / scrub.** Lifecycle is just "running or done." Time scale and pause would come from the `Time` side rather than per-animation state.
- **No serialised animation assets.** When Material assets demonstrated the value, JSON-defined animations might follow.

## Skeletal animation

Vertices moving *relative to each other* within a single mesh, driven by a hierarchy of bones. Composes with the value-animation surface above: a `SkinnedGameObject` carries an `AnimationHost`, ticks it on `Update`, and the attached `ClipAnimation` / `BlendedClipAnimation` / `AdditiveClipAnimation` write into the rig's `Pose`.

### Data primitives

```csharp
public readonly record struct BoneTransform(Vector3 Translation, Quaternion Rotation, Vector3 Scale);

public readonly record struct Bone(string Name, int ParentIndex, Matrix4x4 InverseBindPose);

public sealed class Skeleton
{
    public Bone[] Bones { get; }
    public int BoneCount { get; }
    public Pose CreateRestPose();
    public void ComputeBonePalette(Pose pose, BonePalette outPalette);
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

`BoneTransform` is the struct form of `Transform3D` — same TRS semantics, value type so a `Pose`'s array of N bone locals doesn't allocate N heap objects. Uses `Translation` (not `Position`) to mark the bone-local domain; reading code disambiguates "this is a bone-local transform inside a skeletal pose" from "this is a GameObject's world position."

`Bone` carries everything bind-time-constant: name, hierarchy via `ParentIndex` (flat-array index, `-1` for root), and the `InverseBindPose` matrix the GPU consumes directly.

`Skeleton` enforces a **hierarchy-order invariant** at construction: each bone's `ParentIndex` is either `-1` or strictly less than its own index. `ComputeBonePalette` walks the array once forward with no recursion and no sorting — every parent's world matrix is already filled when a child reads it.

`Pose` carries no reference to its owning `Skeleton`. Animations produce poses; skeletons hold metadata; `ComputeBonePalette` joins them with a bone-count validation at the boundary.

`BonePalette` is a typed wrapper around `Matrix4x4[]` — the GPU-ready output. Bind via `new ShaderUniform("uBones", new Matrix4x4ArrayUniform(palette.Matrices))`.

### Matrix convention (F-016)

Skeletal math uses the same convention as the rest of the engine: **`System.Numerics` row-vector form** — translation in `M41/M42/M43`, a vertex flows left-to-right (`v_row * M`, and `M = A * B` applies `A` first). `GraphicsMatrices.CreateModel` (and therefore `BoneTransform.ToMatrix`) is `Scale * Rotation * Translation` in that form. There are **no transposes** in the skeletal path: `Matrix4x4.Decompose` reads `System.Numerics` matrices directly, so `BoneTransform.FromMatrix` decomposes the matrix as-is, and `Skeleton.ComputeBonePalette` composes `child = local * parentWorld` straight (the same walk `Transform3D.WorldMatrix` uses). See [`architecture.md` → Matrices](architecture.md#conventions) for the full convention and the upload→GLSL story; `Blix.Test.Graphics` Section AH pins it.

A manually built inverse-bind matrix is just `GraphicsMatrices.CreateModel(bindPos, bindRot, bindScale)` inverted — no transpose. `GltfImporter` doesn't transpose either: SharpGLTF already hands back IBMs in this row-vector form.

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

### ClipAnimation

The `IAnimation` adapter for `AnimationClip`:

```csharp
public sealed class ClipAnimation : IAnimation
{
    public AnimationClip Clip { get; init; }
    public Pose Target { get; init; }
    public double StartTime { get; init; }
    public bool Loop { get; init; } = true;
}
```

In `Loop = true` mode (default) it wraps `elapsed % Duration` indefinitely; in `Loop = false` mode it returns false from `Sample` once elapsed exceeds Duration, and `AnimationHost` removes it — same lifecycle pattern as `Transform3DAnimation`.

### Composition: blend + additive

```csharp
public static class PoseBlend
{
    public static void Lerp(Pose poseA, Pose poseB, float weight, Pose outPose);
    public static BoneTransform LerpBone(BoneTransform a, BoneTransform b, float weight);
}

public sealed class BlendedClipAnimation : IAnimation
{
    public AnimationClip ClipA, ClipB;
    public Pose Target, RestPose;
    public float Weight { get; set; }   // mutable; 0 = ClipA, 1 = ClipB
    public bool Loop { get; init; } = true;
}

public static class PoseDelta
{
    // Layers a clip's pose deltas (relative to the rest pose) onto the target.
    public static void LayerOnto(Pose target, Pose rest, Pose clip, float weight);
}

public sealed class AdditiveClipAnimation : IAnimation
{
    public AnimationClip Clip;
    public Pose Target, RestPose;
    public float Weight { get; set; }
    public bool Loop { get; init; } = true;
}
```

**`PoseBlend.Lerp`** is the per-bone interpolation primitive: translation and scale lerp linearly; rotation slerps along the unit-quaternion arc.

**`BlendedClipAnimation`** owns two scratch poses internally (lazy-allocated, reused frame-to-frame). Each `Sample`: sample `ClipA` from rest into `scratchA`, sample `ClipB` from rest into `scratchB`, `PoseBlend.Lerp(scratchA, scratchB, Weight, Target)`. Each clip wraps independently against its own duration — a 1.2s Walk and a 0.8s Run blend correctly without phase-locking.

**`AdditiveClipAnimation`** does per-bone delta math:
- Translation: `target.translation += weight * (clip.translation - rest.translation)`
- Rotation: `target.rotation *= slerp(identity, inverse(rest.rotation) * clip.rotation, weight)`
- Scale: `target.scale *= lerp(1, clip.scale / rest.scale, weight)`

Composes naturally with `AnimationHost` ordering: add the base animation (`ClipAnimation` / `BlendedClipAnimation`) **first**, the additive **second**. The host iterates in insertion order, so the additive reads the base's output and modifies it in place. Multiple additives stack.

`Weight` is mutable, not curve-driven. Game code mutates `Weight` per frame for any crossfade shape (linear ramp, eased transition, UI-slider scrub, state-machine output). The engine deliberately doesn't ship a built-in `CrossfadeClipAnimation` with self-completing lifecycle — every game's crossfade timing/easing is its own decision.

### SkinnedGameObject

```csharp
public sealed class SkinnedGameObject : GameObject, IUpdateable, IAnimated
{
    public Skeleton Skeleton { get; }
    public Pose RestPose { get; }
    public Pose Pose { get; }
    public BonePalette Palette { get; }
    public Submesh[] Submeshes { get; }
    public Bounds3 AggregateBounds { get; }
    public Matrix4x4 MeshNodeTransform { get; }
    public void AddAnimation(IAnimation animation);
    public void Update(Time time);   // CopyFrom(rest) → host.Update → ComputeBonePalette
}
```

Parallel to `AnimatedGameObject` (carries an `AnimationHost`) and `PhysicsGameObject` (sibling kind of game object, no inheritance between them). Each frame's `Update` does the standard skeletal sequence:

1. `Pose.CopyFrom(RestPose)` — partial clips overlay onto a known base.
2. `host.Update(time)` — every attached `ClipAnimation` / `BlendedClipAnimation` / `AdditiveClipAnimation` samples into `Pose`.
3. `Skeleton.ComputeBonePalette(Pose, Palette)` — GPU-ready matrices.

Render code reads `Palette.Matrices` to bind the bone-palette uniform; everything else (transform, mesh, material) is the standard `GameObject` surface.

`MeshNodeTransform` is the glTF mesh-node ancestor matrix the importer captured. Compose into the model matrix at draw time: `uModel = Transform.ToMatrix() * MeshNodeTransform`. Identity for hand-built skinned content.

`AggregateBounds` is the union of every submesh's mesh-local AABB, computed once at construction. Useful for editor pick volumes / culling against the whole character regardless of which submesh's bounds individually contain a point. Rest-pose only — doesn't reflect current `Pose` deformation; accurate posed bounds would need per-frame recomputation from `Palette`, deferred until consumers demand it.

### glTF import

```csharp
public sealed class GltfImporter : IAssetImporter<GltfModel>
{
    public string Name => "rigged-model.gltf";
}

public sealed record GltfModel(
    GltfPrimitive[] Primitives,
    Skeleton Skeleton,
    AnimationClip[] Animations,
    Matrix4x4 MeshNodeTransform);

public sealed record GltfPrimitive(MeshData Mesh, GltfMaterial? Material);

public sealed record GltfMaterial(
    string Name,
    Vector4 BaseColorFactor,
    GltfTexture? BaseColorTexture,
    GltfTexture? NormalTexture,
    GltfTexture? MetallicRoughnessTexture,
    float MetallicFactor,
    float RoughnessFactor);

public sealed record GltfTexture(string Name, byte[] RgbaPixels, int Width, int Height);
```

Registered via `assets.RegisterImporter(new GltfImporter())`, manifested via the dispatch key `"rigged-model.gltf"`.

```csharp
var gltf = assets.Load<GltfModel>(AssetId.Parse("models/cesium_man"));
var skeleton = gltf.Skeleton;
var animations = gltf.Animations;
foreach (var prim in gltf.Primitives)
{
    var mesh = graphicsDevice.CreateMesh(prim.Mesh);
    var material = /* convert prim.Material to runtime Material */;
}
```

Per-file invariants the importer enforces or normalises:

- **First skinned mesh wins as the primary skin.** Multi-mesh characters (body + hair + clothing) sharing one skin are all collected and concatenated into `Primitives[]` provided they share the primary node's `WorldMatrix`.
- **Joints topo-sorted.** glTF's `Skin.Joints` is an ordered list of nodes, but the order isn't required to be hierarchy-ordered. The importer computes parent indices by walking each joint's `VisualParent` chain up to the next ancestor that's also a joint, then DFS-orders the joints so parents come first.
- **Inverse-bind matrices kept in the engine's row-vector form** at the boundary — SharpGLTF already returns them that way, so no transpose is applied (see "Matrix convention (F-016)" above).
- **`LINEAR` interpolation only.** glTF also defines `STEP` and `CUBICSPLINE`; the importer throws `NotSupportedException` on either.
- **Channel filtering by skin.** Animations that don't touch the skin's joints produce empty `AnimationClip`s and are dropped.
- **Morph-target weight channels skipped.** Lands alongside their first real use.

Vertex stream decoding: `POSITION` is required; `NORMAL` defaults to `(0, 1, 0)` if absent; `TEXCOORD_0` defaults to `(0, 0)`; `JOINTS_0` and `WEIGHTS_0` are required (this is the *skinned* mesh importer); `TANGENT` is optional (missing = `(0,0,0,0)` sentinel that the shader falls back from). Indices wider than `ushort` throw.

UV.y is flipped at import (`uv = (rawUv.X, 1 - rawUv.Y)`). glTF authors UVs with origin at the top of the image (Y-down per spec); the engine's pipeline flips PNG/JPEG textures at load so OBJ-style Y-up UVs render correctly. Flipping the glTF UV.y at import puts everything in the same convention.

### Deliberate limits (skeletal)

- **64-bone uniform array cap.** Bigger characters split into multiple skinned submeshes, or upgrade the upload to a UBO / texture later.
- **Rigid-bone normal assumption.** Skinning the normal with the full skin matrix is exact for rotation + uniform scale, approximate for non-uniform per-bone scale. Content avoiding non-uniform per-bone scale is the convention.
- **No CPU skinning fallback.** GPU only.
- **`Weight` on blend/additive not clamped at setter.** `Sample` clamps to [0, 1]; out-of-range values silently behave like 0 or 1.
- **No mask-driven partial blends.** All bones blend with the same Weight. Mask-driven would replace the scalar Weight with a per-bone weight array.
- **No state-machine / animation-graph abstraction.** Game code owns the "what plays when" decision.
- **Aggregate bounds are rest-pose-only.** Animated deformation isn't reflected.
- **One skin per glTF file.** Multi-skin files pick only the primary skin's meshes; secondary skins are silently ignored.
- **`Pose` is mutable shared state.** `ClipAnimation.Target` is a reference to a `Pose` that the host expects to be reset to `RestPose` before each tick. The convention is one `Pose` per `SkinnedGameObject`.

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

`PhysicsGameObject` is the convenience class — a `GameObject` that composes a `PhysicsHost3D` with `Target = this.Transform`, exposes `Physics` for tuning, and dispatches `FixedUpdate`. Sibling pattern to `AnimatedGameObject`; an object needing both behaviours defines its own subclass composing both hosts directly.

`PhysicsHost2D` mirrors `PhysicsHost3D` for 2D: `Transform2D` target, `Vector2` velocity, `Gravity` defaults `(0, 9.81f)` (Y-down to match screen-space ortho where origin is top-left). Used by 2D physics tests (`Blix.Test.Physics2D`); no in-engine consumer wires it for the demo because the demo is 3D.

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

Sibling static class with all-pairs tests + per-primitive raycasts for the 2D primitives. Mirrors the 3D pattern. Pressure-tested by the `Blix.Test.Physics2D` CLI harness (43 cases).

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
be picked anywhere, including one rendered to an off-screen texture. (Picking into such
a view works today; *rendering* one as a panel does not — see the view limitations in
[`architecture.md`](architecture.md).) Returns `null`
when the pointer is outside the view — which is also how *"which view is the cursor
over?"* gets answered: ask each declared view, and for a non-overlapping layout at
most one says yes. Overlapping panels are an ordering question, which is the caller's.

For a view that does fill the window it returns **exactly** what `ScreenPointToRay`
returns; `Blix.Test.Graphics` Section **AN** pins that equality, because a picking
routine that disagrees with the camera is worse than none.

`pointer` is in logical coordinates and is compared against
`ViewDeclaration.LogicalViewport`. A view carries both rectangles — logical for input,
physical for the renderer — so neither is derived at a call site, which is the Retina
bug described above, removed rather than documented.

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

- **Mesh / materials / shaders / render passes** — game code reads `obj.Mesh` and `obj.Material` (a `MaterialHandle`) and records them into the Vulkan `RenderGraph`. See `src/Blix.Demos.VulkanLit/` and `src/Blix.Demos.VulkanSponza/` for the render setup, and [`architecture.md`](architecture.md#the-vulkan-binding-model) for the backend's binding model.
- **`IDebuggable` / `DebugContext` / debug draw** — game code implements `IDebuggable` to contribute UI/values/draw commands; the diagnostics system lives in `Blix.Diagnostics`.
- **2D physics test harness** — `Blix.Test.Physics2D` is a 43-case CLI test runner exercising every `Intersection2D` overload. Pressure-tests the 2D primitives without a visual demo.

## Roadmap

In approximate priority order. Each item is a feature direction, not a structural commitment — concrete shapes get designed when their feature gets built.

### Pending

- **Editor layer.** Scene authoring, material tweaking, save/load. Substantially larger than other items here; the picking + debug-overlay infrastructure already in place is a meaningful head start.
- **Broadphase** (BVH / grid / SAP). Premature until profiling shows pairwise n² in `CollisionWorld3D` is a problem. Mesh-internal BVH first (when triangle counts pass hundreds), world-level second.

### Skipped (explicitly deferred)

- **Forces / impulses / mass + multi-body solver.** Real physics-gameplay. Kinematic depenetration covers the demo; full N-body iterative resolution is a multi-week commitment that isn't justified by current content.
- **Pathfinding.** Graph / navmesh / grid representations. No autonomous-AI content motivates it.
- **Convex hull collider.** No specific content needs it; OBB covers the tilted-prop case.
- **Hot-reload / asset cache.** The cooked-asset pipeline (`.blixtex` / `.blixprobe` / `.blixmesh`) skips the slow import paths for VulkanSponza, but there's no in-memory asset cache or hot-reload; uncooked loads re-import every time. Each becomes a follow-up when iteration speed becomes a bottleneck.
- **Particle system.** Generic GPU/CPU emitter with sorted billboards + soft-particle depth fade. Planned next; a real particle system would enable sparks, embers, dust motes, debris.
