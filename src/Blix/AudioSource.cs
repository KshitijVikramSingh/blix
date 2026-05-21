using System.Numerics;
using Blix.Audio;

namespace Blix;

// Game-layer wrapper for an audio source. Bundles a backend handle with
// transform tracking + the per-source parameters that game code typically
// tweaks. Caller is responsible for the source's lifetime:
//     var source = AudioSource.Create(device, clip);
//     ... per frame:
//         source.Position = obj.Transform.Position;
//         source.Sync(device);
//     ... on scene unload:
//         source.Dispose(device);
//
// Why no auto-Dispose via IDisposable: deletion needs the device handle and
// IDisposable can't take parameters. A pool-aware variant could capture the
// device by closure; defer until a real consumer surfaces.
public sealed class AudioSource
{
    public AudioSourceHandle Handle { get; }
    public AudioClipHandle Clip { get; }

    // Mutable per-frame parameters. Game code updates these before calling Sync.
    public Vector3 Position { get; set; }
    public float Gain { get; set; } = 1.0f;
    public float Pitch { get; set; } = 1.0f;
    public bool IsLooping { get; set; }
    public float ReferenceDistance { get; set; } = 1.0f;
    public float MaxDistance { get; set; } = 100.0f;

    private AudioSource(AudioSourceHandle handle, AudioClipHandle clip)
    {
        Handle = handle;
        Clip = clip;
    }

    public static AudioSource Create(IAudioDevice device, AudioClipHandle clip, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        var handle = device.CreateSource(clip, name);
        return new AudioSource(handle, clip);
    }

    // Pushes current state to the backend. Cheap: the device diffs against the
    // last-pushed state and skips unchanged fields.
    public void Sync(IAudioDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        device.UpdateSource(Handle, new AudioSourceState(
            Position: Position,
            Gain: Gain,
            Pitch: Pitch,
            IsLooping: IsLooping,
            ReferenceDistance: ReferenceDistance,
            MaxDistance: MaxDistance));
    }

    public void Play(IAudioDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        device.PlaySource(Handle);
    }

    public void Pause(IAudioDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        device.PauseSource(Handle);
    }

    public void Stop(IAudioDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        device.StopSource(Handle);
    }

    public bool IsPlaying(IAudioDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.IsSourcePlaying(Handle);
    }

    public void Dispose(IAudioDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        device.DeleteSource(Handle);
    }
}
