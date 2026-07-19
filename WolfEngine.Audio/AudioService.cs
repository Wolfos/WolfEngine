using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Miniaudio;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Audio;

public enum AudioBus
{
	Sfx,
	Music
}

public readonly record struct AudioPlaybackHandle(int Slot, uint Generation)
{
	public static AudioPlaybackHandle Invalid { get; } = new(-1, 0);
	public bool IsValid => Slot >= 0 && Generation != 0;
}

public sealed class AudioPlaybackOptions
{
	public float Volume { get; set; } = 1.0f;
	public float Pitch { get; set; } = 1.0f;
	public bool Loop { get; set; }
	public int Priority { get; set; } = 128;
}

public sealed class AudioMusicOptions
{
	public float Volume { get; set; } = 1.0f;
	public float Pitch { get; set; } = 1.0f;
	public bool Loop { get; set; } = true;
	public TimeSpan CrossfadeDuration { get; set; } = TimeSpan.FromSeconds(1);
}

public interface IAudioContentProvider
{
	AudioContentStream Open(Guid assetId);
}

public interface IAudioService
{
	bool IsAvailable { get; }
	AudioPlaybackHandle PlaySfx(AssetRef<AudioClip> clip, AudioPlaybackOptions? options = null);
	AudioPlaybackHandle PlayMusic(AssetRef<AudioClip> clip, AudioMusicOptions? options = null);
	void Stop(AudioPlaybackHandle handle);
	void Pause(AudioPlaybackHandle handle);
	void Resume(AudioPlaybackHandle handle);
	bool IsPlaying(AudioPlaybackHandle handle);
	void SetVolume(AudioPlaybackHandle handle, float volume);
	void SetPitch(AudioPlaybackHandle handle, float pitch);
	void StopMusic(TimeSpan? fadeDuration = null);
	void StopBus(AudioBus bus);
	void SetBusVolume(AudioBus bus, float volume);
	void SetMasterVolume(float volume);
	Task PreloadAsync(AssetRef<AudioClip> clip, CancellationToken cancellationToken = default);
	void Unload(AssetRef<AudioClip> clip);
}

public interface IAudioRuntime
{
	// Retained for host loop integration. Audio timing and cleanup are owned by the audio thread.
	void Update(float deltaTime);
	void PauseAll();
	void ResumeAll();
	void StopAll();
}

/// <summary>Internal boundary that keeps hardware and native calls on the audio thread.</summary>
internal interface IAudioBackend : IDisposable
{
	IAudioBackendVoice CreateVoice(AudioContentStream content, AudioBus bus, bool loop, float volume, float pitch);
	void SetBusVolume(AudioBus bus, float volume);
	void SetMasterVolume(float volume);
}

internal interface IAudioBackendVoice : IDisposable
{
	bool IsFinished { get; }
	void Start();
	void Pause();
	void SetVolume(float volume);
	void SetPitch(float pitch);
	void StopWithFade(float seconds);
}

/// <summary>Thread-owned MiniAudio mixer. Public methods never call MiniAudio directly.</summary>
public sealed class AudioService : IAudioService, IAudioRuntime, IDisposable
{
	public const int DefaultVoiceCount = 128;
	private const int ReservedMusicVoices = 2;
	private const int Empty = 0, Pending = 1, Playing = 2, Paused = 3;
	private readonly object _slotsLock = new();
	private readonly IAudioContentProvider _content;
	private readonly VoiceSlot[] _voices;
	private readonly Dictionary<Guid, byte[]> _preloaded = [];
	private readonly ConcurrentQueue<Action> _commands = new();
	private readonly AutoResetEvent _commandSignal = new(false);
	private readonly ManualResetEventSlim _started = new(false);
	private readonly Thread _thread;
	private readonly Func<IAudioBackend> _backendFactory;
	private volatile bool _acceptingCommands = true;
	private volatile bool _available;
	private IAudioBackend? _engine;
	private AudioPlaybackHandle _currentMusic = AudioPlaybackHandle.Invalid;
	private AudioPlaybackHandle _outgoingMusic = AudioPlaybackHandle.Invalid;
	private DateTime _crossfadeStartUtc;
	private float _crossfadeSeconds;
	private float _incomingMusicVolume;

	public AudioService(IAudioContentProvider content, int voiceCount = DefaultVoiceCount)
		: this(content, voiceCount, static () => new MiniAudioEngine())
	{
	}

