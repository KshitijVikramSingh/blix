using System.Diagnostics;
using Blix.Diagnostics;
using Blix.Graphics;

namespace Blix.Render;

// Streams texture mips to the GPU across Drain calls. Backend-agnostic: it drives an IGraphicsDevice and is the
// upload pump under MaterialTextureLoader.
//
// Each texture's mips are read off the frame thread -- a task a texture, MaxConcurrentReads at once, reading its levels
// smallest-first into a ready queue (a texture's reader is called from one task only) -- while Drain, on the frame
// thread, hands ready mips to the device: allocated (a lazy enqueue's first, smallest mip, uploaded and waited for
// so its handle is usable), then queued with the frame's own commands (TryQueueTextureUpload: no wait), up to a byte
// budget a frame and the time budget. Ready bytes held are capped (MaxReadyBytes): no more texture reads start
// past it. A mip too large for the frame's staging goes up the old way (UploadTextureMip: waited for).
// It was one mip at a time on the frame thread -- a blocking file read and an upload the device waited on, each --
// and Sponza's textures came in one by one (2026-10-13).
//
// Mips go up smallest-first and share one stable texture handle. Unuploaded levels remain undefined; this class does
// not clamp sampler LOD, so callers gate sampling until the levels they may read are resident (MaterialTextureLoader's
// consumers wait for PendingCount to reach zero; a queued level lands with the frame that recorded it).
//
// Enqueue and Drain run on the frame thread; only the mip readers run elsewhere.
public sealed class ResourceUploader : IDebuggable
{
    private readonly IGraphicsDevice device;
    private readonly Queue<TextureUploadContext> waiting = new();
    private readonly List<TextureUploadContext> active = new();
    private int pendingMips;
    private long uploadedCount;
    private long uploadedBytes;
    private long readyBytes;
    private double lastDrainMillis;
    private long uploadedCountAtLastDebug;

    /// <summary>Textures read at once, off the frame thread.</summary>
    public int MaxConcurrentReads { get; set; } = 6;

    /// <summary>Bytes read and waiting to go up, past which no further texture's reads start.</summary>
    public long MaxReadyBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Bytes handed to the device a Drain at most (the device's per-frame staging is asked for this much).</summary>
    public int ByteBudget { get; }

    public ResourceUploader(IGraphicsDevice device, int byteBudget = 16 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(device);
        this.device = device;
        ByteBudget = byteBudget;
        device.ReserveTextureUploadRing(byteBudget);
    }

    /// <summary>Mips not yet handed to the device.</summary>
    public int PendingCount => pendingMips;
    public long UploadedCount => uploadedCount;
    public long UploadedBytes => uploadedBytes;
    public double LastDrainMillis => lastDrainMillis;

    public string DebugName => "uploader";

    public void Debug(DebugContext debug)
    {
        debug.Stats.Gauge("pending", PendingCount);
        debug.Stats.Gauge("uploaded", UploadedCount);
        debug.Stats.Gauge("uploaded-mb", UploadedBytes / 1048576.0);
        debug.Stats.Gauge("ready-mb", Interlocked.Read(ref readyBytes) / 1048576.0);
        debug.Stats.Gauge("drain-ms", LastDrainMillis);
        var delta = uploadedCount - uploadedCountAtLastDebug;
        uploadedCountAtLastDebug = uploadedCount;
        if (delta > 0)
            debug.Events.Info($"uploaded {delta} mip(s) in {LastDrainMillis:0.00} ms, {PendingCount} pending");
    }

    public void EnqueueRgba8(
        byte[] pixels, int width, int height, SamplerDescription sampler,
        string name, Action<TextureHandle> onUploaded)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        Enqueue(TextureFormat.Rgba8, width, height, new[] { pixels }, sampler, name, onUploaded);
    }

    // Eager enqueue: all mip data is in memory already.
    public void Enqueue(
        TextureFormat format,
        int width, int height,
        IReadOnlyList<byte[]> mipBytes,
        SamplerDescription sampler,
        string name,
        Action<TextureHandle> onUploaded)
    {
        ArgumentNullException.ThrowIfNull(mipBytes);
        if (mipBytes.Count == 0) throw new ArgumentException("At least one mip is required.", nameof(mipBytes));
        EnqueueLazy(format, width, height, mipBytes.Count, level => mipBytes[level], sampler, name, onUploaded);
    }

