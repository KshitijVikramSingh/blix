using System.Diagnostics;
using Blix.Diagnostics;
using Blix.Graphics;

namespace Blix.Render;

// Spreads GPU-side texture uploads over multiple frames so the main thread
// doesn't stall on a synchronous wall of glTexImage2D / glCompressedTexImage2D
// + glGenerateMipmap calls during scene load. Scene loaders enqueue an
// upload + a callback; each frame the runtime calls Drain(budgetMs), which
// processes pending work items until the budget runs out.
//
// Per-mip granularity is the key trick. Each Enqueue splits into N work
// items (one per mip level), so Drain's "do at least one item" guarantee
// means at most one mip's-worth of GL work per Drain iteration. A 4K
// texture has ~11 mips at descending sizes; the smallest mips (4x4, 8x8,
// ...) are microseconds each, so Drain can chew through many of them in
// 4ms. The biggest mip (full 4K = ~64MB) might be 100-200ms on its own,
// but only one such per frame -- a tolerable single-frame stutter
// rather than the multi-second freeze the original "whole texture per
// Drain" path produced.
//
// Smallest-mip-first ordering: the texture handle is created when the
// smallest mip's work item runs. Sampling at any LOD picks the smallest
// mip via GL_TEXTURE_BASE_LEVEL; as finer mips upload, BASE_LEVEL
// decreases. The lit shader sees a usable (if blurry) texture from the
// moment OnFirstMipReady fires, and the texture sharpens over time.
//
// Deliberate non-goals:
// - Not a streaming system. Every enqueue is unconditional.
// - Not thread-safe. All Drain + Enqueue calls run on the GL thread.
public sealed class ResourceUploader : IDebuggable
{
    private readonly IGraphicsDevice device;
    private readonly Queue<MipUpload> queue = new();
    private long uploadedCount;
    private double lastDrainMillis;
    // Tracks uploadedCount at the previous Debug() invocation so we can
    // surface a single Info event per frame summarising the delta —
    // useful for "this frame finished N mips" without spamming one
    // event per mip.
    private long uploadedCountAtLastDebug;

    public ResourceUploader(IGraphicsDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        this.device = device;
    }

    public int PendingCount => queue.Count;
    public long UploadedCount => uploadedCount;
    public double LastDrainMillis => lastDrainMillis;

    public string DebugName => "uploader";

    // Pull-style producer: invoked once per frame from DebugSystem.Run.
    // The counters are state the uploader already maintains, so the
    // implementation is just a typed surface for them — values become
    // graph-able gauges instead of opaque strings.
    public void Debug(DebugContext debug)
    {
        debug.Stats.Gauge("pending", PendingCount);
        debug.Stats.Gauge("uploaded", UploadedCount);
        debug.Stats.Gauge("drain-ms", LastDrainMillis);

        var delta = uploadedCount - uploadedCountAtLastDebug;
        uploadedCountAtLastDebug = uploadedCount;
        if (delta > 0)
        {
            // Info severity — finishing a batch of mip uploads is normal
            // progress, not a warning. The ConsoleEventSink's default
            // minimum severity is Warn, so this stays quiet unless a
            // consumer opts in.
            debug.Events.Info(
                $"uploaded {delta} mip(s) in {LastDrainMillis:0.00} ms, {PendingCount} pending");
        }
    }

