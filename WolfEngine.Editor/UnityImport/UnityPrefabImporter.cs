using System.Numerics;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.Tooling.Imaging;
using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UnityImport;

/// <param name="RendererCount">Mesh renderers the prefab draws with; zero for one that only held, say, a particle system.</param>
/// <param name="UnsupportedComponents">Unity components on its nodes that were not converted, such as colliders or scripts.</param>
public sealed record UnityConvertedPrefab(Guid PrefabId, int RendererCount, IReadOnlyList<string> UnsupportedComponents);

/// <summary>
/// Rebuilds exported Unity prefabs as WolfEngine prefabs: their meshes from the exported glTF, their materials through
/// <see cref="UnityMaterialConversion"/>, and their hierarchy, renderers and LOD group from Unity's own data.
/// </summary>
/// <remarks>
/// The exported meshes are in Unity's space once imported, so Unity's local transforms apply to them unchanged. A
/// renderer with several submeshes becomes one child entity per submesh, since a WolfEngine renderer draws one mesh with
/// one material. Assets are written under the target folder in <c>Meshes</c>, <c>Materials</c>, <c>Textures</c> and
/// <c>Prefabs</c>; prefabs keep their Unity file names.
/// </remarks>
public sealed class UnityPrefabImporter
{
	private readonly IEditorProjectService _projectService;
	private readonly IAssetMetadataStore _metadataStore;
	private readonly IMaterialAssetStore _materialAssetStore;
	private readonly IPrefabAssetCreator _prefabAssetCreator;
	private readonly IEditorSceneFactory _sceneFactory;

	public UnityPrefabImporter(
		IEditorProjectService projectService,
		IAssetMetadataStore metadataStore,
		IMaterialAssetStore materialAssetStore,
		IPrefabAssetCreator prefabAssetCreator,
		IEditorSceneFactory sceneFactory)
	{
		_projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
		_metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
		_materialAssetStore = materialAssetStore ?? throw new ArgumentNullException(nameof(materialAssetStore));
		_prefabAssetCreator = prefabAssetCreator ?? throw new ArgumentNullException(nameof(prefabAssetCreator));
		_sceneFactory = sceneFactory ?? throw new ArgumentNullException(nameof(sceneFactory));
	}

	/// <summary>Converts the prefabs at <paramref name="prefabPaths"/>, each once.</summary>
	/// <returns>The WolfEngine prefab each Unity prefab path became.</returns>
	public IReadOnlyDictionary<string, UnityConvertedPrefab> Import(
		UnitySceneExport export,
		string unityProjectPath,
		string targetFolder,
		IEnumerable<string> prefabPaths,
		ICollection<string> warnings)
	{
		ArgumentNullException.ThrowIfNull(export);
		ArgumentNullException.ThrowIfNull(warnings);

		var session = new Session(export, unityProjectPath, ProjectPathUtility.NormalizeAssetsFolderPath(targetFolder), warnings);
		var prefabsByPath = export.Prefabs.ToDictionary(prefab => prefab.Path, StringComparer.Ordinal);
		var converted = new Dictionary<string, UnityConvertedPrefab>(StringComparer.Ordinal);
		foreach (var path in prefabPaths.Distinct(StringComparer.Ordinal))
		{
			if (prefabsByPath.TryGetValue(path, out var prefab) == false)
			{
				warnings.Add($"Prefab '{path}' is not in the export.");
				continue;
			}

			converted.Add(path, ImportPrefab(session, prefab));
		}

		foreach (var (material, notes) in session.MaterialApproximations)
		{
			warnings.Add($"Material '{material}': {string.Join("; ", notes)}.");
		}

		return converted;
	}

	private UnityConvertedPrefab ImportPrefab(Session session, UnityPrefabExport prefab)
	{
		var scene = _sceneFactory.New();
		var rendererCount = 0;
		var unsupported = new SortedSet<string>(StringComparer.Ordinal);
		var levels = GetRendererLevels(prefab);
		var entities = new Entity[prefab.Nodes.Count];
		for (var i = 0; i < prefab.Nodes.Count; i++)
		{
			var node = prefab.Nodes[i];
			if (IsActive(prefab, i) == false)
			{
				continue;
			}

			var name = i == 0 ? Path.GetFileNameWithoutExtension(prefab.Path) : node.Name;
			var entity = scene.World.CreateEntity(
				name,
				UnitySpace.ToVector3(node.Position),
				UnitySpace.ToQuaternion(node.Rotation),
				UnitySpace.ToVector3(node.Scale));
			entities[i] = entity;
			if (node.Parent >= 0)
			{
				scene.World.SetParent(entity, entities[node.Parent]);
			}

			if (node.Renderer is { Enabled: true } renderer)
			{
				rendererCount += AddRenderers(session, scene, entity, node.Name, renderer, levels.GetValueOrDefault(i));
			}

			unsupported.UnionWith(node.OtherComponents);
		}

		if (prefab.LodGroup is { } group)
		{
			AddLodGroup(session, scene, prefab, group, entities);
		}

		var result = _prefabAssetCreator.SaveEntityAsPrefab(scene, entities[0], $"{session.TargetFolder}/Prefabs");
		if (result.Success == false || result.AssetId is not { } id)
		{
			throw new InvalidOperationException($"Could not save prefab '{prefab.Path}': {result.ErrorMessage}");
		}

		return new UnityConvertedPrefab(id, rendererCount, unsupported.ToArray());
	}

