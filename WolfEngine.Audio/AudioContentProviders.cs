using WolfEngine.AssetPipeline;

namespace WolfEngine.Audio;

public sealed class DelegateAudioContentProvider : IAudioContentProvider
{
	private readonly Func<Guid, Stream> _open;
	public DelegateAudioContentProvider(Func<Guid, Stream> open) => _open = open ?? throw new ArgumentNullException(nameof(open));
	public AudioContentStream Open(Guid assetId) => AudioArtifact.Open(_open(assetId));
}

public sealed class WolfPackAudioContentProvider : IAudioContentProvider
{
	private readonly WolfPackCatalog _catalog;
	public WolfPackAudioContentProvider(WolfPackCatalog catalog) => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
	public AudioContentStream Open(Guid assetId)
	{
		var entry = _catalog.GetEntry(assetId);
		if (!string.Equals(entry.Kind, nameof(AssetType.AudioClip), StringComparison.Ordinal))
			throw new InvalidDataException($"Cooked entry '{assetId:D}' is not an audio clip.");
		return AudioArtifact.Open(_catalog.OpenRead(assetId));
	}
}

public sealed class AudioClipRuntimeResolver : IAudioClipRuntimeResolver
{
	public object Resolve(RuntimeAssetResolveContext context)
		=> new AudioClip(context.AssetId, context.Asset.GetRequiredSummary<AudioClipSummary>());
}
