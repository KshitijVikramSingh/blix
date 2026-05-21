namespace Blix.Audio;

// Backend-agnostic audio device contract. The OpenAL implementation lives in
// Blix.Audio.OpenAL; alternative backends (CoreAudio, WASAPI, Web Audio)
// could plug in without touching game code.
//
// Threading: callers must invoke from a single thread (typically the game
// thread). OpenAL itself is thread-safe across contexts but this device wraps a
// single context, and serialising avoids the need for caller-side locking.
public interface IAudioDevice : IDisposable
{
    // Uploads PCM into a backend buffer. The returned handle is stable for the
    // lifetime of the device or until DeleteClip is called. Same clip may back
    // any number of concurrent sources.
    AudioClipHandle CreateClip(AudioClipData data, string? name = null);

    void DeleteClip(AudioClipHandle handle);

    // Creates a playable source bound to the given clip. The source starts
    // stopped; call UpdateSource to push parameters then Play to begin.
    AudioSourceHandle CreateSource(AudioClipHandle clip, string? name = null);

    void DeleteSource(AudioSourceHandle handle);

    // Pushes the full source state to the backend. Called once per frame for
    // each live source — the device diffs internally and only forwards changed
    // fields to the backend.
    void UpdateSource(AudioSourceHandle handle, AudioSourceState state);

    void PlaySource(AudioSourceHandle handle);
    void PauseSource(AudioSourceHandle handle);
    void StopSource(AudioSourceHandle handle);

    bool IsSourcePlaying(AudioSourceHandle handle);

    // Updates listener pose + master gain. Should be called every frame from
    // the camera/avatar that owns the listener.
    void UpdateListener(AudioListenerState state);
}
