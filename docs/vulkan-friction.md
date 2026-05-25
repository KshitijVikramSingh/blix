# Vulkan-backend API friction notes

Running log of places where the existing engine surface (`IGraphicsDevice`,
`Material`, `MaterialResolver`, `RenderCommandList`, etc.) creaks against
Vulkan during implementation of `Blix.Graphics.Vulkan`. Each entry should
be short — one paragraph max — describing the symptom, the root mismatch,
and a rough redesign direction. The audit at the end of the validation
phase consolidates this into the concrete reshape plan.

Append-only during a push; consolidate during the audit.

---

## Pre-implementation: friction points named before any Vk code was written

These were called out in the strategic discussion before the cube push,
so they don't have a "discovered during" anchor — they're known up front.

### F-001 — `CreateShaderProgram(ShaderSources)` takes GLSL text

GL-style: shader source is GLSL, compiled at runtime against the driver.
Vulkan-style: shader is SPIR-V bytecode, compiled offline. Runtime GLSL→SPV
needs libshaderc or DXC. **Direction:** make SPIR-V bytes the primary
surface (`CreateShaderProgram(byte[] vertSpv, byte[] fragSpv, ...)`); add
an optional layered helper for runtime GLSL compilation when we want hot
reload. Today we have a Vulkan-only `CreateShaderProgramFromSpv` as a
side channel; the main `CreateShaderProgram` throws NotImplemented.

### F-002 — `Material.SetUniform(string name, ...)` / `SetTexture(string name, ...)`

GL-style: uniforms found by name via reflection (`glGetUniformLocation`).
Vulkan-style: bindings are explicit `(set, binding)` pairs declared in the
SPIR-V; names are not consulted at draw time. Today on Vulkan we'd have to
either (a) reflect the SPIR-V to recover names → bindings (`SPIRV-Reflect`),
or (b) declare the layout out-of-band and translate names → slots at the
material layer. **Direction:** material bindings become explicit
`(set, binding)` references, with names kept as developer-facing aliases
in the JSON material descriptor (resolved at cook time, not runtime).

### F-003 — The dozen `CreateTextureCube*` / `CreateTexture2D*` overloads

GL-style: each overload encodes a specific GL upload convention
(`Half[]` mip arrays, packed face-major byte layouts, sRGB-vs-linear via
the format enum, etc.). Vulkan-style: a texture is a `VkImage` + memory +
view + format + extent + miplevels + arraylayers + samples — one
descriptor, many possible payloads. **Direction:** collapse the surface
to one or two entry points (`CreateTexture(TextureDescription, byte[] data)`
and maybe `AllocateTexture(TextureDescription)` for streamed uploads); push
format/aspect/conventions into `TextureDescription`.

### F-004 — `Mesh` + `DrawMesh` lacks instancing / indirect / offsets

GL VAO-style: one mesh = one (vertex, index) pair bound to a pipeline.
Vulkan-style: command buffers can record `vkCmdDrawIndexedIndirect`,
multi-instance, and bind vertex buffers with per-buffer offsets. Today's
abstraction doesn't expose any of this. **Direction:** add `instanceCount`
+ optional `instanceBuffer` to `DrawIndexedCommand`; add a `DrawIndirect`
variant; let `Mesh` carry sub-range offsets so a single big vertex buffer
can host many meshes.

### F-005 — No compute-pass concept

GL 4.1 can't issue compute dispatches at all (compute is 4.3+). The engine
never modeled compute as a result. Vulkan has compute as a first-class
queue/pipeline type. Today's `RenderPass` / `DrawIndexedCommand` shape
has no slot for it. **Direction:** add `ComputePass` (no
RenderPassDescription, no framebuffer, just dispatches) and
`DispatchCommand(pipeline, groupCount, ...)`. Required for froxel fog,
GPU-driven culling, anything modern.

### F-006 — Implicit synchronization everywhere

GL: serial command stream, driver inserts whatever sync is needed.
Vulkan: explicit barriers, queue dependencies, image layout transitions —
the consumer of `IGraphicsDevice` has no way to express "this pass reads
from that pass" so the backend can't insert barriers correctly without
guessing. Today this works for the trivial case (one pass writing to the
swapchain image, declared via subpass dependency in the render pass).
Anything beyond that needs explicit dependency declaration.
**Direction:** add `PassDependency` to `RenderPassDescription`
(reads-from / writes-to resource lists), or fold the whole thing into a
render-graph where dependencies fall out automatically from declared
input/output resources.