	internal AudioService(IAudioContentProvider content, int voiceCount, Func<IAudioBackend> backendFactory)
	{
		_content = content ?? throw new ArgumentNullException(nameof(content));
		_backendFactory = backendFactory ?? throw new ArgumentNullException(nameof(backendFactory));
		if (voiceCount < ReservedMusicVoices + 1) throw new ArgumentOutOfRangeException(nameof(voiceCount));
		_voices = Enumerable.Range(0, voiceCount).Select(_ => new VoiceSlot()).ToArray();
		_thread = new Thread(AudioLoop) { IsBackground = true, Name = "WolfEngine Audio" };
		_thread.Start();
		_started.Wait();
	}

	public bool IsAvailable => _available && _acceptingCommands;

	public AudioPlaybackHandle PlaySfx(AssetRef<AudioClip> clip, AudioPlaybackOptions? options = null)
	{
		options ??= new AudioPlaybackOptions();
		return ReserveAndQueue(clip.NodeId, AudioBus.Sfx, options.Volume, options.Pitch, options.Loop, options.Priority,
			false, TimeSpan.Zero);
	}

	public AudioPlaybackHandle PlayMusic(AssetRef<AudioClip> clip, AudioMusicOptions? options = null)
	{
		options ??= new AudioMusicOptions();
		return ReserveAndQueue(clip.NodeId, AudioBus.Music, options.Volume, options.Pitch, options.Loop, int.MaxValue,
			true, options.CrossfadeDuration);
	}

	public void Stop(AudioPlaybackHandle handle) => Enqueue(() => StopOnAudioThread(handle));

	public void Pause(AudioPlaybackHandle handle) => Enqueue(() =>
	{
		if (TryGet(handle, out var slot) && slot.Native is not null)
		{
			slot.Native.Pause();
			Volatile.Write(ref slot.State, Paused);
		}
	});

	public void Resume(AudioPlaybackHandle handle) => Enqueue(() =>
	{
		if (TryGet(handle, out var slot) && slot.Native is not null)
		{
			slot.Native.Start();
			Volatile.Write(ref slot.State, Playing);
		}
	});

	public bool IsPlaying(AudioPlaybackHandle handle) =>
		TryGet(handle, out var slot) && Volatile.Read(ref slot.State) == Playing;

	public void SetVolume(AudioPlaybackHandle handle, float volume) => Enqueue(() =>
	{
		if (TryGet(handle, out var slot) && slot.Native is not null)
		{
			slot.Volume = ClampVolume(volume);
			slot.Native.SetVolume(slot.Volume);
		}
	});

	public void SetPitch(AudioPlaybackHandle handle, float pitch) => Enqueue(() =>
	{
		if (TryGet(handle, out var slot)) slot.Native?.SetPitch(ClampPitch(pitch));
	});

	public void StopMusic(TimeSpan? fadeDuration = null) => Enqueue(() =>
		StopMusicOnAudioThread((float)(fadeDuration ?? TimeSpan.Zero).TotalSeconds));

	public void StopBus(AudioBus bus) => Enqueue(() =>
	{
		foreach (var slot in _voices)
			if (slot.Bus == bus)
				Release(slot);
	});

	public void SetBusVolume(AudioBus bus, float volume) =>
		Enqueue(() => _engine?.SetBusVolume(bus, ClampVolume(volume)));

	public void SetMasterVolume(float volume) => Enqueue(() => _engine?.SetMasterVolume(ClampVolume(volume)));

	public void Update(float deltaTime)
	{
	} // The dedicated loop owns cleanup and fades.

	public void PauseAll() => Enqueue(() =>
	{
		foreach (var slot in _voices)
			if (slot.Native is not null)
			{
				slot.Native.Pause();
				Volatile.Write(ref slot.State, Paused);
			}
	});

	public void ResumeAll() => Enqueue(() =>
	{
		foreach (var slot in _voices)
			if (slot.Native is not null && Volatile.Read(ref slot.State) == Paused)
			{
				slot.Native.Start();
				Volatile.Write(ref slot.State, Playing);
			}
	});

	public void StopAll() => Enqueue(() =>
	{
		foreach (var slot in _voices) Release(slot);
		_currentMusic = _outgoingMusic = AudioPlaybackHandle.Invalid;
	});

