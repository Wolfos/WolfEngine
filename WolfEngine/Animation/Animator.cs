using System.Numerics;
using System.Text.Json.Serialization;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;

namespace WolfEngine.Animation;

/// <summary>One graph instance shared by every mesh on a character rig.</summary>
public struct Animator : IEntityComponent, IJsonOnDeserialized
{
	public AssetRef<Skeleton> SkeletonAsset;
	public AssetRef<AnimationGraph> GraphAsset;
	public AssetRef<AnimationSet> ClipSetAsset;

	[JsonIgnore] internal Skeleton? Skeleton;
	[JsonIgnore] internal AnimationGraph? Graph;
	[JsonIgnore] internal AnimationSet? ClipSet;
	[JsonIgnore] internal AnimationGraphInstance? GraphInstance;
	[JsonIgnore] internal Pose? Pose;
	[JsonIgnore] internal AnimationOutputBindings? Bindings;
	[JsonIgnore] internal Matrix4x4[]? SkinningMatrices;
	[JsonIgnore] internal Matrix4x4[]? PreviousSkinningMatrices;
	[JsonIgnore] internal bool HasPreviousPose;
	[JsonIgnore] internal bool BindPosePrepared;
	[JsonIgnore] internal uint PoseGeneration;
	[JsonIgnore] internal uint LastRenderedPoseGeneration;
	[JsonIgnore] public string? Diagnostic { get; internal set; }

	private static readonly AnimationNode Rest = new() { Kind = AnimationNodeKind.BindPose };
	private static readonly AnimationNode RestOutput = new() { Kind = AnimationNodeKind.Output, Inputs = [Rest.Id] };
	private static readonly AnimationGraph RestGraph = new() { Nodes = [Rest, RestOutput], Output = RestOutput.Id };

	public static Animator Create(AssetRef<Skeleton> skeleton, AssetRef<AnimationGraph> graph, AssetRef<AnimationSet> clips = default) =>
		new() { SkeletonAsset = skeleton, GraphAsset = graph, ClipSetAsset = clips };

	[JsonIgnore] public AnimationGraphInstance? Instance => TryPrepare() ? GraphInstance : null;

	/// <summary>Prepare a static bind pose for an authoring world without compiling a graph.</summary>
	internal bool TryPrepareBindPose()
	{
		var skeleton = SkeletonAsset.IsValid ? SkeletonAsset.Asset : Skeleton;
		if (skeleton is null)
		{
			return false;
		}

		if (BindPosePrepared && ReferenceEquals(Skeleton, skeleton) && GraphInstance is null && Pose is not null &&
			SkinningMatrices?.Length == skeleton.BoneCount && PreviousSkinningMatrices?.Length == skeleton.BoneCount && HasPreviousPose)
		{
			return true;
		}

		var pose = new Pose(skeleton.BoneCount);
		pose.SetToBindPose(skeleton);
		var matrices = new Matrix4x4[skeleton.BoneCount];
		pose.ComputeSkinningMatrices(skeleton, matrices);

		Skeleton = skeleton;
		GraphInstance = null;
		Pose = pose;
		Bindings = null;
		SkinningMatrices = matrices;
		PreviousSkinningMatrices = (Matrix4x4[])matrices.Clone();
		HasPreviousPose = true;
		BindPosePrepared = true;
		PoseGeneration++;
		Diagnostic = null;
		return true;
	}

	internal bool TryPrepare()
	{
		var skeleton = SkeletonAsset.IsValid ? SkeletonAsset.Asset : Skeleton;
		var graph = GraphAsset.IsValid ? GraphAsset.Asset : Graph ?? RestGraph;
		var clips = ClipSetAsset.IsValid ? ClipSetAsset.Asset : ClipSet;
		if (skeleton is null || graph is null)
		{
			return false;
		}

		if (GraphInstance is not null && ReferenceEquals(Skeleton, skeleton) && ReferenceEquals(Graph, graph) && ReferenceEquals(ClipSet, clips))
		{
			return true;
		}

		try
		{
			var program = AnimationGraphCompiler.GetOrCompile(graph, skeleton, clips, Resolve);
			GraphInstance = program.CreateInstance();
			Skeleton = skeleton;
			Graph = graph;
			ClipSet = clips;
			Pose = GraphInstance.Output;
			SkinningMatrices = new Matrix4x4[skeleton.BoneCount];
			PreviousSkinningMatrices = new Matrix4x4[skeleton.BoneCount];
			HasPreviousPose = false;
			BindPosePrepared = false;
			Bindings = null;
			Diagnostic = null;
			return true;
		}
		catch (InvalidOperationException exception)
		{
			Diagnostic = exception.Message;
			return false;
		}
	}

	internal static object? Resolve(Guid id, Type type) =>
		type == typeof(AnimationSequence) ? AssetDatabase.GetInstance<AnimationSequence>(id) :
		type == typeof(AnimationClip) ? AssetDatabase.GetInstance<AnimationClip>(id) :
		type == typeof(Skeleton) ? AssetDatabase.GetInstance<Skeleton>(id) :
		type == typeof(BoneMask) ? AssetDatabase.GetInstance<BoneMask>(id) : null;

	public void OnDeserialized()
	{
		Skeleton = null;
		Graph = null;
		ClipSet = null;
		GraphInstance = null;
		Pose = null;
		Bindings = null;
		SkinningMatrices = null;
		PreviousSkinningMatrices = null;
		HasPreviousPose = false;
		BindPosePrepared = false;
		PoseGeneration = 0;
		LastRenderedPoseGeneration = 0;
		Diagnostic = null;
	}
}

/// <summary>
/// Marks a bone that should also exist as an entity, so gameplay can parent things to it — a weapon
/// in a hand, a camera on a head. Opt-in because the whole point of keeping the pose in a flat array
/// is that a character does not pay ECS transform costs for bones nobody attaches to.
/// </summary>
public struct ExposedBone : IEntityComponent
{
	/// <summary>Entity carrying the <see cref="Animator"/> whose skeleton this bone belongs to.</summary>
	public Entity AnimatorEntity;

	/// <summary>Resolved from <see cref="BoneName"/> on first use.</summary>
	public int BoneIndex;

	public string BoneName;

	public ExposedBone(Entity animatorEntity, string boneName)
	{
		AnimatorEntity = animatorEntity;
		BoneName = boneName ?? string.Empty;
		BoneIndex = -1;
	}
}
