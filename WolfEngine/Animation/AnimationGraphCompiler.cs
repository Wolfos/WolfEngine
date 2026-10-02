using System.Runtime.CompilerServices;
using System.Numerics;
using System.Text.Json;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Animation;

public readonly record struct AnimationActionInput(int Kind, int Variant, int SequenceId, float NormalizedTime);
public readonly record struct AnimationActionHandles(AnimationParameterHandle Kind, AnimationParameterHandle Variant, AnimationParameterHandle Sequence, AnimationParameterHandle Time);
public sealed record AnimationParameterInfo(string Name, AnimationParameterType Type, float Default);
public readonly record struct AnimationParameterHandle(int Index, AnimationParameterType Type, Guid GraphId);
public readonly record struct AnimationCurveHandle(int Index, Guid GraphId);
public readonly record struct AnimationPresentationMarker(string Name, Guid NodeId, int Sequence, float ClipTime);
public readonly record struct AnimationContribution(Guid NodeId, string ClipSlot, float Weight, float NormalizedTime);
public readonly record struct AnimationStateInspection(Guid NodeId, int State, float BlendRemaining, string Reason);

/// <summary>Immutable, shared rig-bound program. All mutable playback state belongs to an instance.</summary>
public sealed class CompiledAnimationGraph
{
	internal readonly AnimationNode[] Nodes;
	internal readonly int[][] Inputs;
	internal readonly int[] Parameters;
	internal readonly int[] TimeParameters;
	internal readonly int[] SequenceParameters;
	internal readonly int[][] ConditionParameters;
	internal readonly BoundAnimationClip?[] Clips;
	internal readonly float[]?[] Masks;
	internal readonly float[] MinimumMaskWeights;
	internal readonly int[] RemapCurves;
	internal readonly AnimationParameter[] ParameterDefinitions;
	internal readonly int Output;
	internal readonly string[] CurveNames;
	internal readonly AnimationBinding[] TransformBindings;
	internal readonly AnimationBinding[] PropertyBindings;
	internal readonly Dictionary<string, int> ParameterIndices;
	internal readonly Dictionary<string, int> CurveIndices;

	public Guid Id { get; } = Guid.NewGuid();
	public Skeleton Skeleton { get; }
	public int NodeCount => Nodes.Length;
	public IReadOnlyList<AnimationParameterInfo> ParameterSchema { get; }
	public IReadOnlyList<AnimationBinding> AnimatedTransforms => Array.AsReadOnly(TransformBindings);
	public IReadOnlyList<AnimationBinding> AnimatedProperties => Array.AsReadOnly(PropertyBindings);
	public IReadOnlyList<string> Curves => Array.AsReadOnly(CurveNames);