    public void EnqueueRgba8(
        byte[] pixels, int width, int height, SamplerDescription sampler,
        string name, Action<TextureHandle> onUploaded)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        Enqueue(TextureFormat.Rgba8, width, height, new[] { pixels }, sampler, name, onUploaded);
    }

    // Eager enqueue: all mip data is in memory already. Wraps EnqueueLazy
    // by handing the byte arrays through a trivial closure.
    public void Enqueue(
        TextureFormat format,
        int width, int height,
        IReadOnlyList<byte[]> mipBytes,
        SamplerDescription sampler,
        string name,
        Action<TextureHandle> onUploaded)
    {
        ArgumentNullException.ThrowIfNull(mipBytes);
        if (mipBytes.Count == 0)
        {
            throw new ArgumentException("At least one mip is required.", nameof(mipBytes));
        }
        EnqueueLazy(format, width, height, mipBytes.Count,
            mipReader: level => mipBytes[level],
            sampler, name, onUploaded);
    }

    // Lazy enqueue: mip bytes are read on-demand from `mipReader(level)`
    // when the upload pump processes that mip. Lets cooked .blixtex stay
    // on disk until the moment a mip is needed, keeping load-time RAM
    // bounded by "the few mips currently in flight".
    public void EnqueueLazy(
        TextureFormat format,
        int width, int height,
        int mipCount,
        Func<int, byte[]> mipReader,
        SamplerDescription sampler,
        string name,
        Action<TextureHandle> onUploaded)
    {
        ArgumentNullException.ThrowIfNull(mipReader);
        ArgumentNullException.ThrowIfNull(onUploaded);
        if (mipCount <= 0) throw new ArgumentOutOfRangeException(nameof(mipCount));

        // Per-mip granularity, smallest-first. The first ProcessOne call
        // (level = mipCount-1, the smallest mip) allocates storage via
        // AllocateTexture2DMips and uploads that mip; subsequent calls
        // upload progressively finer levels, walking GL_TEXTURE_BASE_LEVEL
        // down so the sampler always reads the highest-quality mip
        // available. The texture becomes bindable as soon as the smallest
        // mip lands (callback fires from inside ProcessOne) -- materials
        // see a usable, if blurry, texture immediately, sharpening over
        // the next ~N frames.
        var ctx = new TextureUploadContext(format, width, height, sampler, name, mipCount, mipReader, onUploaded);
        for (var level = mipCount - 1; level >= 0; level--)
        {
            queue.Enqueue(new MipUpload(ctx, level));
        }
    }

    public void Drain(double budgetMillis)
    {
        if (queue.Count == 0)
        {
            lastDrainMillis = 0.0;
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        do
        {
            var entry = queue.Dequeue();
            ProcessOne(entry);
        }
        while (queue.Count > 0 && stopwatch.Elapsed.TotalMilliseconds < budgetMillis);
        stopwatch.Stop();
        lastDrainMillis = stopwatch.Elapsed.TotalMilliseconds;
    }

    private void ProcessOne(MipUpload entry)
    {
        var ctx = entry.Context;
        var level = entry.MipLevel;
        var bytes = ctx.MipReader(level);
        if (ctx.Handle is null)
        {
            // First (smallest) mip: allocate storage for the full chain.
            // AllocateTexture2DMips sets BASE_LEVEL = MAX_LEVEL = mipCount-1
            // and applies the sampler params (no glGenerateMipmap, since
            // we'll fill levels manually). UploadTextureMip then writes
            // the smallest mip's bytes into level mipCount-1.
            var handle = device.AllocateTexture2DMips(
                new TextureDescription(ctx.Width, ctx.Height, ctx.Format, ctx.Sampler),
                ctx.MipCount,
                ctx.Name);
            ctx.Handle = handle;
            device.UploadTextureMip(handle, level, bytes);
            ctx.OnFirstReady(handle);
        }
        else
        {
            // Finer mip: UploadTextureMip walks GL_TEXTURE_BASE_LEVEL down
            // to `level` so the sampler picks up the new highest-quality
            // mip on the next draw.
            device.UploadTextureMip(ctx.Handle.Value, level, bytes);
        }
        uploadedCount++;
    }

    // State shared across the mip work items of one texture. Mutable so
    // ProcessOne can stamp the texture handle on the first run and read
    // it from later runs.
    private sealed class TextureUploadContext
    {
        public TextureUploadContext(
            TextureFormat format, int width, int height, SamplerDescription sampler,
            string name, int mipCount, Func<int, byte[]> mipReader, Action<TextureHandle> onFirstReady)
        {
            Format = format;
            Width = width;
            Height = height;
            Sampler = sampler;
            Name = name;
            MipCount = mipCount;
            MipReader = mipReader;
            OnFirstReady = onFirstReady;
        }
        public TextureFormat Format { get; }
        public int Width { get; }
        public int Height { get; }
        public SamplerDescription Sampler { get; }
        public string Name { get; }
        public int MipCount { get; }
        public Func<int, byte[]> MipReader { get; }
        public Action<TextureHandle> OnFirstReady { get; }
        public TextureHandle? Handle { get; set; }
    }

    private sealed record MipUpload(
        TextureUploadContext Context,
        int MipLevel);
}