	public async Task PreloadAsync(AssetRef<AudioClip> clip, CancellationToken cancellationToken = default)
	{
		if (clip.NodeId == Guid.Empty) return;
		lock (_slotsLock)
			if (_preloaded.ContainsKey(clip.NodeId))
				return;
		var bytes = await Task.Run<byte[]?>(() =>
		{
			using var opened = _content.Open(clip.NodeId);
			// Streaming assets intentionally remain range-backed; preloading them would defeat music streaming.
			if (opened.Header.StorageMode != AudioStorageMode.Predecoded) return null;
			using var artifact = new MemoryStream();
			AudioArtifact.WriteTo(artifact, opened.Header, opened.Payload);
			return artifact.ToArray();
		}, cancellationToken).ConfigureAwait(false);
		if (bytes is not null)
			lock (_slotsLock)
				_preloaded[clip.NodeId] = bytes;
	}

	public void Unload(AssetRef<AudioClip> clip)
	{
		lock (_slotsLock) _preloaded.Remove(clip.NodeId);
	}

	public void Dispose()
	{
		if (!_acceptingCommands) return;
		_acceptingCommands = false;
		_commandSignal.Set();
		_thread.Join();
		_commandSignal.Dispose();
		_started.Dispose();
	}

	private AudioPlaybackHandle ReserveAndQueue(Guid assetId, AudioBus bus, float volume, float pitch, bool loop,
		int priority, bool music, TimeSpan crossfade)
	{
		if (!IsAvailable || assetId == Guid.Empty) return AudioPlaybackHandle.Invalid;
		AudioPlaybackHandle handle;
		lock (_slotsLock)
		{
			var index = FindSlot(bus, priority);
			if (index < 0) return AudioPlaybackHandle.Invalid;
			var slot = _voices[index];
			slot.Generation = NextGeneration(slot.Generation);
			slot.AssetId = assetId;
			slot.Bus = bus;
			slot.Priority = priority;
			slot.Volume = ClampVolume(volume);
			slot.Pitch = ClampPitch(pitch);
			slot.Loop = loop;
			slot.StopAfterUtc = null;
			slot.EndUtc = null;
			slot.Sequence = DateTime.UtcNow.Ticks;
			Volatile.Write(ref slot.State, Pending);
			handle = new AudioPlaybackHandle(index, slot.Generation);
		}

		Enqueue(() => StartOnAudioThread(handle, music, crossfade));
		return handle;
	}

	private void StartOnAudioThread(AudioPlaybackHandle handle, bool music, TimeSpan crossfade)
	{
		if (!TryGet(handle, out var slot) || Volatile.Read(ref slot.State) != Pending || _engine is null) return;
		ReleaseNativeOnly(slot); // releases a stolen voice after its handle was invalidated by reservation.
		try
		{
			AudioContentStream content;
			lock (_slotsLock)
			{
				content = _preloaded.TryGetValue(slot.AssetId, out var cached)
					? AudioArtifact.Open(new MemoryStream(cached, writable: false))
					: _content.Open(slot.AssetId);
			}

			var startVolume = music && IsHandleActive(_currentMusic) && crossfade > TimeSpan.Zero ? 0 : slot.Volume;
			slot.Native = _engine.CreateVoice(content, slot.Bus, slot.Loop, startVolume, slot.Pitch);
			// Do not poll ma_sound_at_end immediately after start. With a custom streaming
			// data source it can report completion before the mixer has consumed its first
			// buffer, which truncates one-shots to a chirp. Cooking records duration, so it
			// is a stable lifetime source for non-looping voices.
			slot.EndUtc = !slot.Loop && content.Header.DurationSeconds > 0
				? DateTime.UtcNow.AddSeconds(content.Header.DurationSeconds / slot.Pitch)
				: null;
			slot.Native.Start();
			Volatile.Write(ref slot.State, Playing);
			if (music) BeginMusicCrossfade(handle, crossfade);
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Failed to play audio asset '{slot.AssetId:D}': {ex.Message}");
			Release(slot);
		}
	}

