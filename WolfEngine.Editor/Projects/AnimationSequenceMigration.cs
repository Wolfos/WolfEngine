using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

/// <summary>
/// Removes the old sequence indirection before the asset pipeline reads project sources. All
/// references and metadata conflicts are checked before any source is written. Writes precede
/// deletions, so an interrupted migration can safely be run again.
/// </summary>
public static class AnimationSequenceMigration
{
	private const string SequenceTypeName = "WolfEngine.Animation.AnimationSequence";
	private const string SequenceExtension = ".animclip.json";

	public static int UpgradeProject(string projectRoot)
	{
		var assets = AssetPipelinePaths.GetAssetsPath(projectRoot);
		if (Directory.Exists(assets) == false) return 0;

		var sequences = new Dictionary<Guid, SequenceSource>();
		var sets = new List<SetSource>();
		var metadataStore = new AssetMetadataStore();
		foreach (var path in Directory.EnumerateFiles(assets, "*.json", SearchOption.AllDirectories))
		{
			var standaloneSequence = path.EndsWith(SequenceExtension, StringComparison.OrdinalIgnoreCase);
			var standaloneSet = path.EndsWith(AnimationSet.LegacyExtension, StringComparison.OrdinalIgnoreCase);
			if (standaloneSequence == false &&
				standaloneSet == false &&
				path.EndsWith(DataAssetFile.FileExtension, StringComparison.OrdinalIgnoreCase) == false)
			{
				continue;
			}

			var json = File.ReadAllText(path);
			if (standaloneSequence == false && standaloneSet == false &&
				json.Contains(SequenceTypeName, StringComparison.Ordinal) == false &&
				json.Contains(typeof(AnimationSet).FullName!, StringComparison.Ordinal) == false)
			{
				continue;
			}

			var file = JsonNode.Parse(json) as JsonObject
				?? throw new InvalidDataException($"Animation source '{path}' is empty.");
			var isSequence = standaloneSequence || HasType(file, SequenceTypeName);
			var isSet = standaloneSet || HasType(file, typeof(AnimationSet).FullName!);
			if (isSequence == false && isSet == false) continue;

			var data = standaloneSequence || standaloneSet ? file : file["Data"] as JsonObject
				?? throw new InvalidDataException($"Animation source '{path}' has no data.");
			var version = data["Version"]?.GetValue<int>() ?? 1;
			if (isSequence)
			{
				if (version < 1 || version > 2)
				{
					throw new InvalidDataException($"Unsupported animation sequence version {version} in '{path}'.");
				}

				var metadata = metadataStore.Load(path + ".meta");
				var id = metadata.SubAssets.Single(entry => entry.Key == "main").NodeId;
				var timeline = new AnimationClipMetadata
				{
					Curves = data["Curves"]?.Deserialize<List<AnimationCurve>>(AnimationAssetJson.Options) ?? [],
					Markers = data["Markers"]?.Deserialize<List<AnimationMarker>>(AnimationAssetJson.Options) ?? []
				};
				timeline.Validate(float.MaxValue);
				var speed = data["PlaybackSpeed"]?.GetValue<float>() ?? 1;
				if (float.IsFinite(speed) == false || speed < 0)
				{
					throw new InvalidDataException($"Invalid playback speed in '{path}'.");
				}

				sequences.Add(id, new SequenceSource(path, ReadReference(data["Clip"] ?? data["ClipId"]), speed, timeline));
			}
			else if (version < AnimationSet.CurrentVersion)
			{
				if (version < 1 || version > 2)
				{
					throw new InvalidDataException($"Unsupported animation set version {version} in '{path}'.");
				}

				sets.Add(new SetSource(path, file, data, standaloneSet));
			}
		}

		if (sequences.Count == 0 && sets.Count == 0) return 0;

		var writes = new Dictionary<string, string>(StringComparer.Ordinal);
		var deletes = new HashSet<string>(StringComparer.Ordinal);
		var resolvedClipIds = PlanTimelines(assets, sequences, writes, metadataStore);
		foreach (var set in sets)
		{
			var slots = set.Data["Clips"] as JsonObject ?? new JsonObject();
			foreach (var slot in slots.ToArray())
			{
				var sequenceId = ReadReference(slot.Value);
				var entry = new AnimationSetEntry();
				if (sequenceId != Guid.Empty)
				{
					if (sequences.TryGetValue(sequenceId, out var sequence) == false)
					{
						throw new InvalidDataException($"Cannot migrate '{set.Path}', slot '{slot.Key}': sequence {sequenceId} is missing.");
					}

					entry.Clip = new AssetRef<AnimationClip> { NodeId = resolvedClipIds[sequenceId] };
					entry.PlaybackSpeed = sequence.PlaybackSpeed;
				}

				slots[slot.Key] = JsonSerializer.SerializeToNode(entry, AnimationAssetJson.Options);
			}

			set.Data["Clips"] = slots;
			if (set.Data.ContainsKey("SkeletonId"))
			{
				set.Data["Skeleton"] = new JsonObject { ["NodeId"] = ReadReference(set.Data["SkeletonId"]) };
				set.Data.Remove("SkeletonId");
			}

			set.Data["Version"] = AnimationSet.CurrentVersion;
			var destination = set.Standalone ? set.Path[..^5] + DataAssetFile.FileExtension : set.Path;
			var file = set.File;
			if (set.Standalone)
			{
				file = new JsonObject
				{
					["Version"] = DataAssetFile.CurrentVersion,
					["AssetType"] = nameof(AssetType.DataAsset),
					["DataAssetType"] = typeof(AnimationSet).AssemblyQualifiedName,
					["DataAssetTypeId"] = ProjectTypeResolverUtility.GetStableTypeId(typeof(AnimationSet)),
					["Data"] = set.Data.DeepClone()
				};
				if (File.Exists(destination) &&
					JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(destination)), file) == false)
				{
					throw new IOException($"Cannot migrate '{set.Path}': '{destination}' already exists with different data.");
				}

				var metadata = metadataStore.Load(set.Path + ".meta");
				metadata.ImporterId = AssetImporterIds.DataAsset;
				metadata.ImporterVersion = 2;
				foreach (var subAsset in metadata.SubAssets)
				{
					subAsset.Type = AssetType.DataAsset;
					subAsset.Name = Path.GetFileName(destination);
				}

				writes.Add(destination + ".meta", JsonSerializer.Serialize(metadata, AssetJson.SerializerOptions));
				deletes.Add(set.Path);
				deletes.Add(set.Path + ".meta");
			}

