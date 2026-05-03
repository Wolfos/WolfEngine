using System;
using System.Collections.Generic;
using System.Linq;
using WolfEngine.AssetPipeline;
using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Projects;

public readonly record struct TerrainTextureStateSnapshot(
	Guid AssetId,
	int Width,
	int Height,
	bool IsSrgb,
	TextureFormat Format,
	TextureMipData[] MipLevels);

public interface ITerrainTexturePersistenceService
{
	void RecordPendingTextureState(IReadOnlyList<TerrainTextureStateSnapshot> snapshots);
	void ApplyTextureStates(IReadOnlyList<TerrainTextureStateSnapshot> snapshots);
	void SaveDirtyTextures();
}

public sealed class TerrainTexturePersistenceService : ITerrainTexturePersistenceService
{
	private readonly IEditorProjectService _projectService;
	private readonly ITextureGpuCompressionService _textureGpuCompressionService;
	private readonly IRuntimeArtifactTargetProvider _runtimeArtifactTargetProvider;
	private readonly Dictionary<Guid, TerrainTextureStateSnapshot> _dirtyStates = new();

	public TerrainTexturePersistenceService(
		IEditorProjectService projectService,
		ITextureGpuCompressionService textureGpuCompressionService,
		IRuntimeArtifactTargetProvider runtimeArtifactTargetProvider)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_textureGpuCompressionService = textureGpuCompressionService ?? throw new ArgumentNullException(nameof(textureGpuCompressionService));
		_runtimeArtifactTargetProvider = runtimeArtifactTargetProvider ?? throw new ArgumentNullException(nameof(runtimeArtifactTargetProvider));
	}

	public void RecordPendingTextureState(IReadOnlyList<TerrainTextureStateSnapshot> snapshots)
	{
		ArgumentNullException.ThrowIfNull(snapshots);
		for (var i = 0; i < snapshots.Count; i++)
		{
			var snapshot = snapshots[i];
			if (snapshot.AssetId == Guid.Empty)
			{
				continue;
			}

			_dirtyStates[snapshot.AssetId] = CloneSnapshot(snapshot);
		}
	}

	public void ApplyTextureStates(IReadOnlyList<TerrainTextureStateSnapshot> snapshots)
	{
		ArgumentNullException.ThrowIfNull(snapshots);
		for (var i = 0; i < snapshots.Count; i++)
		{
			var snapshot = CloneSnapshot(snapshots[i]);
			var texture = AssetDatabase.GetInstance<Texture>(snapshot.AssetId);
			if (texture is not null)
			{
				texture.ApplyTextureData(
					snapshot.Width,
					snapshot.Height,
					snapshot.IsSrgb,
					snapshot.Format,
					CloneMipLevels(snapshot.MipLevels));
			}

			if (snapshot.AssetId != Guid.Empty)
			{
				_dirtyStates[snapshot.AssetId] = snapshot;
			}
		}
	}

	public void SaveDirtyTextures()
	{
		if (_dirtyStates.Count == 0)
		{
			return;
		}

		var dirtySnapshots = _dirtyStates.Values.ToArray();
		for (var i = 0; i < dirtySnapshots.Length; i++)
		{
			SaveSnapshot(dirtySnapshots[i]);
		}

		_dirtyStates.Clear();
	}

	private void SaveSnapshot(TerrainTextureStateSnapshot snapshot)
	{
		if (_projectService.TryGetAsset(snapshot.AssetId, out var asset) == false || asset.TextureSummary is null)
		{
			return;
		}

		var summary = asset.TextureSummary;
		var topMip = snapshot.MipLevels.Length > 0
			? snapshot.MipLevels[0]
			: new TextureMipData(snapshot.Width, snapshot.Height, new byte[snapshot.Width * snapshot.Height * 4]);
		var importedTexture = new ImportedTexture(
			asset.Name,
			snapshot.Width,
			snapshot.Height,
			snapshot.IsSrgb,
			summary.Semantic,
			[new TextureMipData(topMip.Width, topMip.Height, topMip.Data.ToArray())]);

		if (string.IsNullOrWhiteSpace(summary.RelativeImportedPath) == false)
		{
			ImportedTextureSerializer.Write(_projectService.GetAbsolutePath(summary.RelativeImportedPath), importedTexture);
		}

		var runtimeRelativePath = ResolveRuntimeArtifactRelativePath(asset);
		if (string.IsNullOrWhiteSpace(runtimeRelativePath))
		{
			return;
		}

		var runtimeTexture = CreateRuntimeTexture(importedTexture);
		var compressionFamily = TextureCompressionCompiler.TryGetBcRuntimeFormat(importedTexture.Semantic, out _)
			? TextureCompressionFamily.Bc
			: TextureCompressionFamily.None;
		TextureArtifactSerializer.Write(
			_projectService.GetAbsolutePath(runtimeRelativePath),
			runtimeTexture,
			importedTexture.Semantic,
			compressionFamily);
	}

	private Texture CreateRuntimeTexture(ImportedTexture importedTexture)
	{
		if (TextureCompressionCompiler.TryGetBcRuntimeFormat(importedTexture.Semantic, out _) == false)
		{
			return new Texture(
				importedTexture.NameOrPath,
				importedTexture.Width,
				importedTexture.Height,
				importedTexture.IsSrgb,
				TextureFormat.Rgba8Unorm,
				TextureMipGenerator.GenerateRgba32MipChain(importedTexture.MipLevels[0]));
		}

		return _textureGpuCompressionService.CompileBcTexture(importedTexture);
	}

	private string ResolveRuntimeArtifactRelativePath(AssetDatabaseEntry asset)
	{
		if (asset.TextureSummary is { RelativeRuntimeArtifactPath.Length: > 0 } summary)
		{
			return summary.RelativeRuntimeArtifactPath;
		}

		var target = _runtimeArtifactTargetProvider.CurrentTarget;
		var targetArtifact = asset.Artifacts
			.Where(artifact => string.Equals(artifact.Kind, "RuntimeTexture", StringComparison.Ordinal))
			.FirstOrDefault(artifact => string.Equals(artifact.Target, target, StringComparison.OrdinalIgnoreCase));
		return targetArtifact?.RelativePath ?? string.Empty;
	}

	private static TerrainTextureStateSnapshot CloneSnapshot(TerrainTextureStateSnapshot snapshot)
	{
		return new TerrainTextureStateSnapshot(
			snapshot.AssetId,
			snapshot.Width,
			snapshot.Height,
			snapshot.IsSrgb,
			snapshot.Format,
			CloneMipLevels(snapshot.MipLevels));
	}

	private static TextureMipData[] CloneMipLevels(TextureMipData[] mipLevels)
	{
		var clone = new TextureMipData[mipLevels.Length];
		for (var i = 0; i < mipLevels.Length; i++)
		{
			var mip = mipLevels[i];
			clone[i] = new TextureMipData(mip.Width, mip.Height, mip.Data.ToArray());
		}

		return clone;
	}
}
