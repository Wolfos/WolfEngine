using System.Text.Json;
using System.Text.Json.Serialization;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Animation;

public enum AnimationParameterType
{
	Float,
	Bool,
	Integer
}

public enum AnimationNodeKind
{
	BindPose,
	Clip,
	Blend,
	Select,
	Locomotion1D,
	StateMachine,
	MaskedBlend,
	CurveRemap,
	Output
}

public enum AnimationComparison
{
	Equal,
	NotEqual,
	Less,
	LessOrEqual,
	Greater,
	GreaterOrEqual
}

public sealed class AnimationParameter
{
	public string Name { get; set; } = "";
	public AnimationParameterType Type { get; set; }
	public float Default { get; set; }
}

public sealed class AnimationCondition
{
	public string Parameter { get; set; } = "";
	public AnimationComparison Comparison { get; set; }
	public float Value { get; set; }
}

public sealed class AnimationTransition
{
	public int From { get; set; } = -1;
	public int To { get; set; }
	public float Duration { get; set; } = 0.15f;
	public float ExitTime { get; set; } = -1;
	public List<AnimationCondition> Conditions { get; set; } = [];
}

public sealed class AnimationNode
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public AnimationNodeKind Kind { get; set; }
	public string Name { get; set; } = "";
	public List<Guid> Inputs { get; set; } = [];
	public string Parameter { get; set; } = "";
	public float Value { get; set; }
	public string ClipSlot { get; set; } = "";
	public bool Loop { get; set; } = true;
	public float Speed { get; set; } = 1;
	public float StartTime { get; set; }
	public bool RestartOnActivation { get; set; } = true;
	public string TimeParameter { get; set; } = "";
	public string SequenceParameter { get; set; } = "";
	public Guid MaskId { get; set; }
	public List<float> Thresholds { get; set; } = [];
	public List<AnimationTransition> Transitions { get; set; } = [];
	public string Curve { get; set; } = "";
	public float CurveScale { get; set; } = 1;
	public float CurveOffset { get; set; }
}

public sealed class AnimationNodeLayout
{
	public Guid NodeId { get; set; }
	public float X { get; set; }
	public float Y { get; set; }
}

[RuntimeAsset(AssetType.AnimationGraph, typeof(AnimationGraph), typeof(IAnimationAssetRuntimeResolver))]
public sealed class AnimationGraph
{
	public const string Extension = ".animgraph.json";

	public int Version { get; set; } = 1;
	public string Name { get; set; } = "Animation Graph";
	public List<AnimationParameter> Parameters { get; set; } = [];
	public List<AnimationNode> Nodes { get; set; } = [];
	public Guid Output { get; set; }
	public List<AnimationNodeLayout> Layout { get; set; } = [];
}

[RuntimeAsset(AssetType.DataAsset, typeof(AnimationSet), typeof(IDataAssetRuntimeResolver))]
public sealed class AnimationSet : IDataAsset, IDataAssetDependencies
{
	public const string Extension = DataAssetFile.FileExtension;
	public const string LegacyExtension = ".animset.json";

	public int Version { get; set; } = 1;
	public Guid SkeletonId { get; set; }
	public Dictionary<string, Guid> Clips { get; set; } = new(StringComparer.Ordinal);
	public IEnumerable<Guid> GetDependencies() => Clips.Values.Append(SkeletonId).Where(id => id != Guid.Empty);
}

public sealed class AnimationMarker
{
	public string Name { get; set; } = "";
	public float Time { get; set; }
}

public sealed class AnimationCurve
{
	public string Name { get; set; } = "";
	public CurveInterpolation Interpolation { get; set; } = CurveInterpolation.Linear;
	public float[] Times { get; set; } = [];
	public float[] Values { get; set; } = [];
	public float[]? InTangents { get; set; }
	public float[]? OutTangents { get; set; }