	private static bool IsActive(UnityPrefabExport prefab, int index)
	{
		while (index >= 0)
		{
			if (prefab.Nodes[index].Active == false) return false;

			index = prefab.Nodes[index].Parent;
		}

		return true;
	}

	/// <summary>The first and last Unity LOD each renderer node draws in. Unity's culled range past the last LOD is dropped.</summary>
	private static Dictionary<int, (int First, int Last)?> GetRendererLevels(UnityPrefabExport prefab)
	{
		var levels = new Dictionary<int, (int First, int Last)?>();
		if (prefab.LodGroup is not { } group) return levels;

		for (var level = 0; level < group.Lods.Count; level++)
		{
			foreach (var node in group.Lods[level].RendererNodes)
			{
				var current = levels.GetValueOrDefault(node);
				levels[node] = current is { } range
					? (Math.Min(range.First, level), Math.Max(range.Last, level))
					: (level, level);
			}
		}

		return levels;
	}

	/// <returns>How many renderers were added.</returns>
	private int AddRenderers(
		Session session,
		EditorScene scene,
		Entity entity,
		string nodeName,
		UnityRendererExport renderer,
		(int First, int Last)? levels)
	{
		var meshes = GetMeshes(session, renderer.Mesh);
		if (meshes.Count == 0)
		{
			session.Warnings.Add($"Mesh '{renderer.Mesh}' of '{nodeName}' is not in the export.");
			return 0;
		}

		var added = 0;
		for (var submesh = 0; submesh < meshes.Count; submesh++)
		{
			if (meshes[submesh] is not { } meshId)
			{
				continue;
			}

			var materialKey = submesh < renderer.Materials.Count ? renderer.Materials[submesh] : renderer.Materials.LastOrDefault();
			var target = entity;
			if (meshes.Count > 1)
			{
				target = scene.World.CreateEntity($"{nodeName}_sub{submesh}", Vector3.Zero, Quaternion.Identity, Vector3.One);
				scene.World.SetParent(target, entity);
			}

			scene.World.AddComponent(target, new MeshRenderer
			{
				MeshAsset = new AssetRef<Mesh> { NodeId = meshId },
				MaterialAsset = materialKey is null ? default : new AssetRef<Material> { NodeId = GetMaterial(session, materialKey) }
			});
			if (levels is { } range)
			{
				scene.World.AddComponent(target, new LodGroupMember
				{
					FirstLevel = range.First,
					LastLevel = range.Last
				});
			}

			added++;
		}

		return added;
	}

	/// <summary>
	/// Unity's thresholds carry over, since both engines measure the bounds' projected size as a share of the screen
	/// height. Unity measures its group size, the largest axis of the bounds, so the WolfEngine radius is half of it.
	/// </summary>
	private static void AddLodGroup(Session session, EditorScene scene, UnityPrefabExport prefab, UnityLodGroupExport group, Entity[] entities)
	{
		if (group.Lods.Count < 2) return;

		if (group.Node != 0)
		{
			session.Warnings.Add($"Prefab '{prefab.Path}' has its LOD group below the root; it is kept there, but placed foliage only reads a root group.");
		}

		if (group.Node >= entities.Length || entities[group.Node] == default) return;

		scene.World.AddComponent(entities[group.Node], new LodGroup
		{
			ScreenSizes = group.Lods.Take(group.Lods.Count - 1).Select(lod => lod.ScreenRelativeTransitionHeight).ToArray(),
			BoundsCenter = UnitySpace.ToVector3(group.LocalReferencePoint),
			BoundsRadius = group.Size * 0.5f
		});
	}

