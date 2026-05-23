using Blix.Graphics;
using OpenTK.Graphics.OpenGL4;

namespace Blix.Graphics.OpenGL;

public sealed partial class OpenGLGraphicsDevice
{
    public VertexBufferHandle CreateVertexBuffer(VertexBufferData data, string? name = null)
    {
        ThrowIfDisposed();

        if (data.Description.VertexCount <= 0)
        {
            throw new ArgumentException("A vertex buffer must contain at least one vertex.", nameof(data));
        }

        if (data.Bytes.Length != data.Description.VertexCount * data.Description.Layout.Stride)
        {
            throw new ArgumentException("Vertex buffer byte count must match vertex count multiplied by layout stride.", nameof(data));
        }

        var vertexBuffer = GL.GenBuffer();

        GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Bytes.Length, data.Bytes, MapBufferUsage(data.Description.Usage));
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

        var handleId = NextHandle();
        var resolvedName = name ?? $"vertexBuffer#{handleId}";
        ApplyDebugLabel(ObjectLabelIdentifier.Buffer, vertexBuffer, resolvedName);
        vertexBuffers.Add(handleId, new VertexBufferResource(
            vertexBuffer,
            data.Description.VertexCount,
            data.Description.Layout.Stride,
            resolvedName));
        return new VertexBufferHandle(handleId);
    }

    public void UpdateVertexBuffer(VertexBufferHandle handle, ReadOnlySpan<byte> bytes, int byteOffset = 0)
    {
        ThrowIfDisposed();

        if (!vertexBuffers.TryGetValue(handle.Id, out var resource))
        {
            throw new InvalidOperationException($"Unknown vertex buffer handle: {handle.Id}");
        }

        if (byteOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteOffset), "Byte offset must not be negative.");
        }

        if (bytes.IsEmpty)
        {
            return;
        }

        GL.BindBuffer(BufferTarget.ArrayBuffer, resource.Buffer);
        GL.BufferSubData(
            BufferTarget.ArrayBuffer,
            new IntPtr(byteOffset),
            bytes.Length,
            ref System.Runtime.InteropServices.MemoryMarshal.GetReference(bytes));
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

    public void DestroyVertexBuffer(VertexBufferHandle handle)
    {
        ThrowIfDisposed();

        if (!vertexBuffers.Remove(handle.Id, out var resource))
        {
            return;
        }

        GL.DeleteBuffer(resource.Buffer);
    }

    public IndexBufferHandle CreateIndexBuffer(
        IReadOnlyList<ushort> indices,
        GraphicsBufferUsage usage = GraphicsBufferUsage.Static,
        string? name = null)
    {
        ThrowIfDisposed();

        if (indices.Count == 0)
        {
            throw new ArgumentException("An index buffer must contain at least one index.", nameof(indices));
        }

        var indexBuffer = GL.GenBuffer();
        var packedIndices = indices.ToArray();

        GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer);
        GL.BufferData(BufferTarget.ElementArrayBuffer, packedIndices.Length * sizeof(ushort), packedIndices, MapBufferUsage(usage));
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);

        var handleId = NextHandle();
        var resolvedName = name ?? $"indexBuffer#{handleId}";
        ApplyDebugLabel(ObjectLabelIdentifier.Buffer, indexBuffer, resolvedName);
        indexBuffers.Add(handleId, new IndexBufferResource(indexBuffer, indices.Count, IndexFormat.UInt16, resolvedName));
        return new IndexBufferHandle(handleId);
    }

    public IndexBufferHandle CreateIndexBuffer(
        IReadOnlyList<uint> indices,
        GraphicsBufferUsage usage = GraphicsBufferUsage.Static,
        string? name = null)
    {
        ThrowIfDisposed();

        if (indices.Count == 0)
        {
            throw new ArgumentException("An index buffer must contain at least one index.", nameof(indices));
        }

        var indexBuffer = GL.GenBuffer();
        var packedIndices = indices.ToArray();

        GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer);
        GL.BufferData(BufferTarget.ElementArrayBuffer, packedIndices.Length * sizeof(uint), packedIndices, MapBufferUsage(usage));
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);

        var handleId = NextHandle();
        var resolvedName = name ?? $"indexBuffer#{handleId}";
        ApplyDebugLabel(ObjectLabelIdentifier.Buffer, indexBuffer, resolvedName);
        indexBuffers.Add(handleId, new IndexBufferResource(indexBuffer, indices.Count, IndexFormat.UInt32, resolvedName));
        return new IndexBufferHandle(handleId);
    }

    public void DestroyIndexBuffer(IndexBufferHandle handle)
    {
        ThrowIfDisposed();

        if (!indexBuffers.Remove(handle.Id, out var resource))
        {
            return;
        }

        GL.DeleteBuffer(resource.Buffer);
    }

    public ShaderProgramHandle CreateShaderProgram(ShaderSources sources)
    {
        return CreateShaderProgram(ShaderProgramDescription.FromVertexFragment(
            sources.VertexShader,
            sources.FragmentShader,
            sources.VertexName,
            sources.FragmentName,
            sources.VertexSourceMap,
            sources.FragmentSourceMap));
    }

    public ShaderProgramHandle CreateShaderProgram(ShaderProgramDescription description)
    {
        ThrowIfDisposed();

        var programId = CreateShaderProgramFromDescription(description);
        var handleId = NextHandle();
        var resolvedName = DeriveShaderProgramName(description, handleId);
        ApplyDebugLabel(ObjectLabelIdentifier.Program, programId, resolvedName);
        shaderPrograms.Add(handleId, new ShaderProgramResource(programId, resolvedName));
        return new ShaderProgramHandle(handleId);
    }

    public void DestroyShaderProgram(ShaderProgramHandle handle)
    {
        ThrowIfDisposed();

        if (!shaderPrograms.Remove(handle.Id, out var program))
        {
            return;
        }

        GL.DeleteProgram(program.ProgramId);
    }

    public PipelineHandle CreatePipeline(PipelineDescription description, string? name = null)
    {
        ThrowIfDisposed();

        if (!shaderPrograms.ContainsKey(description.ShaderProgram.Id))
        {
            throw new InvalidOperationException($"Unknown shader program handle: {description.ShaderProgram.Id}");
        }

        var handleId = NextHandle();
        var resolvedName = name ?? $"pipeline#{handleId}";
        pipelines.Add(handleId, new PipelineResource(
            description.ShaderProgram,
            description.VertexLayout,
            description.Topology,
            description.Depth,
            description.Rasterizer,
            description.ColorBlends,
            resolvedName));
        return new PipelineHandle(handleId);
    }

    public void DestroyPipeline(PipelineHandle handle)
    {
        ThrowIfDisposed();
        pipelines.Remove(handle.Id);
    }

    public TextureHandle CreateTexture2D(TextureDescription description, ReadOnlySpan<byte> pixels, string? name = null)
    {
        // Route through the multi-mip path with a single-entry list. The
        // shared implementation handles the format validation + GL call
        // selection (TexImage2D vs CompressedTexImage2D).
        return CreateTexture2DMipped(description, new[] { pixels.ToArray() }, name);
    }

    public TextureHandle CreateTexture2DMipped(
        TextureDescription description,
        IReadOnlyList<byte[]> mipBytes,
        string? name = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(mipBytes);

        if (description.Format == TextureFormat.Depth24)
        {
            throw new ArgumentException(
                "Depth textures must be created through CreateRenderSurface with a DepthTexture attachment.",
                nameof(description));
        }
        if (description.Width <= 0 || description.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(description),
                "Texture width and height must be greater than zero.");
        }
        if (mipBytes.Count == 0)
        {
            throw new ArgumentException("At least one mip level must be supplied.", nameof(mipBytes));
        }

        var isCompressed = description.Format.IsCompressed();
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);

        var internalFormat = MapPixelInternalFormat(description.Format);
        for (var mip = 0; mip < mipBytes.Count; mip++)
        {
            var mipWidth = Math.Max(1, description.Width >> mip);
            var mipHeight = Math.Max(1, description.Height >> mip);
            var expected = description.Format.MipByteCount(mipWidth, mipHeight);
            var bytes = mipBytes[mip];
            if (bytes.Length != expected)
            {
                throw new ArgumentException(
                    $"Mip {mip} of texture '{name ?? "<unnamed>"}' has {bytes.Length} bytes; expected {expected} for {description.Format} at {mipWidth}x{mipHeight}.",
                    nameof(mipBytes));
            }
            if (isCompressed)
            {
                GL.CompressedTexImage2D(
                    TextureTarget.Texture2D, mip,
                    (InternalFormat)internalFormat,
                    mipWidth, mipHeight, border: 0,
                    bytes.Length, bytes);
            }
            else
            {
                GL.TexImage2D(
                    TextureTarget.Texture2D, mip,
                    internalFormat, mipWidth, mipHeight, border: 0,
                    MapPixelFormat(description.Format), MapPixelType(description.Format),
                    ref bytes[0]);
            }
        }

        // GL needs to know which mip levels are valid -- otherwise the
        // sampler may try to read beyond the levels we uploaded and
        // produce black or driver-defined garbage.
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, mipBytes.Count - 1);

        // Apply sampler params WITHOUT mipgen -- ApplySampler's built-in
        // glGenerateMipmap would regenerate the chain from level 0 and
        // clobber any extra mips the caller supplied. Mipgen needs to be
        // a deliberate decision below, not a side effect of sampler setup.
        ApplySamplerNoMipGen(description.Sampler);

        // Run-time mip generation only kicks in for UNCOMPRESSED textures
        // with sampler.GenerateMipmaps when caller didn't supply the full
        // chain. Compressed formats can't glGenerateMipmap; cook-time
        // pre-baked mips are mandatory there.
        if (description.Sampler.GenerateMipmaps && !isCompressed && mipBytes.Count == 1)
        {
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        }

        GL.BindTexture(TextureTarget.Texture2D, 0);

        var handleId = NextHandle();
        var resolvedName = name ?? $"texture#{handleId}";
        ApplyDebugLabel(ObjectLabelIdentifier.Texture, texture, resolvedName);
        textures.Add(handleId, new TextureResource(
            texture,
            description.Width,
            description.Height,
            description.Format,
            resolvedName,
            TextureKind.UserUploaded));
        return new TextureHandle(handleId);
    }

    public TextureHandle AllocateTexture2DMips(
        TextureDescription description,
        int mipCount,
        string? name = null)
    {
        ThrowIfDisposed();
        if (description.Width <= 0 || description.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(description),
                "Texture width and height must be greater than zero.");
        }
        if (mipCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(mipCount));
        }
        if (description.Format == TextureFormat.Depth24)
        {
            throw new ArgumentException(
                "Depth textures must be created through CreateRenderSurface.", nameof(description));
        }

        var isCompressed = description.Format.IsCompressed();
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        var internalFormat = MapPixelInternalFormat(description.Format);

        // Allocate storage for every mip level with no real data uploaded.
        // glTexImage2D with IntPtr.Zero for uncompressed leaves contents
        // undefined; for compressed we pass a zero-filled buffer (driver
        // requires a buffer of exactly the expected block-count size).
        for (var mip = 0; mip < mipCount; mip++)
        {
            var mipWidth = Math.Max(1, description.Width >> mip);
            var mipHeight = Math.Max(1, description.Height >> mip);
            if (isCompressed)
            {
                var size = description.Format.MipByteCount(mipWidth, mipHeight);
                var stub = new byte[size];
                GL.CompressedTexImage2D(
                    TextureTarget.Texture2D, mip,
                    (InternalFormat)internalFormat,
                    mipWidth, mipHeight, border: 0,
                    size, stub);
            }
            else
            {
                GL.TexImage2D(
                    TextureTarget.Texture2D, mip,
                    internalFormat, mipWidth, mipHeight, border: 0,
                    MapPixelFormat(description.Format), MapPixelType(description.Format),
                    IntPtr.Zero);
            }
        }

        // Initially sample only from the smallest mip. As UploadTextureMip
        // adds finer levels, BASE_LEVEL gets walked down so the sampler
        // sees the highest-quality mip available.
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, mipCount - 1);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, mipCount - 1);
        // Apply sampler settings WITHOUT the GenerateMipmaps step -- we
        // already have the chain via the per-level UploadTextureMip flow,
        // and calling glGenerateMipmap here would regenerate from level 0
        // whose storage is still undefined, blasting garbage across the
        // chain.
        ApplySamplerNoMipGen(description.Sampler);
        GL.BindTexture(TextureTarget.Texture2D, 0);

        var handleId = NextHandle();
        var resolvedName = name ?? $"texture#{handleId}";
        ApplyDebugLabel(ObjectLabelIdentifier.Texture, texture, resolvedName);
        textures.Add(handleId, new TextureResource(
            texture, description.Width, description.Height,
            description.Format, resolvedName, TextureKind.UserUploaded));
        return new TextureHandle(handleId);
    }

    public void UploadTextureMip(TextureHandle handle, int mipLevel, ReadOnlySpan<byte> bytes)
    {
        ThrowIfDisposed();
        if (mipLevel < 0) throw new ArgumentOutOfRangeException(nameof(mipLevel));
        if (!textures.TryGetValue(handle.Id, out var res))
        {
            throw new ArgumentException($"Texture handle {handle.Id} not found.", nameof(handle));
        }
        var mipWidth = Math.Max(1, res.Width >> mipLevel);
        var mipHeight = Math.Max(1, res.Height >> mipLevel);
        var expected = res.Format.MipByteCount(mipWidth, mipHeight);
        if (bytes.Length != expected)
        {
            throw new ArgumentException(
                $"Mip {mipLevel} of '{res.Name}' expects {expected} bytes; got {bytes.Length}.",
                nameof(bytes));
        }
        var isCompressed = res.Format.IsCompressed();
        var internalFormat = MapPixelInternalFormat(res.Format);

        GL.BindTexture(res.Target, res.Texture);
        if (isCompressed)
        {
            GL.CompressedTexImage2D(
                res.Target, mipLevel, (InternalFormat)internalFormat,
                mipWidth, mipHeight, border: 0,
                bytes.Length,
                ref System.Runtime.InteropServices.MemoryMarshal.GetReference(bytes));
        }
        else
        {
            GL.TexImage2D(
                res.Target, mipLevel, internalFormat,
                mipWidth, mipHeight, border: 0,
                MapPixelFormat(res.Format), MapPixelType(res.Format),
                ref System.Runtime.InteropServices.MemoryMarshal.GetReference(bytes));
        }

        // Walk BASE_LEVEL down so the sampler sees the highest-quality mip
        // available so far. MAX_LEVEL stays at whatever was set originally
        // (typically mipCount-1) -- we only ever ADD mips, never invalidate.
        GL.GetTexParameter(res.Target, GetTextureParameter.TextureBaseLevel, out int currentBase);
        if (mipLevel < currentBase)
        {
            GL.TexParameter(res.Target, TextureParameterName.TextureBaseLevel, mipLevel);
        }
        GL.BindTexture(res.Target, 0);
    }

    public TextureHandle CreateTexture3D(
        int width, int height, int depth,
        TextureFormat format,
        SamplerDescription sampler,
        ReadOnlySpan<byte> pixels,
        string? name = null)
    {
        ThrowIfDisposed();

        if (format == TextureFormat.Depth24)
        {
            throw new ArgumentException(
                "Depth textures must be created through CreateRenderSurface, not as 3D textures.",
                nameof(format));
        }

        if (width <= 0 || height <= 0 || depth <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                $"Texture3D dimensions must be > 0; got {width}x{height}x{depth}.");
        }

        var expectedByteCount = width * height * depth * GetBytesPerPixel(format);
        if (pixels.Length != expectedByteCount)
        {
            throw new ArgumentException(
                $"Texture3D pixel data length must be {expectedByteCount} bytes (got {pixels.Length}).",
                nameof(pixels));
        }

        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, texture);

        // R8 single-channel uploads default to row alignment 4 in OpenGL; for
        // odd-width textures (e.g., 33-wide voxel slices) this would mis-read
        // the source buffer. Force tight packing for the duration of the
        // upload, then restore.
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage3D(
            TextureTarget.Texture3D,
            level: 0,
            MapPixelInternalFormat(format),
            width,
            height,
            depth,
            border: 0,
            MapPixelFormat(format),
            MapPixelType(format),
            ref System.Runtime.InteropServices.MemoryMarshal.GetReference(pixels));
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);

        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)MapMinFilter(sampler.MinFilter, sampler.GenerateMipmaps));
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)MapMagFilter(sampler.MagFilter));
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)MapTextureWrap(sampler.WrapU));
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)MapTextureWrap(sampler.WrapV));
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)MapTextureWrap(sampler.WrapW));
        if (sampler.GenerateMipmaps)
        {
            GL.GenerateMipmap(GenerateMipmapTarget.Texture3D);
        }
        GL.BindTexture(TextureTarget.Texture3D, 0);

        var handleId = NextHandle();
        var resolvedName = name ?? $"texture3d#{handleId}";
        ApplyDebugLabel(ObjectLabelIdentifier.Texture, texture, resolvedName);
        textures.Add(handleId, new TextureResource(
            texture,
            width,
            height,
            format,
            resolvedName,
            TextureKind.UserUploaded,
            TextureTarget.Texture3D));
        return new TextureHandle(handleId);
    }

    public TextureHandle CreateTextureCube(int faceSize, ReadOnlySpan<byte> faces, SamplerDescription sampler, string? name = null)
    {
        ThrowIfDisposed();

        if (faceSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(faceSize), "Face size must be greater than zero.");
        }

        var bytesPerFace = faceSize * faceSize * 4;
        if (faces.Length != bytesPerFace * 6)
        {
            throw new ArgumentException(
                $"Cubemap face data must be exactly {bytesPerFace * 6} bytes (6 faces of {faceSize}x{faceSize} RGBA8).",
                nameof(faces));
        }

        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.TextureCubeMap, texture);

        // Upload all six faces. GL face enum is contiguous starting at POSITIVE_X, so
        // we can iterate by integer offset. Caller-provided layout: [+X, -X, +Y, -Y, +Z, -Z].
        for (var i = 0; i < 6; i++)
        {
            var faceTarget = TextureTarget.TextureCubeMapPositiveX + i;
            var faceSpan = faces.Slice(i * bytesPerFace, bytesPerFace);
            GL.TexImage2D(
                faceTarget,
                level: 0,
                PixelInternalFormat.Rgba8,
                faceSize,
                faceSize,
                border: 0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                ref System.Runtime.InteropServices.MemoryMarshal.GetReference(faceSpan));
        }

        // Cubemap sampler params. WRAP_R covers the third axis that 2D doesn't have;
        // clamp-to-edge is essentially mandatory or seams between faces appear.
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)MapMinFilter(sampler.MinFilter, sampler.GenerateMipmaps));
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)MapMagFilter(sampler.MagFilter));
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);

        if (sampler.GenerateMipmaps)
        {
            GL.GenerateMipmap(GenerateMipmapTarget.TextureCubeMap);
        }

        GL.BindTexture(TextureTarget.TextureCubeMap, 0);

        var handleId = NextHandle();
        var resolvedName = name ?? $"textureCube#{handleId}";
        ApplyDebugLabel(ObjectLabelIdentifier.Texture, texture, resolvedName);
        textures.Add(handleId, new TextureResource(
            texture,
            faceSize,
            faceSize,
            TextureFormat.Rgba8,
            resolvedName,
            TextureKind.UserUploaded,
            TextureTarget.TextureCubeMap));
        return new TextureHandle(handleId);
    }

    public TextureHandle CreateTextureCubeHdr(int faceSize, ReadOnlySpan<Half> faces, SamplerDescription sampler, string? name = null)
    {
        ThrowIfDisposed();
        if (faceSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(faceSize), "Face size must be greater than zero.");
        }
        var halfsPerFace = faceSize * faceSize * 4;
        if (faces.Length != halfsPerFace * 6)
        {
            throw new ArgumentException(
                $"HDR cubemap face data must be exactly {halfsPerFace * 6} Halfs (6 faces of {faceSize}x{faceSize} RGBA16F).",
                nameof(faces));
        }

        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.TextureCubeMap, texture);

        for (var i = 0; i < 6; i++)
        {
            var faceTarget = TextureTarget.TextureCubeMapPositiveX + i;
            var faceSpan = faces.Slice(i * halfsPerFace, halfsPerFace);
            // Upload as half-float; internal format Rgba16F is the actual GPU storage.
            // MemoryMarshal recasts the Half span to raw bytes for the GL upload.
            var byteSpan = System.Runtime.InteropServices.MemoryMarshal.AsBytes(faceSpan);
            GL.TexImage2D(
                faceTarget,
                level: 0,
                PixelInternalFormat.Rgba16f,
                faceSize,
                faceSize,
                border: 0,
                PixelFormat.Rgba,
                PixelType.HalfFloat,
                ref System.Runtime.InteropServices.MemoryMarshal.GetReference(byteSpan));
        }

        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)MapMinFilter(sampler.MinFilter, sampler.GenerateMipmaps));
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)MapMagFilter(sampler.MagFilter));
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);

        if (sampler.GenerateMipmaps)
        {
            GL.GenerateMipmap(GenerateMipmapTarget.TextureCubeMap);
        }

        GL.BindTexture(TextureTarget.TextureCubeMap, 0);

        var handleId = NextHandle();
        var resolvedName = name ?? $"textureCubeHdr#{handleId}";
        ApplyDebugLabel(ObjectLabelIdentifier.Texture, texture, resolvedName);
        textures.Add(handleId, new TextureResource(
            texture,
            faceSize,
            faceSize,
            TextureFormat.Rgba16F,
            resolvedName,
            TextureKind.UserUploaded,
            TextureTarget.TextureCubeMap));
        return new TextureHandle(handleId);
    }

    public TextureHandle CreateTextureCubeHdrMipped(
        int baseFaceSize,
        IReadOnlyList<Half[]> mipFaces,
        SamplerDescription sampler,
        string? name = null)
    {
        ThrowIfDisposed();
        if (baseFaceSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseFaceSize), "Base face size must be > 0.");
        }
        if (mipFaces is null || mipFaces.Count == 0)
        {
            throw new ArgumentException("mipFaces must contain at least one mip level.", nameof(mipFaces));
        }

        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.TextureCubeMap, texture);

        for (var mip = 0; mip < mipFaces.Count; mip++)
        {
            int mipSize = Math.Max(1, baseFaceSize >> mip);
            int halfsPerFace = mipSize * mipSize * 4;
            var mipData = mipFaces[mip];
            if (mipData.Length != halfsPerFace * 6)
            {
                throw new ArgumentException(
                    $"Mip {mip} expected {halfsPerFace * 6} Halfs (6 faces of {mipSize}x{mipSize} RGBA16F); got {mipData.Length}.",
                    nameof(mipFaces));
            }
            for (var face = 0; face < 6; face++)
            {
                var faceTarget = TextureTarget.TextureCubeMapPositiveX + face;
                var faceSpan = new ReadOnlySpan<Half>(mipData, face * halfsPerFace, halfsPerFace);
                var byteSpan = System.Runtime.InteropServices.MemoryMarshal.AsBytes(faceSpan);
                GL.TexImage2D(
                    faceTarget,
                    level: mip,
                    PixelInternalFormat.Rgba16f,
                    mipSize, mipSize,
                    border: 0,
                    PixelFormat.Rgba,
                    PixelType.HalfFloat,
                    ref System.Runtime.InteropServices.MemoryMarshal.GetReference(byteSpan));
            }
        }

        // Filter: trilinear across mips so textureLod with a non-integer lod
        // interpolates between roughness levels smoothly.
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        // Clamp base/max level so the sampler doesn't try to read undefined
        // mips above what we uploaded.
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureBaseLevel, 0);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMaxLevel, mipFaces.Count - 1);
        GL.BindTexture(TextureTarget.TextureCubeMap, 0);

        var handleId = NextHandle();
        var resolvedName = name ?? $"textureCubeHdrMipped#{handleId}";
        ApplyDebugLabel(ObjectLabelIdentifier.Texture, texture, resolvedName);
        textures.Add(handleId, new TextureResource(
            texture,
            baseFaceSize,
            baseFaceSize,
            TextureFormat.Rgba16F,
            resolvedName,
            TextureKind.UserUploaded,
            TextureTarget.TextureCubeMap));
        return new TextureHandle(handleId);
    }

    public TextureHandle CreateTextureCubeDepth(int faceSize, SamplerDescription sampler, string? name = null)
    {
        ThrowIfDisposed();
        if (faceSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(faceSize), "Face size must be greater than zero.");
        }

        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.TextureCubeMap, texture);

        // Allocate empty depth storage for each face. PixelFormat.DepthComponent +
        // PixelType.Float describe the upload format; the actual GPU format is
        // DepthComponent24 (matches the static-light shadow map). null data pointer
        // is fine because we'll render into each face via per-face surfaces.
        for (var i = 0; i < 6; i++)
        {
            var faceTarget = TextureTarget.TextureCubeMapPositiveX + i;
            GL.TexImage2D(
                faceTarget,
                level: 0,
                PixelInternalFormat.DepthComponent24,
                faceSize,
                faceSize,
                border: 0,
                PixelFormat.DepthComponent,
                PixelType.Float,
                IntPtr.Zero);
        }

        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)MapMinFilter(sampler.MinFilter, sampler.GenerateMipmaps));
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)MapMagFilter(sampler.MagFilter));
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);

        // Compare mode + LEQUAL turns the cubemap into a samplerCubeShadow target.
        // Hardware does the depth compare on each sampled texel; PCF still works
        // via Linear min/mag (with bilinear-cross-face caveats around face seams).
        if (sampler.Compare)
        {
            GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
            GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);
        }

        GL.BindTexture(TextureTarget.TextureCubeMap, 0);

        var handleId = NextHandle();
        var resolvedName = name ?? $"textureCubeDepth#{handleId}";
        ApplyDebugLabel(ObjectLabelIdentifier.Texture, texture, resolvedName);
        textures.Add(handleId, new TextureResource(
            texture,
            faceSize,
            faceSize,
            TextureFormat.Depth24,
            resolvedName,
            TextureKind.RenderSurfaceDepth,
            TextureTarget.TextureCubeMap));
        return new TextureHandle(handleId);
    }

    public void DestroyTexture(TextureHandle handle)
    {
        ThrowIfDisposed();

        if (!textures.Remove(handle.Id, out var resource))
        {
            return;
        }

        GL.DeleteTexture(resource.Texture);
    }

    public RenderSurface CreateRenderSurface(RenderSurfaceDescription description)
    {
        ThrowIfDisposed();
        ValidateRenderSurfaceDescription(description);

        var (width, height) = ResolveRenderSurfaceSize(description.Size);

        var colorHandles = new TextureHandle[description.ColorAttachments.Count];

        for (var i = 0; i < description.ColorAttachments.Count; i++)
        {
            var attachment = description.ColorAttachments[i];
            var attachmentHandleId = NextHandle();
            var attachmentName = $"{description.Name}.color[{i}]";
            textures.Add(attachmentHandleId, CreateColorAttachmentTexture(
                attachment.Format, attachment.Sampler, width, height, attachmentName));
            colorHandles[i] = new TextureHandle(attachmentHandleId);
        }

        TextureHandle? depthHandle = null;

        if (description.Depth is DepthTexture depthTexture)
        {
            var depthHandleId = NextHandle();
            var depthName = $"{description.Name}.depth";
            textures.Add(depthHandleId, CreateDepthAttachmentTexture(
                depthTexture.Sampler, width, height, depthName));
            depthHandle = new TextureHandle(depthHandleId);
        }
        else if (description.Depth is DepthCubeFace cubeFaceDesc)
        {
            // The cubemap was created separately via CreateTextureCubeDepth; the
            // surface just references it. Expose it through the returned
            // RenderSurface.DepthTexture so callers reading
            // `surface.DepthTexture` for the cubemap binding get the right handle.
            depthHandle = cubeFaceDesc.CubeMap;
        }

        var surfaceHandle = new RenderSurfaceHandle(NextHandle());
        renderSurfaces.Add(surfaceHandle.Id, BuildRenderSurfaceResource(description, colorHandles, depthHandle, width, height));
        return new RenderSurface(surfaceHandle, colorHandles, depthHandle);
    }

    public void DestroyRenderSurface(RenderSurfaceHandle handle)
    {
        ThrowIfDisposed();

        if (handle == RenderSurfaceHandle.Default)
        {
            return;
        }

        if (!renderSurfaces.Remove(handle.Id, out var resource))
        {
            return;
        }

        DeleteRenderSurfaceResource(resource);
        DeleteAttachmentTextures(resource);
    }

    private RenderSurfaceResource EnsureRenderSurfaceResource(int surfaceId, RenderSurfaceResource resource)
    {
        var (width, height) = ResolveRenderSurfaceSize(resource.Description.Size);

        if (!resource.Dirty && resource.Width == width && resource.Height == height)
        {
            return resource;
        }

        DeleteRenderSurfaceResource(resource);

        for (var i = 0; i < resource.ColorTextures.Count; i++)
        {
            var attachment = resource.Description.ColorAttachments[i];
            var handle = resource.ColorTextures[i];

            if (textures.Remove(handle.Id, out var old))
            {
                GL.DeleteTexture(old.Texture);
            }

            var attachmentName = $"{resource.Description.Name}.color[{i}]";
            textures[handle.Id] = CreateColorAttachmentTexture(
                attachment.Format, attachment.Sampler, width, height, attachmentName);
        }

        if (resource.Depth is DepthTextureResource depthTextureResource &&
            resource.Description.Depth is DepthTexture depthTextureDescription)
        {
            if (textures.Remove(depthTextureResource.Handle.Id, out var oldDepth))
            {
                GL.DeleteTexture(oldDepth.Texture);
            }

            var depthName = $"{resource.Description.Name}.depth";
            textures[depthTextureResource.Handle.Id] = CreateDepthAttachmentTexture(
                depthTextureDescription.Sampler, width, height, depthName);
        }

        var rebuilt = BuildRenderSurfaceResource(
            resource.Description,
            resource.ColorTextures,
            (resource.Depth as DepthTextureResource)?.Handle,
            width,
            height);
        renderSurfaces[surfaceId] = rebuilt;
        return rebuilt;
    }

    private RenderSurfaceResource BuildRenderSurfaceResource(
        RenderSurfaceDescription description,
        IReadOnlyList<TextureHandle> colorTextures,
        TextureHandle? depthTextureHandle,
        int width,
        int height)
    {
        var framebuffer = GL.GenFramebuffer();
        DepthResource? depthResource = null;

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        ApplyDebugLabel(ObjectLabelIdentifier.Framebuffer, framebuffer, description.Name);

        for (var i = 0; i < colorTextures.Count; i++)
        {
            if (!textures.TryGetValue(colorTextures[i].Id, out var colorTextureResource))
            {
                throw new InvalidOperationException($"Unknown render surface color texture handle: {colorTextures[i].Id}");
            }

            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0 + i,
                TextureTarget.Texture2D,
                colorTextureResource.Texture,
                level: 0);
        }

        switch (description.Depth)
        {
            case DepthRenderbuffer:
                var renderbuffer = GL.GenRenderbuffer();
                GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, renderbuffer);
                GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, width, height);
                GL.FramebufferRenderbuffer(
                    FramebufferTarget.Framebuffer,
                    FramebufferAttachment.DepthAttachment,
                    RenderbufferTarget.Renderbuffer,
                    renderbuffer);
                GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
                ApplyDebugLabel(ObjectLabelIdentifier.Renderbuffer, renderbuffer, $"{description.Name}.depth");
                depthResource = new DepthRenderbufferResource(renderbuffer);
                break;

            case DepthTexture:
                if (depthTextureHandle is not { } depthHandle)
                {
                    throw new InvalidOperationException("DepthTexture description requires a depth texture handle.");
                }

                if (!textures.TryGetValue(depthHandle.Id, out var depthTextureResource))
                {
                    throw new InvalidOperationException($"Unknown depth texture handle: {depthHandle.Id}");
                }

                GL.FramebufferTexture2D(
                    FramebufferTarget.Framebuffer,
                    FramebufferAttachment.DepthAttachment,
                    TextureTarget.Texture2D,
                    depthTextureResource.Texture,
                    level: 0);
                depthResource = new DepthTextureResource(depthHandle);
                break;

            case DepthCubeFace cubeFace:
                if (!textures.TryGetValue(cubeFace.CubeMap.Id, out var cubeResource))
                {
                    throw new InvalidOperationException($"Unknown depth-cubemap handle: {cubeFace.CubeMap.Id}");
                }
                if (cubeFace.Face < 0 || cubeFace.Face > 5)
                {
                    throw new InvalidOperationException($"DepthCubeFace.Face must be in [0, 5]; got {cubeFace.Face}.");
                }
                GL.FramebufferTexture2D(
                    FramebufferTarget.Framebuffer,
                    FramebufferAttachment.DepthAttachment,
                    TextureTarget.TextureCubeMapPositiveX + cubeFace.Face,
                    cubeResource.Texture,
                    level: 0);
                depthResource = new DepthTextureResource(cubeFace.CubeMap);
                break;

            case null:
                break;

            default:
                throw new NotSupportedException($"Unsupported depth attachment: {description.Depth.GetType().Name}");
        }

        var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        if (status != FramebufferErrorCode.FramebufferComplete)
        {
            if (depthResource is DepthRenderbufferResource bornRenderbuffer)
            {
                GL.DeleteRenderbuffer(bornRenderbuffer.Renderbuffer);
            }

            GL.DeleteFramebuffer(framebuffer);
            throw new InvalidOperationException($"Render surface '{description.Name}' framebuffer is incomplete: {status}");
        }

        return new RenderSurfaceResource(
            description,
            framebuffer,
            colorTextures,
            depthResource,
            BuildDrawBuffersArray(colorTextures.Count),
            width,
            height,
            Dirty: false);
    }

    private static DrawBuffersEnum[] BuildDrawBuffersArray(int count)
    {
        if (count == 0)
        {
            return Array.Empty<DrawBuffersEnum>();
        }

        var buffers = new DrawBuffersEnum[count];

        for (var i = 0; i < count; i++)
        {
            buffers[i] = (DrawBuffersEnum)((int)DrawBuffersEnum.ColorAttachment0 + i);
        }

        return buffers;
    }

    private static void DeleteRenderSurfaceResource(RenderSurfaceResource resource)
    {
        GL.DeleteFramebuffer(resource.Framebuffer);

        if (resource.Depth is DepthRenderbufferResource renderbuffer)
        {
            GL.DeleteRenderbuffer(renderbuffer.Renderbuffer);
        }
    }

    private void DeleteAttachmentTextures(RenderSurfaceResource resource)
    {
        foreach (var handle in resource.ColorTextures)
        {
            if (textures.Remove(handle.Id, out var texture))
            {
                GL.DeleteTexture(texture.Texture);
            }
        }

        // DepthCubeFace surfaces reference an externally-owned cubemap; deleting
        // it through the surface destruction path would tear down a texture
        // that's still attached to its other five sibling surfaces. Only
        // surface-owned DepthTextures get deleted here.
        if (resource.Description.Depth is DepthTexture &&
            resource.Depth is DepthTextureResource depthTexture &&
            textures.Remove(depthTexture.Handle.Id, out var depth))
        {
            GL.DeleteTexture(depth.Texture);
        }
    }

    private TextureResource CreateColorAttachmentTexture(TextureFormat format, SamplerDescription sampler, int width, int height, string name)
    {
        if (format == TextureFormat.Depth24)
        {
            throw new ArgumentException("Color attachments must use a color format, not Depth24.", nameof(format));
        }

        var texture = GL.GenTexture();

        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            level: 0,
            MapPixelInternalFormat(format),
            width,
            height,
            border: 0,
            MapPixelFormat(format),
            MapPixelType(format),
            IntPtr.Zero);
        ApplySampler(sampler);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        ApplyDebugLabel(ObjectLabelIdentifier.Texture, texture, name);

        return new TextureResource(texture, width, height, format, name, TextureKind.RenderSurfaceColor);
    }

    private TextureResource CreateDepthAttachmentTexture(SamplerDescription sampler, int width, int height, string name)
    {
        var texture = GL.GenTexture();

        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            level: 0,
            PixelInternalFormat.DepthComponent24,
            width,
            height,
            border: 0,
            PixelFormat.DepthComponent,
            PixelType.UnsignedInt,
            IntPtr.Zero);
        ApplySampler(sampler);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        ApplyDebugLabel(ObjectLabelIdentifier.Texture, texture, name);

        return new TextureResource(texture, width, height, TextureFormat.Depth24, name, TextureKind.RenderSurfaceDepth);
    }

    private (int Width, int Height) ResolveRenderSurfaceSize(RenderSurfaceSize size)
    {
        return size switch
        {
            FixedRenderSurfaceSize fixedSize => (ValidateSurfaceExtent(fixedSize.Width), ValidateSurfaceExtent(fixedSize.Height)),
            MatchDefaultRenderSurfaceSize matchDefault => (
                ResolveMatchedSurfaceExtent(defaultSurfaceWidth, matchDefault.Scale),
                ResolveMatchedSurfaceExtent(defaultSurfaceHeight, matchDefault.Scale)),
            _ => throw new NotSupportedException($"Unsupported render surface size: {size.GetType().Name}")
        };
    }

    private static int ResolveMatchedSurfaceExtent(int defaultExtent, float scale)
    {
        return Math.Max((int)MathF.Round(defaultExtent * scale), 1);
    }

    private static int ValidateSurfaceExtent(int extent)
    {
        if (extent <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(extent), "Render surface dimensions must be greater than zero.");
        }

        return extent;
    }

    private static void ValidateRenderSurfaceDescription(RenderSurfaceDescription description)
    {
        if (string.IsNullOrWhiteSpace(description.Name))
        {
            throw new ArgumentException("Render surface name must not be empty.", nameof(description));
        }

        if (description.ColorAttachments.Count == 0 && description.Depth is null)
        {
            throw new ArgumentException("A render surface must declare at least one color attachment or a depth attachment.", nameof(description));
        }

        for (var i = 0; i < description.ColorAttachments.Count; i++)
        {
            var attachment = description.ColorAttachments[i];

            if (attachment.Format == TextureFormat.Depth24)
            {
                throw new ArgumentException($"Color attachment {i} cannot use Depth24.", nameof(description));
            }

            // GetBytesPerPixel was used as a "is this format valid?" probe, but it
            // intentionally throws for formats that aren't user-uploadable byte arrays
            // (Rgba16F, etc.). Render-surface attachments don't need the byte count -
            // they're allocated empty by glTexImage2D. Rely on MapPixelInternalFormat
            // instead, which validates the format against the actual GL mapping.
            _ = MapPixelInternalFormat(attachment.Format);
        }

        if (description.Size is MatchDefaultRenderSurfaceSize { Scale: <= 0.0f })
        {
            throw new ArgumentOutOfRangeException(nameof(description), "Match-default render surface scale must be greater than zero.");
        }
    }

    // Sampler config without the trailing glGenerateMipmap. Used by paths
    // that pre-provide mip data (CreateTexture2DMipped multi-mip,
    // AllocateTexture2DMips streamed path). Calling glGenerateMipmap there
    // would clobber the supplied mips with regenerated-from-level-0 garbage.
    private static void ApplySamplerNoMipGen(SamplerDescription sampler)
    {
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)MapMinFilter(sampler.MinFilter, sampler.GenerateMipmaps));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)MapMagFilter(sampler.MagFilter));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)MapTextureWrap(sampler.WrapU));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)MapTextureWrap(sampler.WrapV));
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureCompareMode,
            sampler.Compare ? (int)TextureCompareMode.CompareRefToTexture : (int)TextureCompareMode.None);
        if (sampler.Compare)
        {
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureCompareFunc,
                (int)DepthFunction.Lequal);
        }
    }

    private static void ApplySampler(SamplerDescription sampler)
    {
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)MapMinFilter(sampler.MinFilter, sampler.GenerateMipmaps));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)MapMagFilter(sampler.MagFilter));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)MapTextureWrap(sampler.WrapU));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)MapTextureWrap(sampler.WrapV));

        // Hardware depth-compare for sampler2DShadow lookups. With LEQUAL, the comparison
        // returns 1 when the reference depth is <= the stored depth (fragment is closer
        // to the light than the recorded blocker, so unshadowed). Linear filtering on top
        // of this makes the GPU emit a free 2x2 bilinear PCF in every texture() call.
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureCompareMode,
            sampler.Compare ? (int)TextureCompareMode.CompareRefToTexture : (int)TextureCompareMode.None);
        if (sampler.Compare)
        {
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureCompareFunc,
                (int)DepthFunction.Lequal);
        }

        if (sampler.GenerateMipmaps)
        {
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        }
    }

    private static string DeriveShaderProgramName(ShaderProgramDescription description, int handleId)
    {
        var labels = description.Sources
            .Select(source => source.Name ?? source.Stage.ToString())
            .ToArray();

        return labels.Length == 0 ? $"shaderProgram#{handleId}" : string.Join("|", labels);
    }

    private sealed record VertexBufferResource(int Buffer, int Count, int Stride, string Name);

    private sealed record IndexBufferResource(int Buffer, int Count, IndexFormat Format, string Name);

    private sealed record TextureResource(
        int Texture,
        int Width,
        int Height,
        TextureFormat Format,
        string Name,
        TextureKind Kind,
        // GL texture target this resource lives on. Almost everything is Texture2D, but
        // cubemaps live on TextureCubeMap and need to be bound to that target at draw
        // time for samplerCube reads to work.
        TextureTarget Target = TextureTarget.Texture2D);

    private sealed record RenderSurfaceResource(
        RenderSurfaceDescription Description,
        int Framebuffer,
        IReadOnlyList<TextureHandle> ColorTextures,
        DepthResource? Depth,
        DrawBuffersEnum[] DrawBuffers,
        int Width,
        int Height,
        bool Dirty);

    private abstract record DepthResource;

    private sealed record DepthRenderbufferResource(int Renderbuffer) : DepthResource;

    private sealed record DepthTextureResource(TextureHandle Handle) : DepthResource;

    private sealed record PipelineResource(
        ShaderProgramHandle ShaderProgram,
        VertexLayout VertexLayout,
        PrimitiveTopology Topology,
        DepthState Depth,
        RasterizerState Rasterizer,
        IReadOnlyList<BlendState> ColorBlends,
        string Name);

    private sealed class ShaderProgramResource
    {
        private readonly Dictionary<string, int> uniformLocations = [];

        public ShaderProgramResource(int programId, string name)
        {
            ProgramId = programId;
            Name = name;
        }

        public int ProgramId { get; }

        public string Name { get; }

        public int GetUniformLocation(string name)
        {
            if (uniformLocations.TryGetValue(name, out var cached))
            {
                return cached;
            }

            var location = GL.GetUniformLocation(ProgramId, name);
            uniformLocations[name] = location;
            return location;
        }
    }
}
