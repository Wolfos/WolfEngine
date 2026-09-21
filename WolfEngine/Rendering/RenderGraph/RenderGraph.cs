using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using WolfEngine.ECS;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.UI;
using WolfEngine.Mathematics;
using WolfEngine.Profiling;
using WolfEngine.Utility;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering;

/// <summary>
/// Entry point for recording and executing passes in the renderer's frame graph.
/// Responsible for owning pass order, compiling transient resources, and dispatching execution.
/// </summary>
public sealed class RenderGraph : IRenderResourceScheduler, IRenderViewHost
{
	private readonly RenderGraphResourceRegistry _resourceRegistry;
	private readonly RenderGraphFrameBuilder _frameBuilder;
	private readonly IRenderer _renderer;
	private readonly IArenaAllocator _arenaAllocator;
	private readonly List<RenderGraphPass> _passes = new();
	private readonly Queue<RenderGraphPass> _passPool = new();
	private readonly RenderGraphCompiler _compiler;
	private readonly FrameSnapshotBuffer _snapshotBuffer = new();
	// Views the current Execute runs, and the scene data built for each.
	private readonly List<RenderViewId> _executedViews = new();
	// Views OnRender records this frame, and the imported output target of each.
	private readonly List<RenderViewId> _recordedViews = new();
	private readonly Dictionary<RenderViewId, RenderGraphResourceHandle> _sceneColorHandles = new();
	private readonly Dictionary<RenderViewId, SceneDrawData?> _sceneDataByView = new();
	private readonly IUiFrameProvider _uiFrameProvider;
	private readonly IGameplayUiFrameProvider _gameplayUiFrameProvider;
	private GameplayUiRenderFrame _gameplayUiFrame = GameplayUiRenderFrame.Empty;
	private readonly EditorViewportStateBus _viewportStateBus;
	private readonly RenderPresentationOptions _presentationOptions;
	private readonly IMainThreadDispatcher _mainThreadDispatcher;
	private readonly EditorFrameCoordinator _editorFrameCoordinator;
	private readonly RenderFrameCoordinator _renderFrameCoordinator;
	private readonly GpuDrawResources _gpuDrawResources;
	private readonly GpuDrawHardeningStats _hardeningStats;
	private readonly GpuProfiler _gpuProfiler;
	private readonly IImGuiRenderer _imGuiRenderer;
	private readonly GameplayUiGpuRenderer _gameplayUiRenderer;
	private long _pendingShaderRevision;
	private long _appliedShaderRevision;
	private readonly RenderViewRegistry _viewRegistry = new();
	private RenderViewState _view = null!;
	// The view whose passes are being recorded, or None while recording shared passes.
	private RenderViewId _recordingView;
	private readonly int _gpuHardeningLogInterval;
	// Populated from the snapshot buffer at the top of every frame, before anything reads them.
	// Execute() still null-checks _activeSnapshot defensively for the pre-first-frame case.
	private FrameSnapshot _currentSnapshot = null!;
	private FrameSnapshot _activeSnapshot = null!;
	private long _lastObservedEditorFrameSequence;
	private int _frameIndex;
	private int _gpuCaptureRequested;
	private bool _gpuCaptureActive;
	private RayTracingSceneState _latestRayTracingSceneState = RayTracingSceneState.Empty;

	private readonly object _resourceSync = new();
	private readonly HashSet<Material> _pendingMaterials = new(new ReferenceComparer<Material>());
	private readonly HashSet<Texture> _pendingTextures = new(new ReferenceComparer<Texture>());
	private readonly HashSet<Material> _trackedMaterials = new(new ReferenceComparer<Material>());
	private readonly ConcurrentQueue<Mesh> _ensureMeshQueue = new();

	public RenderGraph(
		RenderGraphResourceRegistry resourceRegistry,
		IRenderer renderer,
		IArenaAllocator arenaAllocator,
		GpuDrawResources gpuDrawResources,
		GpuDrawHardeningStats hardeningStats,
		GpuProfiler gpuProfiler,
		IUiFrameProvider uiFrameProvider,
		IGameplayUiFrameProvider gameplayUiFrameProvider,
		EditorViewportStateBus viewportStateBus,
		EditorFrameCoordinator editorFrameCoordinator,
		RenderFrameCoordinator renderFrameCoordinator,
		IMainThreadDispatcher mainThreadDispatcher,
		IImGuiRenderer imGuiRenderer,
		GameplayUiGpuRenderer gameplayUiRenderer,
		IShaderProvider shaderProvider,
		BindlessResourceRegistry bindlessResourceRegistry,
		IGpuDrawBackendBridge gpuDrawBackendBridge,
		RenderPresentationOptions? presentationOptions = null)
	{
		_resourceRegistry = resourceRegistry;
		_renderer = renderer;
		_arenaAllocator = arenaAllocator;
		var passSet = new RenderGraphPassSet(
			renderer,
			shaderProvider,
			bindlessResourceRegistry,
			gpuDrawResources,
			hardeningStats,
			gpuDrawBackendBridge);
		_frameBuilder = new(
			resourceRegistry,
			renderer,
			passSet,
			gpuDrawResources,
			imGuiRenderer,
			gameplayUiRenderer,
			shaderProvider,
			_viewRegistry);
		_gpuDrawResources = gpuDrawResources;
		_hardeningStats = hardeningStats ?? throw new ArgumentNullException(nameof(hardeningStats));
		_gpuProfiler = gpuProfiler ?? throw new ArgumentNullException(nameof(gpuProfiler));
		_imGuiRenderer = imGuiRenderer ?? throw new ArgumentNullException(nameof(imGuiRenderer));
		_gameplayUiRenderer = gameplayUiRenderer ?? throw new ArgumentNullException(nameof(gameplayUiRenderer));
		_uiFrameProvider = uiFrameProvider;
		_gameplayUiFrameProvider = gameplayUiFrameProvider ?? throw new ArgumentNullException(nameof(gameplayUiFrameProvider));
		_viewportStateBus = viewportStateBus ?? throw new ArgumentNullException(nameof(viewportStateBus));
		_presentationOptions = presentationOptions ?? new RenderPresentationOptions();
		// The process-wide presentation mode describes the primary view; further views choose their own.
		_viewRegistry.GetOrCreate(RenderViewId.Primary).Output =
			_presentationOptions.OutputMode == RenderOutputMode.FullWindow
				? RenderViewOutput.Backbuffer
				: RenderViewOutput.Texture;
		_editorFrameCoordinator =
			editorFrameCoordinator ?? throw new ArgumentNullException(nameof(editorFrameCoordinator));
		_renderFrameCoordinator =
			renderFrameCoordinator ?? throw new ArgumentNullException(nameof(renderFrameCoordinator));
		_mainThreadDispatcher = mainThreadDispatcher;
		_compiler = new(resourceRegistry);
		_gpuHardeningLogInterval = GraphicsConfig.GpuHardeningLogIntervalFrames;
		shaderProvider.RevisionChanged += revision => Interlocked.Exchange(ref _pendingShaderRevision, revision);
	}


