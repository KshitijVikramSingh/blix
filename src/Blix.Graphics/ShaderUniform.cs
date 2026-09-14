using System.Numerics;

namespace Blix.Graphics;

public sealed record ShaderUniform(string Name, ShaderUniformValue Value);

public abstract record ShaderUniformValue;

public sealed record Matrix4x4Uniform(Matrix4x4 Value) : ShaderUniformValue;

// Uniform array of column-vector matrices. The canonical use is skinning's bone
// palette: the vertex shader declares `uniform mat4 uName[N]`, and a single upload
// fills the whole array. Callers are responsible for sizing Value to the shader's
// declared array length (or smaller — trailing slots the upload doesn't cover keep
// whatever the backing buffer last held).
//
// <b>The array is COPIED in, so this is a value like every other uniform.</b> A pass body
// records and the backend reads uniforms at Execute, so an array held by reference could be
// refilled by its owner between two draws and hand both the final contents — the same
// deferred-recording aliasing that put seven boxes in one place and that PushConstants is
// copied to prevent. Every other ShaderUniformValue holds a struct and was never exposed to
// this; the three array variants were the only ones that could move, which is why they are
// the ones that copy. Measured before doing it (see RenderCommandDiagnostics): across the lab,
// TankArena, Bulwark, VulkanParticles and RTSGame this path carries ZERO bytes per frame —
// only Sponza's cascade view-projections use it at all, at 4 matrices a pass.
public sealed record Matrix4x4ArrayUniform : ShaderUniformValue
{
    private readonly Matrix4x4[] matrices;

    public Matrix4x4ArrayUniform(Matrix4x4[] Value)
    {
        ArgumentNullException.ThrowIfNull(Value);
        matrices = Value.AsSpan().ToArray();
    }

    public Matrix4x4[] Value
    {
        get => matrices;
        init => matrices = (value ?? throw new ArgumentNullException(nameof(value))).AsSpan().ToArray();
    }
}

public sealed record Vector4Uniform(Vector4 Value) : ShaderUniformValue;

public sealed record Vector3Uniform(Vector3 Value) : ShaderUniformValue;

// Uniform array of vec3s. Shader declares `uniform vec3 uName[N]`; a single upload
// fills the whole array. Caller sizes Value to the shader's array length or smaller
// (untouched tail elements stay at GL default initialisation).
//
// Copied in, for the reason Matrix4x4ArrayUniform gives.
public sealed record Vector3ArrayUniform : ShaderUniformValue
{
    private readonly Vector3[] values;

    public Vector3ArrayUniform(Vector3[] Value)
    {
        ArgumentNullException.ThrowIfNull(Value);
        values = Value.AsSpan().ToArray();
    }

    public Vector3[] Value
    {
        get => values;
        init => values = (value ?? throw new ArgumentNullException(nameof(value))).AsSpan().ToArray();
    }
}

public sealed record Vector2Uniform(Vector2 Value) : ShaderUniformValue;

public sealed record FloatUniform(float Value) : ShaderUniformValue;

// Uniform array of floats. Same contract as Vector3ArrayUniform, copy included.
public sealed record FloatArrayUniform : ShaderUniformValue
{
    private readonly float[] values;

    public FloatArrayUniform(float[] Value)
    {
        ArgumentNullException.ThrowIfNull(Value);
        values = Value.AsSpan().ToArray();
    }

    public float[] Value
    {
        get => values;
        init => values = (value ?? throw new ArgumentNullException(nameof(value))).AsSpan().ToArray();
    }
}