	private void BeginMusicCrossfade(AudioPlaybackHandle next, TimeSpan duration)
	{
		if (IsHandleActive(_outgoingMusic)) StopOnAudioThread(_outgoingMusic);
		_outgoingMusic = IsHandleActive(_currentMusic) ? _currentMusic : AudioPlaybackHandle.Invalid;
		_currentMusic = next;
		_crossfadeSeconds = Math.Max(0, (float)duration.TotalSeconds);
		_crossfadeStartUtc = DateTime.UtcNow;
		_incomingMusicVolume = TryGet(next, out var incoming) ? incoming.Volume : 1;
		if (_crossfadeSeconds <= 0 || !IsHandleActive(_outgoingMusic))
		{
			if (TryGet(next, out var solo)) solo.Native?.SetVolume(_incomingMusicVolume);
			if (IsHandleActive(_outgoingMusic)) StopOnAudioThread(_outgoingMusic);
			_outgoingMusic = AudioPlaybackHandle.Invalid;
		}
	}

	private void StopMusicOnAudioThread(float fadeSeconds)
	{
		if (!IsHandleActive(_currentMusic)) return;
		if (fadeSeconds <= 0) StopOnAudioThread(_currentMusic);
		else if (TryGet(_currentMusic, out var slot))
		{
			slot.Native?.StopWithFade(fadeSeconds);
			slot.StopAfterUtc = DateTime.UtcNow.AddSeconds(fadeSeconds);
		}

		_currentMusic = AudioPlaybackHandle.Invalid;
	}

	private void StopOnAudioThread(AudioPlaybackHandle handle)
	{
		if (TryGet(handle, out var slot)) Release(slot);
		if (_currentMusic == handle) _currentMusic = AudioPlaybackHandle.Invalid;
		if (_outgoingMusic == handle) _outgoingMusic = AudioPlaybackHandle.Invalid;
	}

	private void AudioLoop()
	{
		try
		{
			_engine = _backendFactory();
			_available = true;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Audio output is unavailable: {ex.Message}");
		}
		finally
		{
			_started.Set();
		}

		while (_acceptingCommands)
		{
			_commandSignal.WaitOne(5);
			while (_commands.TryDequeue(out var command)) command();
			TickOnAudioThread();
		}

		while (_commands.TryDequeue(out var command)) command();
		foreach (var slot in _voices) Release(slot);
		_engine?.Dispose();
		_available = false;
	}

	private void TickOnAudioThread()
	{
		foreach (var slot in _voices)
		{
			if ((slot.EndUtc is { } endAt && DateTime.UtcNow >= endAt) ||
			    (slot.StopAfterUtc is { } stopAt && DateTime.UtcNow >= stopAt))
				Release(slot);
		}

		if (_crossfadeSeconds <= 0 || !IsHandleActive(_currentMusic))
		{
			return;
		}
		var t = Math.Clamp((float)(DateTime.UtcNow - _crossfadeStartUtc).TotalSeconds / _crossfadeSeconds, 0, 1);
		if (TryGet(_currentMusic, out var incoming))
		{
			incoming.Native?.SetVolume(_incomingMusicVolume * t);
		}

		if (TryGet(_outgoingMusic, out var outgoing))
		{
			outgoing.Native?.SetVolume(outgoing.Volume * (1 - t));
		}

		if (t < 1)
		{
			return;
		}
		StopOnAudioThread(_outgoingMusic);
		_outgoingMusic = AudioPlaybackHandle.Invalid;
		_crossfadeSeconds = 0;
	}

	private int FindSlot(AudioBus bus, int priority)
	{
		var start = bus == AudioBus.Music ? _voices.Length - ReservedMusicVoices : 0;
		var end = bus == AudioBus.Music ? _voices.Length : _voices.Length - ReservedMusicVoices;
		for (var i = start; i < end; i++)
			if (Volatile.Read(ref _voices[i].State) == Empty)
				return i;
		if (bus == AudioBus.Music) return -1;
		return Enumerable.Range(start, end - start).Where(i => _voices[i].Priority <= priority)
			.OrderBy(i => _voices[i].Priority).ThenBy(i => _voices[i].Sequence).FirstOrDefault(-1);
	}

	private bool TryGet(AudioPlaybackHandle handle, out VoiceSlot slot)
	{
		slot = null!;
		if (!handle.IsValid || handle.Slot >= _voices.Length) return false;
		var candidate = _voices[handle.Slot];
		if (candidate.Generation != handle.Generation || Volatile.Read(ref candidate.State) == Empty) return false;
		slot = candidate;
		return true;
	}

	private bool IsHandleActive(AudioPlaybackHandle handle) => TryGet(handle, out _);

	private static void ReleaseNativeOnly(VoiceSlot slot)
	{
		slot.Native?.Dispose();
		slot.Native = null;
	}

