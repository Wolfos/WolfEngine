using WolfEngine.Editor.UnityImport;
using System.Collections.Concurrent;
using System.Numerics;
using WolfEngine.Editor.Tooling.Imaging;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.Input;
using WolfEngine.Gameplay;
using WolfEngine.Profiling;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.UI;
using WolfEngine.Build;
using WolfEngine.Vfx;

namespace WolfEngine.Editor.Automation;

/// <summary>Direct in-process automation API. Mutations are dispatched onto the editor thread.</summary>
public sealed partial class EditorRemoteAutomationController
{
	private readonly string _projectPath;
	private readonly EditorCameraSystem _cameraSystem;
	private readonly IEditorProjectService _projectService;
	private readonly IGameplayAssemblyHost _gameplayAssemblyHost;
	private readonly IEditorSceneWorkspace _sceneWorkspace;
	private readonly IAssetSelectionService _assetSelectionService;
	private readonly IEditorSceneSnapshotService _sceneSnapshotService;
	private readonly IEditorPlaySession _playSession;
	private readonly SceneLoadRequests _sceneLoadRequests;
	private readonly IApplicationQuitService _quitService;
	private readonly IInputSystem _inputSystem;
	private readonly IImGuiInputSink _imguiInput;
	private readonly IEditorInteractionState _interactionState;
	private readonly IEditorCommandService _commandService;
	private readonly ISceneAuthoringService _sceneAuthoring;
	private readonly ITerrainAuthoringService _terrainAuthoringService;
	private readonly FoliageAuthoringService _foliageAuthoringService;
	private readonly UnitySceneImporter _unitySceneImporter;
	private readonly IProjectAssetPipelineService _assetPipelineService;
	private readonly IPrefabAssetCreator _prefabAssetCreator;
	private readonly IRenderer _renderer;
	private readonly EditorFrameCoordinator _editorFrameCoordinator;
	private readonly RenderFrameCoordinator _renderFrameCoordinator;
	private readonly RenderGraph _renderGraph;
	private readonly GpuProfiler _gpuProfiler;
	private readonly EditorViewportStateBus _viewportStateBus;
	private readonly IEditorWorkspaceService _workspaces;
	private readonly EditorWindowRegistry _windows;
	private readonly SemaphoreSlim _captureGate = new(1, 1);
	private readonly ConcurrentQueue<Action> _pendingCommands = new();
	private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private bool _initialized;

	private readonly IGameBuildService _gameBuildService;
	private readonly AnimationClipEditorSession _clipEditor;
	private readonly VfxService _vfx;
	private readonly VfxAssetEditor _vfxEditor;
	private readonly Rendering.Splash.SplashScreen _splashScreen;
	private readonly Rendering.Shaders.EngineShaderOptions _engineShaderOptions;
	private readonly Rendering.Shaders.IShaderProvider _shaderProvider;
	private readonly Rendering.BindlessResourceRegistry _bindlessRegistry;

	public EditorRemoteAutomationController(
		string projectPath,
		IEditorProjectService projectService,
		IGameplayAssemblyHost gameplayAssemblyHost,
		IEditorSceneWorkspace sceneWorkspace,
		IAssetSelectionService assetSelectionService,
		IEditorSceneSnapshotService sceneSnapshotService,
		IEditorPlaySession playSession,
		IInputSystem inputSystem,
		IEditorInteractionState interactionState,
		IEditorCommandService commandService,
		ISceneAuthoringService sceneAuthoring,
		ITerrainAuthoringService terrainAuthoringService,
		FoliageAuthoringService foliageAuthoringService,
		UnitySceneImporter unitySceneImporter,
		IProjectAssetPipelineService assetPipelineService,
		IPrefabAssetCreator prefabAssetCreator,
		IRenderer renderer,
		EditorFrameCoordinator editorFrameCoordinator,
		RenderFrameCoordinator renderFrameCoordinator,
		RenderGraph renderGraph,
		GpuProfiler gpuProfiler,
		EditorViewportStateBus viewportStateBus,
		IEditorWorkspaceService workspaces,
		EditorWindowRegistry windows,
		EditorCameraSystem cameraSystem,
		IImGuiInputSink imguiInput,
		SceneLoadRequests sceneLoadRequests,
		IApplicationQuitService quitService,
		IGameBuildService gameBuildService,
		AnimationClipEditorSession clipEditor,
		VfxService vfx,
		VfxAssetEditor vfxEditor,
		Rendering.Splash.SplashScreen splashScreen,
		Rendering.Shaders.EngineShaderOptions engineShaderOptions,
		Rendering.Shaders.IShaderProvider shaderProvider,
		Rendering.BindlessResourceRegistry bindlessRegistry)
	{
		_splashScreen = splashScreen;
		_engineShaderOptions = engineShaderOptions;
		_shaderProvider = shaderProvider;
		_bindlessRegistry = bindlessRegistry;
		_clipEditor = clipEditor;
		_vfx = vfx;
		_vfxEditor = vfxEditor;
		_sceneLoadRequests = sceneLoadRequests;
		_quitService = quitService;
		_gameBuildService = gameBuildService;
		_cameraSystem = cameraSystem;
		_viewportStateBus = viewportStateBus;
		_projectPath = projectPath;
		_projectService = projectService;
		_gameplayAssemblyHost = gameplayAssemblyHost;
		_sceneWorkspace = sceneWorkspace;
		_assetSelectionService = assetSelectionService;
		_sceneSnapshotService = sceneSnapshotService;
		_playSession = playSession;
		_inputSystem = inputSystem;
		_imguiInput = imguiInput;
		_interactionState = interactionState;
		_commandService = commandService;
		_sceneAuthoring = sceneAuthoring;
		_terrainAuthoringService = terrainAuthoringService;
		_foliageAuthoringService = foliageAuthoringService;
		_unitySceneImporter = unitySceneImporter ?? throw new ArgumentNullException(nameof(unitySceneImporter));
		_assetPipelineService = assetPipelineService;
		_prefabAssetCreator = prefabAssetCreator;
		_renderer = renderer;
		_editorFrameCoordinator = editorFrameCoordinator;
		_renderFrameCoordinator = renderFrameCoordinator;
		_renderGraph = renderGraph;
		_gpuProfiler = gpuProfiler;
		_workspaces = workspaces;
		_windows = windows;
	}

	public Task<EditorWorkspaceStateResult> GetWorkspaceStateAsync(CancellationToken cancellationToken) =>
		Enqueue(CreateWorkspaceState, cancellationToken);

