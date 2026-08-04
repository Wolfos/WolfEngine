using System.Collections.Concurrent;
using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.Profiling;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Automation;

/// <summary>Direct in-process automation API. Mutations are dispatched onto the editor thread.</summary>
public sealed class EditorRemoteAutomationController
{
	private readonly string _projectPath;
	private readonly IEditorProjectService _projectService;
	private readonly IGameplayAssemblyHost _gameplayAssemblyHost;
	private readonly IEditorSceneWorkspace _sceneWorkspace;
	private readonly IEditorSceneSnapshotService _sceneSnapshotService;
	private readonly IEditorPlaySession _playSession;
	private readonly IEditorInteractionState _interactionState;
	private readonly IEditorCommandService _commandService;
	private readonly ITerrainAuthoringService _terrainAuthoringService;
	private readonly IProjectAssetPipelineService _assetPipelineService;
	private readonly IRenderer _renderer;
	private readonly EditorFrameCoordinator _editorFrameCoordinator;
	private readonly RenderFrameCoordinator _renderFrameCoordinator;
	private readonly RenderGraph _renderGraph;
	private readonly GpuProfiler _gpuProfiler;
	private readonly ConcurrentQueue<Action> _pendingCommands = new();
	private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private bool _initialized;

	public EditorRemoteAutomationController(
		string projectPath,
		IEditorProjectService projectService,
		IGameplayAssemblyHost gameplayAssemblyHost,
		IEditorSceneWorkspace sceneWorkspace,
		IEditorSceneSnapshotService sceneSnapshotService,
		IEditorPlaySession playSession,
		IEditorInteractionState interactionState,
		IEditorCommandService commandService,
		ITerrainAuthoringService terrainAuthoringService,
		IProjectAssetPipelineService assetPipelineService,
		IRenderer renderer,
		EditorFrameCoordinator editorFrameCoordinator,
		RenderFrameCoordinator renderFrameCoordinator,
		RenderGraph renderGraph,
		GpuProfiler gpuProfiler)
	{
		_projectPath = projectPath;
		_projectService = projectService;
		_gameplayAssemblyHost = gameplayAssemblyHost;
		_sceneWorkspace = sceneWorkspace;
		_sceneSnapshotService = sceneSnapshotService;
		_playSession = playSession;
		_interactionState = interactionState;
		_commandService = commandService;
		_terrainAuthoringService = terrainAuthoringService;
		_assetPipelineService = assetPipelineService;
		_renderer = renderer;
		_editorFrameCoordinator = editorFrameCoordinator;
		_renderFrameCoordinator = renderFrameCoordinator;
		_renderGraph = renderGraph;
		_gpuProfiler = gpuProfiler;
	}

	public Task Ready => _ready.Task;
	public Task Stopped => _stopped.Task;
	public bool ShutdownRequested { get; private set; }
	public bool ShouldStop => ShutdownRequested;

	public void Initialize()
	{
		if (_initialized) return;
		_initialized = true;
		try
		{
			if (_projectService.OpenProject(_projectPath, out var error) == false) throw new InvalidOperationException(error);
			_gameplayAssemblyHost.EnsureLoaded();
			_ready.TrySetResult();
		}
		catch (Exception exception)
		{
			_ready.TrySetException(exception);
			ShutdownRequested = true;
			_renderer.RequestShutdown();
		}
	}

	public void ProcessPendingCommands()
	{
		while (_pendingCommands.TryDequeue(out var command)) command();
		if (ShutdownRequested) _renderer.RequestShutdown();
	}