	internal CompiledAnimationGraph(AnimationGraph graph, Skeleton skeleton, AnimationSet? set, Func<Guid, Type, object?> resolve)
	{
		Skeleton = skeleton;
		if (graph.Version != 1 || (set is not null && set.Version != 1))
		{
			throw new InvalidOperationException("Unsupported animation asset version.");
		}

		// Copy authoring data once so live edits cannot mutate a program already in use.
		var copy = JsonSerializer.Deserialize<AnimationGraph>(JsonSerializer.Serialize(graph, AnimationAssetJson.Options), AnimationAssetJson.Options)!;
		if (copy.Nodes is null || copy.Parameters is null)
		{
			throw new InvalidOperationException("Graph nodes and parameters must be arrays.");
		}

		foreach (var node in copy.Nodes)
		{
			if (node is null || node.Inputs is null || node.Transitions is null || node.Thresholds is null || Enum.IsDefined(node.Kind) == false)
			{
				throw new InvalidOperationException("Graph contains a malformed node.");
			}
		}

		Nodes = copy.Nodes.ToArray();
		ParameterDefinitions = copy.Parameters.ToArray();
		ParameterSchema = Array.AsReadOnly(ParameterDefinitions.Select(p => new AnimationParameterInfo(p.Name, p.Type, p.Default)).ToArray());
		ParameterIndices = new(StringComparer.Ordinal);
		for (var i = 0; i < ParameterDefinitions.Length; i++)
		{
			var definition = ParameterDefinitions[i];
			if (definition is null ||
				Enum.IsDefined(definition.Type) == false ||
				string.IsNullOrWhiteSpace(definition.Name) ||
				float.IsFinite(definition.Default) == false ||
				ParameterIndices.TryAdd(definition.Name, i) == false)
			{
				throw new InvalidOperationException($"Invalid or duplicate parameter '{definition?.Name}'.");
			}

			if (definition.Type != AnimationParameterType.Float && definition.Default != MathF.Truncate(definition.Default))
			{
				throw new InvalidOperationException($"Parameter '{definition.Name}' requires an integer default.");
			}

			if (definition.Type == AnimationParameterType.Bool && definition.Default is not (0 or 1))
			{
				throw new InvalidOperationException($"Parameter '{definition.Name}' requires a boolean default.");
			}
		}

		var indices = new Dictionary<Guid, int>();
		for (var i = 0; i < Nodes.Length; i++)
		{
			if (Nodes[i].Id == Guid.Empty || indices.TryAdd(Nodes[i].Id, i) == false)
			{
				throw new InvalidOperationException("Node IDs must be unique and nonempty.");
			}
		}

		if (indices.TryGetValue(copy.Output, out Output) == false || Nodes[Output].Kind != AnimationNodeKind.Output)
		{
			throw new InvalidOperationException("Graph requires an Output node.");
		}

		Inputs = new int[Nodes.Length][];
		Parameters = new int[Nodes.Length];
		TimeParameters = new int[Nodes.Length];
		SequenceParameters = new int[Nodes.Length];
		ConditionParameters = new int[Nodes.Length][];
		Clips = new BoundAnimationClip?[Nodes.Length];
		Masks = new float[]?[Nodes.Length];
		MinimumMaskWeights = new float[Nodes.Length];
		RemapCurves = new int[Nodes.Length];

		var curveNames = new List<string>();
		var transforms = new List<AnimationBinding>();
		var properties = new List<AnimationBinding>();
		CurveIndices = new(StringComparer.Ordinal);
		for (var i = 0; i < Nodes.Length; i++)
		{
			var node = Nodes[i];
			Inputs[i] = node.Inputs
				.Select(id => indices.TryGetValue(id, out var input)
					? input
					: throw new InvalidOperationException($"Node '{node.Name}' has a missing input {id}."))
				.ToArray();
			Parameters[i] = FindParameter(node.Parameter);
			TimeParameters[i] = FindParameter(node.TimeParameter);
			SequenceParameters[i] = FindParameter(node.SequenceParameter);
			if (node.Transitions.Any(t => t is null || t.Conditions is null || t.Conditions.Any(c => c is null || Enum.IsDefined(c.Comparison) == false)))
			{
				throw new InvalidOperationException($"Malformed transition in '{node.Name}'.");
			}

			ConditionParameters[i] = node.Transitions
				.SelectMany(t => t.Conditions)
				.Select(c => FindParameter(c.Parameter, true))
				.ToArray();
			ValidateNode(i);

			if (node.Kind == AnimationNodeKind.Clip)
			{
				if (set is null || set.Clips.TryGetValue(node.ClipSlot, out var sequenceId) == false)
				{
					throw new InvalidOperationException($"Missing clip slot '{node.ClipSlot}'.");
				}

				var sequence = resolve(sequenceId, typeof(AnimationSequence)) as AnimationSequence
					?? throw new InvalidOperationException($"Unresolved sequence '{node.ClipSlot}'.");
				var clip = resolve(sequence.ClipId, typeof(AnimationClip)) as AnimationClip
					?? throw new InvalidOperationException($"Unresolved imported clip '{node.ClipSlot}'.");
				Clips[i] = new BoundAnimationClip(clip, sequence, skeleton, transforms, properties, curveNames);
			}

			if (node.Kind == AnimationNodeKind.MaskedBlend)
			{
				var mask = resolve(node.MaskId, typeof(BoneMask)) as BoneMask
					?? throw new InvalidOperationException($"Missing mask for '{node.Name}'.");
				if (mask.SkeletonId != Guid.Empty && ReferenceEquals(resolve(mask.SkeletonId, typeof(Skeleton)), skeleton) == false)
				{
					throw new InvalidOperationException($"Mask '{node.Name}' targets another skeleton.");
				}

				Masks[i] = mask.Compile(skeleton);
				MinimumMaskWeights[i] = Masks[i]!.Length == 0 ? 1 : Masks[i]!.Min();
			}

			if (node.Kind == AnimationNodeKind.CurveRemap && curveNames.Contains(node.Curve, StringComparer.Ordinal) == false)
			{
				curveNames.Add(node.Curve);
			}
		}

		CurveNames = curveNames.ToArray();
		TransformBindings = transforms.ToArray();
		PropertyBindings = properties.ToArray();
		for (var i = 0; i < CurveNames.Length; i++)
		{
			CurveIndices.Add(CurveNames[i], i);
		}

		for (var i = 0; i < Nodes.Length; i++)
		{
			RemapCurves[i] = Nodes[i].Kind == AnimationNodeKind.CurveRemap ? CurveIndices[Nodes[i].Curve] : -1;
		}

		var marks = new byte[Nodes.Length];

		void Visit(int index)
		{
			if (marks[index] == 1)
			{
				throw new InvalidOperationException($"Pose cycle at '{Nodes[index].Name}'.");
			}

			if (marks[index] == 2)
			{
				return;
			}

			marks[index] = 1;
			foreach (var input in Inputs[index])
			{
				Visit(input);
			}

			marks[index] = 2;
		}

		for (var i = 0; i < Nodes.Length; i++)
		{
			Visit(i);
		}

		var syncOwners = new Dictionary<int, int>();
		for (var i = 0; i < Nodes.Length; i++)
		{
			if (Nodes[i].Kind != AnimationNodeKind.Locomotion1D)
			{
				continue;
			}

			foreach (var input in Inputs[i])
			{
				if (Nodes[input].Kind != AnimationNodeKind.Clip || TimeParameters[input] >= 0 || Nodes[input].Loop == false)
				{
					throw new InvalidOperationException("Locomotion inputs must be internally timed looping clip players.");
				}

				if (syncOwners.TryAdd(input, i) == false)
				{
					throw new InvalidOperationException("A clip player may belong to only one locomotion synchronization group.");
				}
			}
		}

		if (set?.SkeletonId is { } skeletonId && skeletonId != Guid.Empty && ReferenceEquals(resolve(skeletonId, typeof(Skeleton)), skeleton) == false)
		{
			throw new InvalidOperationException("Clip set targets a different skeleton.");
		}
	}