	public RenderGraphBuilder AddPass(string name, PassKind kind)
	{
		var pass = _passPool.Count > 0 ? _passPool.Dequeue() : new RenderGraphPass();
		pass.Configure(name, kind, _recordingView);
		_passes.Add(pass);
		return new(pass, _resourceRegistry);
	}

	/// <summary>
	/// Tags every pass added until <see cref="EndViewRecording"/> as belonging to <paramref name="view"/>.
	/// Passes added outside a view recording are shared by every view.
	/// </summary>
	internal void BeginViewRecording(RenderViewId view)
	{
		if (_recordingView.IsValid)
		{
			throw new InvalidOperationException($"Already recording {_recordingView}; view recordings do not nest.");
		}

		_recordingView = view;
	}

	internal void EndViewRecording() => _recordingView = RenderViewId.None;

	internal IReadOnlyList<RenderGraphPass> Passes => _passes;

	public void Execute()
	{
		ApplyPendingShaderReload();
		// Compile barriers before execution
		_compiler.Compile(_passes);

		// Every view this graph executes: the bound one, which runs the shared passes and publishes state even
		// when it recorded nothing, then any other view whose passes were recorded, in recording order.
		var boundView = _view.View;
		_executedViews.Clear();
		_executedViews.Add(boundView);
		for (var i = 0; i < _passes.Count; i++)
		{
			var passView = _passes[i].View;
			if (passView.IsValid && _executedViews.Contains(passView) == false)
			{
				_executedViews.Add(passView);
			}
		}

		_frameBuilder.BeginViewportResolve();
		for (var i = 0; i < _executedViews.Count; i++)
		{
			SelectView(_executedViews[i]);
			ResolveViewProjection();
			_frameBuilder.PrepareSceneViewport();
		}

		_frameBuilder.ResolveUiViewportTextures();
		SelectView(boundView);

		var device = _renderer.GetGfxDevice();
		var profilerBackend = (device as IGpuProfilerDevice)?.GpuProfilerBackend;

		var frameSnapshot = _activeSnapshot;
		if (frameSnapshot is null)
		{
			ReleasePasses();
			return;
		}

		// Scene data per executed view, each against its own camera, lights and history.
		_sceneDataByView.Clear();
		var anyViewWithoutSceneData = false;
		for (var i = 0; i < _executedViews.Count; i++)
		{
			SelectView(_executedViews[i]);
			if (TryBuildSceneData(frameSnapshot.GetOrCreateView(_view.View), out var viewSceneData) == false)
			{
				SelectView(boundView);
				ReleasePasses();
				return;
			}

			_sceneDataByView[_view.View] = viewSceneData;
			anyViewWithoutSceneData |= viewSceneData is null;
		}

		SelectView(boundView);
		var snapshot = frameSnapshot.GetOrCreateView(boundView);
		var sceneData = _sceneDataByView[boundView];

		if (anyViewWithoutSceneData &&
		    _passes.Any(p => p.Name != "ImGui")) // filthy, but we want to let the ImGui pass through even if there is no scene
		{
			ReleasePasses();
			return;
		}

		var gpuFrameCapture = _gpuProfiler.BeginFrame((ulong)_frameIndex);
		var commandList = _passes.Count == 0
			? null
			: _passes[0].Kind == PassKind.Graphics
				? device.BeginGraphics()
				: device.BeginCompute();
		commandList?.SetBindlessTable(device.GlobalTable);
		foreach (var pass in _passes)
		{
			using (FrameProfiler.Instance.Measure($"Pass: {pass.Name}"))
			{
				// Materialize resources used by this pass
				for (var i = 0; i < pass.Reads.Count; i++)
				{
					_resourceRegistry.GetResource(pass.Reads[i]);
				}

				for (var i = 0; i < pass.Writes.Count; i++)
				{
					_resourceRegistry.GetResource(pass.Writes[i]);
				}

				if (commandList is null)
				{
					throw new InvalidOperationException("The render graph command list was not created.");
				}
				if (gpuFrameCapture is not null && profilerBackend is IGpuProfilerCaptureBackend captureBackend)
				{
					captureBackend.Attach(commandList, gpuFrameCapture.AddPass(pass.Name));
				}

				commandList.BeginEvent(pass.Name);

				// Inject barriers before the pass executes, as one batch so the backend can collapse a
				// pass's transitions into a single flush rather than one per resource.
				commandList.Barriers(pass.BarrierSpan);

				// A view pass runs against its own view's state, because pass callbacks read it when they execute.
				// Shared passes run against the bound view, not whichever view happened to execute last.
				var passSnapshot = snapshot;
				var passSceneData = sceneData;
				if (pass.View.IsValid)
				{
					_frameBuilder.BindView(pass.View);
					passSnapshot = frameSnapshot.GetOrCreateView(pass.View);
					passSceneData = _sceneDataByView[pass.View];
				}
				else
				{
					_frameBuilder.BindView(boundView);
				}

				// Execute the pass with the command list and scene data
				var context = new RenderGraphContext(_resourceRegistry, pass.Name)
				{
					CommandList = commandList,
					// Null only on ImGui-only frames (see the guard above); RenderGraphContext.SceneData
					// throws if a pass that needs scene data reads it.
					SceneData = passSceneData!,
					GpuDrawDatabase = passSnapshot.GpuDrawDatabase,
					FrameSnapshot = frameSnapshot,
					ViewSnapshot = passSnapshot
				};
				pass.Execute(context);
				commandList.EndEvent();
			}
		}
		if (commandList is not null)
		{
			device.Submit(commandList);
		}

		gpuFrameCapture?.Seal();

		SelectView(boundView);
		ReleasePasses();
	}

