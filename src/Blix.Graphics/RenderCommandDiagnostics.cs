namespace Blix.Graphics;

/// <summary>
/// Record-time fingerprints for the command payloads that are <b>not</b> copied.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hazard, stated exactly.</b> A pass body RECORDS; the GPU work happens later, at Execute.
/// <c>PushConstants</c> is copied into the command at record time precisely because of that, and the
/// comment above <see cref="DrawIndexedCommand"/> has said for two arcs that the <c>Uniforms</c> and
/// <c>Textures</c> lists are NOT — "that one wants measuring before it is done".
/// </para>
/// <para>
/// The per-draw uniform arena did not settle it. That change gave each distinct block its own slice
/// in a per-frame ring, bound with a dynamic offset — which fixes the <i>destination</i>. This is
/// about the <i>source</i>: <c>WriteUniformsAcrossSets</c> runs at Execute, walking
/// <c>pass.Commands</c>, so it reads the caller's arrays and lists long after the draw was recorded.
/// A caller that fills one scratch <c>Matrix4x4[]</c>, records a draw, refills it and records a
/// second still hands both draws the array's FINAL contents — into two separate slices, which is a
/// tidier way to be wrong.
/// </para>
/// <para>
/// <b>What is actually at risk, which is narrower than the comment implied.</b> Every scalar and
/// vector uniform value is a record holding a <i>struct</i> by value — a <see cref="Matrix4x4Uniform"/>
/// cannot be changed by whoever built it. Only two things can move under a recorded command: the
/// LIST (if it is a reused <c>List&lt;T&gt;</c> rather than a fresh array), and the arrays inside
/// <see cref="Matrix4x4ArrayUniform"/>, <see cref="Vector3ArrayUniform"/> and
/// <see cref="FloatArrayUniform"/>. The fingerprint covers both, because a mutated list swaps whole
/// uniforms and a mutated array changes one in place.
/// </para>
/// <para>
/// <b>Make it visible before paying for it.</b> Freezing on record is the obvious fix and it is not
/// obviously cheap — hence this: a fingerprint taken at record and re-checked at execute, which
/// turns a hazard nothing has ever been able to see into a throw that names the uniform and the
/// pass. The same walk counts what a freeze WOULD cost, so the measurement the comment asked for
/// falls out of the check rather than needing its own instrument.
/// </para>
/// <para>
/// Gated on <c>BLIX_VK_VALIDATE</c>, which this type now owns — <c>VulkanGraphicsDevice</c> reads it
/// from here rather than parsing the variable a second time. Same reasoning the uniform-conflict
/// detector records: the check is ours and needs no layer, and a machine without the layers
/// installed is exactly where a silent aliasing bug would live longest.
/// </para>
/// </remarks>
public static class RenderCommandDiagnostics
{
    /// <summary>Whether commands fingerprint their un-copied payloads. <c>BLIX_VK_VALIDATE=1</c>.</summary>
    /// <remarks>
    /// Read once. A process cannot half-enable this: a command fingerprinted at record must be
    /// verifiable at execute, and a switch that could flip between the two would produce a
    /// mismatch report about itself.
    /// </remarks>
    public static bool Enabled { get; } = ReadSwitch();

    // What a record-time freeze would have to copy, accumulated over the process. Two numbers
    // rather than one, because the two halves have completely different costs and only one of them
    // has a consumer today:
    //   Entries      — list slots. A freeze copies n references per command, whatever they hold.
    //   ArrayBytes   — the payload inside array uniforms. Zero unless something uses the
    //                  uniform-array path at all, which in this tree is Sponza's cascades.
    //
    // MEASURED (120 frames each, BLIX_VK_VALIDATE=1): the toolchain lab 2,880 uniform-bearing
    // commands / 10,560 uniform entries / 5,032 texture entries / 0 B of array payload; TankArena,
    // Bulwark, VulkanParticles and the external RTSGame consumer each ZERO uniform-bearing commands — every one of them
    // passes its per-draw values as push constants and materials. So the "copying them per draw
    // costs far more than 128 bytes" worry was about a shape no application in this tree has, and
    // the array payload it named is 0 B/frame everywhere that can be run here. That is what made
    // freezing the VALUES obviously right and left the LIST waiting for a draw-heavy consumer.
    private static long commandCount;
    private static long uniformEntryCount;
    private static long textureEntryCount;
    private static long arrayPayloadBytes;