	private int FindParameter(string name, bool required = false)
	{
		if (string.IsNullOrEmpty(name) && required == false)
		{
			return -1;
		}

		return ParameterIndices.TryGetValue(name, out var index)
			? index
			: throw new InvalidOperationException($"Missing parameter '{name}'.");
	}

	private void ValidateNode(int index)
	{
		var node = Nodes[index];
		var count = Inputs[index].Length;
		var valid = node.Kind switch
		{
			AnimationNodeKind.BindPose or AnimationNodeKind.Clip => count == 0,
			AnimationNodeKind.Blend or AnimationNodeKind.MaskedBlend => count == 2,
			AnimationNodeKind.Output or AnimationNodeKind.CurveRemap => count == 1,
			AnimationNodeKind.Select or AnimationNodeKind.StateMachine => count > 0,
			AnimationNodeKind.Locomotion1D => count > 1 && node.Thresholds.Count == count,
			_ => false
		};

		if (valid == false)
		{
			throw new InvalidOperationException($"Invalid inputs for {node.Kind} '{node.Name}'.");
		}

		if (float.IsFinite(node.Speed) == false || float.IsFinite(node.Value) == false || float.IsFinite(node.StartTime) == false)
		{
			throw new InvalidOperationException($"Invalid playback or value for '{node.Name}'.");
		}

		if (node.Kind == AnimationNodeKind.Select && (Parameters[index] < 0 || ParameterDefinitions[Parameters[index]].Type == AnimationParameterType.Float))
		{
			throw new InvalidOperationException("Select requires an integer or boolean parameter.");
		}

		if (TimeParameters[index] >= 0 && ParameterDefinitions[TimeParameters[index]].Type != AnimationParameterType.Float)
		{
			throw new InvalidOperationException("External clip time requires a float parameter.");
		}

		if (SequenceParameters[index] >= 0 && ParameterDefinitions[SequenceParameters[index]].Type != AnimationParameterType.Integer)
		{
			throw new InvalidOperationException("Sequence ID requires an integer parameter.");
		}

		if (node.Kind == AnimationNodeKind.Locomotion1D)
		{
			for (var i = 0; i < node.Thresholds.Count; i++)
			{
				if (float.IsFinite(node.Thresholds[i]) == false || node.Thresholds[i] < 0 || (i > 0 && node.Thresholds[i] <= node.Thresholds[i - 1]))
				{
					throw new InvalidOperationException("Locomotion thresholds must be finite, nonnegative and strictly increasing.");
				}
			}
		}

		foreach (var transition in node.Transitions)
		{
			if (transition.From < -1 ||
				transition.From >= count ||
				transition.To < 0 ||
				transition.To >= count ||
				float.IsFinite(transition.Duration) == false ||
				transition.Duration < 0 ||
				float.IsFinite(transition.ExitTime) == false)
			{
				throw new InvalidOperationException($"Invalid transition in '{node.Name}'.");
			}

			foreach (var condition in transition.Conditions)
			{
				if (float.IsFinite(condition.Value) == false)
				{
					throw new InvalidOperationException("Condition values must be finite.");
				}
			}
		}

		if (float.IsFinite(node.CurveScale) == false ||
			float.IsFinite(node.CurveOffset) == false ||
			(node.Kind == AnimationNodeKind.CurveRemap && string.IsNullOrWhiteSpace(node.Curve)))
		{
			throw new InvalidOperationException("Invalid curve remap.");
		}
	}