    // Lazy enqueue: mip bytes are read when the texture's reader gets to them (off the frame thread), so cooked
    // .blixtex stay on disk until their texture's turn; the first (smallest) mip allocates the chain.
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
        Add(new TextureUploadContext(format, width, height, sampler, name, mipCount, mipReader, onUploaded));
    }

    // Stream mips into an ALREADY-allocated texture (from AllocateTexture2DMips), smallest-first. No allocation and
    // no callback: the caller has the handle and binds it at once; its levels fill in over the next frames.
    public void EnqueueInto(
        TextureHandle handle, TextureFormat format, int width, int height, int mipCount,
        Func<int, byte[]> mipReader)
    {
        ArgumentNullException.ThrowIfNull(mipReader);
        if (mipCount <= 0) throw new ArgumentOutOfRangeException(nameof(mipCount));
        Add(new TextureUploadContext(format, width, height, SamplerDescription.LinearClamp, string.Empty, mipCount, mipReader, _ => { })
        {
            Handle = handle,
        });
    }

    private void Add(TextureUploadContext ctx)
    {
        waiting.Enqueue(ctx);
        pendingMips += ctx.MipCount;
    }

    // Starts readers for the waiting textures while fewer than MaxConcurrentReads are reading and the ready bytes are
    // under the cap.
    private void StartReaders()
    {
        var reading = 0;
        foreach (var c in active) if (!c.ReadDone) reading++;
        while (waiting.Count > 0 && reading < MaxConcurrentReads && Interlocked.Read(ref readyBytes) < MaxReadyBytes)
        {
            var ctx = waiting.Dequeue();
            active.Add(ctx);
            reading++;
            ctx.Reader = Task.Run(() =>
            {
                for (var level = ctx.MipCount - 1; level >= 0; level--)
                {
                    var bytes = ctx.MipReader(level);
                    Interlocked.Add(ref readyBytes, bytes.Length);
                    ctx.Ready.Enqueue((level, bytes));
                }
                ctx.ReadDone = true;
            });
        }
    }

    public void Drain(double budgetMillis)
    {
        if (pendingMips == 0)
        {
            lastDrainMillis = 0.0;
            return;
        }
        var stopwatch = Stopwatch.StartNew();
        StartReaders();
        long bytesThisDrain = 0;
        var stop = false;
        for (var a = 0; a < active.Count && !stop; a++)
        {
            var ctx = active[a];
            if (ctx.Reader is { IsFaulted: true } failed)
                throw new InvalidOperationException($"reading texture '{ctx.Name}' failed", failed.Exception?.GetBaseException());
            while (ctx.Ready.TryPeek(out var item))
            {
                var length = item.Bytes.Length;
                if (bytesThisDrain > 0 && bytesThisDrain + length > ByteBudget) { stop = true; break; }
                if (ctx.Handle is null)
                {
                    // First (smallest) mip of a lazy enqueue: allocate the chain, upload this level and wait (the
                    // callback hands out a usable handle).
                    var handle = device.AllocateTexture2DMips(new TextureDescription(ctx.Width, ctx.Height, ctx.Format, ctx.Sampler), ctx.MipCount, ctx.Name);
                    ctx.Handle = handle;
                    device.UploadTextureMip(handle, item.Level, item.Bytes);
                    ctx.OnFirstReady(handle);
                }
                else if (!device.TryQueueTextureUpload(ctx.Handle.Value, item.Level, item.Bytes))
                {
                    // The frame's staging is full. A mip that would not fit an empty one goes up the old way.
                    if (bytesThisDrain > 0 || length < ByteBudget) { stop = true; break; }
                    device.UploadTextureMip(ctx.Handle.Value, item.Level, item.Bytes);
                }
                ctx.Ready.TryDequeue(out _);
                ctx.Uploaded++;
                Interlocked.Add(ref readyBytes, -length);
                bytesThisDrain += length;
                uploadedBytes += length;
                uploadedCount++;
                pendingMips--;
                if (stopwatch.Elapsed.TotalMilliseconds >= budgetMillis) { stop = true; break; }
            }
        }
        active.RemoveAll(c => c.ReadDone && c.Ready.IsEmpty && c.Uploaded == c.MipCount);
        StartReaders();
        stopwatch.Stop();
        lastDrainMillis = stopwatch.Elapsed.TotalMilliseconds;
    }

    // State shared across one texture's mips: its reader's output, and what has gone up.
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
        public readonly System.Collections.Concurrent.ConcurrentQueue<(int Level, byte[] Bytes)> Ready = new();
        public Task? Reader;
        public volatile bool ReadDone;
        public int Uploaded;
    }
}
