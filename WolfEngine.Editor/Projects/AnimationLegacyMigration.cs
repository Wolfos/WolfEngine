using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

/// <summary>Explicit, idempotent source-content migration. No legacy runtime playback path remains.</summary>
public static class AnimationLegacyMigration
{
    public static int UpgradeProject(string projectRoot)
    {
        var assets = Path.Combine(projectRoot, "Assets");
        if (!Directory.Exists(assets)) throw new DirectoryNotFoundException(assets);
        var count = MigrateStandaloneAssets(assets);
        foreach (var path in Directory.EnumerateFiles(assets, "*.json", SearchOption.AllDirectories).ToArray())
        {
            if (!(path.EndsWith(".cell.json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".prefab.json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".scene.json", StringComparison.OrdinalIgnoreCase))) continue;
            var source = JsonNode.Parse(File.ReadAllText(path)); var changed = 0;
            Walk(source);
            if (changed == 0) continue;
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, source!.ToJsonString(AssetJson.SerializerOptions)); File.Move(temporary, path, true);
            count += changed;
            void Walk(JsonNode? node)
            {
                if (node is JsonObject component && component["Data"] is JsonObject data && data["ClipAsset"] is JsonObject clipRef &&
                    component["Type"]?.GetValue<string>()?.StartsWith("WolfEngine.Animation.Animator", StringComparison.Ordinal) == true)
                {
                    var clipId = clipRef["NodeId"]?.GetValue<Guid>() ?? Guid.Empty;
                    if (clipId != Guid.Empty)
                    {
                        var signature = data.ToJsonString(); var key = StableId(signature).ToString("N");
                        var sequenceId = StableId("sequence:" + key); var setId = StableId("set:" + key); var graphId = StableId("graph:" + key);
                        var skeletonId = data["SkeletonAsset"]?["NodeId"]?.GetValue<Guid>() ?? Guid.Empty;
                        var playing = data["Playing"]?.GetValue<bool>() ?? true;
                        var clip = new AnimationNode { Kind = AnimationNodeKind.Clip, Name = "Clip", ClipSlot = "Clip",
                            Loop = data["Loop"]?.GetValue<bool>() ?? true, Speed = data["Speed"]?.GetValue<float>() ?? 1,
                            StartTime = data["Time"]?.GetValue<float>() ?? 0, Parameter = "Playing" };
                        var output = new AnimationNode { Kind = AnimationNodeKind.Output, Name = "Output", Inputs = [clip.Id] };
                        var graph = new AnimationGraph { Nodes = [clip, output], Output = output.Id,
                            Parameters = [new() { Name = "Playing", Type = AnimationParameterType.Bool, Default = playing ? 1 : 0 }],
                            Layout = [new() { NodeId = clip.Id, X = 20, Y = 20 }, new() { NodeId = output.Id, X = 250, Y = 20 }] };
                        Write(sequenceId, key + ".clip" + AnimationSequence.Extension, new AnimationSequence { ClipId = clipId }, AssetType.DataAsset);
                        Write(setId, key + ".set" + AnimationSet.Extension, new AnimationSet { SkeletonId = skeletonId, Clips = new() { ["Clip"] = sequenceId } }, AssetType.DataAsset);
                        Write(graphId, key + AnimationGraph.Extension, graph, AssetType.AnimationGraph);
                        data["GraphAsset"] = new JsonObject { ["NodeId"] = graphId }; data["ClipSetAsset"] = new JsonObject { ["NodeId"] = setId };
                    }
                    foreach (var field in new[] { "ClipAsset", "Time", "Playing", "Speed", "Loop" }) data.Remove(field);
                    changed++; return;
                }
                if (node is JsonObject obj) foreach (var value in obj.ToArray()) Walk(value.Value);
                if (node is JsonArray array) foreach (var value in array) Walk(value);
            }
        }
        return count;
        void Write(Guid id, string name, object asset, AssetType type)
        {
            var path = Path.Combine(assets, "Animation", "Migrated", name);
            if (File.Exists(path)) return;
            if (asset is IDataAsset dataAsset) new DataAssetStore().SaveAsset(path, asset.GetType(), dataAsset);
            else AnimationAssetJson.Write(path, asset);
            new AssetMetadataStore().Save(path + ".meta", new AssetSourceMetaFile
            {
                SourceId = StableId("source:" + id), ImporterId = asset is IDataAsset ? AssetImporterIds.DataAsset : "animation-asset", ImporterVersion = 1,
                SubAssets = [new() { Key = "main", NodeId = id, Type = type, Name = name }]
            });
        }
    }
    private static int MigrateStandaloneAssets(string assets)
    {
        var store = new DataAssetStore();
        var metadataStore = new AssetMetadataStore();
        var count = 0;
        foreach (var (extension, type) in new[]
        {
            (AnimationSet.LegacyExtension, typeof(AnimationSet)),
            (AnimationSequence.LegacyExtension, typeof(AnimationSequence)),
            (BoneMask.LegacyExtension, typeof(BoneMask))
        })
        {
            foreach (var oldPath in Directory.EnumerateFiles(assets, "*" + extension, SearchOption.AllDirectories).ToArray())
            {
                var newPath = oldPath[..^".json".Length] + DataAssetFile.FileExtension;
                if (File.Exists(newPath))
                    throw new IOException($"Cannot migrate '{oldPath}': '{newPath}' already exists.");
                var asset = (IDataAsset)AnimationAssetJson.Read(oldPath, type);
                var metadata = metadataStore.Load(oldPath + ".meta");
                store.SaveAsset(newPath, type, asset);
                metadata.ImporterId = AssetImporterIds.DataAsset;
                metadata.ImporterVersion = 1;
                foreach (var subAsset in metadata.SubAssets)
                {
                    subAsset.Type = AssetType.DataAsset;
                    subAsset.Name = Path.GetFileName(newPath);
                }
                metadataStore.Save(newPath + ".meta", metadata);
                File.Delete(oldPath);
                File.Delete(oldPath + ".meta");
                count++;
            }
        }
        return count;
    }
    private static Guid StableId(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
}