	public FloatCurve Compile() => new(
		(float[])Times.Clone(),
		(float[])Values.Clone(),
		Interpolation,
		(float[]?)InTangents?.Clone(),
		(float[]?)OutTangents?.Clone());
}

[RuntimeAsset(AssetType.DataAsset, typeof(AnimationSequence), typeof(IDataAssetRuntimeResolver))]
public sealed class AnimationSequence : IDataAsset, IDataAssetDependencies
{
	public const string Extension = DataAssetFile.FileExtension;
	public const string LegacyExtension = ".animclip.json";

	public int Version { get; set; } = 1;
	public Guid ClipId { get; set; }
	public float PlaybackSpeed { get; set; } = 1;
	public List<AnimationCurve> Curves { get; set; } = [];
	public List<AnimationMarker> Markers { get; set; } = [];
	public IEnumerable<Guid> GetDependencies() => ClipId == Guid.Empty ? [] : [ClipId];
}

public sealed class BoneMaskEntry
{
	public string Bone { get; set; } = "";
	public float Weight { get; set; } = 1;
	public bool IncludeChildren { get; set; } = true;
}

[RuntimeAsset(AssetType.DataAsset, typeof(BoneMask), typeof(IDataAssetRuntimeResolver))]
public sealed class BoneMask : IDataAsset, IDataAssetDependencies
{
	public const string Extension = DataAssetFile.FileExtension;
	public const string LegacyExtension = ".bonemask.json";

	public int Version { get; set; } = 1;
	public Guid SkeletonId { get; set; }
	public List<BoneMaskEntry> Bones { get; set; } = [];
	public IEnumerable<Guid> GetDependencies() => SkeletonId == Guid.Empty ? [] : [SkeletonId];

	public float[] Compile(Skeleton skeleton)
	{
		var weights = new float[skeleton.BoneCount];
		foreach (var entry in Bones)
		{
			if (float.IsFinite(entry.Weight) == false || entry.Weight < 0 || entry.Weight > 1)
			{
				throw new InvalidOperationException($"Invalid weight for '{entry.Bone}'.");
			}

			if (skeleton.TryGetBoneIndex(entry.Bone, out var index) == false)
			{
				throw new InvalidOperationException($"Mask bone '{entry.Bone}' is absent from '{skeleton.Name}'.");
			}

			weights[index] = entry.Weight;
			if (entry.IncludeChildren == false)
			{
				continue;
			}

			for (var i = index + 1; i < weights.Length; i++)
			{
				for (var parent = skeleton.ParentIndices[i]; parent >= 0; parent = skeleton.ParentIndices[parent])
				{
					if (parent == index)
					{
						weights[i] = entry.Weight;
						break;
					}
				}
			}
		}

		return weights;
	}
}

public static class AnimationAssetJson
{
	public static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = true,
		IncludeFields = true,
		Converters = { new JsonStringEnumConverter() }
	};

	public static T Read<T>(string path) =>
		JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
		?? throw new InvalidOperationException($"Animation asset '{path}' is empty.");

	public static void Write<T>(string path, T asset)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		var temporary = path + ".tmp";
		File.WriteAllText(temporary, JsonSerializer.Serialize(asset, Options));
		File.Move(temporary, path, true);
	}

	public static Type? GetAssetType(string path) =>
		path.EndsWith(AnimationGraph.Extension, StringComparison.OrdinalIgnoreCase) ? typeof(AnimationGraph) :
		null;

	public static object Read(string path, Type type) =>
		JsonSerializer.Deserialize(File.ReadAllText(path), type, Options)
		?? throw new InvalidOperationException($"Animation asset '{path}' is empty.");

	public static IEnumerable<Guid> Dependencies(object asset) => asset switch
	{
		IDataAssetDependencies dependencies => dependencies.GetDependencies(),
		AnimationGraph graph => graph.Nodes.Select(node => node.MaskId).Where(id => id != Guid.Empty),
		_ => []
	};
}
