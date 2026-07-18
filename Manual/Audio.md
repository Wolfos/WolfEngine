# Audio

WolfEngine audio is provided by `IAudioService` from dependency injection. It plays
non-spatial SFX and music clips authored as `AudioClip` assets. Positioning and 3D
audio are deliberately not part of this first API, so use the same calls regardless
of where gameplay code runs in the world.

## Importing clips

Use **Assets > Import Audio...** and choose WAV, FLAC, MP3, or Ogg Vorbis. The
importer stores an `AudioClip` asset, which can be assigned to an
`AssetRef<AudioClip>` field like other runtime assets.

The **Usage** import setting controls cooking:

- **Auto**: clips shorter than 10 seconds are cooked as SFX; clips of 10 seconds
  or longer are cooked as streamed music.
- **Sfx**: decodes the clip to 48 kHz PCM16 during import. This minimizes runtime
  startup latency and supports preloading.
- **Music**: preserves the validated source encoding and decodes it incrementally
  from the editor artifact or the packaged `audio.wolfpack` entry.

Set Usage explicitly whenever duration alone is not the desired behaviour, such as
for a short streamed ambience loop or a long UI sound.

## Getting the service

Request `IAudioService` from the game service provider or inject it into a system
using the engine's normal dependency-injection pattern.

```csharp
using WolfEngine.Audio;
using WolfEngine.AssetPipeline;

public sealed class PlayerAudio
{
    private readonly IAudioService _audio;

    public PlayerAudio(IAudioService audio) => _audio = audio;

    public AssetRef<AudioClip> JumpSound;
    public AssetRef<AudioClip> BackgroundMusic;
}
```

Check `IsAvailable` only when gameplay needs to react to unavailable output. The
service installs a silent fallback if an output device cannot be opened, so games
can otherwise continue normally.

## Playing SFX

`PlaySfx` returns an `AudioPlaybackHandle`. A handle is safe to retain, but may
become invalid if the sound completes, is stopped, or is stolen to make room for a
higher-priority SFX.

```csharp
var handle = _audio.PlaySfx(JumpSound, new AudioPlaybackOptions
{
    Volume = 0.8f,
    Pitch = 1.05f,
    Priority = 160
});

if (_audio.IsPlaying(handle))
    _audio.SetVolume(handle, 0.6f);
```

SFX use the `Sfx` bus. WolfEngine has 128 voices by default; two are reserved for
music. When SFX capacity is exhausted, the oldest sound among the lowest-priority
eligible sounds is stolen. Music voices are never stolen by SFX.

Use `Loop = true` for looping non-music effects. Stop it when the gameplay state
ends:

```csharp
_audio.Stop(handle);
```

`Stop`, `Pause`, `Resume`, `SetVolume`, and `SetPitch` harmlessly ignore stale or
invalid handles. Pitch is clamped to the supported range of 0.125 to 8.0; volume is
clamped to 0 to 4.

## Playing music

`PlayMusic` manages the current logical music track. Starting another track replaces
it and crossfades from the outgoing track. Music loops by default and uses a
one-second linear crossfade by default.

```csharp
_audio.PlayMusic(BackgroundMusic, new AudioMusicOptions
{
    Volume = 0.7f,
    Loop = true,
    CrossfadeDuration = TimeSpan.FromSeconds(2)
});

// Fade the active music track out.
_audio.StopMusic(TimeSpan.FromMilliseconds(500));
```

Only one logical music track is active at a time. During a crossfade, the incoming
and outgoing tracks consume the two reserved music voices.

## Buses and master volume

Use buses for user settings and broad gameplay changes:

```csharp
_audio.SetMasterVolume(0.9f);
_audio.SetBusVolume(AudioBus.Sfx, 0.75f);
_audio.SetBusVolume(AudioBus.Music, 0.5f);

// Immediately releases all voices on the selected bus.
_audio.StopBus(AudioBus.Sfx);
```

## Preloading SFX

Preload frequently used, predecoded SFX before a sequence that cannot tolerate its
first file access:

```csharp
await _audio.PreloadAsync(JumpSound, cancellationToken);
```

Preloading is intentionally ignored for streamed music: keeping music range-backed
is what avoids extracting or materializing large packaged assets. Release cached
SFX data when it is no longer useful:

```csharp
_audio.Unload(JumpSound);
```

## Threading and lifecycle

Gameplay code can call `IAudioService` from its normal update thread. Calls queue
work to the dedicated WolfEngine audio thread; gameplay code must not call MiniAudio
directly. The native output callback is owned by MiniAudio separately.

The editor pauses active voices when Play mode pauses, resumes them when Play mode
resumes, and releases session voices when Play mode stops, the gameplay world reloads,
or the application shuts down. Gameplay systems should still stop state-specific
loops explicitly rather than relying on session cleanup.

## Future spatial audio

The current API intentionally takes only clip references and playback options. It
does not serialize source positions or listener state. This keeps existing asset and
handle usage stable when listener/emitter components and 3D spatialization are added
later.
