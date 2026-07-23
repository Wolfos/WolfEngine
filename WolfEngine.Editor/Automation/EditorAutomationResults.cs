namespace WolfEngine.Editor.Automation;

public sealed record SceneLoadResult(
	string ScenePath,
	Guid SceneAssetId,
	long EditorFrameSequence,
	long RenderFrameSequence);

public sealed record RenderFrameWaitResult(
	int RequestedFrameCount,
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