	public AnimationParameterHandle GetParameter(string name, AnimationParameterType type)
	{
		var index = FindParameter(name, true);
		if (ParameterDefinitions[index].Type != type)
		{
			throw new InvalidOperationException($"Wrong type for '{name}'.");
		}

		return new(index, type, Id);
	}

	public AnimationCurveHandle GetCurve(string name) =>
		new(CurveIndices.TryGetValue(name, out var index) ? index : throw new InvalidOperationException($"Unknown curve '{name}'."), Id);

	public AnimationGraphInstance CreateInstance() => new(this);
}

internal sealed class BoundAnimationClip
{
	internal readonly AnimationClip Clip;
	internal readonly float PlaybackSpeed;
	internal readonly int[] BoneSlots;
	internal readonly int[] TransformSlots;
	internal readonly int[] PropertySlots;
	internal readonly int[] CurveSlots;
	internal readonly FloatCurve[] Curves;
	internal readonly Vector3?[] ConstantPositions;
	internal readonly Vector3?[] ConstantScales;
	internal readonly float[]? SharedTransformTimes;
	internal readonly AnimationMarker[] Markers;

	private static Vector3? ConstantValue(Vector3Curve curve)
	{
		// Empty channels retain instance-captured defaults. Cubic curves may move even
		// with equal endpoint values, so leave their tangent evaluation untouched.
		if (curve.Values.Length == 0 || curve.Interpolation == CurveInterpolation.CubicHermite)
		{
			return null;
		}

		var value = curve.Values[0];
		foreach (var key in curve.Values)
		{
			if (key != value)
			{
				return null;
			}
		}

		return value;
	}