	private void ApplyPendingShaderReload()
	{
		var revision = Interlocked.Read(ref _pendingShaderRevision);
		if (revision == 0 || revision == _appliedShaderRevision) return;
		var device = _renderer.GetGfxDevice();
		device.WaitForIdle();
		_frameBuilder.InvalidateShaderPipelines();
		ShaderPipelineInvalidation.Invalidate(_gpuDrawResources);
		ShaderPipelineInvalidation.Invalidate(_renderer);
		_imGuiRenderer.InvalidateShaderPipeline();
		device.ClearPipelineCache();
		lock (_resourceSync)
		{
			foreach (var material in _trackedMaterials)
			{
				material.MarkGpuResourcesDirty();
				_pendingMaterials.Add(material);
			}
		}

		_appliedShaderRevision = revision;
	}

	/// <summary>
	/// Builds scene data for the selected view. Returns false when the frame has to be abandoned; true with
	/// null scene data when the view's camera cannot be inverted, which callers treat as no scene this frame.
	/// </summary>
	private bool TryBuildSceneData(RenderViewSnapshot snapshot, out SceneDrawData? sceneData)
	{
		// Build scene data from snapshot
		sceneData = null;
		var world = snapshot.CameraWorldTransform.LocalToWorld;
		var taaEnabled = snapshot.Config.AntiAliasing.Enabled;
		// FSR3 owns the sequence length. At native resolution this is eight phases; once
		// render/display sizes split, the display width belongs in the second argument.
		var phaseCount = snapshot.Config.AntiAliasing.Mode == AntiAliasingMode.Fsr3
			? Fsr3Constants.GetJitterPhaseCount(_view.SceneRenderSize.X, _view.SceneRenderSize.X)
			: Math.Max(1, snapshot.Config.AntiAliasing.Taa.PhaseCount);
		var jitterPixels = taaEnabled
			? TemporalJitter.GetHaltonJitterPixels(
				(ulong)_frameIndex,
				phaseCount)
			: Vector2.Zero;
		var previousJitterPixels = taaEnabled && _frameIndex > 0
			? TemporalJitter.GetHaltonJitterPixels(
				(ulong)(_frameIndex - 1),
				phaseCount)
			: jitterPixels;
		var jitterNdc = TemporalJitter.GetJitterNdc(jitterPixels, _view.SceneRenderSize);
		var jitteredProjection = taaEnabled
			? TemporalJitter.ApplyProjectionJitter(_view.ResolvedProjection, jitterNdc)
			: _view.ResolvedProjection;
		if (Matrix4x4.Invert(world, out var view) &&
		    Matrix4x4.Decompose(world, out _, out _, out var cameraPosition) &&
		    Matrix4x4.Invert(jitteredProjection, out var invProjection))
		{
			_view.RenderLights.Clear();
			for (var i = 0; i < snapshot.LightPackets.Count; i++)
			{
				var lightPacket = snapshot.LightPackets[i];
				var lightTransform = lightPacket.Transform;
				lightTransform.Translation -= cameraPosition;
				_view.RenderLights.Add(new LightPacket(lightPacket.Light, lightTransform));
			}

			// Remove camera translation from the view matrix since objects are camera-relative
			view.Translation = Vector3.Zero;
			var viewProjection = view * jitteredProjection;
			var unjitteredViewProjection = view * _view.ResolvedProjection;
			if (Matrix4x4.Invert(viewProjection, out var invViewProjection) == false)
			{
				return false;
			}

			var hasPreviousCameraState = TryCreatePreviousCameraState(
				snapshot,
				unjitteredViewProjection,
				_view.ResolvedProjection,
				_view.HasPreviousResolvedProjection ? _view.PreviousResolvedProjection : _view.ResolvedProjection,
				cameraPosition,
				out var previousProjection,
				out var previousViewProjection,
				out var previousCameraOrigin);
			var projectionChanged = hasPreviousCameraState &&
			                        TemporalJitter.HasProjectionChanged(
				                        _view.ResolvedProjection,
				                        previousProjection);

			sceneData = new(
				view,
				viewProjection,
				_view.ResolvedProjection,
				unjitteredViewProjection,
				previousProjection,
				previousViewProjection,
				invProjection,
				invViewProjection,
				cameraPosition,
				previousCameraOrigin,
				_view.SceneRenderSize,
				snapshot.Camera.NearPlane > 0.0f ? snapshot.Camera.NearPlane : Camera.DefaultNearPlane,
				snapshot.Camera.FarPlane > 0.0f ? snapshot.Camera.FarPlane : Camera.DefaultFarPlane,
				jitterPixels,
				previousJitterPixels,
				jitterNdc,
				hasPreviousCameraState == false ||
				projectionChanged ||
				(taaEnabled && (!_view.SceneDataPreviousTaaEnabled ||
				 _view.SceneDataPreviousAntiAliasingMode != snapshot.Config.AntiAliasing.Mode ||
				 _view.PreviousJitterPhaseCount != phaseCount)),
				_view.RenderLights,
				snapshot.DecalPackets,
				snapshot.FogVolumePackets,
				snapshot.OutlinePackets);

			_view.PreviousResolvedProjection = _view.ResolvedProjection;
			_view.HasPreviousResolvedProjection = true;
			_view.SceneDataPreviousTaaEnabled = taaEnabled;
			_view.SceneDataPreviousAntiAliasingMode = snapshot.Config.AntiAliasing.Mode;
			_view.PreviousJitterPhaseCount = phaseCount;
		}

		return true;
	}

