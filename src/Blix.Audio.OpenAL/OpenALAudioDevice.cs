using System.Numerics;
using Blix.Audio;
using OpenTK.Audio.OpenAL;

namespace Blix.Audio.OpenAL;

// OpenAL Soft implementation of IAudioDevice. Same backend-isolation pattern
// as Blix.Graphics.Vulkan: this assembly knows about OpenAL; everyone above it
// only sees the abstract IAudioDevice. One device, one context, one thread.
//
// Resource tracking uses int-id dictionaries to mirror the AL buffer/source
// integer handles, with a separate counter so external handle ids don't
// collide with AL's (the AL ids leak through the int-keyed dictionaries
// internally but are never exposed).
public sealed class OpenALAudioDevice : IAudioDevice
{
    private readonly ALDevice device;
    private readonly ALContext context;
    private readonly Dictionary<int, ClipResource> clips = new();
    private readonly Dictionary<int, SourceResource> sources = new();
    private int nextClipId = 1;
    private int nextSourceId = 1;
    private bool disposed;

    // Opens the system's default audio device. Throws if no device is present
    // (headless CI, sandboxed machines without audio) — callers that want to
    // tolerate that should wrap construction in a try/catch and fall back to a
    // no-op IAudioDevice.
    public OpenALAudioDevice()
    {
        // On macOS, OpenTK by default loads /System/Library/Frameworks/OpenAL.framework/OpenAL —
        // Apple's bundled OpenAL, last updated 2014 and deprecated since macOS 10.15. It
        // silently no-ops on many calls (looping, source state transitions, often
        // playback itself), so the right backend on macOS is OpenAL Soft via Homebrew.
        // We look for it in the standard locations and override the loader's library
        // path before the first AL/ALC call — Apple's framework stays as the fallback.
        TryOverrideMacOSLibraryPath();

        device = ALC.OpenDevice(null);
        if (device == ALDevice.Null)
        {
            throw new InvalidOperationException("OpenAL: ALC.OpenDevice(null) returned no device — no audio backend available.");
        }

        context = ALC.CreateContext(device, (int[]?)null);
        if (context == ALContext.Null)
        {
            ALC.CloseDevice(device);
            throw new InvalidOperationException($"OpenAL: failed to create context on default device (alc error {ALC.GetError(device)}).");
        }

        if (!ALC.MakeContextCurrent(context))
        {
            ALC.DestroyContext(context);
            ALC.CloseDevice(device);
            throw new InvalidOperationException("OpenAL: MakeContextCurrent returned false.");
        }

        // AL_INVERSE_DISTANCE_CLAMPED is the de-facto game-audio default: gain
        // is unity inside ReferenceDistance and falls off as ~1/distance past
        // it, with MaxDistance acting as a hard clamp so sounds never go fully
        // silent (which screws with one-shot bookkeeping).
        AL.DistanceModel(ALDistanceModel.InverseDistanceClamped);

        Vendor = AL.Get(ALGetString.Vendor) ?? "(unknown)";
        Renderer = AL.Get(ALGetString.Renderer) ?? "(unknown)";
        Version = AL.Get(ALGetString.Version) ?? "(unknown)";
    }

    public string Vendor { get; }
    public string Renderer { get; }
    public string Version { get; }

    public AudioClipHandle CreateClip(AudioClipData data, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ThrowIfDisposed();

        var format = ResolveFormat(data.Channels, data.BitsPerSample);
        var bufferId = AL.GenBuffer();
        CheckAlError($"GenBuffer for clip '{name}'");
        AL.BufferData(bufferId, format, data.PcmData, data.SampleRate);
        CheckAlError($"BufferData for clip '{name}' ({data.PcmData.Length} bytes, {data.SampleRate} Hz, {data.Channels}ch, {data.BitsPerSample}-bit)");

        var id = nextClipId++;
        clips[id] = new ClipResource(bufferId, name ?? $"clip#{id}");
        return new AudioClipHandle(id);
    }

