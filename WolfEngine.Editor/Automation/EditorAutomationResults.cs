namespace WolfEngine.Editor.Automation;

public sealed record EditorWorkspaceSummaryResult(Guid Id, string Name, IReadOnlyList<string> OpenWindowIds);

public sealed record EditorWorkspaceStateResult(
	Guid ActiveWorkspaceId,
	IReadOnlyList<EditorWorkspaceSummaryResult> Workspaces,
	int SavedLayoutCharacterCount);

/// <summary>One editor panel as of the last drawn frame.</summary>
public sealed record EditorUiWindowResult(
	string Id,
	string DisplayName,
	bool IsOpen,
	bool IsDocked,
	string DockId,
	bool IsSelectedTab,
	bool IsFocused,
	bool IsHovered,
	float X,
	float Y,
	float Width,
	float Height);

/// <summary>Panels that share one dock node, and which of them is the selected tab.</summary>
public sealed record EditorUiDockNodeResult(
	string DockId,
	string? SelectedWindowId,
	IReadOnlyList<string> WindowIds);

public sealed record EditorUiStateResult(
	Guid ActiveWorkspaceId,
	string ActiveWorkspaceName,
	string? FocusedWindowId,
	IReadOnlyList<EditorUiWindowResult> Windows,
	IReadOnlyList<EditorUiDockNodeResult> DockNodes,
	long EditorFrameSequence,
	long RenderFrameSequence);

public sealed record SceneLoadResult(
	string ScenePath,
	Guid SceneAssetId,
	long EditorFrameSequence,
	long RenderFrameSequence);

public sealed record PlayModeStateResult(
	string State,
	long EditorFrameSequence,
	long RenderFrameSequence);

/// <param name="RenderFrameSequence">Every presented frame, including frames that only drew the UI.</param>
/// <param name="SceneFrameSequence">Frames that ran the render graph; the wait counts these.</param>
public sealed record RenderFrameWaitResult(
	int RequestedFrameCount,
	long EditorFrameSequence,
	long RenderFrameSequence,
	long SceneFrameSequence);

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
	long RenderFrameSequence)
{
    public global::WolfEngine.SkinnedGeometryResourceStatistics? PrivateGeometry { get; init; }
    public int LastSkinningDispatchCount { get; init; }
}

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

public sealed record GameplayFrameCaptureResult(
	string OutputPath,
	int Width,
	int Height,
	string CameraName,
	float CameraFov,
	float CameraPositionX,
	float CameraPositionY,
	float CameraPositionZ,
	float CameraForwardX,
	float CameraForwardY,
	float CameraForwardZ,
	bool EnteredPlayMode,
	int SettledRenderFrameCount,
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
	long RenderFrameSequence)
{
	/// <summary>Sum of measured GPU scopes per completed frame, excluding uninstrumented work.</summary>
	public GpuTimingStatistics? MeasuredGpuWork { get; init; }
}

public sealed record CpuProfileNodeResult(
	string Name,
	double DurationMilliseconds,
	long AllocatedBytes,
	IReadOnlyList<CpuProfileNodeResult> Children);

public sealed record CpuThreadProfileResult(
	int ThreadId,
	string ThreadName,
	CpuProfileNodeResult Root);

public sealed record CpuFrameProfileResult(
	IReadOnlyList<CpuThreadProfileResult> Threads,
	long EditorFrameSequence,
	long RenderFrameSequence);

public sealed record AnimationGraphInspectionResult(string? Diagnostic, float Time, bool Playing,
    int BoneCount, int NodeCount, IReadOnlyList<global::WolfEngine.Animation.AnimationContribution> Contributions,
    IReadOnlyList<global::WolfEngine.Animation.AnimationStateInspection> States, IReadOnlyDictionary<string, float> Curves,
    IReadOnlyList<global::WolfEngine.Animation.AnimationPresentationMarker> Markers)
{
    public ulong PoseHash { get; init; }
}

/// <summary>The clip editor as automation sees it: the open clip, selection, playhead, keys and preview pose.</summary>
/// <param name="KeyFrames">Keyed frames per bone name.</param>
/// <param name="UnkeyedBones">Bones with edits that are shown but not keyed.</param>
/// <param name="SelectedBoneLocal">The selected bone's local transform at the playhead, as position, Euler degrees and scale.</param>
/// <param name="PreviewPoseHash">Hash of the preview pose, or zero without a preview. Changes whenever any bone moves.</param>
public sealed record AnimationClipEditorStateResult(
	string? AssetId,
	string ClipName,
	string? Diagnostic,
	bool IsReadOnly,
	bool IsDirty,
	bool IsPromptPending,
	int FramesPerSecond,
	int LengthFrames,
	bool Loop,
	float PlayheadFrame,
	bool IsPlaying,
	bool AutoKey,
	string? SkeletonId,
	int SkeletonBoneCount,
	string? PreviewModelId,
	string? SelectedBone,
	AnimationClipBoneTransformResult? SelectedBoneLocal,
	IReadOnlyDictionary<string, int[]> KeyFrames,
	IReadOnlyList<string> UnkeyedBones,
	int SelectedKeyCount,
	bool CanUndo,
	bool CanRedo,
	ulong PreviewPoseHash,
	bool CanEditMetadata,
	Animation.AnimationCurve[] Curves,
	Animation.AnimationMarker[] Markers);

public sealed record AnimationClipBoneTransformResult(float[] Position, float[] RotationDegrees, float[] Scale);