    /// <summary>Fingerprints one uniform per entry, or null when the check is off or the list is empty.</summary>
    public static ulong[]? Fingerprint(IReadOnlyList<ShaderUniform>? uniforms)
    {
        if (!Enabled || uniforms is null || uniforms.Count == 0) return null;

        var prints = new ulong[uniforms.Count];
        for (var i = 0; i < uniforms.Count; i++) prints[i] = HashUniform(uniforms[i]);

        Interlocked.Increment(ref commandCount);
        Interlocked.Add(ref uniformEntryCount, uniforms.Count);
        return prints;
    }

    /// <summary>Fingerprints one texture binding per entry, or null when the check is off or the list is empty.</summary>
    public static ulong[]? Fingerprint(IReadOnlyList<ShaderTextureBinding>? textures)
    {
        if (!Enabled || textures is null || textures.Count == 0) return null;

        var prints = new ulong[textures.Count];
        for (var i = 0; i < textures.Count; i++)
        {
            var t = textures[i];
            var h = HashText(FnvOffset, t.Name);
            h = Mix(h, (uint)t.Texture.Id);
            h = Mix(h, (uint)t.Slot);
            prints[i] = Mix(h, (uint)t.ArrayIndex);
        }

        Interlocked.Add(ref textureEntryCount, textures.Count);
        return prints;
    }

    /// <summary>
    /// Throws when a recorded payload changed between record and execute.
    /// </summary>
    /// <remarks>
    /// Called from the backend with the command's own fingerprint. A null fingerprint means the
    /// check is off or there was nothing to print, and is not a fault.
    /// </remarks>
    public static void VerifyUniforms(
        string commandKind,
        string passName,
        IReadOnlyList<ShaderUniform>? uniforms,
        ulong[]? recorded)
    {
        if (recorded is null || uniforms is null) return;

        if (uniforms.Count != recorded.Length)
        {
            throw new InvalidOperationException(
                $"{commandKind} in pass '{passName}' was recorded with {recorded.Length} uniform(s) and is " +
                $"executing with {uniforms.Count}. The LIST handed to the command was mutated after it was " +
                $"recorded — a pass body records, and the backend reads these values at Execute. Hand each " +
                $"draw its own list, or reuse one only if nothing between the two draws touches it.");
        }

        for (var i = 0; i < uniforms.Count; i++)
        {
            if (HashUniform(uniforms[i]) == recorded[i]) continue;

            throw new InvalidOperationException(
                $"Uniform '{uniforms[i].Name}' on a {commandKind} in pass '{passName}' changed between " +
                $"RECORD and EXECUTE. A pass body records; the GPU work happens later, so the backend reads " +
                $"this value long after the draw was recorded — a caller that refills one scratch array " +
                $"between draws gives every draw the array's FINAL contents, and the per-draw uniform arena " +
                $"does not help because the aliasing is on the source rather than the destination.\n" +
                $"Scalar and vector uniforms hold their value by struct copy and cannot do this; the ones " +
                $"that can are the array uniforms and a reused list. Give this draw its own array, or copy " +
                $"into a fresh one per draw. See RenderCommand.cs and plan-blix-character.md (prologue).");
        }
    }

    /// <inheritdoc cref="VerifyUniforms"/>
    public static void VerifyTextures(
        string commandKind,
        string passName,
        IReadOnlyList<ShaderTextureBinding>? textures,
        ulong[]? recorded)
    {
        if (recorded is null || textures is null) return;

        if (textures.Count != recorded.Length)
        {
            throw new InvalidOperationException(
                $"{commandKind} in pass '{passName}' was recorded with {recorded.Length} texture binding(s) " +
                $"and is executing with {textures.Count}. The list was mutated after the command was " +
                $"recorded; hand each draw its own.");
        }

        for (var i = 0; i < textures.Count; i++)
        {
            var t = textures[i];
            var h = HashText(FnvOffset, t.Name);
            h = Mix(h, (uint)t.Texture.Id);
            h = Mix(h, (uint)t.Slot);
            if (Mix(h, (uint)t.ArrayIndex) == recorded[i]) continue;

            throw new InvalidOperationException(
                $"Texture binding '{t.Name}' on a {commandKind} in pass '{passName}' changed between RECORD " +
                $"and EXECUTE — the binding list was reused and rewritten between two draws. Each draw's " +
                $"bindings are read at Execute, so both draws would sample whatever the last one left.");
        }
    }

