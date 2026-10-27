using System.Text.Json.Nodes;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

/// <summary>
/// Idempotent source migration from version 1 animation assets, which stored references as raw GUIDs,
/// to version 2, which stores them as <see cref="AssetRef{T}"/> so they use the shared asset picker and
/// dependency collection.
/// </summary>
public static class AnimationAssetRefMigration
{
	private const int GuidReferenceVersion = 1;
	private static readonly string AnimationNamespace = typeof(AnimationSet).Namespace + ".";

	public static int UpgradeProject(string projectRoot)
	{
		var assetsPath = AssetPipelinePaths.GetAssetsPath(projectRoot);
		if (Directory.Exists(assetsPath) == false)
		{
			return 0;
		}

		var count = 0;
		foreach (var path in Directory.EnumerateFiles(assetsPath, "*" + DataAssetFile.FileExtension, SearchOption.AllDirectories).ToArray())
		{
			if (TryUpgradeDataAssetFile(path))
			{
				count++;
			}
		}

		foreach (var path in Directory.EnumerateFiles(assetsPath, "*" + AnimationGraph.Extension, SearchOption.AllDirectories).ToArray())
		{
			if (TryUpgradeGraphFile(path))
			{
				count++;
			}
		}

		return count;
	}

	/// <summary>Upgrades the serialized data of an animation set, sequence or bone mask in place.</summary>
	/// <returns>True when the data was changed.</returns>
	public static bool UpgradeDataAssetData(JsonObject data, Type dataAssetType)
	{
		ArgumentNullException.ThrowIfNull(data);
		ArgumentNullException.ThrowIfNull(dataAssetType);

		if (GetVersion(data) != GuidReferenceVersion)
		{
			return false;
		}

		if (dataAssetType == typeof(AnimationSet))
		{
			MoveToAssetRef(data, "SkeletonId", nameof(AnimationSet.Skeleton));
			if (data[nameof(AnimationSet.Clips)] is JsonObject clips)
			{
				foreach (var slot in clips.Select(clip => clip.Key).ToArray())
				{
					clips[slot] = CreateAssetRef(clips[slot]);
				}
			}

			data[nameof(AnimationSet.Version)] = AnimationSet.CurrentVersion;
			return true;
		}

		if (dataAssetType == typeof(AnimationSequence))
		{
			MoveToAssetRef(data, "ClipId", nameof(AnimationSequence.Clip));
			data[nameof(AnimationSequence.Version)] = AnimationSequence.CurrentVersion;
			return true;
		}

		if (dataAssetType == typeof(BoneMask))
		{
			MoveToAssetRef(data, "SkeletonId", nameof(BoneMask.Skeleton));
			data[nameof(BoneMask.Version)] = BoneMask.CurrentVersion;
			return true;
		}

		return false;
	}

	/// <summary>Upgrades a serialized animation graph in place.</summary>
	/// <returns>True when the graph was changed.</returns>
	public static bool UpgradeGraph(JsonObject graph)
	{
		ArgumentNullException.ThrowIfNull(graph);

		if (GetVersion(graph) != GuidReferenceVersion)
		{
			return false;
		}

		if (graph[nameof(AnimationGraph.Nodes)] is JsonArray nodes)
		{
			foreach (var node in nodes.OfType<JsonObject>())
			{
				MoveToAssetRef(node, "MaskId", nameof(AnimationNode.Mask));
			}
		}

		graph[nameof(AnimationGraph.Version)] = AnimationGraph.CurrentVersion;
		return true;
	}

	private static bool TryUpgradeDataAssetFile(string path)
	{
		// Every data asset is visited on project open, so skip parsing the ones that cannot match.
		var json = File.ReadAllText(path);
		if (json.Contains(AnimationNamespace, StringComparison.Ordinal) == false)
		{
			return false;
		}

		if (JsonNode.Parse(json) is not JsonObject file ||
		    file[nameof(DataAssetFile.Data)] is not JsonObject data ||
		    TryGetAnimationDataAssetType(file) is not { } dataAssetType ||
		    UpgradeDataAssetData(data, dataAssetType) == false)
		{
			return false;
		}

		WriteAtomically(path, file.ToJsonString(AssetJson.SerializerOptions));
		return true;
	}

	private static bool TryUpgradeGraphFile(string path)
	{
		if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject graph ||
		    UpgradeGraph(graph) == false)
		{
			return false;
		}

		WriteAtomically(path, graph.ToJsonString(AnimationAssetJson.Options));
		return true;
	}

	private static Type? TryGetAnimationDataAssetType(JsonObject file)
	{
		var typeName = file[nameof(DataAssetFile.DataAssetType)]?.GetValue<string>() ?? string.Empty;
		var typeId = file[nameof(DataAssetFile.DataAssetTypeId)]?.GetValue<string>() ?? string.Empty;
		foreach (var type in new[] { typeof(AnimationSet), typeof(AnimationSequence), typeof(BoneMask) })
		{
			var stableTypeId = ProjectTypeResolverUtility.GetStableTypeId(type);
			if (typeName.StartsWith(type.FullName + ",", StringComparison.Ordinal) ||
			    (typeId.Length > 0 && string.Equals(typeId, stableTypeId, StringComparison.Ordinal)))
			{
				return type;
			}
		}

		return null;
	}

	private static int GetVersion(JsonObject value)
	{
		return value["Version"]?.GetValue<int>() ?? GuidReferenceVersion;
	}

	private static void MoveToAssetRef(JsonObject owner, string guidProperty, string assetRefProperty)
	{
		// Keep the property's position so the upgraded source diffs cleanly.
		var index = owner.IndexOf(guidProperty);
		if (index < 0)
		{
			return;
		}

		var id = owner[guidProperty];
		owner.RemoveAt(index);
		owner.Insert(index, assetRefProperty, CreateAssetRef(id));
	}

	private static JsonObject CreateAssetRef(JsonNode? id)
	{
		return new JsonObject
		{
			[nameof(AssetRef<IDataAsset>.NodeId)] = id?.DeepClone() ?? JsonValue.Create(Guid.Empty)
		};
	}

	private static void WriteAtomically(string path, string json)
	{
		var temporaryPath = path + ".tmp";
		File.WriteAllText(temporaryPath, json);
		File.Move(temporaryPath, path, true);
	}
}