	/// <summary>The snapshot's existing entry for <paramref name="view"/>, without creating one.</summary>
	private static RenderViewSnapshot FindView(FrameSnapshot snapshot, RenderViewId view)
	{
		for (var i = 0; i < snapshot.Views.Count; i++)
		{
			if (snapshot.Views[i].View == view)
			{
				return snapshot.Views[i];
			}
		}

		throw new InvalidOperationException($"The frame snapshot has no entry for {view}.");
	}

	/// <summary>Points both the graph and the frame builder at <paramref name="view"/>'s state.</summary>
	private void SelectView(RenderViewId view)
	{
		_view = _viewRegistry.GetOrCreate(view);
		_frameBuilder.BindView(view);
	}

	private static bool TryCreatePreviousCameraState(
		RenderViewSnapshot snapshot,
		in Matrix4x4 fallbackViewProjection,
		in Matrix4x4 fallbackProjection,
		in Matrix4x4 previousResolvedProjection,
		in Vector3 fallbackCameraOrigin,
		out Matrix4x4 previousProjection,
		out Matrix4x4 previousViewProjection,
		out Vector3 previousCameraOrigin)
	{
		if (snapshot.HasPreviousCameraState == false ||
		    Matrix4x4.Invert(snapshot.PreviousCameraWorldTransform.LocalToWorld, out var previousView) == false ||
		    Matrix4x4.Decompose(snapshot.PreviousCameraWorldTransform.LocalToWorld, out _, out _,
			    out previousCameraOrigin) == false)
		{
			previousProjection = fallbackProjection;
			previousViewProjection = fallbackViewProjection;
			previousCameraOrigin = fallbackCameraOrigin;
			return false;
		}

		previousView.Translation = Vector3.Zero;
		previousProjection = previousResolvedProjection;
		previousViewProjection = previousView * previousProjection;
		return true;
	}

	/// <summary>
	/// Resolves the projection for the view being recorded from its own render size. The camera component's
	/// baked projection carries whatever aspect was last written into it by a global, which is only right for
	/// one view; a degenerate render size means the scene is not being drawn, so the baked value stands in.
	/// </summary>
	private void ResolveViewProjection()
	{
		if (_activeSnapshot is not { } frameSnapshot)
		{
			return;
		}
		var snapshot = frameSnapshot.GetOrCreateView(_view.View);

		_view.ResolvedProjection = _view.SceneRenderSize.X > 0 && _view.SceneRenderSize.Y > 0
			? snapshot.Camera.GetPerspective(_view.SceneRenderSize)
			: snapshot.Camera.Perspective;
	}

	public RenderViewId CreateView(in RenderViewDescriptor descriptor)
	{
		return _viewRegistry.Create(descriptor.World, descriptor.Name, descriptor.Output).View;
	}

	public bool DestroyView(RenderViewId view)
	{
		if (view == RenderViewId.Primary)
		{
			return false;
		}

		var released = _viewRegistry.Release(view, _renderer.GetGfxDevice());
		if (released)
		{
			_viewportStateBus.RemoveView(view);
		}

		return released;
	}

	public bool TryGetViewTexture(RenderViewId view, out nint textureId, out Int2 size)
	{
		if (_viewRegistry.TryGet(view, out var state) == false)
		{
			textureId = 0;
			size = Int2.Zero;
			return false;
		}

		var resolved = state.ResolvedSceneViewportState;
		textureId = resolved.TextureId;
		size = resolved.RenderSizePixels;
		return textureId != 0;
	}

	public bool TryGetViewForWorld(World world, out RenderViewId view) =>
		_viewRegistry.TryGetViewForWorld(world, out view);