    public void DeleteClip(AudioClipHandle handle)
    {
        ThrowIfDisposed();
        if (!clips.Remove(handle.Id, out var resource))
        {
            return;
        }

        // Unbind from any source still referencing the clip. AL would otherwise
        // throw AL_INVALID_OPERATION on DeleteBuffer for a buffer that's still
        // attached, and CheckAlError isn't called here so the failure would be
        // silent. Stop + detach makes the delete clean.
        foreach (var (_, source) in sources)
        {
            if (source.Clip.Id == handle.Id)
            {
                AL.SourceStop(source.SourceId);
                AL.Source(source.SourceId, ALSourcei.Buffer, 0);
            }
        }

        AL.DeleteBuffer(resource.BufferId);
    }

    public AudioSourceHandle CreateSource(AudioClipHandle clip, string? name = null)
    {
        ThrowIfDisposed();
        if (!clips.TryGetValue(clip.Id, out var clipResource))
        {
            throw new InvalidOperationException($"Unknown clip handle: {clip.Id}");
        }

        var sourceId = AL.GenSource();
        CheckAlError($"GenSource for source '{name}'");
        AL.Source(sourceId, ALSourcei.Buffer, clipResource.BufferId);
        CheckAlError($"Source(Buffer) for source '{name}'");

        var id = nextSourceId++;
        sources[id] = new SourceResource(sourceId, clip, name ?? $"source#{id}", LastState: default);
        return new AudioSourceHandle(id);
    }

    public void DeleteSource(AudioSourceHandle handle)
    {
        ThrowIfDisposed();
        if (!sources.Remove(handle.Id, out var resource))
        {
            return;
        }
        AL.SourceStop(resource.SourceId);
        AL.DeleteSource(resource.SourceId);
    }

    public void UpdateSource(AudioSourceHandle handle, AudioSourceState state)
    {
        ThrowIfDisposed();
        if (!sources.TryGetValue(handle.Id, out var resource))
        {
            throw new InvalidOperationException($"Unknown source handle: {handle.Id}");
        }

        // Diff against last-pushed state — OpenAL's parameter setters are cheap
        // but skipping unchanged ones keeps the al-call count proportional to
        // actual game-side change rate, which matters when a scene has dozens of
        // stationary ambient sources. `LastStatePushed` distinguishes "first
        // push" from "last push happened to be all-defaults" — using
        // default-state equality would falsely flag explicitly-silenced sources
        // as never-pushed.
        var last = resource.LastState;
        var first = !resource.LastStatePushed;

        if (first || last.Position != state.Position)
        {
            AL.Source(resource.SourceId, ALSource3f.Position, state.Position.X, state.Position.Y, state.Position.Z);
        }

        if (first || last.Gain != state.Gain)
        {
            AL.Source(resource.SourceId, ALSourcef.Gain, state.Gain);
        }

        if (first || last.Pitch != state.Pitch)
        {
            AL.Source(resource.SourceId, ALSourcef.Pitch, state.Pitch);
        }

        if (first || last.IsLooping != state.IsLooping)
        {
            AL.Source(resource.SourceId, ALSourceb.Looping, state.IsLooping);
        }

        if (first || last.ReferenceDistance != state.ReferenceDistance)
        {
            AL.Source(resource.SourceId, ALSourcef.ReferenceDistance, state.ReferenceDistance);
        }

        if (first || last.MaxDistance != state.MaxDistance)
        {
            AL.Source(resource.SourceId, ALSourcef.MaxDistance, state.MaxDistance);
        }

        sources[handle.Id] = resource with { LastState = state, LastStatePushed = true };
    }

    public void PlaySource(AudioSourceHandle handle)
    {
        AL.SourcePlay(LookupSource(handle).SourceId);
    }

    public void PauseSource(AudioSourceHandle handle)
    {
        AL.SourcePause(LookupSource(handle).SourceId);
    }

    public void StopSource(AudioSourceHandle handle)
    {
        AL.SourceStop(LookupSource(handle).SourceId);
    }

    public bool IsSourcePlaying(AudioSourceHandle handle)
    {
        var sourceId = LookupSource(handle).SourceId;
        // OpenAL doesn't ship a dedicated GetSourceState — read the integer
        // SourceState attribute and round-trip through the typed enum.
        return (ALSourceState)AL.GetSource(sourceId, ALGetSourcei.SourceState) == ALSourceState.Playing;
    }

