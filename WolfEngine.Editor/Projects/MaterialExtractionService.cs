using System.Text.Json;
using System.Text.Json.Nodes;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.UI;

namespace WolfEngine.Editor.Projects;

public interface IMaterialExtractionService
{
	EditorAssetCreationResult Extract(AssetDatabaseEntry material);
}

public sealed class MaterialExtractionService(
	IEditorProjectService projectService,
	IMaterialAssetStore materialStore,
	IAssetMetadataStore metadataStore,
	IEditorPlaySession playSession) : IMaterialExtractionService
{
	public EditorAssetCreationResult Extract(AssetDatabaseEntry material)
	{
		if (!projectService.HasOpenProject || !material.IsGenerated || material.Type != AssetType.Material)
			return EditorAssetCreationResult.Failed("Select an imported material in an open project.");
		if (projectService.IsAssetReadOnly(material.Id))
			return EditorAssetCreationResult.Failed("Materials from read-only asset mounts cannot be extracted next to their source.");

		string? outputPath = null;
		var created = false;
		var files = new List<FileChange>();
		var components = new List<ComponentChange>();
		try
		{
			var folder = Path.GetDirectoryName(material.RelativeSourcePath)?.Replace('\\', '/')
				?? throw new InvalidOperationException("The imported material has no source folder.");
			folder = ProjectPathUtility.NormalizeAssetsFolderPath(folder);
			var name = string.Join("_", material.Name.Split(Path.GetInvalidFileNameChars())).Trim();
			if (string.IsNullOrEmpty(name)) name = "Material";
			var baseName = $"{Path.GetFileNameWithoutExtension(material.RelativeSourcePath)} {name}";
			var relativePath = $"{folder}/{baseName}{MaterialAsset.FileExtension}";
			for (var suffix = 1; File.Exists(projectService.GetAbsolutePath(relativePath)) ||
			     File.Exists(projectService.GetAbsolutePath(relativePath) + ".meta"); suffix++)
				relativePath = $"{folder}/{baseName} {suffix}{MaterialAsset.FileExtension}";
			outputPath = projectService.GetAbsolutePath(relativePath);
			var extracted = materialStore.LoadAsset(projectService.GetAbsoluteAssetPath(material.Id, material.RelativeAssetPath));
			var newId = Guid.NewGuid();

			// Prepare the complete replacement before touching any source files or live components.
			foreach (var path in Directory.EnumerateFiles(projectService.AssetsPath!, "*.json", SearchOption.AllDirectories))
			{
				if (!path.EndsWith(Cell.FileExtension, StringComparison.OrdinalIgnoreCase) &&
				    !path.EndsWith(PrefabAssetFile.FileExtension, StringComparison.OrdinalIgnoreCase)) continue;
				var before = File.ReadAllText(path);
				var document = JsonNode.Parse(before) ?? throw new InvalidOperationException($"Invalid asset: {path}");
				if (ReplaceSavedReferences(document, material.Id, newId))
					files.Add(new FileChange(path, before, document.ToJsonString(AssetJson.SerializerOptions)));
			}
			PrepareScene(playSession.AuthoringScene, material.Id, newId, components);
			if (playSession.RuntimeScene is { } runtime && !ReferenceEquals(runtime, playSession.AuthoringScene))
				PrepareScene(runtime, material.Id, newId, components);

			created = true;
			materialStore.SaveAsset(outputPath, extracted);
			metadataStore.Save(outputPath + ".meta", new AssetSourceMetaFile
			{
				SourceId = Guid.NewGuid(), ImporterId = AssetImporterIds.Material, ImporterVersion = 1,
				SubAssets = [new AssetSubAssetManifestEntry
				{
					Key = "main", NodeId = newId, Type = AssetType.Material,
					Name = Path.GetFileName(relativePath)[..^MaterialAsset.FileExtension.Length]
				}]
			});
			projectService.RefreshAssetSource(relativePath);

			foreach (var file in files) WriteAtomically(file.Path, file.After);
			foreach (var file in files) projectService.RefreshAssetSource(GetRelativePath(file.Path));
			foreach (var change in components)
			{
				var value = EditorEntityReferenceUtility.DeserializeComponentData(change.Scene, change.After, change.Type)
					?? throw new InvalidOperationException($"Failed to replace material in {change.Type.Name}.");
				ApplyComponentChange(change, value);
			}
			return EditorAssetCreationResult.Succeeded(newId);
		}
		catch (Exception ex)
		{
			// Restore both representations if writing, importing, or applying a component fails.
			try
			{
				foreach (var change in components)
					ApplyComponentChange(change, change.Before);
				if (created)
				{
					foreach (var file in files) WriteAtomically(file.Path, file.Before);
					if (outputPath is not null && (File.Exists(outputPath) || File.Exists(outputPath + ".meta")))
						projectService.DeleteAssetSource(GetRelativePath(outputPath));
					foreach (var file in files) projectService.RefreshAssetSource(GetRelativePath(file.Path));
				}
			}
			catch (Exception rollbackError)
			{
				return EditorAssetCreationResult.Failed($"Material extraction failed: {ex.Message}. Recovery failed: {rollbackError.Message}");
			}
			return EditorAssetCreationResult.Failed($"Material extraction failed: {ex.Message}");
		}
	}

	private string GetRelativePath(string path) => Path.GetRelativePath(projectService.ProjectRootPath!, path).Replace('\\', '/');

	private static void ApplyComponentChange(ComponentChange change, object value)
	{
		RuntimeComponentAccessor.WriteBoxed(change.Scene.World, change.Entity, change.Type, value);
		// As with a manual MeshRenderer edit, refresh the renderer's cached draw entry
		// as well as the component's asset reference and resolved material.
		if (change.Type == typeof(MeshRenderer))
			change.Scene.World.MarkWorldTransformChanged(change.Entity);
	}

	private static void PrepareScene(EditorScene scene, Guid oldId, Guid newId, List<ComponentChange> changes)
	{
		var entities = new List<Entity>();
		scene.World.GetAllEntities(entities);
		var types = new List<Type>();
		foreach (var entity in entities)
		{
			types.Clear();
			scene.World.GetComponentTypes(entity, types);
			foreach (var type in types)
			{
				if (Attribute.IsDefined(type, typeof(NotSerializedAttribute)) ||
				    Attribute.IsDefined(type, typeof(EditorOnlyAttribute)) ||
				    Attribute.IsDefined(type, typeof(ExcludeFromEditorAttribute))) continue;
				var before = RuntimeComponentAccessor.ReadBoxed(scene.World, entity, type);
				var data = EditorEntityReferenceUtility.SerializeComponentData(scene, type, before);
				var node = JsonNode.Parse(data.GetRawText());
				if (ReplaceReferenceNodes(node, oldId, newId))
					changes.Add(new ComponentChange(scene, entity, type, before!, JsonSerializer.SerializeToElement(node)));
			}
		}
	}

	internal static bool ReplaceSavedReferences(JsonNode document, Guid oldId, Guid newId)
	{
		var changed = false;
		if (document["Entities"] is not JsonArray entities) return false;
		foreach (var entity in entities)
		{
			if (entity?["Components"] is not JsonArray components) continue;
			foreach (var component in components)
			{
				if (!ReplaceReferenceNodes(component?["Data"], oldId, newId)) continue;
				changed = true;
				// The source prefab may still contain the imported material (for example in a
				// read-only mount). Preserve the replacement when loading inherited components.
				if (entity["PrefabSourcePath"] is not JsonArray { Count: > 0 }) continue;
				var overrides = entity["PrefabOverrides"] ??= new JsonObject();
				var ids = overrides["ComponentTypeIds"] as JsonArray;
				if (ids is null) overrides["ComponentTypeIds"] = ids = new JsonArray();
				var typeId = component!["TypeId"]?.GetValue<string>();
				if (string.IsNullOrEmpty(typeId)) typeId = component["Type"]?.GetValue<string>();
				if (!string.IsNullOrEmpty(typeId) && !ids.Any(id => id?.GetValue<string>() == typeId)) ids.Add(typeId);
			}
		}
		return changed;
	}

	private static bool ReplaceReferenceNodes(JsonNode? node, Guid oldId, Guid newId)
	{
		var changed = false;
		if (node is JsonObject obj)
		{
			if (obj["NodeId"] is JsonValue value && value.TryGetValue<string>(out var text) &&
			    Guid.TryParse(text, out var id) && id == oldId)
			{
				obj["NodeId"] = newId.ToString();
				changed = true;
			}
			foreach (var property in obj) changed |= ReplaceReferenceNodes(property.Value, oldId, newId);
		}
		else if (node is JsonArray array)
			foreach (var child in array) changed |= ReplaceReferenceNodes(child, oldId, newId);
		return changed;
	}

	private static void WriteAtomically(string path, string text)
	{
		var temporaryPath = path + ".tmp";
		File.WriteAllText(temporaryPath, text);
		File.Move(temporaryPath, path, true);
	}

	private sealed record FileChange(string Path, string Before, string After);
	private sealed record ComponentChange(EditorScene Scene, Entity Entity, Type Type, object Before, JsonElement After);
}