	internal bool TryGetViewBinding(RenderViewId view, out World world, out long bindingGeneration)
	{
		if (_viewRegistry.TryGet(view, out var state) && state.World is not null)
		{
			world = state.World;
			bindingGeneration = state.BindingGeneration;
			return true;
		}

		world = null!;
		bindingGeneration = 0;
		return false;
	}

	public IReadOnlyList<RenderViewId> Views => _viewRegistry.ViewIds;

	public void Startup(Action startup, Action<float> update)
	{
		_renderer.Run(startup, update, OnRender);
	}

	public bool TryBeginSnapshotWrite(out FrameSnapshot snapshot)
	{
		return _snapshotBuffer.TryBeginWrite(out snapshot);
	}

	public bool TryPublishSnapshot()
	{
		return _snapshotBuffer.TryPublishWrite();
	}

	public void CompleteSnapshotPublishing()
	{
		_snapshotBuffer.Complete();
	}

	public void SetSkybox(SkyboxResources skybox)
	{
		_frameBuilder.SetSkybox(skybox);
	}

	public void OnRender(float deltaTime)
	{
		FrameProfiler.Instance.BeginFrame("Render Frame");
		var changedMaterials = new List<Material>();
		if (_renderer.GetGfxDevice() is IGpuSubmissionTimeline submissionTimeline)
		{
			submissionTimeline.PumpCompleted();
		}

		var gpuProfilerBackend = (_renderer.GetGfxDevice() as IGpuProfilerDevice)?.GpuProfilerBackend;
		_gpuProfiler.SetBackendAvailability(
			gpuProfilerBackend?.IsSupported == true,
			gpuProfilerBackend?.UnsupportedReason ?? "The active graphics backend does not support GPU profiling.");
		using (FrameProfiler.Instance.Measure("Upload resources"))
		{
			var changedTextures = new List<Texture>();
			ProcessPendingTextures(changedTextures);
			if (changedTextures.Count > 0)
			{
				MarkDependentMaterialsPending(changedTextures);
			}

			changedMaterials.Clear();
			ProcessPendingMaterials(changedMaterials);

			while (_ensureMeshQueue.TryDequeue(out var mesh))
			{
				if (mesh is null)
				{
					continue;
				}

				_renderer.EnsureMeshResources(mesh);
			}
		}

		_mainThreadDispatcher.ExecutePending();
		bool framePublished;
		using (FrameProfiler.Instance.Measure("Wait For Editor Frame"))
		{
			framePublished = _editorFrameCoordinator.TryWaitForNextFrame(
				_lastObservedEditorFrameSequence,
				_mainThreadDispatcher.ExecutePending,
				out _lastObservedEditorFrameSequence);
		}

		if (framePublished == false)
		{
			FrameProfiler.Instance.EndFrame();
			return;
		}

		_resourceRegistry.SetDevice(_renderer.GetGfxDevice());
		_gpuDrawResources.EnsureCreated(_renderer.GetGfxDevice());
		_view = _viewRegistry.GetOrCreate(RenderViewId.Primary);
		_view.SceneRenderTarget.Advance(_renderer.GetGfxDevice());

		using (FrameProfiler.Instance.Measure("Begin Frame"))
		{
			_renderer.BeginFrame();
		}

		BeginGpuCaptureIfRequested();


		using (FrameProfiler.Instance.Measure("Build Frame"))
		{
			var uiFrame = UiFrameData.Empty;
			try
			{
				_resourceRegistry.BeginFrame();
				ReleasePasses();

				if (_uiFrameProvider.TryConsumeLatest(out var latestUi))
				{
					uiFrame = latestUi;
				}

				if (_snapshotBuffer.TryConsumeLatest(out var snapshot) == false)
				{
					snapshot = _currentSnapshot;
				}

				_currentSnapshot = snapshot;
				_activeSnapshot = snapshot;
				for (var viewIndex = 0; viewIndex < snapshot.Views.Count; viewIndex++)
				{
					var database = snapshot.Views[viewIndex].GpuDrawDatabase;
					for (var i = 0; i < changedMaterials.Count; i++)
					{
						database.NotifyMaterialChanged(changedMaterials[i]);
					}
				}

				var frameBufferSize = _renderer.GetFrameBufferSize();
				// Gameplay screen UI is recorded into the full presentation targets. The editor later
				// scales those targets into its scene viewport, while standalone presents them directly.
				_gameplayUiFrameProvider.SetViewportSize(frameBufferSize, ComputeDisplayScale(frameBufferSize));
				if (_gameplayUiFrameProvider.TryConsumeLatest(out var latestGameplayUi))
				{
					_gameplayUiFrame.Release();
					_gameplayUiFrame = latestGameplayUi;
				}

				// The primary view always records, as it did when it was the only one; any other view records
				// when the published snapshot carries an entry for it. The render thread must not create
				// entries: the snapshot has been published and belongs to the game thread's view list.
				_recordedViews.Clear();
				_recordedViews.Add(RenderViewId.Primary);
				for (var i = 0; i < snapshot.Views.Count; i++)
				{
					var candidate = snapshot.Views[i].View;
					if (candidate != RenderViewId.Primary && _viewRegistry.TryGet(candidate, out _))
					{
						_recordedViews.Add(candidate);
					}
				}

				// Shared setup takes its sun and sky from the primary view; see BeginSharedFrame.
				var primarySnapshot = snapshot.GetOrCreateView(RenderViewId.Primary);
				_frameBuilder.BeginSharedFrame(
					frameBufferSize,
					primarySnapshot.SunDirection,
					primarySnapshot.SunIntensityScale,
					primarySnapshot.Config.SkyboxConfig);
				_frameBuilder.SetUiFrame(uiFrame);
				_frameBuilder.SetGameplayUiFrame(_gameplayUiFrame);

				_sceneColorHandles.Clear();
				for (var viewIndex = 0; viewIndex < _recordedViews.Count; viewIndex++)
				{
					var recordedView = _recordedViews[viewIndex];
					SelectView(recordedView);
					var viewSnapshot = recordedView == RenderViewId.Primary
						? primarySnapshot
						: FindView(snapshot, recordedView);
					var sceneViewportState = _viewportStateBus.GetUiState(recordedView);
					var renderSceneToWindow = _view.Output == RenderViewOutput.Backbuffer;
					var sceneEnabled = renderSceneToWindow
						? TryComputeFullWindowSceneRenderSize(frameBufferSize, out var sceneRenderSize)
						: TryComputeSceneRenderSize(sceneViewportState, out sceneRenderSize);
					if (recordedView == RenderViewId.Primary)
					{
						var currentResolution = sceneEnabled ? sceneRenderSize : frameBufferSize;
						if (currentResolution.X > 0 && currentResolution.Y > 0)
						{
							Screen.CurrentResolution = currentResolution;
						}
					}
					else if (sceneEnabled == false)
					{
						// A hidden secondary view records nothing this frame and keeps its history for when it returns.
						_recordedViews.RemoveAt(viewIndex);
						viewIndex--;
						continue;
					}

					_view.SceneRenderSize = sceneRenderSize;
					var renderSceneToViewport = sceneEnabled && !renderSceneToWindow;
					var sceneColorHandle = default(RenderGraphResourceHandle);
					if (renderSceneToViewport)
					{
						var sceneTarget = _view.SceneRenderTarget.EnsureTarget(_renderer.GetGfxDevice(), sceneRenderSize);
						sceneColorHandle = _resourceRegistry.ImportTexture(
							sceneTarget,
							takeOwnership: false,
							initialState: _view.SceneRenderTarget.CurrentState);
					}

					_sceneColorHandles[recordedView] = sceneColorHandle;
					if (!Matrix4x4.Decompose(
						    viewSnapshot.CameraWorldTransform.LocalToWorld,
						    out _,
						    out _,
						    out var viewCameraPosition))
					{
						viewCameraPosition = viewSnapshot.Config.DiffuseGlobalIllumination.Origin;
					}

					_frameBuilder.SetSceneViewportSelection(sceneViewportState.RequestedDebugViewId);
					_frameBuilder.BeginViewFrame(
						recordedView,
						frameBufferSize,
						sceneRenderSize,
						sceneColorHandle,
						renderSceneToViewport || renderSceneToWindow,
						viewSnapshot.DecalPackets.Count > 0,
						viewSnapshot.Config,
						viewCameraPosition);
				}

				// Every view is set up before anything is recorded: the shared draw update records first and
				// has to know which views draw this frame. View state is per view, so setting up the next view
				// does not disturb one already set up.
				_frameBuilder.RecordSharedPreparation(this);
				for (var viewIndex = 0; viewIndex < _recordedViews.Count; viewIndex++)
				{
					SelectView(_recordedViews[viewIndex]);
					_frameBuilder.RecordBoundView(this);
				}

				// The primary view is bound for execution: it runs the shared passes and owns presentation.
				SelectView(RenderViewId.Primary);
				_frameBuilder.RecordSharedPresentation(this);
				Execute();
				var sceneCaptureHandle = _frameBuilder.GetCaptureColorHandle();
				var windowCaptureHandle = _frameBuilder.GetFinalColorHandle();
				if (sceneCaptureHandle.IsValid || windowCaptureHandle.IsValid)
				{
					_renderer.CompletePendingFrameCapture(_resourceRegistry, sceneCaptureHandle, windowCaptureHandle);
				}

				for (var viewIndex = 0; viewIndex < _recordedViews.Count; viewIndex++)
				{
					SelectView(_recordedViews[viewIndex]);
					_frameBuilder.CompleteFrame();
					var viewSceneColor = _sceneColorHandles[_recordedViews[viewIndex]];
					if (viewSceneColor.IsValid)
					{
						_view.SceneRenderTarget.SetCurrentState(_resourceRegistry.GetResourceState(viewSceneColor));
					}
				}

				SelectView(RenderViewId.Primary);
				_renderer.Render(_resourceRegistry, _frameBuilder.GetFinalColorHandle());
				foreach (var liveView in _viewRegistry.Views)
				{
					// A view that did not record this frame publishes nothing, so the UI cannot sample a texture
					// resolved on an earlier frame.
					if (_recordedViews.Contains(liveView.View) == false)
					{
						liveView.ResolvedSceneViewportState = SceneViewportRenderState.Empty;
					}

					_viewportStateBus.PublishRenderState(liveView.View, liveView.ResolvedSceneViewportState);
				}

				_resourceRegistry.EndFrame();
			}
			finally
			{
				uiFrame.Release();
			}
		}

		EndGpuCaptureIfActive();

		// Clear for next frame
		_arenaAllocator.Reset();
		_frameIndex++;
		var retirementStats = _renderer.GetGfxDevice().RetirementStats;
		_hardeningStats.SetDeferredReleaseBacklog(retirementStats.UnsealedCount + retirementStats.PendingCount);
		LogGpuHardeningStatsIfNeeded();
		PublishRayTracingSceneState();
		_renderFrameCoordinator.PublishCompletedFrame();
		FrameProfiler.Instance.EndFrame();
	}