---

## During implementation

Sections per push, dated. Each push's section is "what we learned today."

### Push: spinning cube (depth + descriptors + UBO + name→offset)

Validation push to feel where the existing API creaked when we plumbed
real per-frame matrices through the cross-backend `ShaderUniform` flow.
All observations below were captured inline in the code with `// FRICTION`
markers pointing back to entries here.

### F-007 — Per-draw UBO collision when draws share a program

In `TranslateDrawIndexed` (Swapchain.cs) we route each draw's
`perDrawUniforms` into the program's single per-frame UBO. Two draws
issued in the same frame sharing the same shader program would
overwrite each other's uniforms before either GPU draw executed. The
hello-cube has one draw so it works; Sponza wouldn't.
**Direction (in order of escalation):** push constants for small per-draw
data (4 mat4s fits in 256 bytes which is the conservative push-constant
budget); dynamic-offset UBOs with one big ring buffer for everything;
per-draw descriptor sets allocated from a per-frame transient pool. The
cross-backend `DrawIndexedCommand.Uniforms` shape doesn't telegraph
which of these is intended — it implicitly assumes "set this many uniforms
just for this draw" with no rules about lifetime. Needs explicit
classification: per-pass vs per-material vs per-draw.

### F-008 — `System.Numerics.Matrix4x4` row-vector ↔ GLSL column-vector

REVISED after observation: the row-major memory layout of .NET's
`Matrix4x4` IS the transpose, because GLSL `std140` reads mat4 bytes
column-major. Writing .NET's row-vector matrix directly to the UBO
makes GLSL see it as the column-vector equivalent — mathematically
identical transformation. **No explicit `Matrix4x4.Transpose()` is
needed.** First write of `WriteUniformValue` added one, which
double-transposed and scrambled the cube's projection into garbage clip
positions — the visible symptom was "clipped + stretched pyramid"
because most vertices landed outside the [-1,1] x [-1,1] x [0,1] clip
cube.
**Direction:** no API change needed. Worth a brief note in the engine
docs once they exist — "matrix uniforms: pass System.Numerics matrices
unchanged; GLSL std140 column-major reading already produces the
correct transform." Footgun risk is real (the failure mode is silent
visual corruption, not a compile error), so the rule belongs somewhere
authoritative once the redesign lands.

### F-009 — Array uniforms hit std140 padding rules we don't model

`Matrix4x4ArrayUniform`, `Vector3ArrayUniform`, `FloatArrayUniform` are
all in the cross-backend API today but the Vulkan backend's
`WriteUniformValue` throws `NotImplemented` for them. The reason is
std140's array-stride rule (every array element is padded to a
multiple of 16 bytes — `float arr[4]` takes 64 bytes, not 16), and we
haven't modeled per-array stride in `UniformBlockMember`.
**Direction:** either extend `UniformBlockMember` with `ElementStride`
and write loops, or move arrays out of UBOs and into SSBOs (Vulkan's
preferred path for >handful arrays). Bone palettes specifically are
better as SSBOs.

### F-010 — No Vulkan-NDC-aware perspective helper

`HelloLoop.VulkanPerspective` is hand-rolled in the demo because
`System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView` returns a
GL-convention matrix (Y up, depth [-1,1]) and Vulkan wants Y down +
depth [0,1]. Every Vulkan-targeting demo will re-derive this.
**Direction:** add `Blix.Graphics.Matrices.PerspectiveVulkan(fovY,
aspect, near, far)` (or just make the engine's projection helpers
backend-aware via a static convention). Same story for orthographic
+ off-center, and same story for `CreateLookAt` (which is right-handed
in System.Numerics and matches Vulkan view space — no fix needed there).

### F-011 — `DescriptorSetLayout` shape is hardcoded in the backend

