using WolfEngine.Editor.UI;

namespace WolfEngine.Editor.Projects;

public readonly record struct TerrainTexturePreviewRegistration(
	Guid AssetId,
	TerrainAuthoringSurfaceTarget SurfaceTarget,
	Texture PreviewTexture);

public interface ITerrainTexturePreviewRegistry
{
	void RegisterPreview(Guid assetId, TerrainAuthoringSurfaceTarget surfaceTarget, Texture previewTexture);
	void UnregisterPreview(Guid assetId, TerrainAuthoringSurfaceTarget surfaceTarget, Texture previewTexture);
	IReadOnlyList<TerrainTexturePreviewRegistration> GetPreviews(Guid assetId);
}

public sealed class TerrainTexturePreviewRegistry : ITerrainTexturePreviewRegistry
{
	private readonly object _sync = new();
	private readonly Dictionary<Guid, List<TerrainTexturePreviewRegistration>> _registrations = new();

	public void RegisterPreview(Guid assetId, TerrainAuthoringSurfaceTarget surfaceTarget, Texture previewTexture)
	{
		ArgumentNullException.ThrowIfNull(previewTexture);
		if (assetId == Guid.Empty)
		{
			return;
		}

		lock (_sync)
		{
			if (_registrations.TryGetValue(assetId, out var registrations) == false)
			{
				registrations = new List<TerrainTexturePreviewRegistration>();
				_registrations.Add(assetId, registrations);
			}

			for (var i = 0; i < registrations.Count; i++)
			{
				var registration = registrations[i];
				if (registration.SurfaceTarget == surfaceTarget &&
				    ReferenceEquals(registration.PreviewTexture, previewTexture))
				{
					return;
				}
			}

			registrations.Add(new TerrainTexturePreviewRegistration(assetId, surfaceTarget, previewTexture));
		}
	}

	public void UnregisterPreview(Guid assetId, TerrainAuthoringSurfaceTarget surfaceTarget, Texture previewTexture)
	{
		ArgumentNullException.ThrowIfNull(previewTexture);
		if (assetId == Guid.Empty)
		{
			return;
		}

		lock (_sync)
		{
			if (_registrations.TryGetValue(assetId, out var registrations) == false)
			{
				return;
			}

			for (var i = registrations.Count - 1; i >= 0; i--)
			{
				var registration = registrations[i];
				if (registration.SurfaceTarget == surfaceTarget &&
				    ReferenceEquals(registration.PreviewTexture, previewTexture))
				{
					registrations.RemoveAt(i);
				}
			}

			if (registrations.Count == 0)
			{
				_registrations.Remove(assetId);
			}
		}
	}

	public IReadOnlyList<TerrainTexturePreviewRegistration> GetPreviews(Guid assetId)
	{
		if (assetId == Guid.Empty)
		{
			return Array.Empty<TerrainTexturePreviewRegistration>();
		}

		lock (_sync)
		{
			if (_registrations.TryGetValue(assetId, out var registrations) == false || registrations.Count == 0)
			{
				return Array.Empty<TerrainTexturePreviewRegistration>();
			}

			return registrations.ToArray();
		}
	}
}
