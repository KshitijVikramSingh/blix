using System.Numerics;

namespace Blix.Graphics;

public sealed record ShaderUniform(string Name, ShaderUniformValue Value);

public abstract record ShaderUniformValue;

public sealed record Matrix4x4Uniform(Matrix4x4 Value) : ShaderUniformValue;

// Uniform array of column-vector matrices. The canonical use is skinning's bone
// palette: the vertex shader declares `uniform mat4 uName[N]`, and a single upload
// fills the whole array. Callers are responsible for sizing Value to the shader's
// declared array length (or smaller — OpenGL leaves untouched slots at their default
// initialisation).
public sealed record Matrix4x4ArrayUniform(Matrix4x4[] Value) : ShaderUniformValue;

public sealed record Vector4Uniform(Vector4 Value) : ShaderUniformValue;

public sealed record Vector3Uniform(Vector3 Value) : ShaderUniformValue;

public sealed record Vector2Uniform(Vector2 Value) : ShaderUniformValue;

public sealed record FloatUniform(float Value) : ShaderUniformValue;