	/// <summary>Imports a mesh's glTF once and returns its submeshes' node ids, in submesh order.</summary>
	private List<Guid?> GetMeshes(Session session, string meshKey)
	{
		if (session.Meshes.TryGetValue(meshKey, out var known)) return known;

		var meshes = new List<Guid?>();
		var exported = session.Export.Meshes.FirstOrDefault(mesh => mesh.Key == meshKey);
		if (exported is null)
		{
			session.Meshes.Add(meshKey, meshes);
			return meshes;
		}

		var relativeGltf = $"{session.TargetFolder}/Meshes/{Path.GetFileName(exported.File)}";
		var absoluteGltf = _projectService.GetAbsolutePath(relativeGltf);
		Directory.CreateDirectory(Path.GetDirectoryName(absoluteGltf)!);
		var sourceGltf = Path.Combine(session.Export.FolderPath, exported.File);
		File.Copy(sourceGltf, absoluteGltf, overwrite: true);
		File.Copy(Path.ChangeExtension(sourceGltf, ".bin"), Path.ChangeExtension(absoluteGltf, ".bin"), overwrite: true);
		_projectService.RefreshAssetSource(relativeGltf);

		var metadata = _metadataStore.Load(AssetFileExtensions.GetMetaPath(absoluteGltf));
		var byName = metadata.SubAssets
			.Where(subAsset => subAsset.Type == AssetType.Mesh)
			.ToDictionary(subAsset => subAsset.Name, subAsset => subAsset.NodeId, StringComparer.Ordinal);
		for (var submesh = 0; submesh < exported.SubMeshCount; submesh++)
		{
			meshes.Add(byName.TryGetValue($"sub{submesh}", out var id) ? id : null);
		}

		session.Meshes.Add(meshKey, meshes);

		return meshes;
	}