    public void UpdateListener(AudioListenerState state)
    {
        ThrowIfDisposed();
        AL.Listener(ALListener3f.Position, state.Position.X, state.Position.Y, state.Position.Z);

        // Orientation is the canonical 6-float (forward, up) pair. OpenAL
        // documents "at" as "the vector the listener is looking at FROM the
        // listener" — i.e. the camera-forward vector. Pack into a float[6] to
        // use the array overload and avoid OpenTK's Vector3 ref.
        var orientation = new float[6]
        {
            state.OrientationForward.X, state.OrientationForward.Y, state.OrientationForward.Z,
            state.OrientationUp.X,      state.OrientationUp.Y,      state.OrientationUp.Z,
        };
        AL.Listener(ALListenerfv.Orientation, orientation);

        AL.Listener(ALListenerf.Gain, state.Gain);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;

        foreach (var (_, resource) in sources)
        {
            AL.SourceStop(resource.SourceId);
            AL.DeleteSource(resource.SourceId);
        }
        sources.Clear();

        foreach (var (_, resource) in clips)
        {
            AL.DeleteBuffer(resource.BufferId);
        }
        clips.Clear();

        ALC.MakeContextCurrent(ALContext.Null);
        ALC.DestroyContext(context);
        ALC.CloseDevice(device);
    }

    private SourceResource LookupSource(AudioSourceHandle handle)
    {
        ThrowIfDisposed();
        if (!sources.TryGetValue(handle.Id, out var resource))
        {
            throw new InvalidOperationException($"Unknown source handle: {handle.Id}");
        }
        return resource;
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(OpenALAudioDevice));
        }
    }

    private static ALFormat ResolveFormat(int channels, int bitsPerSample)
    {
        return (channels, bitsPerSample) switch
        {
            (1, 8) => ALFormat.Mono8,
            (1, 16) => ALFormat.Mono16,
            (2, 8) => ALFormat.Stereo8,
            (2, 16) => ALFormat.Stereo16,
            _ => throw new NotSupportedException($"Audio format {channels} channel(s), {bitsPerSample}-bit is not supported. v0 covers mono/stereo 8-bit unsigned and 16-bit signed PCM only."),
        };
    }

    private static void CheckAlError(string context)
    {
        var error = AL.GetError();
        if (error != ALError.NoError)
        {
            throw new InvalidOperationException($"OpenAL error during {context}: {error} ({AL.GetErrorString(error)}).");
        }
    }

    private sealed record ClipResource(int BufferId, string Name);

    private sealed record SourceResource(int SourceId, AudioClipHandle Clip, string Name, AudioSourceState LastState, bool LastStatePushed = false);

    private static void TryOverrideMacOSLibraryPath()
    {
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
        {
            return;
        }

        // Apple Silicon Homebrew + Intel Homebrew prefixes. Order matters: prefer the
        // architecture-native installation. Files probed in dependency-load priority.
        string[] candidates =
        {
            "/opt/homebrew/opt/openal-soft/lib/libopenal.dylib",
            "/opt/homebrew/opt/openal-soft/lib/libopenal.1.dylib",
            "/opt/homebrew/lib/libopenal.dylib",
            "/opt/homebrew/lib/libopenal.1.dylib",
            "/usr/local/opt/openal-soft/lib/libopenal.dylib",
            "/usr/local/opt/openal-soft/lib/libopenal.1.dylib",
            "/usr/local/lib/libopenal.dylib",
            "/usr/local/lib/libopenal.1.dylib",
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                OpenTK.Audio.OpenAL.OpenALLibraryNameContainer.OverridePath = candidate;
                Console.WriteLine($"OpenAL: using {candidate}");
                return;
            }
        }

        Console.WriteLine("OpenAL: no OpenAL Soft (libopenal.dylib) found under /opt/homebrew or /usr/local. Falling back to /System/Library/Frameworks/OpenAL.framework — Apple's framework is deprecated and may produce no audio. Install OpenAL Soft with: brew install openal-soft");
    }
}