	internal BoundAnimationClip(
		AnimationClip clip,
		AnimationSequence sequence,
		Skeleton skeleton,
		List<AnimationBinding> transforms,
		List<AnimationBinding> properties,
		List<string> curveNames)
	{
		Clip = clip;
		if (float.IsFinite(sequence.PlaybackSpeed) == false || sequence.PlaybackSpeed < 0)
		{
			throw new InvalidOperationException("Clip playback speed must be finite and nonnegative.");
		}

		PlaybackSpeed = sequence.PlaybackSpeed;
		if (clip.TransformTracks.Select(t => t.Binding).Distinct().Count() != clip.TransformTracks.Length ||
			clip.PropertyTracks.Select(t => t.Binding).Distinct().Count() != clip.PropertyTracks.Length)
		{
			throw new InvalidOperationException($"Clip '{clip.Name}' contains duplicate output bindings.");
		}

		BoneSlots = new int[clip.TransformTracks.Length];
		TransformSlots = new int[BoneSlots.Length];
		ConstantPositions = new Vector3?[BoneSlots.Length];
		ConstantScales = new Vector3?[BoneSlots.Length];
		SharedTransformTimes = FindSharedTransformTimes(clip.TransformTracks);
		Array.Fill(BoneSlots, -1);
		Array.Fill(TransformSlots, -1);

		var matches = 0;
		for (var i = 0; i < BoneSlots.Length; i++)
		{
			var track = clip.TransformTracks[i];
			ConstantPositions[i] = ConstantValue(track.Position);
			ConstantScales[i] = ConstantValue(track.Scale);
			if (track.IsBoneTrack)
			{
				if (skeleton.TryGetBoneIndex(track.Binding.Path, out BoneSlots[i]) == false)
				{
					throw new InvalidOperationException($"Clip bone '{track.Binding.Path}' is absent from '{skeleton.Name}'.");
				}

				matches++;
			}
			else
			{
				TransformSlots[i] = Slot(transforms, track.Binding);
			}
		}

		if (matches > 0 && clip.SourceBindPoseLocal.Length > 0 && clip.SourceSkeletonName == skeleton.Name && clip.SourceBindPoseLocal.Length != skeleton.BoneCount)
		{
			throw new InvalidOperationException("Clip source rig differs from the destination rig.");
		}

		PropertySlots = clip.PropertyTracks.Select(track => Slot(properties, track.Binding)).ToArray();
		Curves = sequence.Curves.Select(curve =>
		{
			if (string.IsNullOrWhiteSpace(curve.Name) || curve.Times.Length != curve.Values.Length || Enum.IsDefined(curve.Interpolation) == false)
			{
				throw new InvalidOperationException("Malformed named curve.");
			}

			for (var i = 0; i < curve.Times.Length; i++)
			{
				if (float.IsFinite(curve.Times[i]) == false ||
					float.IsFinite(curve.Values[i]) == false ||
					curve.Times[i] < 0 ||
					curve.Times[i] > clip.Duration ||
					(i > 0 && curve.Times[i] <= curve.Times[i - 1]))
				{
					throw new InvalidOperationException($"Invalid keys for '{curve.Name}'.");
				}
			}

			if (curve.Interpolation == CurveInterpolation.CubicHermite &&
				(curve.InTangents?.Length != curve.Times.Length || curve.OutTangents?.Length != curve.Times.Length))
			{
				throw new InvalidOperationException("Cubic curves require tangents at every key.");
			}

			return curve.Compile();
		}).ToArray();

		if (sequence.Curves.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != sequence.Curves.Count)
		{
			throw new InvalidOperationException("Duplicate named curve.");
		}

		CurveSlots = sequence.Curves.Select(curve => Slot(curveNames, curve.Name)).ToArray();
		Markers = sequence.Markers.OrderBy(marker => marker.Time).Select(marker =>
		{
			if (string.IsNullOrWhiteSpace(marker.Name) || float.IsFinite(marker.Time) == false || marker.Time < 0 || marker.Time > clip.Duration)
			{
				throw new InvalidOperationException("Invalid animation marker.");
			}

			return new AnimationMarker { Name = marker.Name, Time = marker.Time };
		}).ToArray();
	}

	private static float[]? FindSharedTransformTimes(TransformTrack[] tracks)
	{
		if (tracks.Length == 0) return null;

		var times = tracks[0].Rotation.Times;
		if (times.Length == 0) return null;

		foreach (var track in tracks)
		{
			if (track.Position.Times.AsSpan().SequenceEqual(times) == false ||
				track.Rotation.Times.AsSpan().SequenceEqual(times) == false ||
				track.Scale.Times.AsSpan().SequenceEqual(times) == false)
			{
				return null;
			}
		}

		return times;
	}

	private static int Slot<T>(List<T> slots, T binding)
	{
		var index = slots.IndexOf(binding);
		if (index >= 0)
		{
			return index;
		}

		slots.Add(binding);
		return slots.Count - 1;
	}
}

public static class AnimationGraphCompiler
{
	private static readonly ConditionalWeakTable<AnimationGraph, Dictionary<(Skeleton, AnimationSet?), CompiledAnimationGraph>> Cache = new();

	public static CompiledAnimationGraph Compile(AnimationGraph graph, Skeleton skeleton, AnimationSet? set, Func<Guid, Type, object?> resolve) =>
		new(graph, skeleton, set, resolve);

	public static CompiledAnimationGraph GetOrCompile(AnimationGraph graph, Skeleton skeleton, AnimationSet? set, Func<Guid, Type, object?> resolve)
	{
		var programs = Cache.GetOrCreateValue(graph);
		lock (programs)
		{
			if (programs.TryGetValue((skeleton, set), out var program) == false)
			{
				program = Compile(graph, skeleton, set, resolve);
				programs.Add((skeleton, set), program);
			}

			return program;
		}
	}
}