	private Guid GetMaterial(Session session, string materialKey)
	{
		if (session.Materials.TryGetValue(materialKey, out var known)) return known;

		var exported = session.Export.Materials.FirstOrDefault(material => material.Key == materialKey);
		if (exported is null)
		{
			session.Warnings.Add($"Material '{materialKey}' is not in the export; its renderers use the default material.");
			session.Materials.Add(materialKey, Guid.Empty);
			return Guid.Empty;
		}

		var plan = UnityMaterialConversion.Describe(exported);
		if (plan.Approximations.Count > 0)
		{
			session.MaterialApproximations[plan.Name] = plan.Approximations;
		}

		var material = _materialAssetStore.CreateDefault(plan.Type);
		material.AlphaCutoff = plan.AlphaCutoff;
		material.DoubleSided = plan.DoubleSided;
		material.BaseColor = new ColorRGBA(plan.BaseColor.X, plan.BaseColor.Y, plan.BaseColor.Z, plan.BaseColor.W);
		material.NormalScale = plan.NormalScale;
		material.MetallicFactor = plan.MetallicFactor;
		material.RoughnessFactor = plan.RoughnessFactor;
		var albedoSemantic = plan.Type == MaterialAssetType.Opaque ? TextureSemantic.BaseColor : TextureSemantic.BaseColorTransparent;
		material.Textures.Albedo = WriteTexture(session, plan.AlbedoPath, "Albedo", albedoSemantic, image => image);
		material.Textures.Normal = WriteTexture(session, plan.NormalPath, "Normal", TextureSemantic.Normal, image => image);
		if (plan.Mask is { } mask)
		{
			material.Textures.Orm = WriteTexture(session, mask.Path, $"ORM{RangeTag(mask)}", TextureSemantic.MetallicRoughness, image => CreateOrm(image, mask));
		}

		var fileName = session.ReserveName("material", SanitizeFileName(exported.Name));
		var relativePath = $"{session.TargetFolder}/Materials/{fileName}{MaterialAsset.FileExtension}";
		var absolutePath = _projectService.GetAbsolutePath(relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
		var nodeId = Guid.NewGuid();
		_materialAssetStore.SaveAsset(absolutePath, material);
		_metadataStore.Save(AssetFileExtensions.GetMetaPath(absolutePath), CreateMaterialMetadata(fileName, nodeId));
		_projectService.RefreshAssetSource(relativePath);
		session.Materials.Add(materialKey, nodeId);

		return nodeId;
	}

	/// <summary>Unity mask maps hold metallic, occlusion, a detail mask and smoothness; ORM holds occlusion, roughness and metallic.</summary>
	private static RgbaImage CreateOrm(RgbaImage mask, UnityMaskConversion conversion)
	{
		var output = new RgbaImage(mask.Width, mask.Height);
		for (var y = 0; y < mask.Height; y++)
		{
			for (var x = 0; x < mask.Width; x++)
			{
				var pixel = mask[x, y];
				var smoothness = Remap(pixel.A, conversion.SmoothnessRange);
				output[x, y] = new RgbaPixel(
					Remap(pixel.G, conversion.OcclusionRange),
					(byte)(255 - smoothness),
					Remap(pixel.R, conversion.MetallicRange));
			}
		}

		return output;
	}

	private static byte Remap(byte value, Vector2 range)
	{
		var remapped = range.X + ((value / 255.0f) * (range.Y - range.X));

		return (byte)Math.Clamp((int)MathF.Round(remapped * 255.0f), 0, 255);
	}

	/// <summary>Materials sharing a mask with different ranges need different ORM maps, so the ranges name the file.</summary>
	private static string RangeTag(UnityMaskConversion mask)
	{
		var identity = mask.MetallicRange == new Vector2(0.0f, 1.0f) &&
		               mask.OcclusionRange == new Vector2(0.0f, 1.0f) &&
		               mask.SmoothnessRange == new Vector2(0.0f, 1.0f);
		if (identity) return string.Empty;

		var hash = HashCode.Combine(mask.MetallicRange, mask.OcclusionRange, mask.SmoothnessRange);

		return $"_{(uint)hash:x8}";
	}

	private AssetRef<Texture> WriteTexture(
		Session session,
		string? unityPath,
		string kind,
		TextureSemantic semantic,
		Func<RgbaImage, RgbaImage> convert)
	{
		if (string.IsNullOrWhiteSpace(unityPath)) return default;

		var key = $"{unityPath}|{kind}|{semantic}";
		if (session.Textures.TryGetValue(key, out var known))
		{
			return new AssetRef<Texture> { NodeId = known };
		}

		var source = Path.Combine(session.UnityProjectPath, unityPath);
		if (File.Exists(source) == false)
		{
			session.Warnings.Add($"Texture '{unityPath}' was not found in the Unity project.");
			session.Textures.Add(key, Guid.Empty);
			return default;
		}

		var fileName = session.ReserveName("texture", $"{SanitizeFileName(Path.GetFileNameWithoutExtension(unityPath))}_{kind}");
		var relativePath = $"{session.TargetFolder}/Textures/{fileName}.png";
		var nodeId = GeneratedTextureWriter.Write(_projectService, _metadataStore, convert(RgbaImage.Load(source)), relativePath, semantic);
		_projectService.RefreshAssetSource(relativePath);
		session.Textures.Add(key, nodeId);

		return new AssetRef<Texture> { NodeId = nodeId };
	}

	private static AssetSourceMetaFile CreateMaterialMetadata(string name, Guid nodeId)
	{
		return new AssetSourceMetaFile
		{
			SourceId = Guid.NewGuid(),
			ImporterId = AssetImporterIds.Material,
			ImporterVersion = 1,
			SubAssets =
			[
				new AssetSubAssetManifestEntry
				{
					Key = "main",
					NodeId = nodeId,
					Type = AssetType.Material,
					Name = name
				}
			]
		};
	}

	private static string SanitizeFileName(string name)
	{
		var invalid = Path.GetInvalidFileNameChars();
		var cleaned = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();

		return cleaned.Length == 0 ? "Imported" : cleaned;
	}

	private sealed class Session(UnitySceneExport export, string unityProjectPath, string targetFolder, ICollection<string> warnings)
	{
		public UnitySceneExport Export { get; } = export;
		public string UnityProjectPath { get; } = unityProjectPath;
		public string TargetFolder { get; } = targetFolder;
		public ICollection<string> Warnings { get; } = warnings;
		public Dictionary<string, List<Guid?>> Meshes { get; } = new(StringComparer.Ordinal);
		public Dictionary<string, Guid> Materials { get; } = new(StringComparer.Ordinal);
		public Dictionary<string, Guid> Textures { get; } = new(StringComparer.Ordinal);
		public Dictionary<string, IReadOnlyList<string>> MaterialApproximations { get; } = new(StringComparer.Ordinal);
		private readonly HashSet<string> _reservedNames = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Returns <paramref name="name"/>, or it with a number when this import already wrote an asset of that kind under
		/// the same name, as two Unity materials from different folders can share one.
		/// </summary>
		public string ReserveName(string kind, string name)
		{
			var candidate = name;
			var index = 2;
			while (_reservedNames.Add($"{kind}:{candidate}") == false)
			{
				candidate = $"{name} {index}";
				index++;
			}

			return candidate;
		}
	}
}
