using WolfEngine.Audio;
using WolfEngine.AssetPipeline;
using System.Security.Cryptography;

namespace WolfEngine.Audio.Tests;

public sealed class AudioPipelineTests
{
	private string _root = null!;

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), "WolfEngineAudioTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_root);
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(_root)) Directory.Delete(_root, true);
	}

	[Test]
	public void Cooker_ProbesAndConvertsSfxToPcm16At48Khz()
	{
		var source = Path.Combine(_root, "tone.wav");
		var artifact = Path.Combine(_root, "tone.wolfaudio");
		WritePcmWave(source, 24000, 1, 2400);

		var result = AudioCooker.Cook(source, artifact, new AudioImportSettings { Usage = AudioUsage.Sfx });
		using var opened = AudioArtifact.Open(artifact);

		Assert.Multiple(() =>
		{
			Assert.That(result.Summary.StorageMode, Is.EqualTo(AudioStorageMode.Predecoded));
			Assert.That(result.Summary.SampleRate, Is.EqualTo(48000));
			Assert.That(result.Summary.Channels, Is.EqualTo(1));
			Assert.That(opened.Header.Codec, Is.EqualTo("wav-pcm16"));
		});
		var riff = new byte[4];
		opened.Payload.ReadExactly(riff);
		Assert.That(riff, Is.EqualTo("RIFF"u8.ToArray()));
	}

	[Test]
	public void Cooker_PreservesStreamingSourcePayload()
	{
		var source = Path.Combine(_root, "music.wav");
		var artifact = Path.Combine(_root, "music.wolfaudio");
		WritePcmWave(source, 44100, 2, 4410);
		var expected = File.ReadAllBytes(source);

		var result = AudioCooker.Cook(source, artifact, new AudioImportSettings { Usage = AudioUsage.Music });
		using var opened = AudioArtifact.Open(artifact);
		using var payload = new MemoryStream();
		opened.Payload.CopyTo(payload);

		Assert.That(result.Summary.StorageMode, Is.EqualTo(AudioStorageMode.Streaming));
		Assert.That(payload.ToArray(), Is.EqualTo(expected));
	}

	[Test]
	public void Cooker_AutoUsesSfxBelowTenSecondsAndMusicAtTheBoundary()
	{
		var shortSource = Path.Combine(_root, "short.wav");
		var boundarySource = Path.Combine(_root, "boundary.wav");
		WritePcmWave(shortSource, 48000, 2, 479_999);
		WritePcmWave(boundarySource, 48000, 2, 480_000);

		var shortResult = AudioCooker.Cook(shortSource, Path.Combine(_root, "short.wolfaudio"), new AudioImportSettings());
		var boundaryResult = AudioCooker.Cook(boundarySource, Path.Combine(_root, "boundary.wolfaudio"), new AudioImportSettings());

		Assert.Multiple(() =>
		{
			Assert.That(shortResult.Summary.StorageMode, Is.EqualTo(AudioStorageMode.Predecoded));
			Assert.That(shortResult.Summary.Channels, Is.EqualTo(2));
			Assert.That(boundaryResult.Summary.StorageMode, Is.EqualTo(AudioStorageMode.Streaming));
		});
	}

	[Test]
	public void Cooker_ProducesDeterministicArtifacts()
	{
		var source = Path.Combine(_root, "tone.wav");
		var first = Path.Combine(_root, "first.wolfaudio");
		var second = Path.Combine(_root, "second.wolfaudio");
		WritePcmWave(source, 44100, 2, 4410);
		AudioCooker.Cook(source, first, new AudioImportSettings { Usage = AudioUsage.Sfx });
		AudioCooker.Cook(source, second, new AudioImportSettings { Usage = AudioUsage.Sfx });

		Assert.That(SHA256.HashData(File.ReadAllBytes(first)), Is.EqualTo(SHA256.HashData(File.ReadAllBytes(second))));
	}

	[Test]
	public void Artifact_RejectsInvalidHeader()
	{
		Assert.Throws<InvalidDataException>(() => AudioArtifact.Open(new MemoryStream("bad audio"u8.ToArray())));
	}

	[TestCase(".aac")]
	[TestCase(".txt")]
	public void SupportedSources_RejectUnknownExtensions(string extension)
	{
		Assert.That(AudioAssetConstants.IsSupportedSource("clip" + extension), Is.False);
	}

	[Test]
	public async Task Service_UsesDedicatedThreadAndInvalidatesStolenSfxHandles()
	{
		var backend = new FakeBackend();
		using var service = new AudioService(new TestContentProvider(), 3, () => backend);
		var first = service.PlaySfx(Clip(Guid.NewGuid()), new AudioPlaybackOptions { Priority = 1 });
		await Eventually(() => backend.VoiceCount == 1);
		var second = service.PlaySfx(Clip(Guid.NewGuid()), new AudioPlaybackOptions { Priority = 1 });
		await Eventually(() => backend.VoiceCount == 2);

		Assert.Multiple(() =>
		{
			Assert.That(service.IsPlaying(first), Is.False, "stolen handles must become harmless");
			Assert.That(service.IsPlaying(second), Is.True);
			Assert.That(backend.VoiceCreateThreadIds.Distinct().Count(), Is.EqualTo(1));
			Assert.That(backend.VoiceCreateThreadIds.Distinct().Single(), Is.Not.EqualTo(Environment.CurrentManagedThreadId));
		});
	}

	[Test]
	public async Task Service_ReservesMusicVoicesAndCrossfadesWithoutStealingThem()
	{
		var backend = new FakeBackend();
		using var service = new AudioService(new TestContentProvider(), 4, () => backend);
		var firstMusic = service.PlayMusic(Clip(Guid.NewGuid()), new AudioMusicOptions { CrossfadeDuration = TimeSpan.FromMilliseconds(100) });
		await Eventually(() => service.IsPlaying(firstMusic));
		var secondMusic = service.PlayMusic(Clip(Guid.NewGuid()), new AudioMusicOptions { CrossfadeDuration = TimeSpan.FromMilliseconds(100) });
		await Eventually(() => backend.VoiceCount == 2);
		var sfx = service.PlaySfx(Clip(Guid.NewGuid()));
		await Eventually(() => service.IsPlaying(sfx));
		await Eventually(() => !service.IsPlaying(firstMusic), timeoutMilliseconds: 1000);

		Assert.Multiple(() =>
		{
			Assert.That(service.IsPlaying(secondMusic), Is.True);
			Assert.That(backend.Voices[0].Disposed, Is.True, "outgoing music is released after the fade");
			Assert.That(backend.Voices[1].Disposed, Is.False, "SFX must not steal a music voice");
		});
	}

	[Test]
	public async Task Service_QueuesBusAndMasterControlsOnAudioThread()
	{
		var backend = new FakeBackend();
		using var service = new AudioService(new TestContentProvider(), 3, () => backend);
		service.SetMasterVolume(0.25f);
		service.SetBusVolume(AudioBus.Sfx, 0.5f);
		await Eventually(() => backend.MasterVolume == 0.25f && backend.SfxVolume == 0.5f);
		Assert.That(backend.ThreadIds.All(id => id != Environment.CurrentManagedThreadId), Is.True);
	}

	[Test]
	public async Task Service_PreloadsOnlyPredecodedClips()
	{
		var sfxProvider = new TestContentProvider(AudioStorageMode.Predecoded);
		var backend = new FakeBackend();
		using var service = new AudioService(sfxProvider, 3, () => backend);
		var sfx = Clip(Guid.NewGuid());
		await service.PreloadAsync(sfx);
		service.PlaySfx(sfx);
		await Eventually(() => backend.VoiceCount == 1);
		Assert.That(sfxProvider.OpenCount, Is.EqualTo(1), "the preloaded SFX must not perform another content open");

		var musicProvider = new TestContentProvider(AudioStorageMode.Streaming);
		var musicBackend = new FakeBackend();
		using var musicService = new AudioService(musicProvider, 3, () => musicBackend);
		var music = Clip(Guid.NewGuid());
		await musicService.PreloadAsync(music);
		musicService.PlayMusic(music);
		await Eventually(() => musicBackend.VoiceCount == 1);
		Assert.That(musicProvider.OpenCount, Is.EqualTo(2), "streamed music must reopen a range-backed source for playback");
	}

	[Test]
	public void Service_DegradesToUnavailableWhenBackendInitializationFails()
	{
		using var service = new AudioService(new TestContentProvider(), 3, static () => throw new InvalidOperationException("no device"));
		Assert.That(service.IsAvailable, Is.False);
		Assert.That(service.PlaySfx(Clip(Guid.NewGuid())), Is.EqualTo(AudioPlaybackHandle.Invalid));
	}

	private static void WritePcmWave(string path, int sampleRate, int channels, int frames)
	{
		var dataLength = frames * channels * sizeof(short);
		using var stream = File.Create(path);
		using var writer = new BinaryWriter(stream);
		writer.Write("RIFF"u8); writer.Write(36 + dataLength); writer.Write("WAVE"u8);
		writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)channels);
		writer.Write(sampleRate); writer.Write(sampleRate * channels * sizeof(short));
		writer.Write((short)(channels * sizeof(short))); writer.Write((short)16);
		writer.Write("data"u8); writer.Write(dataLength);
		for (var i = 0; i < frames * channels; i++) writer.Write((short)(Math.Sin(i * 0.05) * short.MaxValue * 0.1));
	}

	private static AssetRef<AudioClip> Clip(Guid id) => new() { NodeId = id };

	private static async Task Eventually(Func<bool> condition, int timeoutMilliseconds = 500)
	{
		var until = Environment.TickCount64 + timeoutMilliseconds;
		while (!condition() && Environment.TickCount64 < until) await Task.Delay(5);
		Assert.That(condition(), Is.True, "Timed out waiting for the audio thread.");
	}

	private sealed class TestContentProvider(AudioStorageMode storageMode = AudioStorageMode.Predecoded) : IAudioContentProvider
	{
		private int _openCount;
		public int OpenCount => Volatile.Read(ref _openCount);
		public AudioContentStream Open(Guid assetId)
		{
			Interlocked.Increment(ref _openCount);
			return new()
		{
			Header = new AudioArtifactHeader { Codec = "wav-pcm16", StorageMode = storageMode, Channels = 1, SampleRate = 48000 },
			Payload = new MemoryStream("fake"u8.ToArray())
		};
		}
	}

	private sealed class FakeBackend : IAudioBackend
	{
		private readonly object _lock = new();
		public List<FakeVoice> Voices { get; } = [];
		public List<int> ThreadIds { get; } = [];
		public List<int> VoiceCreateThreadIds { get; } = [];
		public int VoiceCount { get { lock (_lock) return Voices.Count; } }
		public float MasterVolume { get; private set; }
		public float SfxVolume { get; private set; }
		public IAudioBackendVoice CreateVoice(AudioContentStream content, AudioBus bus, bool loop, float volume, float pitch)
		{
			TrackThread();
			lock (_lock) VoiceCreateThreadIds.Add(Environment.CurrentManagedThreadId);
			var voice = new FakeVoice(content);
			lock (_lock) Voices.Add(voice);
			return voice;
		}
		public void SetBusVolume(AudioBus bus, float volume) { TrackThread(); if (bus == AudioBus.Sfx) SfxVolume = volume; }
		public void SetMasterVolume(float volume) { TrackThread(); MasterVolume = volume; }
		public void Dispose() => TrackThread();
		private void TrackThread() { lock (_lock) ThreadIds.Add(Environment.CurrentManagedThreadId); }
	}

	private sealed class FakeVoice(AudioContentStream content) : IAudioBackendVoice
	{
		public bool Disposed { get; private set; }
		public bool IsFinished => false;
		public void Start() { }
		public void Pause() { }
		public void SetVolume(float volume) { }
		public void SetPitch(float pitch) { }
		public void StopWithFade(float seconds) { }
		public void Dispose() { Disposed = true; content.Dispose(); }
	}
}