`CreateDescriptorInfrastructure` declares one UBO at binding 0 visible
to vertex+fragment stages. That's the only thing it'll ever produce.
Real shaders have multiple bindings (uniform blocks + samplers +
storage), distributed across sets (per-frame, per-pass, per-material,
per-draw), with stage-specific visibility. The cross-backend API has
no place to declare this today — `UniformBlockLayout` describes the
inside of one block, not the set's binding shape.
**Direction:** introduce `ShaderProgramInterface` carried alongside the
SPIR-V bytes: list of `(set, binding, type, stages, count, blockLayout?)`.
Could be cooked offline from SPIR-V reflection; could be hand-declared.
Material descriptor sets layer on top by referencing `(set, binding)`
explicitly.

### F-012 — `ClearDepth` is a `bool` but render passes always clear depth now

`RenderPassDescription.ClearDepth: bool` was fine for GL (you either
clear or you don't). With Vulkan's render-pass-baked load ops, the
choice is baked at `VkRenderPass` create time, not per `vkCmdBeginRenderPass`.
The bool today is effectively ignored — the depth attachment always
clears via the render pass's `LoadOp.Clear`. Same will become true of
color: when offscreen render passes land, their LoadOp/StoreOp need
to be declared at surface-creation time, not at pass-record time.
**Direction:** move clear ops + load/store ops to `RenderSurfaceDescription`
(or to a new `PassDescriptor` baked at cook time). Per-frame `Pass()`
calls just pick a pre-baked pass shape.

### F-015 — Render-pass compatibility forces identical subpass dependencies

The Phase-2 overlay pass needed `LoadOp.Load` to draw on top of the
cube's clear pass. To reuse the existing per-swapchain-image framebuffer
across both render passes, the framebuffer must be created with a render
pass that's **compatible** with the one used at `vkCmdBeginRenderPass`
time. The Vulkan spec lists what compatibility allows to differ (load
ops, initial/final layouts, etc.) — but the validator treats subpass
dependencies as part of compatibility too. Each diff in a dependency
field (stageMask, accessMask, dependencyFlags) produces a validation
error. Practical consequence: the default pass and the overlay pass
have to share a single conservative `SubpassDependency` shape even
though their actual needs differ. The shape ends up being a superset:
all stages + all accesses that either pass might need.
**Direction:** the render-graph abstraction (likely outcome of the
reshape) should bake `(loadOps, dependencies)` together at pass
declaration. Multiple "passes" sharing the same swapchain framebuffer
all use one render-pass instance under the hood, and dependencies are
derived from the declared resource reads/writes — eliminating the
manual-unification-of-dependency-shapes burden entirely.

### F-014 — `dFdx`/`dFdy` cross product sign flips between GL and Vulkan

In OpenGL framebuffers Y is up; in Vulkan Y is down. Code that derives
a face normal from `cross(dFdx(worldPos), dFdy(worldPos))` produces an
OUTWARD normal in GL and an INWARD normal in Vulkan — same source, same
shader, silently flipped lighting. First cube draft used the GL order
and the top face ended up dark while the bottom face lit, which read
as "the light is coming from the wrong direction."
**Direction:** the shader library (`src/Blix.Shaders/`) should grow a
`blix_face_normal(worldPos)` helper that does the right thing for the
current backend. Could be backend-conditional via `#define`, or just
documented as "use `cross(dy, dx)` for Vulkan / `cross(dx, dy)` for GL"
in the shader-authoring guide. The cross order is one character but
the failure mode is subtle visual wrongness, so it belongs in a helper.

### F-013 — Friction is concentrated in `Material` / uniform plumbing, not in handles

Strong signal: handle-based resource APIs (`VertexBuffer`, `IndexBuffer`,
`Pipeline`, `ShaderProgram`) survive cleanly. Resource lifecycles
(`Create*` / `Destroy*`) survive cleanly. The shape of `RenderCommandList`
+ `Pass` + `DrawIndexedCommand` survives cleanly. What doesn't survive:
the *binding* layer — `ShaderUniform`, `ShaderTextureBinding`,
`Material.SetUniform(name, ...)`, `MaterialResolver.RegisterPipeline(name)`,
all the name-keyed reflection-driven stuff. That layer is the reshape
target. Below it (handles, lists, passes) is portable.

---

## Audit phase consolidation

To be written after the validation push wraps. Each F-xxx gets a final
disposition (kept / replaced / deferred), grouped into a small number
of redesign vectors with concrete API sketches.