			writes.Add(destination, file.ToJsonString(AssetJson.SerializerOptions));
		}

		foreach (var sequence in sequences.Values)
		{
			deletes.Add(sequence.Path);
			deletes.Add(sequence.Path + ".meta");
		}

		foreach (var write in writes)
		{
			WriteAtomically(write.Key, write.Value);
		}

		foreach (var path in deletes)
		{
			File.Delete(path);
		}

		return sets.Count + sequences.Count;
	}

	private static Dictionary<Guid, Guid> PlanTimelines(
		string assets,
		Dictionary<Guid, SequenceSource> sequences,
		Dictionary<string, string> writes,
		AssetMetadataStore metadataStore)
	{
		var resolved = sequences.ToDictionary(pair => pair.Key, pair => pair.Value.ClipId);
		var groups = sequences.GroupBy(pair => pair.Value.ClipId)
			.Where(group => group.Any(pair => pair.Value.Timeline.Curves.Count > 0 || pair.Value.Timeline.Markers.Count > 0))
			.ToDictionary(group => group.Key, group => group.OrderBy(pair => pair.Value.Path, StringComparer.Ordinal).ToArray());
		if (groups.Count == 0) return resolved;

		foreach (var path in Directory.EnumerateFiles(assets, "*.meta", SearchOption.AllDirectories))
		{
			var metadata = metadataStore.Load(path);
			var changed = false;
			foreach (var subAsset in metadata.SubAssets.ToArray())
			{
				if (groups.TryGetValue(subAsset.NodeId, out var group) == false) continue;

				var timelines = group.GroupBy(pair => JsonSerializer.Serialize(pair.Value.Timeline, AnimationAssetJson.Options))
					.Select(timeline => timeline.ToArray()).ToArray();
				var sourcePath = path[..^5];
				if (AuthoredAnimationClipJson.IsAuthoredClipPath(sourcePath))
				{
					if (timelines.Length > 1)
					{
						throw new InvalidDataException($"Cannot migrate '{sourcePath}': sequences for this authored clip have different curves or markers. Duplicate the authored clip first.");
					}

					var clip = AuthoredAnimationClipJson.Read(sourcePath);
					var timeline = timelines[0][0].Value.Timeline;
					CheckExisting(new AnimationClipMetadata { Curves = clip.Curves, Markers = clip.Markers }, timeline, sourcePath);
					timeline.Validate(clip.GetDurationSeconds());
					clip.Curves = timeline.Curves;
					clip.Markers = timeline.Markers;
					writes[sourcePath] = AuthoredAnimationClipJson.Serialize(clip);
				}
				else
				{
					if (metadata.ImporterId != AssetImporterIds.ThreeDScene)
					{
						throw new InvalidDataException($"Cannot migrate timeline metadata onto '{sourcePath}': unsupported importer '{metadata.ImporterId}'.");
					}

					var settings = metadata.GetImportSettingsOrDefault(() => new ModelImportSettings());
					var existing = settings.AnimationClips.GetValueOrDefault(subAsset.Key) ?? new AnimationClipMetadata();
					var baseTimeline = timelines.FirstOrDefault(timeline => Equal(timeline[0].Value.Timeline, existing));
					if (baseTimeline is null && existing.Curves.Count == 0 && existing.Markers.Count == 0)
					{
						baseTimeline = timelines[0];
					}

					foreach (var timelineGroup in timelines)
					{
						var timeline = timelineGroup[0].Value.Timeline;
						if (ReferenceEquals(timelineGroup, baseTimeline))
						{
							settings.AnimationClips[subAsset.Key] = timeline;
							continue;
						}

						// Keep variant ids distinct from wrapper ids: an existing Library may still
						// index the deleted wrapper until the source refresh finishes.
						var representative = timelineGroup[0];
						var variantId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
							"animation-clip-variant:" + representative.Key.ToString("D"))).AsSpan(0, 16));
						var variantKey = subAsset.Key + ":timeline:" + representative.Key.ToString("N");
						var name = Path.GetFileName(representative.Value.Path)
							.Replace(DataAssetFile.FileExtension, "", StringComparison.OrdinalIgnoreCase)
							.Replace(SequenceExtension, "", StringComparison.OrdinalIgnoreCase);
						var variant = metadata.SubAssets.FirstOrDefault(entry => entry.Key == variantKey);
						if (variant is null)
						{
							metadata.SubAssets.Add(new AssetSubAssetManifestEntry
							{
								Key = variantKey,
								NodeId = variantId,
								Type = AssetType.AnimationClip,
								Name = name
							});
						}
						else if (variant.NodeId != variantId)
						{
							throw new InvalidDataException($"Cannot migrate '{sourcePath}': clip variant '{variantKey}' has a conflicting asset id.");
						}

						settings.AnimationClipVariants[variantKey] = new AnimationClipImportVariant
						{
							SourceClipKey = subAsset.Key,
							Name = name
						};
						settings.AnimationClips[variantKey] = timeline;
						foreach (var sequence in timelineGroup)
						{
							resolved[sequence.Key] = variantId;
						}
					}

					metadata.SetImportSettings(settings);
					changed = true;
				}

				groups.Remove(subAsset.NodeId);
			}

			if (changed)
			{
				writes[path] = JsonSerializer.Serialize(metadata, AssetJson.SerializerOptions);
			}
		}

		if (groups.Count > 0)
		{
			throw new InvalidDataException($"Cannot migrate curves or markers: source metadata for clip {groups.Keys.First()} is missing.");
		}

		return resolved;
	}

	private static bool HasType(JsonObject file, string typeName) =>
		MatchesType(file["DataAssetType"]?.GetValue<string>(), typeName) ||
		MatchesType(file["DataAssetTypeId"]?.GetValue<string>(), typeName);

	private static bool MatchesType(string? value, string typeName) =>
		value == typeName || value?.StartsWith(typeName + ",", StringComparison.Ordinal) == true;

	private static Guid ReadReference(JsonNode? value) =>
		(value is JsonObject reference ? reference["NodeId"] : value)?.GetValue<Guid>() ?? Guid.Empty;

	private static bool Equal(AnimationClipMetadata left, AnimationClipMetadata right) =>
		JsonNode.DeepEquals(
			JsonSerializer.SerializeToNode(left, AnimationAssetJson.Options),
			JsonSerializer.SerializeToNode(right, AnimationAssetJson.Options));

	private static void CheckExisting(AnimationClipMetadata existing, AnimationClipMetadata incoming, string path)
	{
		if ((existing.Curves.Count > 0 || existing.Markers.Count > 0) && Equal(existing, incoming) == false)
		{
			throw new InvalidDataException($"Cannot migrate '{path}': the clip already has different curves or markers.");
		}
	}

	private static void WriteAtomically(string path, string json)
	{
		var temporary = path + ".tmp";
		File.WriteAllText(temporary, json);
		File.Move(temporary, path, true);
	}

	private sealed record SequenceSource(string Path, Guid ClipId, float PlaybackSpeed, AnimationClipMetadata Timeline);
	private sealed record SetSource(string Path, JsonObject File, JsonObject Data, bool Standalone);
}