	/// <summary>
	/// Arms a programmatic GPU capture of the next rendered frame. Safe to call from any thread; the
	/// capture is taken on the render thread at the next frame boundary.
	/// </summary>
	public void RequestGpuCapture() => Volatile.Write(ref _gpuCaptureRequested, 1);

	private void BeginGpuCaptureIfRequested()
	{
		var requestedFrame = GraphicsConfig.GpuCaptureFrameIndex;
		var requested = Interlocked.Exchange(ref _gpuCaptureRequested, 0) != 0 ||
		                (requestedFrame > 0 && _frameIndex + 1 == requestedFrame);
		if (requested == false || _renderer.IsGpuCaptureActive)
		{
			return;
		}

		if (_renderer.TryStartGpuCapture($"frame{_frameIndex + 1}", out var error) == false)
		{
			Console.WriteLine($"[gpu capture] frame {_frameIndex + 1} could not be captured: {error}");
			return;
		}

		_gpuCaptureActive = true;
	}

	private void EndGpuCaptureIfActive()
	{
		if (_gpuCaptureActive == false)
		{
			return;
		}

		_gpuCaptureActive = false;
		if (_renderer.TryStopGpuCapture(out var error) == false)
		{
			Console.WriteLine($"[gpu capture] failed to stop the capture: {error}");
		}
	}