	private static void Release(VoiceSlot slot)
	{
		ReleaseNativeOnly(slot);
		slot.AssetId = Guid.Empty;
		slot.StopAfterUtc = null;
		slot.EndUtc = null;
		Volatile.Write(ref slot.State, Empty);
	}

	private void Enqueue(Action action)
	{
		if (!_acceptingCommands) return;
		_commands.Enqueue(action);
		_commandSignal.Set();
	}

	private static uint NextGeneration(uint current) => current == uint.MaxValue ? 1 : current + 1;
	private static float ClampVolume(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 4) : 1;
	private static float ClampPitch(float value) => float.IsFinite(value) ? Math.Clamp(value, 0.125f, 8) : 1;

	private sealed class VoiceSlot
	{
		public uint Generation;
		public int State;
		public Guid AssetId;
		public AudioBus Bus;
		public int Priority;
		public long Sequence;
		public float Volume;
		public float Pitch;
		public bool Loop;
		public DateTime? StopAfterUtc;
		public DateTime? EndUtc;
		public IAudioBackendVoice? Native;
	}
}

internal sealed unsafe class MiniAudioEngine : IAudioBackend
{
	private ma_engine* _engine;
	private ma_sound* _sfxGroup;
	private ma_sound* _musicGroup;

	public MiniAudioEngine()
	{
		_engine = (ma_engine*)NativeMemory.AllocZeroed((nuint)sizeof(ma_engine));
		Ensure(ma.engine_init(null, _engine), "initialize audio engine");
		try
		{
			_sfxGroup = CreateGroup();
			_musicGroup = CreateGroup();
		}
		catch
		{
			Dispose();
			throw;
		}
	}

	public IAudioBackendVoice CreateVoice(AudioContentStream content, AudioBus bus, bool loop, float volume,
		float pitch) => new MiniAudioVoice(_engine, bus == AudioBus.Sfx ? _sfxGroup : _musicGroup, content, loop,
		volume, pitch);

	public void SetBusVolume(AudioBus bus, float volume) =>
		ma.sound_group_set_volume(bus == AudioBus.Sfx ? _sfxGroup : _musicGroup, volume);

	public void SetMasterVolume(float volume) => Ensure(ma.engine_set_volume(_engine, volume), "set master volume");

	private ma_sound* CreateGroup()
	{
		var group = (ma_sound*)NativeMemory.AllocZeroed((nuint)sizeof(ma_sound));
		var result = ma.sound_group_init(_engine, 0, null, group);
		if (result != ma_result.MA_SUCCESS)
		{
			NativeMemory.Free(group);
			Ensure(result, "create audio bus");
		}

		return group;
	}

	public void Dispose()
	{
		if (_musicGroup is not null)
		{
			ma.sound_group_uninit(_musicGroup);
			NativeMemory.Free(_musicGroup);
			_musicGroup = null;
		}

		if (_sfxGroup is not null)
		{
			ma.sound_group_uninit(_sfxGroup);
			NativeMemory.Free(_sfxGroup);
			_sfxGroup = null;
		}

		if (_engine is not null)
		{
			ma.engine_uninit(_engine);
			NativeMemory.Free(_engine);
			_engine = null;
		}
	}

	internal static void Ensure(ma_result result, string action)
	{
		if (result != ma_result.MA_SUCCESS)
			throw new InvalidOperationException($"MiniAudio failed to {action} ({result}).");
	}
}

internal sealed unsafe class MiniAudioVoice : IAudioBackendVoice
{
	private ma_sound* _sound;
	private ma_decoder* _decoder;
	private GCHandle _stateHandle;
	private GCHandle _payloadHandle;
	private byte[]? _payloadBytes;
	private AudioContentStream? _content;

