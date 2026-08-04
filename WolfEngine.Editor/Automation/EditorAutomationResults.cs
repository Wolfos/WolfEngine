namespace WolfEngine.Editor.Automation;

public sealed record SceneLoadResult(
	string ScenePath,
	Guid SceneAssetId,
	long EditorFrameSequence,
	long RenderFrameSequence);

public sealed record PlayModeStateResult(
	string State,
	long EditorFrameSequence,
	long RenderFrameSequence);

public sealed record RenderFrameWaitResult(
	int RequestedFrameCount,
	long EditorFrameSequence,
	long RenderFrameSequence);

public sealed record TerrainLayerPaintResult(
	Guid TerrainEntityId,
	int LayerIndex,
	float LocalX,
	float LocalZ,
	float RadiusMeters,
	float Strength,
	bool Invert,
	long EditorFrameSequence,
	long RenderFrameSequence);

public sealed record EditorUndoResult(
	bool Applied,
	long EditorFrameSequence,
	long RenderFrameSequence);

public sealed record InstantiatedModelResult(
	string AssetName,
	Guid ModelNodeId,
	Guid RootEntityId,
	int SkinnedMeshRendererCount,
	int AnimatorCount,
	long EditorFrameSequence);

/// <summary>
/// Runtime state of one animator, so animation can be validated by measurement rather than only by
/// looking at a screenshot.
/// </summary>
public sealed record AnimatorStateResult(
	Guid EntityId,
	string EntityName,
	string ClipName,
	string SkeletonName,
	int BoneCount,
	int TransformTrackCount,
	int MatchedBoneTrackCount,
	int UnmatchedBoneTrackCount,
	float Time,
	float Duration,
	bool Playing,
	/// <summary>Largest translation difference between the current skinning matrices and the bind pose.</summary>
	float MaxBoneOffsetFromBindPose);

public sealed record SkinnedRendererStateResult(
	string EntityName,
	bool HasGpuVertexRange,
	int VertexCount,
	float BindPoseBoundsRadius,
	float InstanceBoundsRadius,
	float WorldScaleX,
	float WorldPositionX,
	float WorldPositionY,
	float WorldPositionZ);

public sealed record AnimationStateResult(
	int AnimatorCount,
	int SkinnedMeshRendererCount,
	int SkinnedInstancesWithGpuRange,
	IReadOnlyList<AnimatorStateResult> Animators,
	IReadOnlyList<SkinnedRendererStateResult> SkinnedRenderers,
	long EditorFrameSequence,
	long RenderFrameSequence);

public sealed record RayTracingSceneStateResult(
	string TlasIdentity,
	long TlasGeneration,
	int TlasInstanceCount,
	int MeshBlasCount,
	int TerrainBlasCount,
	int PendingBlasBuilds,
	string LastTlasUpdateReason,
	int TerrainInstanceCount,
	int PendingRtResourceRetirements,
	ulong LastSubmittedId,
	ulong CompletedId,
	long RenderFrameSequence);

public sealed record FrameCaptureResult(
	string OutputPath,
	int Width,
	int Height,
	long EditorFrameSequence,
	long RenderFrameSequence);

public sealed record GpuTimingStatistics(
	int SampleCount,
	double MedianMilliseconds,
	double P95Milliseconds,
	double MaximumMilliseconds);

public sealed record GpuScopeProfileResult(string Name, GpuTimingStatistics Timing);

public sealed record GpuPassProfileResult(
	string Name,
	GpuTimingStatistics Timing,
	IReadOnlyList<GpuScopeProfileResult> Scopes);

public sealed record GpuFrameProfileResult(
	int RequestedFrameCount,
	IReadOnlyList<ulong> GpuFrameIndices,
	IReadOnlyList<GpuPassProfileResult> Passes,
	long EditorFrameSequence,
	long RenderFrameSequence);