	private float ComputeDisplayScale(Int2 frameBufferSize)
	{
		var windowSize = _renderer.GetWindowSize();
		if (windowSize.X <= 0 || windowSize.Y <= 0) return 1.0f;

		return (frameBufferSize.X / (float)windowSize.X + frameBufferSize.Y / (float)windowSize.Y) * 0.5f;
	}

	public Int2 GetFrameBufferSize() => _renderer.GetFrameBufferSize();

	/// <summary>Returns the most recent renderer-thread RTAS snapshot without synchronizing the GPU.</summary>
	public RayTracingSceneState GetRayTracingSceneState() => Volatile.Read(ref _latestRayTracingSceneState);

	private void PublishRayTracingSceneState()
	{
		var state = _frameBuilder.GetRayTracingSceneState();
		if (_renderer.GetGfxDevice() is IGpuSubmissionTimeline timeline)
		{
			state = state with
			{
				LastSubmittedId = timeline.LastSubmittedId,
				CompletedId = timeline.CompletedId
			};
		}

		Volatile.Write(ref _latestRayTracingSceneState, state);
	}

	public void EnsureMaterialResources(Material material)
	{
		if (material is null)
		{
			throw new ArgumentNullException(nameof(material));
		}

		material.MarkResourceRequested();
		lock (_resourceSync)
		{
			_trackedMaterials.Add(material);
			_pendingMaterials.Add(material);
		}

		var textures = material.GetTrackedTextures();
		for (var i = 0; i < textures.Length; i++)
		{
			if (textures[i] is not null)
			{
				EnsureTextureResources(textures[i]);
			}
		}
	}

	public void RefreshMaterialResources(Material material)
	{
		if (material is null)
		{
			throw new ArgumentNullException(nameof(material));
		}

		material.MarkGpuResourcesDirty();
		EnsureMaterialResources(material);
	}

	public void EnsureTextureResources(Texture texture)
	{
		if (texture is null)
		{
			throw new ArgumentNullException(nameof(texture));
		}

		// Persistent render targets are created and published by their owning renderer. Treating their
		// placeholder mip as an asset upload would replace the stable shader-resource descriptor.
		if (texture.IsRenderTarget)
		{
			return;
		}

		texture.MarkResourceRequested();
		lock (_resourceSync)
		{
			_pendingTextures.Add(texture);
		}
	}

	public void EnsureMeshResources(Mesh mesh)
	{
		if (mesh is null)
		{
			throw new ArgumentNullException(nameof(mesh));
		}

		_ensureMeshQueue.Enqueue(mesh);
	}

	public void ReleaseMeshResources(Mesh mesh)
	{
		if (mesh is null)
		{
			throw new ArgumentNullException(nameof(mesh));
		}

		_renderer.ReleaseMeshResources(mesh);
	}


	private void ReleasePasses()
	{
		foreach (var pass in _passes)
		{
			pass.Clear();
			_passPool.Enqueue(pass);
		}

		_passes.Clear();
	}

	private static bool TryComputeSceneRenderSize(SceneViewportUiState state, out Int2 sceneRenderSize)
	{
		if (state.Visible == false || state.ContentSizePixels.X <= 0 || state.ContentSizePixels.Y <= 0)
		{
			sceneRenderSize = Int2.Zero;
			return false;
		}

		var scale = Math.Clamp(state.ResolutionScale, 0.5f, 1.0f);
		var width = Math.Max(1, (int)MathF.Round(state.ContentSizePixels.X * scale));
		var height = Math.Max(1, (int)MathF.Round(state.ContentSizePixels.Y * scale));
		sceneRenderSize = new Int2(width, height);
		return true;
	}

	private static bool TryComputeFullWindowSceneRenderSize(Int2 framebufferSize, out Int2 sceneRenderSize)
	{
		sceneRenderSize = framebufferSize;
		return framebufferSize.X > 0 && framebufferSize.Y > 0;
	}

	private static int ParsePositiveIntEnvironmentVariable(string name, int fallback)
	{
		var raw = Environment.GetEnvironmentVariable(name);
		if (int.TryParse(raw, out var parsed) && parsed > 0)
		{
			return parsed;
		}

		return fallback;
	}