	public MiniAudioVoice(ma_engine* engine, ma_sound* group, AudioContentStream content, bool loop, float volume,
		float pitch)
	{
		_content = content;
		_decoder = (ma_decoder*)NativeMemory.AllocZeroed((nuint)sizeof(ma_decoder));
		_sound = (ma_sound*)NativeMemory.AllocZeroed((nuint)sizeof(ma_sound));
		try
		{
			var config = ma.decoder_config_init_default();
			if (content.Header.StorageMode == AudioStorageMode.Predecoded)
			{
				// Cooked SFX are PCM WAV. Give MiniAudio a stable buffer directly instead of
				// using managed read callbacks on the real-time playback path.
				_payloadBytes = ReadAllBytes(content.Payload);
				_payloadHandle = GCHandle.Alloc(_payloadBytes, GCHandleType.Pinned);
				MiniAudioEngine.Ensure(
					ma.decoder_init_memory(_payloadHandle.AddrOfPinnedObject().ToPointer(), (nuint)_payloadBytes.Length,
						&config, _decoder), "initialize SFX decoder");
			}
			else
			{
				_stateHandle = GCHandle.Alloc(new StreamDecoderState(content.Payload));
				MiniAudioEngine.Ensure(
					ma.decoder_init(&Read, &Seek, (void*)GCHandle.ToIntPtr(_stateHandle), &config, _decoder),
					"initialize stream decoder");
			}
			MiniAudioEngine.Ensure(
				ma.sound_init_from_data_source(engine, _decoder, (uint)ma_sound_flags.MA_SOUND_FLAG_NO_SPATIALIZATION,
					group, _sound), "create sound");
			ma.sound_set_spatialization_enabled(_sound, 0);
			ma.sound_set_looping(_sound, loop ? 1u : 0u);
			SetVolume(volume);
			SetPitch(pitch);
		}
		catch
		{
			Dispose();
			throw;
		}
	}

	public bool IsFinished => _sound is not null && ma.sound_at_end(_sound) != 0;

	public void Start()
	{
		if (_sound is not null) MiniAudioEngine.Ensure(ma.sound_start(_sound), "start sound");
	}

	public void Pause()
	{
		if (_sound is not null) MiniAudioEngine.Ensure(ma.sound_stop(_sound), "pause sound");
	}

	public void SetVolume(float volume)
	{
		if (_sound is not null) ma.sound_set_volume(_sound, volume);
	}

	public void SetPitch(float pitch)
	{
		if (_sound is not null) ma.sound_set_pitch(_sound, pitch);
	}

	public void StopWithFade(float seconds)
	{
		if (_sound is not null)
			MiniAudioEngine.Ensure(ma.sound_stop_with_fade_in_milliseconds(_sound, checked((ulong)(seconds * 1000))),
				"fade sound");
	}

	public void Dispose()
	{
		if (_sound is not null)
		{
			ma.sound_uninit(_sound);
			NativeMemory.Free(_sound);
			_sound = null;
		}

		if (_decoder is not null)
		{
			if (_decoder->pBackend is not null) ma.decoder_uninit(_decoder);
			NativeMemory.Free(_decoder);
			_decoder = null;
		}

		if (_stateHandle.IsAllocated) _stateHandle.Free();
		if (_payloadHandle.IsAllocated) _payloadHandle.Free();
		_payloadBytes = null;
		_content?.Dispose();
		_content = null;
	}

	private static byte[] ReadAllBytes(Stream stream)
	{
		using var memory = new MemoryStream();
		stream.CopyTo(memory);
		return memory.ToArray();
	}

	[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
	private static ma_result Read(ma_decoder* decoder, void* output, nuint bytesToRead, nuint* bytesRead)
	{
		try
		{
			var state = (StreamDecoderState)GCHandle.FromIntPtr((nint)decoder->pUserData).Target!;
			var total = 0;
			var remaining = bytesToRead;
			while (remaining > 0)
			{
				var amount = checked((int)Math.Min(remaining, (nuint)int.MaxValue));
				var read = state.Stream.Read(new Span<byte>((byte*)output + total, amount));
				if (read == 0) break;
				total += read;
				remaining -= (nuint)read;
			}

			*bytesRead = (nuint)total;
			return total == 0 ? ma_result.MA_AT_END : ma_result.MA_SUCCESS;
		}
		catch
		{
			*bytesRead = 0;
			return ma_result.MA_IO_ERROR;
		}
	}

	[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
	private static ma_result Seek(ma_decoder* decoder, long offset, ma_seek_origin origin)
	{
		try
		{
			var state = (StreamDecoderState)GCHandle.FromIntPtr((nint)decoder->pUserData).Target!;
			state.Stream.Seek(offset, (int)origin == 0 ? SeekOrigin.Begin : SeekOrigin.Current);
			return ma_result.MA_SUCCESS;
		}
		catch
		{
			return ma_result.MA_BAD_SEEK;
		}
	}

	private sealed record StreamDecoderState(Stream Stream);
}