	public Task<string> SelectAssetAsync(Guid assetId, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (!_projectService.TryGetAsset(assetId, out var asset))
				throw new InvalidOperationException($"Asset '{assetId}' was not found in the open project.");
			_assetSelectionService.Select(assetId);
			return $"Selected asset '{asset.Name}' ({assetId:D}) in the Asset Editor.";
		}, cancellationToken);

	public Task<EditorWorkspaceStateResult> CreateWorkspaceAsync(string name, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (!_workspaces.TryCreate(name, EditorDocumentKind.Scene, out _, out var error)) throw new InvalidOperationException(error);
			return CreateWorkspaceState();
		}, cancellationToken);

	/// <summary>Renames a workspace and, when <paramref name="ownedDocument"/> is given, changes the document it owns.</summary>
	public Task<EditorWorkspaceStateResult> ConfigureWorkspaceAsync(
		string workspaceId,
		string name,
		string? ownedDocument,
		CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (Guid.TryParse(workspaceId, out var id) == false)
			{
				throw new InvalidOperationException("workspace_id must be a GUID.");
			}

			var workspace = _workspaces.Workspaces.FirstOrDefault(candidate => candidate.Id == id) ??
				throw new InvalidOperationException("Workspace was not found.");
			var kind = workspace.OwnedDocument;
			if (ownedDocument is not null &&
			    (Enum.TryParse(ownedDocument, ignoreCase: true, out kind) == false || Enum.IsDefined(kind) == false))
			{
				throw new InvalidOperationException(
					$"owned_document must be one of: {string.Join(", ", Enum.GetNames<EditorDocumentKind>())}.");
			}

			if (_workspaces.TryConfigure(id, name, kind, out var error) == false)
			{
				throw new InvalidOperationException(error);
			}

			return CreateWorkspaceState();
		}, cancellationToken);

	public Task<EditorWorkspaceStateResult> ActivateWorkspaceAsync(string workspaceId, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (!Guid.TryParse(workspaceId, out var id) || !_workspaces.Activate(id)) throw new InvalidOperationException("Workspace was not found.");
			return CreateWorkspaceState();
		}, cancellationToken);

	public Task<EditorWorkspaceStateResult> DeleteWorkspaceAsync(string workspaceId, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (!Guid.TryParse(workspaceId, out var id) || !_workspaces.Delete(id)) throw new InvalidOperationException("Workspace was not found or is the only workspace.");
			return CreateWorkspaceState();
		}, cancellationToken);

	public Task<EditorWorkspaceStateResult> SetWorkspaceWindowOpenAsync(string windowId, bool open, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (!EditorWindowIds.All.Contains(windowId)) throw new InvalidOperationException($"Unknown window id '{windowId}'.");
			if (open) _windows.Open(windowId); else _workspaces.CloseWindow(windowId);
			return CreateWorkspaceState();
		}, cancellationToken);

	private EditorWorkspaceStateResult CreateWorkspaceState() => new(
		_workspaces.ActiveWorkspace.Id,
		_workspaces.Workspaces.Select(workspace => new EditorWorkspaceSummaryResult(
			workspace.Id,
			workspace.Name,
			workspace.OwnedDocument.ToString(),
			_commandService.GetDocument(workspace.OwnedDocument)?.IsDirty == true,
			workspace.OpenWindows.OrderBy(id => id, StringComparer.Ordinal).ToArray())).ToArray(),
		EditorPreferences.GetWorkspaceSettings()?.ImGuiSettings.Length ?? 0);

	/// <summary>
	/// Reports the editor's panel and docking state as of the last drawn frame. Wait for render frames
	/// after any change that should be reflected here, because the values are recorded while drawing.
	/// </summary>
	public Task<EditorUiStateResult> GetEditorUiStateAsync(CancellationToken cancellationToken) =>
		Enqueue(CreateEditorUiState, cancellationToken);

	private EditorUiStateResult CreateEditorUiState()
	{
		var windows = _windows.GetUiState();
		var results = windows
			.Select(window => new EditorUiWindowResult(
				window.Id,
				window.DisplayName,
				window.IsOpen,
				window.IsDocked,
				FormatDockId(window.DockId),
				window.IsSelectedTab,
				window.IsFocused,
				window.IsHovered,
				window.X,
				window.Y,
				window.Width,
				window.Height))
			.ToArray();

		var dockNodes = results
			.Where(window => window.IsDocked)
			.GroupBy(window => window.DockId, StringComparer.Ordinal)
			.OrderBy(group => group.Key, StringComparer.Ordinal)
			.Select(group => new EditorUiDockNodeResult(
				group.Key,
				group.FirstOrDefault(window => window.IsSelectedTab)?.Id,
				group.Select(window => window.Id).ToArray()))
			.ToArray();

		return new EditorUiStateResult(
			_workspaces.ActiveWorkspace.Id,
			_workspaces.ActiveWorkspace.Name,
			results.FirstOrDefault(window => window.IsFocused)?.Id,
			results,
			dockNodes,
			_editorFrameCoordinator.CompletedSequence,
			_renderFrameCoordinator.CompletedSequence);
	}

	private static string FormatDockId(uint dockId) => $"0x{dockId:X8}";

	public Task<string> SetViewportFrameLimitAsync(int maxFramesPerSecond, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (maxFramesPerSecond < 0)
			{
				throw new ArgumentOutOfRangeException(nameof(maxFramesPerSecond), "The limit cannot be negative.");
			}

			// Automation runs on isolated preferences, so this does not touch the user's saved settings.
			EditorPreferences.SetLimitFPS(maxFramesPerSecond > 0);
			EditorPreferences.SetMaxFPS(maxFramesPerSecond);
			return maxFramesPerSecond > 0
				? $"Viewport limited to {EditorPreferences.GetMaxFPS()} FPS."
				: "Viewport frame limit off.";
		}, cancellationToken);

	public Task<string> SetAntiAliasingAsync(string mode, bool enabled, bool casSharpening, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (!Enum.TryParse<AntiAliasingMode>(mode, true, out var selectedMode) || !Enum.IsDefined(selectedMode))
				throw new ArgumentException("Anti-aliasing mode must be Taa or Fsr3.", nameof(mode));
			RenderConfig? config = null;
			foreach (var entry in _sceneWorkspace.CurrentScene.World.View<WorldSettings>())
				config = entry.First.RenderConfigAsset.Asset;
			if (config is null)
				throw new InvalidOperationException("The authoring scene has no resolved render config asset.");
			var settings = config.AntiAliasing;
			settings.Mode = selectedMode;
			settings.Enabled = enabled;
			var taa = settings.Taa;
			taa.EnableCasSharpen = casSharpening;
			settings.Taa = taa;
			config.AntiAliasing = settings;
			return $"Anti-aliasing: {selectedMode}, enabled: {enabled}, CAS: {settings.UsesCasSharpening}. Changed in memory only.";
		}, cancellationToken);

	public Task<string> SetRenderScaleAsync(float renderScale, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var scene = _playSession.RuntimeScene ?? _sceneWorkspace.CurrentScene;
			RenderConfig? config = null;
			foreach (var entry in scene.World.View<WorldSettings>())
			{
				config = entry.First.RenderConfigAsset.Asset;
			}

			if (config is null)
			{
				throw new InvalidOperationException("The active scene has no resolved render config asset.");
			}

			config.RenderScale = renderScale;

			return $"Render scale: {config.RenderScale}. Changed in memory only.";
		}, cancellationToken);

	public Task<string> SetFsr3SettingsAsync(
		float alphaTestReactiveScale,
		float transparencyAndCompositionMaskScale,
		bool sharpening,
		float sharpness,
		CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var scene = _playSession.RuntimeScene ?? _sceneWorkspace.CurrentScene;
			RenderConfig? config = null;
			foreach (var entry in scene.World.View<WorldSettings>())
			{
				config = entry.First.RenderConfigAsset.Asset;
			}

			if (config is null)
			{
				throw new InvalidOperationException("The active scene has no resolved render config asset.");
			}

			var settings = config.AntiAliasing;
			settings.AlphaTestReactiveScale = alphaTestReactiveScale;
			settings.TransparencyAndCompositionMaskScale = transparencyAndCompositionMaskScale;
			settings.EnableSharpening = sharpening;
			settings.Sharpness = sharpness;
			config.AntiAliasing = settings;

			return $"FSR3: alpha-test reactive {alphaTestReactiveScale}, composition mask {transparencyAndCompositionMaskScale}, RCAS {sharpening} at {sharpness}. Changed in memory only.";
		}, cancellationToken);

	public Task<string> SetRenderEffectsAsync(bool ambientOcclusion, bool reflections, bool diffuseGlobalIllumination,
		bool volumetricFog, float? fogShadowDimming, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var scene = _playSession.RuntimeScene ?? _sceneWorkspace.CurrentScene;
			RenderConfig? config = null;
			foreach (var entry in scene.World.View<WorldSettings>())
				config = entry.First.RenderConfigAsset.Asset;
			if (config is null)
				throw new InvalidOperationException("The active scene has no resolved render config asset.");
			var ao = config.AmbientOcclusion;
			ao.Enabled = ambientOcclusion;
			config.AmbientOcclusion = ao;
			var reflection = config.Reflections;
			reflection.Enabled = reflections;
			config.Reflections = reflection;
			var gi = config.DiffuseGlobalIllumination;
			gi.Enabled = diffuseGlobalIllumination;
			config.DiffuseGlobalIllumination = gi;
			var fog = config.VolumetricFog;
			fog.Enabled = volumetricFog;
			if (fogShadowDimming is { } dimming)
			{
				fog.ShadowDimming = dimming;
			}

			config.VolumetricFog = fog;
			return "Render effects updated in memory only.";
		}, cancellationToken);

	public Task<string> SetShadowCasterCullingAsync(bool enabled, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var scene = _playSession.RuntimeScene ?? _sceneWorkspace.CurrentScene;
			RenderConfig? config = null;
			foreach (var entry in scene.World.View<WorldSettings>())
				config = entry.First.RenderConfigAsset.Asset;
			if (config is null)
				throw new InvalidOperationException("The active scene has no resolved render config asset.");
			var settings = config.ShadowMaps;
			settings.TightCasterCulling = enabled;
			config.ShadowMaps = settings;
			return $"Tight shadow-caster culling: {enabled}. Changed in memory only.";
		}, cancellationToken);

	public Task<string> SetMeshLodAsync(
		bool? enabled,
		float? bias,
		float? shadowBias,
		CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var scene = _playSession.RuntimeScene ?? _sceneWorkspace.CurrentScene;
			RenderConfig? config = null;
			foreach (var entry in scene.World.View<WorldSettings>())
			{
				config = entry.First.RenderConfigAsset.Asset;
			}

			if (config is null)
			{
				throw new InvalidOperationException("The active scene has no resolved render config asset.");
			}

			var settings = config.MeshLod;
			if (enabled.HasValue)
			{
				settings.Enabled = enabled.Value;
			}

			if (bias.HasValue)
			{
				settings.Bias = bias.Value;
			}

			if (shadowBias.HasValue)
			{
				settings.ShadowBias = shadowBias.Value;
			}

			config.MeshLod = settings;
			return $"Mesh LOD enabled: {settings.Enabled}, bias: {settings.Bias}, shadow bias: {settings.ShadowBias}. Changed in memory only.";
		}, cancellationToken);

	public Task<string> SetShadowVertexWeldingAsync(bool enabled, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var scene = _playSession.RuntimeScene ?? _sceneWorkspace.CurrentScene;
			RenderConfig? config = null;
			foreach (var entry in scene.World.View<WorldSettings>())
				config = entry.First.RenderConfigAsset.Asset;
			if (config is null)
				throw new InvalidOperationException("The active scene has no resolved render config asset.");
			var settings = config.ShadowMaps;
			settings.WeldShadowVertices = enabled;
			config.ShadowMaps = settings;
			return $"Shadow vertex welding: {enabled}. Changed in memory only.";
		}, cancellationToken);

	public Task<string> SetDdgiRelocationAsync(bool enabled, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var scene = _playSession.RuntimeScene ?? _sceneWorkspace.CurrentScene;
			RenderConfig? config = null;
			foreach (var entry in scene.World.View<WorldSettings>())
				config = entry.First.RenderConfigAsset.Asset;
			if (config is null)
				throw new InvalidOperationException("The active scene has no resolved render config asset.");
			var settings = config.DiffuseGlobalIllumination;
			settings.ProbeRelocationEnabled = enabled;
			config.DiffuseGlobalIllumination = settings;
			return $"DDGI relocation: {enabled}. Changed in memory only.";
		}, cancellationToken);

	public Task<string> SetDdgiFinalGatherAsync(bool enabled, string resolution, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (Enum.TryParse<DdgiFinalGatherResolution>(resolution, ignoreCase: true, out var parsedResolution) == false)
				throw new ArgumentException($"Unknown DDGI final gather resolution '{resolution}'. Use Full, Half or Quarter.", nameof(resolution));
			var scene = _playSession.RuntimeScene ?? _sceneWorkspace.CurrentScene;
			RenderConfig? config = null;
			foreach (var entry in scene.World.View<WorldSettings>())
				config = entry.First.RenderConfigAsset.Asset;
			if (config is null)
				throw new InvalidOperationException("The active scene has no resolved render config asset.");
			var settings = config.DiffuseGlobalIllumination;
			var finalGather = settings.FinalGather;
			finalGather.Enabled = enabled;
			finalGather.Resolution = parsedResolution;
			settings.FinalGather = finalGather;
			config.DiffuseGlobalIllumination = settings;
			return $"DDGI final gather: {enabled} at {parsedResolution} resolution. Changed in memory only.";
		}, cancellationToken);

	public Task<string> SetIndirectLightMinimumAsync(float minimum, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var scene = _playSession.RuntimeScene ?? _sceneWorkspace.CurrentScene;
			RenderConfig? config = null;
			foreach (var entry in scene.World.View<WorldSettings>())
				config = entry.First.RenderConfigAsset.Asset;
			if (config is null)
				throw new InvalidOperationException("The active scene has no resolved render config asset.");
			var settings = config.Lighting;
			settings.IndirectLightMinimum = minimum;
			config.Lighting = settings;
			return $"Indirect light minimum: {minimum}. Changed in memory only.";
		}, cancellationToken);

	/// <summary>
	/// Sets an entity's local rotation as one scene-authoring transform edit: undoable in the authoring
	/// scene, unrecorded in the Play-mode runtime scene.
	/// </summary>
	public Task SetEntityRotationAsync(Guid entityId, Vector3 eulerDegrees, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var scene = _playSession.ActiveScene;
			var entity = GetActiveSceneEntity(scene, entityId);
			var radians = eulerDegrees * (MathF.PI / 180.0f);
			var edit = _sceneAuthoring.BeginTransformEdit(scene, [entity]);
			edit.SetLocalRotation(entity, Quaternion.CreateFromYawPitchRoll(radians.Y, radians.X, radians.Z));
			edit.Commit();
		}, cancellationToken);

	/// <inheritdoc cref="SetEntityRotationAsync"/>
	public Task SetEntityScaleAsync(Guid entityId, Vector3 scale, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var scene = _playSession.ActiveScene;
			var entity = GetActiveSceneEntity(scene, entityId);
			var edit = _sceneAuthoring.BeginTransformEdit(scene, [entity]);
			edit.SetLocalScale(entity, scale);
			edit.Commit();
		}, cancellationToken);

	private static Entity GetActiveSceneEntity(EditorScene scene, Guid entityId)
	{
		if (scene.TryGetEntity(entityId, out var entity) == false)
		{
			throw new InvalidOperationException($"Entity '{entityId:D}' was not found in the active scene.");
		}

		return entity;
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
			var gameplayLoad = _gameplayAssemblyHost.EnsureLoaded();
			if (gameplayLoad.BuildFailureOutput is { } buildFailureOutput)
			{
				Console.WriteLine(GameplayStaleBuildMessage.Create(buildFailureOutput));
			}

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

	/// <summary>
	/// Creates an empty entity through scene authoring, the way the hierarchy's Create menu does: undoable
	/// in the authoring scene, unrecorded in the Play-mode runtime scene.
	/// </summary>
	public Task<(Guid EntityId, string Name)> CreateEntityAsync(string? name, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var scene = _playSession.ActiveScene;
			var created = _sceneAuthoring.CreateEntity(scene, EntityCreationPreset.Empty, Vector3.Zero, name);
			ThrowIfRefused(created.RefusalReason);

			var entity = created.Value;
			return (scene.EntityIds[entity], scene.World.GetComponent<NameComponent>(entity).Name);
		}, cancellationToken);

	private static void ThrowIfRefused(string? refusalReason)
	{
		if (refusalReason is not null)
		{
			throw new InvalidOperationException(refusalReason);
		}
	}

	/// <summary>
	/// Places an imported 3D model, found by name in the project's asset catalog (mounted engine assets
	/// included), through scene authoring, the way the Assets window's Add to Scene does.
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

			var models = _projectService.CurrentAssetCatalog.Assets
				.Where(mountedAsset => mountedAsset.Asset.Type == AssetType.Model3D)
				.Select(mountedAsset => mountedAsset.Asset)
				.ToList();
			var model = models.FirstOrDefault(asset => asset.Name.Contains(assetName, StringComparison.OrdinalIgnoreCase));
			if (model is null)
			{
				var available = string.Join(", ", models.Select(asset => asset.Name));
				throw new InvalidOperationException(
					$"No 3D model asset matching '{assetName}'. Available models: {available}");
			}

			var scene = _playSession.ActiveScene;
			var placed = _sceneAuthoring.Instantiate(scene, model.Id, spawnPosition);
			ThrowIfRefused(placed.RefusalReason);

			var world = scene.World;
			var rootEntity = placed.Value.Root;
			if (Math.Abs(uniformScale - 1.0f) > float.Epsilon)
			{
				var edit = _sceneAuthoring.BeginTransformEdit(scene, [rootEntity]);
				edit.SetLocalScale(rootEntity, new Vector3(uniformScale, uniformScale, uniformScale));
				edit.Commit();
			}

			var skinnedMeshRendererCount = 0;
			var animatorCount = 0;
			var meshLodLevelCounts = new List<int>();
			foreach (var entity in placed.Value.Entities)
			{
				if (world.HasComponent<SkinnedMeshRenderer>(entity))
				{
					skinnedMeshRendererCount++;
				}

				if (world.HasComponent<Animator>(entity))
				{
					animatorCount++;
				}

				if (world.HasComponent<MeshRenderer>(entity))
				{
					var meshRenderer = world.GetComponent<MeshRenderer>(entity);
					meshRenderer.TryValidate();
					meshLodLevelCounts.Add(meshRenderer.Mesh?.LodLevelCount ?? 0);
				}
			}

			return new InstantiatedModelResult(
				model.Name,
				model.Id,
				scene.EntityIds[rootEntity],
				skinnedMeshRendererCount,
				animatorCount,
				_editorFrameCoordinator.CompletedSequence,
				meshLodLevelCounts.Count,
				meshLodLevelCounts);
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
				var instance = animator.GraphInstance;
                var clip = instance?.Program.Clips.FirstOrDefault(c => c is not null)?.Clip;
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
                if (instance is not null)
                    matched = instance.Program.Clips.Where(c => c is not null).Sum(c => c!.BoneSlots.Count(slot => slot >= 0));

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
					instance?.Time ?? 0,
					clip?.Duration ?? 0.0f,
					instance?.Playing ?? false,
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
				_renderFrameCoordinator.CompletedSequence)
            { PrivateGeometry = _renderGraph.GetSkinnedGeometryResourceStatistics(), LastSkinningDispatchCount = _renderGraph.LastSkinningDispatchCount };
		}, cancellationToken);

	/// <summary>
	/// Deletes an entity and its children through scene authoring, the way the hierarchy's Delete does:
	/// undoable in the authoring scene, and refused inside a prefab instance.
	/// </summary>
	public Task DeleteEntityAsync(Guid entityId, CancellationToken cancellationToken) => Enqueue(() =>
	{
		var scene = _playSession.ActiveScene;
		if (scene.TryGetEntity(entityId, out var entity) == false)
		{
			throw new InvalidOperationException($"Entity '{entityId:D}' was not found.");
		}

		ThrowIfRefused(_sceneAuthoring.DeleteEntities(scene, [entity]).RefusalReason);
	}, cancellationToken);

	/// <summary>
	/// Saves an entity and its children as a prefab asset through the same path as the entity
	/// hierarchy's Save as Prefab, so an imported model can become a reusable prefab.
	/// </summary>
	public Task<SavedPrefabResult> SaveEntityAsPrefabAsync(
		Guid entityId,
		string folderPath,
		CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var scene = _sceneWorkspace.CurrentScene;
			if (scene.TryGetEntity(entityId, out var entity) == false)
			{
				throw new InvalidOperationException($"Entity '{entityId:D}' was not found.");
			}

			var result = _prefabAssetCreator.SaveEntityAsPrefab(scene, entity, folderPath);
			if (result.Success == false || result.AssetId is null)
			{
				throw new InvalidOperationException(result.ErrorMessage ?? "Saving the prefab failed.");
			}

			var prefabNodeId = result.AssetId.Value;
			if (_projectService.TryGetAsset(prefabNodeId, out var prefabAsset) == false)
			{
				throw new InvalidOperationException($"Prefab '{prefabNodeId:D}' was saved but is not in the asset database.");
			}

			return new SavedPrefabResult(
				prefabNodeId,
				prefabAsset.RelativeSourcePath,
				_editorFrameCoordinator.CompletedSequence);
		}, cancellationToken);

	/// <summary>
	/// Selects entities by persistent GUID through the same selection the entity
	/// hierarchy and viewport picking write to, so anything driven by selection --
	/// the transform gizmo, the outline -- behaves as it does for a real click.
	/// </summary>
	public Task<int> SelectEntitiesAsync(IReadOnlyList<Guid> entityIds, CancellationToken cancellationToken) => Enqueue(() =>
	{
		ArgumentNullException.ThrowIfNull(entityIds);
		var scene = _sceneWorkspace.CurrentScene;
		EditorGui.ClearEntitySelection();
		if (entityIds.Count == 0)
		{
			return 0;
		}

		var selected = 0;
		foreach (var entityId in entityIds)
		{
			if (scene.TryGetEntity(entityId, out var entity) == false)
			{
				throw new InvalidOperationException($"Entity '{entityId:D}' was not found.");
			}

			EditorGui.AddEntitySelection(entity, scene.World, requestFocus: false);
			selected++;
		}

		return selected;
	}, cancellationToken);

	/// <summary>Imports a Unity export as a new scene through <see cref="UnitySceneImporter"/>, then opens that scene.</summary>
	public Task<UnitySceneImportResult> ImportUnitySceneAsync(
		string exportFolder,
		string unityProjectPath,
		string targetFolder,
		string sceneName,
		string? renderConfigPath,
		CancellationToken cancellationToken) =>
		EnqueueAsync(async () =>
		{
			if (_playSession.State != EditorPlayState.Edit)
			{
				throw new InvalidOperationException("Stop Play mode before importing a scene.");
			}

			var report = _unitySceneImporter.Import(new UnitySceneImportRequest(
				exportFolder,
				targetFolder,
				sceneName,
				unityProjectPath,
				renderConfigPath));
			await _commandService.LoadSceneForAutomationAsync(report.SceneId, cancellationToken).ConfigureAwait(false);

			return new UnitySceneImportResult(
				report,
				_editorFrameCoordinator.CompletedSequence,
				_renderFrameCoordinator.CompletedSequence);
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

	/// <summary>
	/// Creates a project and switches the editor to it through the File menu's New Project path. The switch is
	/// queued as a scene replacement, so this waits on completed render frames until the new project is open.
	/// A failed creation is reported to the editor's notifications and leaves the old project open, which this
	/// reports as a timeout.
	/// </summary>
	public async Task<string> CreateProjectAsync(string parentFolder, string projectName, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(parentFolder))
		{
			throw new InvalidOperationException("parent_folder is required.");
		}

		if (string.IsNullOrWhiteSpace(projectName))
		{
			throw new InvalidOperationException("project_name is required.");
		}

		var fullParentFolder = Path.GetFullPath(parentFolder);
		var expectedProjectRoot = Path.GetFullPath(Path.Combine(fullParentFolder, projectName.Trim()));

		var queued = await Enqueue(
			() => _commandService.RequestCreateProject(fullParentFolder, projectName),
			cancellationToken).ConfigureAwait(false);

		if (queued == false)
		{
			throw new InvalidOperationException("The editor could not queue the project switch. Stop Play mode and wait for other operations to finish.");
		}

		const int maxFramesToWait = 600;

		for (var frame = 0; frame < maxFramesToWait; frame++)
		{
			await WaitForRenderFramesAsync(1, cancellationToken).ConfigureAwait(false);

			var isOpen = await Enqueue(
				() => string.Equals(_projectService.ProjectRootPath, expectedProjectRoot, StringComparison.Ordinal) &&
					_commandService.LoadingSceneAssetId.HasValue == false,
				cancellationToken).ConfigureAwait(false);

			if (isOpen)
			{
				return expectedProjectRoot;
			}
		}

		throw new TimeoutException(
			$"Project '{expectedProjectRoot}' was not open after {maxFramesToWait} render frames. " +
			"If the folder already existed or could not be written, the editor reported the error as a notification.");
	}

	/// <summary>
	/// Builds the open project into a runnable game, as the Build menu does. Building compiles gameplay code and
	/// publishes the runtime, which takes far longer than a frame, so like the menu it runs off the editor thread.
	/// </summary>
	public Task<GameBuildResult> BuildGameAsync(string outputPath, bool debug, string? target, CancellationToken cancellationToken)
	{
		var fullOutputPath = Path.GetFullPath(outputPath);

		return Task.Run(() => _gameBuildService.Build(fullOutputPath, debug, target), cancellationToken);
	}

	public Task<SceneLoadResult> LoadPlaySceneAsync(string scenePath, CancellationToken cancellationToken) =>
		EnqueueAsync(async () =>
		{
			var scene = ResolveSceneAsset(scenePath);
			var world = _playSession.RuntimeScene?.World ?? throw new InvalidOperationException("Scene loads require Play mode.");
			await _sceneLoadRequests.LoadAsync(world, new AssetRef<SceneAsset> { NodeId = scene.Id }).ConfigureAwait(false);
			return new SceneLoadResult(scene.RelativeAssetPath, scene.Id,
				_editorFrameCoordinator.CompletedSequence, _renderFrameCoordinator.CompletedSequence);
		}, cancellationToken);

	public Task<PlayModeStateResult> EnterPlayModeAsync(CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (_playSession.State == EditorPlayState.Paused)
			{
				if (_playSession.Resume() == false)
				{
					throw new InvalidOperationException("Could not resume Play mode.");
				}

				return GetPlayModeState();
			}

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

	/// <summary>
	/// Holds or releases the game pause reason in bit <paramref name="reasonIndex"/> on the Play-mode world, as gameplay
	/// code does through <see cref="World.Pause"/> and <see cref="World.Resume"/>. This is the game's own pause, not the
	/// editor's Pause.
	/// </summary>
	public Task<PlayModeStateResult> SetGamePauseAsync(int reasonIndex, bool paused, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var world = _playSession.RuntimeScene?.World ??
			            throw new InvalidOperationException("Pausing the game requires Play mode.");
			var reason = new PauseReason(reasonIndex);

			if (paused)
			{
				world.Pause(reason);
			}
			else
			{
				world.Resume(reason);
			}

			return GetPlayModeState();
		}, cancellationToken);

	/// <summary>
	/// Quits the way a gameplay Quit button does, through <see cref="IApplicationQuitService"/>, and reports the Play
	/// state once the editor has handled the request: Play mode stops, and the editor stays open.
	/// </summary>
	public Task<PlayModeStateResult> QuitGameAsync(CancellationToken cancellationToken) =>
		EnqueueAsync(async () =>
		{
			var world = _playSession.RuntimeScene?.World ??
			            throw new InvalidOperationException("Quitting the game requires Play mode.");
			_quitService.Quit(world);

			// The editor takes the request later in this frame. The first completed scene frame may be the previous
			// frame still in flight, so the second is the earliest that was published after the request was handled.
			await _renderFrameCoordinator
				.WaitForCompletedSceneFramesAsync(2, cancellationToken)
				.ConfigureAwait(false);

			return await Enqueue(GetPlayModeState, cancellationToken).ConfigureAwait(false);
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

	/// <summary>Inject window-relative input into both independent mouse consumers, in any editor mode.</summary>
	public Task SetScenePointerAsync(Vector2 position, bool? pressed, CancellationToken cancellationToken, bool? focused = null) =>
		Enqueue(() =>
		{
			var viewport = _viewportStateBus.GetUiState(RenderViewId.Primary);
			if (!viewport.Visible) throw new InvalidOperationException("The Scene viewport is not visible.");
			if (focused.HasValue) _inputSystem.SetPointerFocus(focused.Value);
			var windowPosition = viewport.ImageMin + position;
			_inputSystem.SetAxis2D(InputActionBinding.MousePosition, windowPosition);
			_imguiInput.SetMousePosition(windowPosition);
			if (pressed.HasValue) _imguiInput.SetMouseButton(0, pressed.Value);
			if (pressed.HasValue) _inputSystem.SetButton(InputActionBinding.MouseButtonLeft, pressed.Value);
		}, cancellationToken);

	public Task<string> GetScenePointerStateAsync(CancellationToken cancellationToken) => Enqueue(() =>
	{
		var state = _viewportStateBus.GetUiState(RenderViewId.Primary);
		return System.Text.Json.JsonSerializer.Serialize(new { state.Visible, state.Focused, state.PointerAvailable, state.PointerCaptured,
			NativeFocused = _inputSystem.PointerFocused, Origin = new[] { state.ImageMin.X, state.ImageMin.Y }, Size = new[] { state.ImageMax.X - state.ImageMin.X, state.ImageMax.Y - state.ImageMin.Y } });
	}, cancellationToken);

	public Task SetEditorPointerAsync(
		Vector2 position,
		bool? pressed,
		int button,
		float wheel,
		CancellationToken cancellationToken,
		float wheelX = 0.0f,
		float magnification = 0.0f,
		bool? control = null,
		bool preciseScroll = false) =>
		Enqueue(() =>
		{
			if (button < 0 || button > 2) throw new ArgumentOutOfRangeException(nameof(button));
			if (float.IsFinite(position.X) == false ||
				float.IsFinite(position.Y) == false ||
				float.IsFinite(wheel) == false ||
				float.IsFinite(wheelX) == false ||
				float.IsFinite(magnification) == false ||
				magnification <= -1.0f)
			{
				throw new ArgumentException("Pointer input must be finite and magnification must exceed -1.");
			}

			_imguiInput.SetMousePosition(position);
			if (pressed.HasValue) _imguiInput.SetMouseButton(button, pressed.Value);
			if (control.HasValue) _imguiInput.SetKey(ImGuiNET.ImGuiKey.LeftCtrl, control.Value);
			if (wheel != 0.0f || wheelX != 0.0f)
			{
				var scroll = new Vector2(wheelX, wheel);
				_imguiInput.AddMouseScroll(scroll, preciseScroll ? scroll * 35.0f : null);
			}
			if (magnification != 0.0f) _imguiInput.AddMouseMagnification(magnification);
		}, cancellationToken);

	public Task<AnimationGraphEditorState> GetAnimationEditorStateAsync(CancellationToken cancellationToken) =>
		Enqueue(() => AnimationEditor.GetEditorState(), cancellationToken);

	public Task<AssetBrowserState> GetAssetBrowserStateAsync(CancellationToken cancellationToken) =>
		Enqueue(() => ((AssetsWindow)_windows.Get(EditorWindowIds.Assets).Window).GetBrowserState(), cancellationToken);

	public Task ConfigureAssetBrowserAsync(
		string folderPath,
		string search,
		Guid? expandedSourceId,
		string subAssetSearch,
		CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			_workspaces.Activate(EditorWorkspaceService.AssetsWorkspaceId);
			_workspaces.OpenWindow(EditorWindowIds.Assets);
			((AssetsWindow)_windows.Get(EditorWindowIds.Assets).Window).ConfigureBrowser(
				folderPath,
				search,
				expandedSourceId,
				subAssetSearch);
		}, cancellationToken);

	public Task<EntitiesWindowState> GetEntitiesWindowStateAsync(CancellationToken cancellationToken) =>
		Enqueue(() => ((EntitiesWindow)_windows.Get(EditorWindowIds.Entities).Window).GetState(), cancellationToken);

	public Task SetEntitiesSearchAsync(string search, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			_workspaces.Activate(EditorWorkspaceService.SceneWorkspaceId);
			_workspaces.OpenWindow(EditorWindowIds.Entities);
			((EntitiesWindow)_windows.Get(EditorWindowIds.Entities).Window).SetSearch(search);
		}, cancellationToken);

	public Task SetEditorKeyAsync(string key, bool pressed, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (Enum.TryParse<ImGuiNET.ImGuiKey>(key, out var parsed) == false ||
				Enum.IsDefined(parsed) == false ||
				parsed < ImGuiNET.ImGuiKey.Tab ||
				parsed >= ImGuiNET.ImGuiKey.GamepadStart)
			{
				throw new ArgumentException("Use an ImGui key name such as LeftShift, Space, Escape or Delete.", nameof(key));
			}

			_imguiInput.SetKey(parsed, pressed);
		}, cancellationToken);

	/// <summary>Injects a button binding through the same input system used by gameplay.</summary>
	public Task SetInputButtonAsync(string binding, bool pressed, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (_playSession.State != EditorPlayState.Playing)
			{
				throw new InvalidOperationException("Input injection is only available while Play mode is running.");
			}
			if (Enum.TryParse<InputActionBinding>(binding, ignoreCase: true, out var parsed) == false ||
			    parsed == InputActionBinding.None)
			{
				throw new InvalidOperationException($"Unknown input button binding '{binding}'.");
			}

			_inputSystem.SetButton(parsed, pressed);
		}, cancellationToken);

	/// <summary>Injects a two-dimensional axis binding through the gameplay input system.</summary>
	public Task SetInputAxis2DAsync(
		string binding,
		Vector2 value,
		CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (_playSession.State != EditorPlayState.Playing)
			{
				throw new InvalidOperationException("Input injection is only available while Play mode is running.");
			}
			if (Enum.TryParse<InputActionBinding>(binding, ignoreCase: true, out var parsed) == false ||
			    parsed == InputActionBinding.None)
			{
				throw new InvalidOperationException($"Unknown input axis binding '{binding}'.");
			}

			_inputSystem.SetAxis2D(parsed, value);
		}, cancellationToken);

	public Task<RenderFrameWaitResult> WaitForRenderFramesAsync(int frameCount, CancellationToken cancellationToken) =>
		EnqueueAsync(async () =>
		{
			var sceneSequence = await _renderFrameCoordinator
				.WaitForCompletedSceneFramesAsync(frameCount, cancellationToken)
				.ConfigureAwait(false);
			return new RenderFrameWaitResult(
				frameCount,
				_editorFrameCoordinator.CompletedSequence,
				_renderFrameCoordinator.CompletedSequence,
				sceneSequence);
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

	public Task<TerrainSculptResult> SculptTerrainAsync(
		string? terrainEntityId,
		float localX,
		float localZ,
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
				TerrainAuthoringSurfaceTarget.Heightmap,
				TerrainBrushOperation.RaiseLower,
				new TerrainBrushSettings(radiusMeters, strength, falloff, 0, null));
			if (_terrainAuthoringService.BeginStroke(scene, entity, request) == false)
			{
				throw new InvalidOperationException($"Could not begin a sculpt stroke on terrain entity '{persistentId:D}'.");
			}

			try
			{
				_terrainAuthoringService.AppendStamp(
					new Vector3(localX, 0.0f, localZ),
					1.0f,
					new TerrainBrushModifierState(invert));
				if (_terrainAuthoringService.EndStroke() == false)
				{
					throw new InvalidOperationException($"Could not end the sculpt stroke on terrain entity '{persistentId:D}'.");
				}
			}
			catch
			{
				_terrainAuthoringService.CancelStroke();
				throw;
			}

			return new TerrainSculptResult(
				persistentId,
				localX,
				localZ,
				radiusMeters,
				strength,
				invert,
				_editorFrameCoordinator.CompletedSequence,
				_renderFrameCoordinator.CompletedSequence);
		}, cancellationToken);

	public Task<FoliageSnapResult> SnapFoliageToTerrainAsync(string? terrainEntityId, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (_playSession.State != EditorPlayState.Edit)
			{
				throw new InvalidOperationException("Foliage authoring is only available outside Play mode.");
			}

			var scene = _sceneWorkspace.CurrentScene;
			var (entity, persistentId) = ResolveTerrainEntity(scene, terrainEntityId);
			var moved = _foliageAuthoringService.SnapToTerrain(scene, entity);
			var total = scene.World.GetComponent<TerrainComponent>(entity).Foliage.Asset?.InstanceCount ?? 0;

			return new FoliageSnapResult(persistentId, moved, total);
		}, cancellationToken);

	public Task<FoliagePaintResult> PaintFoliageAsync(
		string? terrainEntityId,
		float localX,
		float localZ,
		float radiusMeters,
		float density,
		IReadOnlyList<string> foliageTypePaths,
		bool erase,
		int? seed,
		CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (_playSession.State != EditorPlayState.Edit)
			{
				throw new InvalidOperationException("Foliage authoring is only available outside Play mode.");
			}

			ValidateFinite(localX, nameof(localX));
			ValidateFinite(localZ, nameof(localZ));
			var settings = new FoliageBrushSettings { RadiusMeters = radiusMeters, Density = density };
			var palette = foliageTypePaths.Distinct(StringComparer.OrdinalIgnoreCase)
				.Select(ResolveFoliagePaletteEntry)
				.ToArray();
			var scene = _sceneWorkspace.CurrentScene;
			var (entity, persistentId) = ResolveTerrainEntity(scene, terrainEntityId);
			if (_foliageAuthoringService.BeginStroke(scene, entity, settings, palette, seed) == false)
			{
				throw new InvalidOperationException($"Could not begin a foliage stroke on terrain entity '{persistentId:D}'.");
			}

			try
			{
				var result = _foliageAuthoringService.AppendStamp(
					new Vector2(localX, localZ),
					new TerrainBrushModifierState(erase));
				var assetId = _foliageAuthoringService.AssetId.GetValueOrDefault();
				return new FoliagePaintResult(
					persistentId,
					assetId == Guid.Empty ? null : assetId,
					result.Added,
					result.Removed,
					_foliageAuthoringService.InstanceCount);
			}
			finally
			{
				_foliageAuthoringService.EndStroke();
			}
		}, cancellationToken);

	private FoliagePaletteEntry ResolveFoliagePaletteEntry(string path)
	{
		var projectRoot = _projectService.ProjectRootPath!;
		var fullPath = Path.GetFullPath(path, projectRoot);
		var relativePath = Normalize(Path.GetRelativePath(projectRoot, fullPath));
		var asset = _projectService.CurrentAssetCatalog.Assets
			.Select(mounted => mounted.Asset)
			.FirstOrDefault(entry =>
				string.Equals(entry.RelativeSourcePath, relativePath, StringComparison.OrdinalIgnoreCase) &&
				FoliageAssetTypes.IsFoliageType(entry));
		if (asset is null)
		{
			throw new InvalidOperationException($"Foliage type '{path}' was not found in the project asset catalog.");
		}

		return new FoliagePaletteEntry(new AssetRef<FoliageType> { NodeId = asset.Id });
	}

	public Task<EngineShaderReloadResult> ReloadEngineShadersAsync(CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var result = _commandService.ReloadEngineShaders();
			var failures = result?.Failures
				.Select(failure => $"{failure.Request}: {failure.Error}")
				.ToArray() ?? [];

			return new EngineShaderReloadResult(
				result is not null,
				result?.AppliedArtifactCount ?? 0,
				failures,
				_shaderProvider.Revision,
				_editorFrameCoordinator.CompletedSequence,
				_renderFrameCoordinator.CompletedSequence);
		}, cancellationToken);

	public Task<EditorUndoResult> UndoAsync(CancellationToken cancellationToken) =>
		Enqueue(() => new EditorUndoResult(
			_commandService.SceneDocument.Undo(),
			_editorFrameCoordinator.CompletedSequence,
			_renderFrameCoordinator.CompletedSequence), cancellationToken);

	public Task<EditorUndoResult> RedoAsync(CancellationToken cancellationToken) =>
		Enqueue(() => new EditorUndoResult(
			_commandService.SceneDocument.Redo(),
			_editorFrameCoordinator.CompletedSequence,
			_renderFrameCoordinator.CompletedSequence), cancellationToken);

	public Task<GameplayStateResult> GetGameplayStateAsync(CancellationToken cancellationToken) =>
		Enqueue(() => new GameplayStateResult(
			_gameplayAssemblyHost.CurrentGeneration,
			_gameplayAssemblyHost.LoadedAssemblyPath,
			_gameplayAssemblyHost.CheckSourceStale(),
			_gameplayAssemblyHost.IsBuildInProgress,
			_gameplayAssemblyHost.LastBuildFailedForCurrentSource), cancellationToken);

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

	public Task<FoliageStateResult> GetFoliageStateAsync(CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var stats = _renderGraph.GetFoliageStats();
			return new FoliageStateResult(
				stats.SetCount,
				stats.InstanceCount,
				stats.RendererInstanceCount,
				stats.ReservationCount,
				stats.ReservedBatchSlots,
				stats.ExpansionCapacity,
				stats.DetailSetCount,
				stats.DetailTypeCount,
				stats.DetailCandidateCount,
				stats.DetailDistanceScale,
				_renderGraph.GetRayTracingSceneState().TopLevelInstanceCount,
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
					_renderFrameCoordinator.CompletedSequence)
				{ MeasuredGpuWork = SummarizeTimings(frames.Select(frame => frame.DurationMs)) };
			}
			finally
			{
				_gpuProfiler.Enabled = wasEnabled;
			}
		}, cancellationToken);

	public Task<CpuFrameProfileResult> GetCpuFrameProfileAsync(CancellationToken cancellationToken) =>
		Enqueue(() => new CpuFrameProfileResult(
			FrameProfiler.Instance.GetLastFrames()
				.Select(frame => new CpuThreadProfileResult(
					frame.ThreadId,
					frame.ThreadName,
					CopyCpuNode(frame.Root)))
				.ToArray(),
			_editorFrameCoordinator.CompletedSequence,
			_renderFrameCoordinator.CompletedSequence), cancellationToken);

	/// <summary>Sets the scene debug-view override used by frame capture.</summary>
	public Task SetSceneDebugViewAsync(string? debugViewId, CancellationToken cancellationToken) =>
		Enqueue(() => _viewportStateBus.OverrideDebugView(debugViewId), cancellationToken);

	public Task<FrameCaptureResult> CaptureFrameAsync(string outputPath, CancellationToken cancellationToken) =>
		CaptureFrameAsync(outputPath, FrameCaptureTarget.SceneColor, cancellationToken);

	/// <summary>
	/// Captures the whole editor window, including the ImGui panels, docking tabs and menus composited
	/// over the scene. This is what to use when the thing under test is the editor UI itself.
	/// </summary>
	public Task<FrameCaptureResult> CaptureEditorWindowAsync(string outputPath, CancellationToken cancellationToken) =>
		CaptureFrameAsync(outputPath, FrameCaptureTarget.Window, cancellationToken);

	private async Task<FrameCaptureResult> CaptureFrameAsync(
		string outputPath,
		FrameCaptureTarget target,
		CancellationToken cancellationToken)
	{
		await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			return await EnqueueAsync(async () =>
			{
				var fullOutputPath = ResolveOutputPath(outputPath);
				var capture = await _renderer.CaptureNextFrameAsync(target, cancellationToken).ConfigureAwait(false);
				Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
				new RgbaImage(capture.Width, capture.Height, capture.Rgba8).SavePng(fullOutputPath);
				return new FrameCaptureResult(fullOutputPath, capture.Width, capture.Height,
					_editorFrameCoordinator.CompletedSequence, _renderFrameCoordinator.CompletedSequence);
			}, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_captureGate.Release();
		}
	}

	/// <summary>
	/// Enters or resumes Play mode, waits for gameplay startup to publish a runtime camera, verifies the
	/// camera that the viewport will use, and captures the next frame from that camera.
	/// </summary>
	public async Task<GameplayFrameCaptureResult> CaptureGameplayFrameAsync(
		string outputPath,
		int settleFrameCount,
		CancellationToken cancellationToken)
	{
		if (settleFrameCount < 1)
		{
			throw new InvalidOperationException("settle_frame_count must be positive.");
		}

		var enteredPlayMode = await Enqueue(() =>
		{
			if (_playSession.State == EditorPlayState.Playing)
			{
				return false;
			}

			if (_playSession.State == EditorPlayState.Paused)
			{
				if (_playSession.Resume() == false)
				{
					throw new InvalidOperationException("Could not resume Play mode for gameplay-camera capture.");
				}
				return false;
			}

			if (_playSession.EnterPlay() == false)
			{
				throw new InvalidOperationException($"Cannot enter Play mode while the editor is {_playSession.State}.");
			}
			return true;
		}, cancellationToken).ConfigureAwait(false);

		await _renderFrameCoordinator
			.WaitForCompletedFramesAsync(settleFrameCount, cancellationToken)
			.ConfigureAwait(false);

		var camera = await Enqueue(GetGameplayCameraInfo, cancellationToken).ConfigureAwait(false);
		var capture = await CaptureFrameAsync(outputPath, cancellationToken).ConfigureAwait(false);
		return new GameplayFrameCaptureResult(
			capture.OutputPath,
			capture.Width,
			capture.Height,
			camera.Name,
			camera.Fov,
			camera.Position.X,
			camera.Position.Y,
			camera.Position.Z,
			camera.Forward.X,
			camera.Forward.Y,
			camera.Forward.Z,
			enteredPlayMode,
			settleFrameCount,
			capture.EditorFrameSequence,
			capture.RenderFrameSequence);
	}

	public Task SetEditorCameraAsync(Vector3 position, Vector3 target, EditorCameraLens lens, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (_cameraSystem.TrySetCameraPose(RenderViewId.Primary, position, target, lens) == false)
			{
				throw new InvalidOperationException("The editor camera is not ready; wait for render frames first.");
			}
		}, cancellationToken);

	public Task ShutdownAsync(CancellationToken cancellationToken) => Enqueue(() => { ShutdownRequested = true; }, cancellationToken);

	private (string Name, float Fov, Vector3 Position, Vector3 Forward) GetGameplayCameraInfo()
	{
		if (_playSession.State != EditorPlayState.Playing || _playSession.RuntimeScene is not { } runtimeScene)
		{
			throw new InvalidOperationException("Gameplay-camera capture requires a running Play-mode scene.");
		}

		var world = runtimeScene.World;
		foreach (var entry in world.View<Camera, WorldTransform>())
		{
			if (world.IsEnabled(entry.Entity) == false)
			{
				continue;
			}

			var name = world.HasComponent<NameComponent>(entry.Entity)
				? world.GetComponent<NameComponent>(entry.Entity).Name
				: string.Empty;
			var transform = entry.Second.LocalToWorld;
			var forward = Vector3.TransformNormal(Vector3.UnitZ, transform);
			forward = forward.LengthSquared() > 1e-8f ? Vector3.Normalize(forward) : Vector3.UnitZ;
			return (name, entry.First.Fov, transform.Translation, forward);
		}

		throw new InvalidOperationException(
			"The running gameplay scene has no enabled Camera with a WorldTransform; refusing to capture the editor camera as a fallback.");
	}

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

			if (scene.TryGetEntity(requestedId, out var requestedEntity) == false)
			{
				throw new InvalidOperationException($"Terrain entity '{requestedId:D}' was not found in the authoring scene.");
			}

			if (scene.World.HasComponent<TerrainComponent>(requestedEntity) == false)
			{
				throw new InvalidOperationException($"Entity '{requestedId:D}' is not a terrain entity.");
			}

			return (requestedEntity, requestedId);
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

	private PlayModeStateResult GetPlayModeState()
	{
		return new PlayModeStateResult(
			_playSession.State.ToString(),
			_playSession.RuntimeScene?.World.PauseReasons ?? 0,
			_editorFrameCoordinator.CompletedSequence,
			_renderFrameCoordinator.CompletedSequence);
	}

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

	private static CpuProfileNodeResult CopyCpuNode(FrameProfiler.ProfileNode node) => new(
		node.Name,
		node.DurationMs,
		node.AllocatedBytes,
		node.Children.Select(CopyCpuNode).ToArray());

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
    private AnimationWindow AnimationEditor => (AnimationWindow)_windows.Get(EditorWindowIds.Animation).Window;
    public Task<AnimationGraphInspectionResult> ConfigureAnimationPreviewAsync(Guid assetId, Guid modelId, Guid setId, CancellationToken token) =>
        Enqueue(() => { AnimationEditor.ConfigurePreview(assetId, modelId, setId); return InspectAnimation(AnimationEditor.PreviewInstance!, AnimationEditor.Diagnostic); }, token);
    public Task<AnimationGraphInspectionResult> AnimationPreviewTransportAsync(bool playing, float? seek, float? step, CancellationToken token) =>
        Enqueue(() => { AnimationEditor.PreviewTransport(playing, seek, step); return InspectAnimation(AnimationEditor.PreviewInstance!, AnimationEditor.Diagnostic); }, token);
    public Task<AnimationGraphInspectionResult> GetAnimationGraphAsync(Guid? entityId, CancellationToken token) =>
        Enqueue(() => InspectAnimation(ResolveAnimationInstance(entityId), AnimationEditor.Diagnostic), token);
    public Task<AnimationGraphInspectionResult> SetAnimationParameterAsync(Guid? entityId, string name, float value, CancellationToken token) =>
        Enqueue(() =>
        {
            var instance = ResolveAnimationInstance(entityId);
            var definition = instance.Program.ParameterSchema.FirstOrDefault(parameter => parameter.Name == name)
                ?? throw new InvalidOperationException($"Unknown animation parameter '{name}'.");
            var handle = instance.Program.GetParameter(name, definition.Type);
            switch (definition.Type)
            {
                case AnimationParameterType.Float: instance.SetFloat(handle, value); break;
                case AnimationParameterType.Bool:
                    if (value is not (0 or 1)) throw new InvalidOperationException("Boolean parameter requires 0 or 1.");
                    instance.SetBool(handle, value != 0); break;
                case AnimationParameterType.Integer:
                    if (!float.IsFinite(value) || value != MathF.Truncate(value)) throw new InvalidOperationException("Integer parameter requires an integer value.");
                    instance.SetInteger(handle, checked((int)value)); break;
            }
            return InspectAnimation(instance, null);
        }, token);
    public Task<AnimationGraphInspectionResult> EditAnimationGraphAsync(string operation, Guid nodeId, int inputIndex, Guid inputId, CancellationToken token) =>
        Enqueue(() => { AnimationEditor.EditGraph(operation, nodeId, inputIndex, inputId); return InspectAnimation(AnimationEditor.PreviewInstance!, AnimationEditor.Diagnostic); }, token);
    public async Task<FrameCaptureResult> CaptureAnimationPreviewAsync(string path, CancellationToken token)
    {
        var view = await Enqueue(() => AnimationEditor.PreviewView ?? throw new InvalidOperationException("No preview is open."), token);
        var previous = _renderGraph.SceneCaptureView;
        _renderGraph.SetSceneCaptureView(view);
        try { return await CaptureFrameAsync(path, token); }
        finally { _renderGraph.SetSceneCaptureView(previous); }
    }
    public Task<bool> SetSkinningForceUpdatesAsync(bool forced, CancellationToken token) =>
        Enqueue(() => { SkinningPass.ForceUpdates = forced; return forced; }, token);
    private AnimationGraphInstance ResolveAnimationInstance(Guid? entityId)
    {
        if (entityId is null) return AnimationEditor.PreviewInstance ?? throw new InvalidOperationException("No animation preview is open.");
        var scene = _playSession.RuntimeScene ?? _sceneWorkspace.CurrentScene;
        if (scene.TryGetEntity(entityId.Value, out var entity) == false || scene.World.HasComponent<Animator>(entity) == false) throw new InvalidOperationException("Animator entity was not found.");
        ref var animator = ref scene.World.GetComponent<Animator>(entity);
        return animator.Instance ?? throw new InvalidOperationException(animator.Diagnostic ?? "Animator is unavailable.");
    }
    private static AnimationGraphInspectionResult InspectAnimation(AnimationGraphInstance instance, string? diagnostic) =>
        new(diagnostic, instance.Time, instance.Playing, instance.Program.Skeleton.BoneCount, instance.Program.NodeCount,
            instance.Contributions.ToArray(), instance.States.ToArray(),
            instance.Program.Curves.ToDictionary(name => name, name => instance.GetCurve(instance.Program.GetCurve(name))), instance.Markers.ToArray()) { PoseHash = HashPose(instance.Output) };
    private static ulong HashPose(Pose pose)
    {
        ulong hash = 14695981039346656037;
        void Add(float value) { hash = (hash ^ BitConverter.SingleToUInt32Bits(value)) * 1099511628211; }
        foreach(var bone in pose.Bones) { Add(bone.Position.X); Add(bone.Position.Y); Add(bone.Position.Z); Add(bone.Rotation.X); Add(bone.Rotation.Y); Add(bone.Rotation.Z); Add(bone.Rotation.W); Add(bone.Scale.X); Add(bone.Scale.Y); Add(bone.Scale.Z); }
        return hash;
    }

}