	public Task<(Guid EntityId, string Name)> CreateEntityAsync(string? name, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var resolvedName = string.IsNullOrWhiteSpace(name) ? "Entity" : name.Trim();
			var scene = _sceneWorkspace.CurrentScene;
			var entity = scene.World.CreateEntity(resolvedName, Matrix4x4.Identity);
			var entityId = _sceneSnapshotService.EnsurePersistentEntityId(scene, entity);
			scene.EntityCellKeys[entity] = SceneCellKey.Global;
			_interactionState.MarkSceneDirty();
			return (entityId, resolvedName);
		}, cancellationToken);

	/// <summary>
	/// Instantiates an imported 3D model into the authoring scene by asset name, through the same
	/// path the Assets window uses for a drag and drop.
	/// </summary>
	public Task<InstantiatedModelResult> InstantiateModelAsync(
		string assetName,
		Vector3? spawnPosition,
		float uniformScale,
		CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (string.IsNullOrWhiteSpace(assetName))
			{
				throw new InvalidOperationException("asset_name is required.");
			}

			var projectRoot = Path.GetFullPath(_projectPath);
			var database = _assetPipelineService.LoadDatabase(projectRoot);
			var model = database.Assets.FirstOrDefault(asset =>
				asset.Type == AssetType.Model3D &&
				asset.Name.Contains(assetName, StringComparison.OrdinalIgnoreCase));
			if (model is null)
			{
				var available = string.Join(", ", database.Assets
					.Where(asset => asset.Type == AssetType.Model3D)
					.Select(asset => asset.Name));
				throw new InvalidOperationException(
					$"No 3D model asset matching '{assetName}'. Available models: {available}");
			}

			var scene = _sceneWorkspace.CurrentScene;
			var world = scene.World;
			var existingEntities = new HashSet<Entity>(EnumerateEntities(world));

			_assetPipelineService.InstantiateImportedModel(projectRoot, model.Id, world, spawnPosition);

			var rootEntity = default(Entity);
			var skinnedMeshRendererCount = 0;
			var animatorCount = 0;
			foreach (var entity in EnumerateEntities(world))
			{
				if (existingEntities.Contains(entity))
				{
					continue;
				}

				scene.EntityCellKeys[entity] = SceneCellKey.Global;
				if (world.HasComponent<Parent>(entity) == false && rootEntity.IsValid == false)
				{
					rootEntity = entity;
				}

				if (world.HasComponent<SkinnedMeshRenderer>(entity))
				{
					skinnedMeshRendererCount++;
				}

				if (world.HasComponent<Animator>(entity))
				{
					animatorCount++;
				}
			}

			if (rootEntity.IsValid && Math.Abs(uniformScale - 1.0f) > float.Epsilon)
			{
				world.SetLocalScale(rootEntity, new Vector3(uniformScale, uniformScale, uniformScale));
			}

			var rootEntityId = rootEntity.IsValid
				? _sceneSnapshotService.EnsurePersistentEntityId(scene, rootEntity)
				: Guid.Empty;
			_interactionState.MarkSceneDirty();

			return new InstantiatedModelResult(
				model.Name,
				model.Id,
				rootEntityId,
				skinnedMeshRendererCount,
				animatorCount,
				_editorFrameCoordinator.CompletedSequence);
		}, cancellationToken);

	/// <summary>
	/// Reports what every animator in the authoring scene is actually doing. The bind-pose offset is
	/// the useful number: it is zero when a character is posed at rest, so a non-zero value is
	/// positive evidence that clip data reached the skinning matrices.
	/// </summary>
	public Task<AnimationStateResult> GetAnimationStateAsync(CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var world = _sceneWorkspace.CurrentScene.World;
			var animators = new List<AnimatorStateResult>();

			foreach (var entry in world.View<Animator>())
			{
				ref var animator = ref entry.First;
				var skeleton = animator.Skeleton;
				var clip = animator.Clip;
				var matrices = animator.SkinningMatrices;

				var maxOffset = 0.0f;
				if (matrices is not null)
				{
					for (var i = 0; i < matrices.Length; i++)
					{
						maxOffset = MathF.Max(maxOffset, matrices[i].Translation.Length());
					}
				}

				var matched = 0;
				var unmatched = 0;
				if (animator.PoseSource is SingleClipPoseSource clipSource)
				{
					matched = clipSource.MatchedBoneTrackCount;
					unmatched = clipSource.UnmatchedBoneTrackCount;
				}

				animators.Add(new AnimatorStateResult(
					_sceneSnapshotService.EnsurePersistentEntityId(_sceneWorkspace.CurrentScene, entry.Entity),
					world.HasComponent<NameComponent>(entry.Entity)
						? world.GetComponent<NameComponent>(entry.Entity).Name
						: string.Empty,
					clip?.Name ?? string.Empty,
					skeleton?.Name ?? string.Empty,
					skeleton?.BoneCount ?? 0,
					clip?.TransformTracks.Length ?? 0,
					matched,
					unmatched,
					animator.Time,
					clip?.Duration ?? 0.0f,
					animator.Playing,
					maxOffset));
			}

			var skinnedRendererCount = 0;
			var skinnedWithGpuRange = 0;
			var skinnedRenderers = new List<SkinnedRendererStateResult>();
			foreach (var entry in world.View<WorldTransform, SkinnedMeshRenderer>())
			{
				skinnedRendererCount++;
				ref var renderer = ref entry.Second;
				if (renderer.SkinnedInstance?.HasGpuVertexRange == true)
				{
					skinnedWithGpuRange++;
				}

				var localToWorld = entry.First.LocalToWorld;
				Matrix4x4.Decompose(localToWorld, out var scale, out _, out var translation);
				skinnedRenderers.Add(new SkinnedRendererStateResult(
					world.HasComponent<NameComponent>(entry.Entity)
						? world.GetComponent<NameComponent>(entry.Entity).Name
						: string.Empty,
					renderer.SkinnedInstance?.HasGpuVertexRange ?? false,
					renderer.Mesh?.Vertices.Length ?? 0,
					renderer.Mesh?.BoundingSphere.Radius ?? 0.0f,
					renderer.SkinnedInstance?.BoundingSphere.Radius ?? 0.0f,
					scale.X,
					translation.X,
					translation.Y,
					translation.Z));
			}

			return new AnimationStateResult(
				animators.Count,
				skinnedRendererCount,
				skinnedWithGpuRange,
				animators,
				skinnedRenderers,
				_editorFrameCoordinator.CompletedSequence,
				_renderFrameCoordinator.CompletedSequence);
		}, cancellationToken);

	/// <summary>
	/// Snapshots the transform-bearing entities. Materialised into a list rather than returned lazily
	/// because the ECS view enumerator is a ref struct and cannot survive an iterator.
	/// </summary>
	private static List<Entity> EnumerateEntities(World world)
	{
		var entities = new List<Entity>();
		foreach (var entry in world.View<LocalTransform>())
		{
			entities.Add(entry.Entity);
		}

		return entities;
	}

	public Task DeleteEntityAsync(Guid entityId, CancellationToken cancellationToken) => Enqueue(() =>
	{
		var scene = _sceneWorkspace.CurrentScene;
		if (scene.EntityIds.Values.Contains(entityId) == false) throw new InvalidOperationException($"Entity '{entityId:D}' was not found.");
		_sceneSnapshotService.DeleteEntitiesByPersistentIds(scene, [entityId]);
		_interactionState.MarkSceneDirty();
	}, cancellationToken);

	public Task<SceneLoadResult> LoadSceneAsync(string scenePath, CancellationToken cancellationToken) =>
		EnqueueAsync(async () =>
		{
			var scene = ResolveSceneAsset(scenePath);
			await _commandService.LoadSceneForAutomationAsync(scene.Id, cancellationToken).ConfigureAwait(false);
			return new SceneLoadResult(
				scene.RelativeAssetPath,
				scene.Id,
				_editorFrameCoordinator.CompletedSequence,
				_renderFrameCoordinator.CompletedSequence);
		}, cancellationToken);

	public Task<PlayModeStateResult> EnterPlayModeAsync(CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (_playSession.EnterPlay() == false)
			{
				throw new InvalidOperationException($"Cannot enter Play mode while the editor is {_playSession.State}.");
			}
			return GetPlayModeState();
		}, cancellationToken);

	public Task<PlayModeStateResult> PausePlayModeAsync(CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (_playSession.Pause() == false)
			{
				throw new InvalidOperationException($"Cannot pause Play mode while the editor is {_playSession.State}.");
			}
			return GetPlayModeState();
		}, cancellationToken);

	public Task<PlayModeStateResult> StopPlayModeAsync(CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (_playSession.Stop() == false)
			{
				throw new InvalidOperationException("The editor is not in Play mode.");
			}
			return GetPlayModeState();
		}, cancellationToken);

	public Task<RenderFrameWaitResult> WaitForRenderFramesAsync(int frameCount, CancellationToken cancellationToken) =>
		EnqueueAsync(async () =>
		{
			var renderSequence = await _renderFrameCoordinator
				.WaitForCompletedFramesAsync(frameCount, cancellationToken)
				.ConfigureAwait(false);
			return new RenderFrameWaitResult(frameCount, _editorFrameCoordinator.CompletedSequence, renderSequence);
		}, cancellationToken);

	public Task<TerrainLayerPaintResult> PaintTerrainLayerAsync(
		string? terrainEntityId,
		float localX,
		float localZ,
		int layerIndex,
		float radiusMeters,
		float strength,
		float falloff,
		bool invert,
		CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (_playSession.State != EditorPlayState.Edit)
			{
				throw new InvalidOperationException("Terrain authoring is only available outside Play mode.");
			}

			ValidateFinite(localX, nameof(localX));
			ValidateFinite(localZ, nameof(localZ));
			ValidateFinite(radiusMeters, nameof(radiusMeters));
			ValidateFinite(strength, nameof(strength));
			ValidateFinite(falloff, nameof(falloff));
			if (radiusMeters <= 0.0f) throw new InvalidOperationException("radius_meters must be positive.");
			if (strength < 0.0f || strength > 1.0f) throw new InvalidOperationException("strength must be between 0 and 1.");
			if (falloff <= 0.0f) throw new InvalidOperationException("falloff must be positive.");

			var scene = _sceneWorkspace.CurrentScene;
			var (entity, persistentId) = ResolveTerrainEntity(scene, terrainEntityId);
			var request = new TerrainBrushStrokeRequest(
				TerrainAuthoringSurfaceTarget.LayerMaps,
				TerrainBrushOperation.PaintLayer,
				new TerrainBrushSettings(radiusMeters, strength, falloff, layerIndex, null));
			if (_terrainAuthoringService.BeginStroke(scene, entity, request) == false)
			{
				throw new InvalidOperationException($"Could not begin a layer-paint stroke on terrain entity '{persistentId:D}'.");
			}

			try
			{
				_terrainAuthoringService.AppendStamp(
					new Vector3(localX, 0.0f, localZ),
					1.0f,
					new TerrainBrushModifierState(invert));
				if (_terrainAuthoringService.EndStroke() == false)
				{
					throw new InvalidOperationException($"Could not end the layer-paint stroke on terrain entity '{persistentId:D}'.");
				}
			}
			catch
			{
				_terrainAuthoringService.CancelStroke();
				throw;
			}

			return new TerrainLayerPaintResult(
				persistentId,
				layerIndex,
				localX,
				localZ,
				radiusMeters,
				strength,
				invert,
				_editorFrameCoordinator.CompletedSequence,
				_renderFrameCoordinator.CompletedSequence);
		}, cancellationToken);

	public Task<EditorUndoResult> UndoAsync(CancellationToken cancellationToken) =>
		Enqueue(() => new EditorUndoResult(
			_commandService.Undo(),
			_editorFrameCoordinator.CompletedSequence,
			_renderFrameCoordinator.CompletedSequence), cancellationToken);

	public Task<RayTracingSceneStateResult> GetRayTracingSceneStateAsync(CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var state = _renderGraph.GetRayTracingSceneState();
			return new RayTracingSceneStateResult(
				state.TopLevelAccelerationStructureIdentity ?? "None",
				state.TopLevelAccelerationStructureGeneration,
				state.TopLevelInstanceCount,
				state.MeshBottomLevelAccelerationStructureCount,
				state.TerrainBottomLevelAccelerationStructureCount,
				state.PendingBottomLevelBuildCount,
				state.LastTopLevelUpdateReason.ToString(),
				state.TerrainInstanceCount,
				state.PendingResourceRetirementCount,
				state.LastSubmittedId,
				state.CompletedId,
				_renderFrameCoordinator.CompletedSequence);
		}, cancellationToken);

	public Task<GpuFrameProfileResult> ProfileGpuFramesAsync(int frameCount, CancellationToken cancellationToken) =>
		EnqueueAsync(async () =>
		{
			var wasEnabled = _gpuProfiler.Enabled;
			var marker = _gpuProfiler.BeginCollection();
			try
			{
				var frames = await _gpuProfiler
					.CollectCompletedFramesAsync(marker, frameCount, cancellationToken)
					.ConfigureAwait(false);
				return new GpuFrameProfileResult(
					frameCount,
					frames.Select(frame => frame.FrameIndex).ToArray(),
					SummarizeGpuFrames(frames),
					_editorFrameCoordinator.CompletedSequence,
					_renderFrameCoordinator.CompletedSequence);
			}
			finally
			{
				_gpuProfiler.Enabled = wasEnabled;
			}
		}, cancellationToken);

	public Task<FrameCaptureResult> CaptureFrameAsync(string outputPath, CancellationToken cancellationToken) =>
		EnqueueAsync(async () =>
		{
			var fullOutputPath = ResolveOutputPath(outputPath);
			var capture = await _renderer.CaptureNextFrameAsync(cancellationToken).ConfigureAwait(false);
			Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
			using var image = Image.LoadPixelData<Rgba32>(capture.Rgba8, capture.Width, capture.Height);
			image.SaveAsPng(fullOutputPath);
			return new FrameCaptureResult(
				fullOutputPath,
				capture.Width,
				capture.Height,
				_editorFrameCoordinator.CompletedSequence,
				_renderFrameCoordinator.CompletedSequence);
		}, cancellationToken);

	public Task ShutdownAsync(CancellationToken cancellationToken) => Enqueue(() => { ShutdownRequested = true; }, cancellationToken);

	private Task<T> Enqueue<T>(Func<T> operation, CancellationToken cancellationToken)
	{
		var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pendingCommands.Enqueue(() =>
		{
			try { completion.TrySetResult(operation()); }
			catch (Exception exception) { completion.TrySetException(exception); }
		});
		return completion.Task.WaitAsync(cancellationToken);
	}

	private Task<T> EnqueueAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
	{
		var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pendingCommands.Enqueue(() =>
		{
			try
			{
				_ = CompleteAsync(operation(), completion);
			}
			catch (Exception exception)
			{
				completion.TrySetException(exception);
			}
		});
		return completion.Task.WaitAsync(cancellationToken);
	}

	private Task Enqueue(Action operation, CancellationToken cancellationToken)
	{
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_pendingCommands.Enqueue(() =>
		{
			try { operation(); completion.TrySetResult(); }
			catch (Exception exception) { completion.TrySetException(exception); }
		});
		return completion.Task.WaitAsync(cancellationToken);
	}

	private AssetDatabaseEntry ResolveSceneAsset(string scenePath)
	{
		if (string.IsNullOrWhiteSpace(scenePath))
		{
			throw new InvalidOperationException("scene_path is required.");
		}

		var fullProjectPath = Path.GetFullPath(_projectPath);
		var fullScenePath = Path.GetFullPath(Path.IsPathRooted(scenePath)
			? scenePath
			: Path.Combine(fullProjectPath, scenePath));
		var relativeScenePath = Normalize(Path.GetRelativePath(fullProjectPath, fullScenePath));
		if (relativeScenePath.Equals("..", StringComparison.Ordinal) ||
			relativeScenePath.StartsWith("../", StringComparison.Ordinal))
		{
			throw new InvalidOperationException("scene_path must be inside the open project.");
		}

		var scene = _projectService.CurrentAssetDatabase.Assets.SingleOrDefault(asset =>
			asset.Type == AssetType.Scene &&
			string.Equals(Normalize(asset.RelativeAssetPath), relativeScenePath, StringComparison.OrdinalIgnoreCase));
		return scene ?? throw new InvalidOperationException(
			$"Scene '{scenePath}' was not found in the open project's asset database.");
	}

	private (Entity Entity, Guid PersistentId) ResolveTerrainEntity(EditorScene scene, string? terrainEntityId)
	{
		if (string.IsNullOrWhiteSpace(terrainEntityId) == false)
		{
			if (Guid.TryParse(terrainEntityId, out var requestedId) == false)
			{
				throw new InvalidOperationException("terrain_entity_id must be a GUID.");
			}

			foreach (var entry in scene.EntityIds)
			{
				if (entry.Value != requestedId)
				{
					continue;
				}

				if (scene.World.IsAlive(entry.Key) == false || scene.World.HasComponent<TerrainComponent>(entry.Key) == false)
				{
					throw new InvalidOperationException($"Entity '{requestedId:D}' is not a terrain entity.");
				}

				return (entry.Key, requestedId);
			}

			throw new InvalidOperationException($"Terrain entity '{requestedId:D}' was not found in the authoring scene.");
		}

		(Entity Entity, Guid PersistentId)? match = null;
		foreach (var entry in scene.EntityIds)
		{
			if (scene.World.IsAlive(entry.Key) == false || scene.World.HasComponent<TerrainComponent>(entry.Key) == false)
			{
				continue;
			}

			if (match.HasValue)
			{
				throw new InvalidOperationException("The authoring scene contains multiple terrain entities; terrain_entity_id is required.");
			}

			match = (entry.Key, entry.Value);
		}

		return match ?? throw new InvalidOperationException("The authoring scene does not contain a terrain entity.");
	}

	private string ResolveOutputPath(string outputPath)
	{
		if (string.IsNullOrWhiteSpace(outputPath))
		{
			throw new InvalidOperationException("output_path is required.");
		}

		return Path.GetFullPath(Path.IsPathRooted(outputPath)
			? outputPath
			: Path.Combine(_projectPath, outputPath));
	}

	private PlayModeStateResult GetPlayModeState() => new(
		_playSession.State.ToString(),
		_editorFrameCoordinator.CompletedSequence,
		_renderFrameCoordinator.CompletedSequence);

	private static IReadOnlyList<GpuPassProfileResult> SummarizeGpuFrames(IReadOnlyList<GpuProfileFrame> frames)
	{
		return frames
			.SelectMany(frame => frame.Passes)
			.GroupBy(pass => pass.Name, StringComparer.Ordinal)
			.OrderBy(group => group.Key, StringComparer.Ordinal)
			.Select(passGroup => new GpuPassProfileResult(
				passGroup.Key,
				SummarizeTimings(passGroup.Select(pass => pass.DurationMs)),
				passGroup
					.SelectMany(pass => pass.Scopes)
					.GroupBy(scope => scope.Name, StringComparer.Ordinal)
					.OrderBy(group => group.Key, StringComparer.Ordinal)
					.Select(scopeGroup => new GpuScopeProfileResult(
						scopeGroup.Key,
						SummarizeTimings(scopeGroup.Select(scope => scope.DurationMs))))
					.ToArray()))
			.ToArray();
	}

	private static void ValidateFinite(float value, string parameterName)
	{
		if (float.IsFinite(value) == false)
		{
			throw new InvalidOperationException($"{parameterName} must be finite.");
		}
	}

	private static GpuTimingStatistics SummarizeTimings(IEnumerable<double> samples)
	{
		var sorted = samples.OrderBy(sample => sample).ToArray();
		if (sorted.Length == 0)
		{
			return new GpuTimingStatistics(0, 0.0, 0.0, 0.0);
		}

		var middle = sorted.Length / 2;
		var median = (sorted.Length & 1) == 0
			? (sorted[middle - 1] + sorted[middle]) * 0.5
			: sorted[middle];
		var p95 = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
		return new GpuTimingStatistics(sorted.Length, median, p95, sorted[^1]);
	}

	private static async Task CompleteAsync<T>(Task<T> operation, TaskCompletionSource<T> completion)
	{
		try
		{
			completion.TrySetResult(await operation.ConfigureAwait(false));
		}
		catch (OperationCanceledException exception)
		{
			completion.TrySetCanceled(exception.CancellationToken);
		}
		catch (Exception exception)
		{
			completion.TrySetException(exception);
		}
	}

	private static string Normalize(string path) => path.Replace('\\', '/');

	public void NotifyStopped() => _stopped.TrySetResult();
}