    /// <summary>What a record-time freeze of these payloads would have cost, so far.</summary>
    /// <remarks>
    /// The measurement <c>RenderCommand.cs</c> asked for, taken by the check rather than by a second
    /// instrument. Printed by the device at teardown when the switch is on.
    /// </remarks>
    public static string Report() =>
        $"record-time freeze cost: {Volatile.Read(ref commandCount):n0} command(s) carrying uniforms, " +
        $"{Volatile.Read(ref uniformEntryCount):n0} uniform entries, " +
        $"{Volatile.Read(ref textureEntryCount):n0} texture entries, " +
        $"{Volatile.Read(ref arrayPayloadBytes):n0} B of array payload";

    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    // FNV-1a over 32-bit words rather than bytes. Not a cryptographic hash and not trying to be:
    // this detects CHANGE in a payload the process itself wrote moments ago, where the adversary is
    // a reused scratch array rather than a person.
    private static ulong Mix(ulong hash, uint word) => (hash ^ word) * FnvPrime;

    private static ulong MixFloat(ulong hash, float value) =>
        Mix(hash, BitConverter.SingleToUInt32Bits(value));

    private static ulong HashText(ulong hash, string text)
    {
        foreach (var c in text) hash = Mix(hash, c);
        return hash;
    }

    private static ulong HashUniform(ShaderUniform u) => HashValue(HashText(FnvOffset, u.Name), u.Value);

    private static ulong HashValue(ulong h, ShaderUniformValue value)
    {
        switch (value)
        {
            case Matrix4x4Uniform m:
                return HashMatrix(h, m.Value);

            case Vector4Uniform v:
                return MixFloat(MixFloat(MixFloat(MixFloat(h, v.Value.X), v.Value.Y), v.Value.Z), v.Value.W);

            case Vector3Uniform v:
                return MixFloat(MixFloat(MixFloat(h, v.Value.X), v.Value.Y), v.Value.Z);

            case Vector2Uniform v:
                return MixFloat(MixFloat(h, v.Value.X), v.Value.Y);

            case FloatUniform f:
                return MixFloat(h, f.Value);

            // The three that can actually move under a recorded command.
            case Matrix4x4ArrayUniform ma:
                Interlocked.Add(ref arrayPayloadBytes, ma.Value.Length * 64);
                h = Mix(h, (uint)ma.Value.Length);
                foreach (var m in ma.Value) h = HashMatrix(h, m);
                return h;

            case Vector3ArrayUniform va:
                Interlocked.Add(ref arrayPayloadBytes, va.Value.Length * 12);
                h = Mix(h, (uint)va.Value.Length);
                foreach (var v in va.Value) h = MixFloat(MixFloat(MixFloat(h, v.X), v.Y), v.Z);
                return h;

            case FloatArrayUniform fa:
                Interlocked.Add(ref arrayPayloadBytes, fa.Value.Length * 4);
                h = Mix(h, (uint)fa.Value.Length);
                foreach (var f in fa.Value) h = MixFloat(h, f);
                return h;

            // <b>A switch that can no longer skip.</b> Same medicine the debug primitives got: five of
            // them were silently skipped for their whole lives because an unhandled case did nothing.
            // A uniform type this does not know would be a payload the check silently stopped covering,
            // which is worse than not having the check — it would report clean about a value it never read.
            default:
                throw new NotImplementedException(
                    $"RenderCommandDiagnostics has no fingerprint for {value.GetType().Name}. Add one beside " +
                    $"the backend's WriteUniformValue case for it; a uniform the check cannot read is a " +
                    $"uniform it would quietly stop checking.");
        }
    }

    private static ulong HashMatrix(ulong h, System.Numerics.Matrix4x4 m)
    {
        h = MixFloat(MixFloat(MixFloat(MixFloat(h, m.M11), m.M12), m.M13), m.M14);
        h = MixFloat(MixFloat(MixFloat(MixFloat(h, m.M21), m.M22), m.M23), m.M24);
        h = MixFloat(MixFloat(MixFloat(MixFloat(h, m.M31), m.M32), m.M33), m.M34);
        return MixFloat(MixFloat(MixFloat(MixFloat(h, m.M41), m.M42), m.M43), m.M44);
    }

    private static bool ReadSwitch()
    {
        var env = Environment.GetEnvironmentVariable("BLIX_VK_VALIDATE");
        return env == "1" || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase);
    }
}
