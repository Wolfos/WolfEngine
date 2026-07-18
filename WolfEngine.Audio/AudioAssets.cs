using WolfEngine.AssetPipeline;

namespace WolfEngine.Audio;

public enum AudioUsage
{
	Auto,
	Sfx,
	Music
}

public enum AudioStorageMode
{
	Predecoded,
	Streaming
}

public sealed class AudioImportSettings
{
	public AudioUsage Usage { get; set; } = AudioUsage.Auto;
}

public sealed class AudioClipSummary
{
	public string Codec { get; set; } = string.Empty;
	public double DurationSeconds { get; set; }
	public int Channels { get; set; }
	public int SampleRate { get; set; }
	public long FrameCount { get; set; }
	public AudioUsage RequestedUsage { get; set; }
	public AudioStorageMode StorageMode { get; set; }
}

public interface IAudioClipRuntimeResolver : IRuntimeAssetResolver;

[RuntimeAsset(AssetType.AudioClip, typeof(AudioImportSettings), typeof(IAudioClipRuntimeResolver))]
public sealed class AudioClip
{
	public AudioClip(Guid assetId, AudioClipSummary? summary = null)
	{
		AssetId = assetId;
		Summary = summary;
	}

	public Guid AssetId { get; }
	public AudioClipSummary? Summary { get; }
}

public static class AudioAssetConstants
{
	public const string RuntimeArtifactKind = "RuntimeAudio";
	public const string RuntimeArtifactTarget = "generic";
	public const string RuntimeArtifactExtension = ".wolfaudio";
	public const double AutoMusicThresholdSeconds = 10.0;

	public static bool IsSupportedSource(string path)
	{
		var extension = Path.GetExtension(path);
		return extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
		       extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) ||
		       extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
		       extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase);
	}
}
