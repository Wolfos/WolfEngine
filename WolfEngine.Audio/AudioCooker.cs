using System.Runtime.InteropServices;
using Miniaudio;

namespace WolfEngine.Audio;

public readonly record struct AudioCookResult(AudioClipSummary Summary, AudioArtifactHeader Header);

public static unsafe class AudioCooker
{
	public static AudioCookResult Cook(string sourcePath, string artifactPath, AudioImportSettings settings)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
		ArgumentNullException.ThrowIfNull(settings);
		if (!AudioAssetConstants.IsSupportedSource(sourcePath))
			throw new InvalidDataException($"Unsupported audio source '{Path.GetExtension(sourcePath)}'.");

		var probe = Probe(sourcePath);
		if (probe.Channels is < 1 or > 2)
			throw new InvalidDataException("The first audio pipeline supports mono and stereo sources only.");
		var mode = settings.Usage switch
		{
			AudioUsage.Sfx => AudioStorageMode.Predecoded,
			AudioUsage.Music => AudioStorageMode.Streaming,
			_ => probe.DurationSeconds < AudioAssetConstants.AutoMusicThresholdSeconds
				? AudioStorageMode.Predecoded
				: AudioStorageMode.Streaming
		};

		using Stream payload = mode == AudioStorageMode.Predecoded
			? DecodePcm16Wave(sourcePath)
			: new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
		var header = new AudioArtifactHeader
		{
			Codec = mode == AudioStorageMode.Predecoded ? "wav-pcm16" : probe.Codec,
			StorageMode = mode,
			Channels = probe.Channels,
			SampleRate = mode == AudioStorageMode.Predecoded ? 48000 : probe.SampleRate,
			FrameCount = mode == AudioStorageMode.Predecoded
				? GetWaveFrameCount(payload, probe.Channels)
				: probe.FrameCount,
			DurationSeconds = probe.DurationSeconds
		};
		payload.Position = 0;
		AudioArtifact.Write(artifactPath, header, payload);
		return new AudioCookResult(
			new AudioClipSummary
			{
				Codec = header.Codec,
				DurationSeconds = header.DurationSeconds,
				Channels = header.Channels,
				SampleRate = header.SampleRate,
				FrameCount = header.FrameCount,
				RequestedUsage = settings.Usage,
				StorageMode = mode
			}, header);
	}

	public static AudioClipSummary Probe(string sourcePath)
	{
		var sourceBytes = File.ReadAllBytes(sourcePath);
		var sourceHandle = GCHandle.Alloc(sourceBytes, GCHandleType.Pinned);
		ma_decoder* decoder = null;
		try
		{
			decoder = (ma_decoder*)NativeMemory.AllocZeroed((nuint)sizeof(ma_decoder));
			var config = ma.decoder_config_init_default();
			EnsureSuccess(ma.decoder_init_memory(sourceHandle.AddrOfPinnedObject().ToPointer(), (nuint)sourceBytes.Length, &config, decoder), sourcePath);
			ma_format format;
			uint channels;
			uint sampleRate;
			EnsureSuccess(ma.decoder_get_data_format(decoder, &format, &channels, &sampleRate, null, 0), sourcePath);
			ulong frames;
			EnsureSuccess(ma.decoder_get_length_in_pcm_frames(decoder, &frames), sourcePath);
			return new AudioClipSummary
			{
				Codec = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant(),
				DurationSeconds = sampleRate == 0 ? 0 : (double)frames / sampleRate,
				Channels = checked((int)channels),
				SampleRate = checked((int)sampleRate),
				FrameCount = checked((long)frames)
			};
		}
		finally
		{
			if (decoder is not null)
			{
				if (decoder->pBackend is not null) ma.decoder_uninit(decoder);
				NativeMemory.Free(decoder);
			}
			sourceHandle.Free();
		}
	}

	private static MemoryStream DecodePcm16Wave(string sourcePath)
	{
		var sourceBytes = File.ReadAllBytes(sourcePath);
		var sourceHandle = GCHandle.Alloc(sourceBytes, GCHandleType.Pinned);
		ma_decoder* decoder = null;
		try
		{
			decoder = (ma_decoder*)NativeMemory.AllocZeroed((nuint)sizeof(ma_decoder));
			var config = ma.decoder_config_init(ma_format.ma_format_s16, 0, 48000);
			EnsureSuccess(ma.decoder_init_memory(sourceHandle.AddrOfPinnedObject().ToPointer(), (nuint)sourceBytes.Length, &config, decoder), sourcePath);
			ma_format format;
			uint channels;
			uint sampleRate;
			EnsureSuccess(ma.decoder_get_data_format(decoder, &format, &channels, &sampleRate, null, 0), sourcePath);
			if (channels is < 1 or > 2) throw new InvalidDataException("Only mono and stereo audio can be cooked.");

			var output = new MemoryStream();
			output.Position = 44;
			const int chunkFrames = 4096;
			var buffer = new byte[chunkFrames * checked((int)channels) * sizeof(short)];
			fixed (byte* destination = buffer)
			{
				while (true)
				{
					ulong read;
					var result = ma.decoder_read_pcm_frames(decoder, destination, chunkFrames, &read);
					if (read > 0) output.Write(buffer, 0, checked((int)read * (int)channels * sizeof(short)));
					if (result == ma_result.MA_AT_END || read == 0) break;
					EnsureSuccess(result, sourcePath);
				}
			}
			WriteWaveHeader(output, checked((int)channels), checked((int)sampleRate));
			output.Position = 0;
			return output;
		}
		finally
		{
			if (decoder is not null)
			{
				if (decoder->pBackend is not null) ma.decoder_uninit(decoder);
				NativeMemory.Free(decoder);
			}
			sourceHandle.Free();
		}
	}

	private static void WriteWaveHeader(MemoryStream stream, int channels, int sampleRate)
	{
		var dataLength = checked((int)stream.Length - 44);
		stream.Position = 0;
		using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
		writer.Write("RIFF"u8); writer.Write(36 + dataLength); writer.Write("WAVE"u8);
		writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)channels);
		writer.Write(sampleRate); writer.Write(sampleRate * channels * sizeof(short));
		writer.Write((short)(channels * sizeof(short))); writer.Write((short)16);
		writer.Write("data"u8); writer.Write(dataLength);
	}

	private static long GetWaveFrameCount(Stream wave, int channels) => Math.Max(0, wave.Length - 44) / (channels * sizeof(short));

	private static void EnsureSuccess(ma_result result, string source)
	{
		if (result != ma_result.MA_SUCCESS)
			throw new InvalidDataException($"MiniAudio could not decode '{source}' ({result}).");
	}
}