	private void LogGpuHardeningStatsIfNeeded()
	{
		if (_gpuHardeningLogInterval <= 0 ||
		    (_frameIndex % _gpuHardeningLogInterval) != 0)
		{
			return;
		}

		var snapshot = _hardeningStats.Snapshot();
		var logLine =
			$"[GpuHardening] frame={_frameIndex} staleRejects={snapshot.StaleHandleRejects} " +
			$"fallbackSubs={snapshot.FallbackProxySubstitutions} overflowRecoveries={snapshot.UpdateOverflowRecoveries} " +
			$"packedCapacityFailures={snapshot.PackedCapacityFailures} visibleClampHits={snapshot.VisibleListClampHits} " +
			$"materialFallbackDrawHits={snapshot.MaterialFallbackDrawHits} " +
			$"deferredBacklog={snapshot.DeferredReleaseBacklog} icbStarvationStalls={snapshot.IcbSlotStarvationStalls}";
		for (var i = 0; i < snapshot.BucketDiagnostics.Count; i++)
		{
			var bucket = snapshot.BucketDiagnostics[i];
			logLine +=
				$" bucket[{bucket.BucketId}:{bucket.ExecutionIndex}]={{submitted:{bucket.SubmittedDrawCount}," +
				$"visible:{bucket.VisibleDrawCount},range:{bucket.ExecutionRangeStart}-{bucket.ExecutionRangeEndExclusive}," +
				$"fallbacks:{bucket.MaterialFallbackIncidents}}}";
		}

		Console.WriteLine(logLine);
	}

	private void ProcessPendingTextures(List<Texture> changedTextures)
	{
		ArgumentNullException.ThrowIfNull(changedTextures);

		var pendingTextures = DrainPendingTextures();
		for (var i = 0; i < pendingTextures.Count; i++)
		{
			var texture = pendingTextures[i];
			texture.ClearResourceRequestPending();
			if (texture.HasGpuResources && texture.Resources is not null)
			{
				continue;
			}

			var currentResources = texture.Resources;
			var resources = currentResources is not null &&
			                _renderer.TryUpdateTextureResources(texture, currentResources)
				? currentResources
				: _renderer.CreateTextureResources(texture);
			var previousResources = texture.MarkGpuResourcesCreated(resources);
			if (previousResources is not null)
			{
				QueueTextureResourceRelease(previousResources);
			}

			changedTextures.Add(texture);
		}
	}

	private void QueueTextureResourceRelease(ITextureResources resources)
	{
		var texture = resources.Texture;
		_renderer.GetGfxDevice().Retire(
			() =>
			{
				(texture as IDisposable)?.Dispose();
				if (ReferenceEquals(resources, texture) == false)
				{
					(resources as IDisposable)?.Dispose();
				}
			},
			texture.Name ?? "Replaced texture resources");
	}

	private void MarkDependentMaterialsPending(IReadOnlyList<Texture> changedTextures)
	{
		var trackedMaterials = DrainTrackedMaterialsSnapshot();
		for (var i = 0; i < trackedMaterials.Count; i++)
		{
			var material = trackedMaterials[i];
			for (var textureIndex = 0; textureIndex < changedTextures.Count; textureIndex++)
			{
				if (material.DependsOnTexture(changedTextures[textureIndex]) == false)
				{
					continue;
				}

				material.MarkGpuResourcesDirty();
				material.MarkResourceRequested();
				lock (_resourceSync)
				{
					_pendingMaterials.Add(material);
				}

				break;
			}
		}
	}

	private void ProcessPendingMaterials(List<Material> changedMaterials)
	{
		ArgumentNullException.ThrowIfNull(changedMaterials);

		var pendingMaterials = DrainPendingMaterials();
		for (var i = 0; i < pendingMaterials.Count; i++)
		{
			var material = pendingMaterials[i];
			material.ClearResourceRequestPending();

			var textures = material.GetTrackedTextures();
			for (var textureIndex = 0; textureIndex < textures.Length; textureIndex++)
			{
				if (textures[textureIndex] is { IsRenderTarget: true } renderTarget)
				{
					_gameplayUiRenderer.EnsureTarget(_renderer.GetGfxDevice(), renderTarget);
				}
				else if (textures[textureIndex] is not null)
				{
					EnsureTextureResources(textures[textureIndex]);
				}
			}

			if (material.AreRequiredTextureResourcesReady() == false)
			{
				material.MarkResourceRequested();
				lock (_resourceSync)
				{
					_pendingMaterials.Add(material);
				}

				continue;
			}

			if (material.NeedsGpuResourceRebuild() == false)
			{
				continue;
			}

			var resources = _renderer.CreateMaterialResources(material);
			material.MarkGpuResourcesBuilt(resources);
			changedMaterials.Add(material);
		}
	}

	private List<Texture> DrainPendingTextures()
	{
		lock (_resourceSync)
		{
			if (_pendingTextures.Count == 0)
			{
				return new List<Texture>();
			}

			var result = _pendingTextures.ToList();
			_pendingTextures.Clear();
			return result;
		}
	}

	private List<Material> DrainPendingMaterials()
	{
		lock (_resourceSync)
		{
			if (_pendingMaterials.Count == 0)
			{
				return new List<Material>();
			}

			var result = _pendingMaterials.ToList();
			_pendingMaterials.Clear();
			return result;
		}
	}

	private List<Material> DrainTrackedMaterialsSnapshot()
	{
		lock (_resourceSync)
		{
			return _trackedMaterials.ToList();
		}
	}

	private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
	{
		public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

		public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
	}
}
