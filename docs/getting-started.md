# Getting started

This is the shortest complete path through a Blix checkout. It begins after
prerequisite downloads and is intended to take about ten minutes: establish a
green tree, see the smallest application host, see a composed renderer, and
make one visible code change.

Blix's supported development path today is macOS, .NET 8, and the Vulkan +
Silk.NET runtime. Other platforms are not part of the current verification
contract.

## 1. Install the prerequisites

Install the .NET 8 SDK and confirm that `dotnet` is available. Then install the
native dependencies with Homebrew:

```sh
brew install molten-vk vulkan-loader vulkan-headers vulkan-tools vulkan-validationlayers shaderc spirv-cross openal-soft
```

## 2. Get the source

```sh
git clone https://github.com/KshitijVikramSingh/blix.git
cd blix
```

Blix is consumed as source. There is no NuGet or binary distribution to install
for this release.

## 3. Bootstrap and build

```sh
./blix ls
dotnet build Blix.sln
./blix ls
```

The first `./blix ls` builds the small resolver and app indexer when they are
absent. It does not build every application. The solution build creates an
index beside each built assembly; the second list therefore shows the complete
set of apps currently visible from the repository root.

## 4. Prove the checkout

```sh
./blix test
```

The root project marker declares seven headless suites covering graphics,
diagnostics, 2D and 3D physics, app discovery, the Studio reference pipeline,
and asset recipes. The command exits non-zero if any declared suite fails.

This is the repository gate. Individual demos are executable specifications,
but launching a window is not a substitute for running it.

## 5. Run the smallest application

```sh
./blix run Blix.Demos.Chassis
```

Chassis opens a window with a colour that moves over time and an ImGui panel.
It owns its loop, window options, render pass, UI, and input handling explicitly;
Blix supplies the host and graphics mechanisms underneath. It has no shaders of
its own and is the smallest honest example of the application boundary.

Close the window when you have seen the panel update.

## 6. Run a composed renderer

```sh
./blix run Blix.Demos.VulkanLit
```

Vulkan Lit uses the same application-facing pieces to compose a materially
richer scene: meshes, materials, lighting, shadows, skinning, and the render
graph remain visible in ordinary C# rather than disappearing behind an engine
scene or editor. Close the window when you are done.

## 7. Make a visible change

Open `src/Demos/Blix.Demos.Chassis/Program.cs` and find the clear colour in
`ChassisLoop.OnRender`:

```csharp
new(0.05f + tint, 0.06f + tint * 0.5f, 0.09f, 1f)
```

Change it to a cooler blue pulse:

```csharp
new(0.03f, 0.08f + tint, 0.16f + tint, 1f)
```

Build only that application and run it again:

```sh
dotnet build src/Demos/Blix.Demos.Chassis/Blix.Demos.Chassis.csproj
./blix run Blix.Demos.Chassis
```

The changed background is the complete edit loop: application code records a
render pass, the runtime executes it, and the application retains ownership of
the visual choice. Restore the original expression if you do not want to keep
the experiment.

## Where to go next

- Read [Workflow](workflow.md) to declare an app, understand discovery and
  bounded runs, or consume Blix from another repository.
- Read [Game-facing API](blix.md) and continue from Chassis when composing a
  game-owned loop.
- Read [Assets](assets.md) and use `inspect`, `check`, `cook`, or `view` when
  working on the source-to-runtime asset path.
- Read [Renderer](renderer.md) and continue from Vulkan Lit for graphics work.
- Read [Demos](demos.md) to choose a focused executable specification or a
  larger proving ground.

The repository documents the current tree rather than a promised universal
workflow. In particular, Blix does not prescribe an ECS, scene format, editor,
scripting layer, fixed renderer, or engine-owned game loop.
