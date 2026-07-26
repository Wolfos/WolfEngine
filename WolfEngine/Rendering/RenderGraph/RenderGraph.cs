using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
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
public sealed class RenderGraph
{
	private readonly RenderGraphResourceRegistry _resourceRegistry;
	private readonly RenderGraphFrameBuilder _frameBuilder;
	private readonly IRenderer _renderer;
	private readonly IArenaAllocator _arenaAllocator;
	private readonly List<RenderGraphPass> _passes = new();
	private readonly Queue<RenderGraphPass> _passPool = new();
	private readonly RenderGraphCompiler _compiler;
	private readonly FrameSnapshotBuffer _snapshotBuffer = new();
	private readonly List<LightPacket> _renderLights = new();
	private readonly IUiFrameProvider _uiFrameProvider;
	private readonly EditorViewportStateBus _viewportStateBus;
	private readonly RenderPresentationOptions _presentationOptions;
	private readonly IMainThreadDispatcher _mainThreadDispatcher;
	private readonly EditorFrameCoordinator _editorFrameCoordinator;
	private readonly RenderFrameCoordinator _renderFrameCoordinator;
	private readonly GpuDrawResources _gpuDrawResources;
	private readonly GpuDrawHardeningStats _hardeningStats;
	private readonly GpuProfiler _gpuProfiler;
	private readonly IImGuiRenderer _imGuiRenderer;
	private long _pendingShaderRevision;
	private long _appliedShaderRevision;
	private readonly EditorSceneRenderTargetManager _sceneRenderTargetManager = new();
	private readonly int _gpuHardeningLogInterval;
	private FrameSnapshot _currentSnapshot;
	private FrameSnapshot _activeSnapshot;
	private long _lastObservedEditorFrameSequence;
	private long _lastProcessWorkingSetBytes;
	private bool _hasLastProcessMemorySnapshot;
	private int _frameIndex;
	private bool _previousTaaEnabled;
	private Int2 _currentSceneRenderSize;
	private RayTracingSceneState _latestRayTracingSceneState = RayTracingSceneState.Empty;

	private readonly object _resourceSync = new();
	private readonly HashSet<Material> _pendingMaterials = new(new ReferenceComparer<Material>());
	private readonly HashSet<Texture> _pendingTextures = new(new ReferenceComparer<Texture>());
	private readonly HashSet<Material> _trackedMaterials = new(new ReferenceComparer<Material>());
	private readonly List<PendingTextureResourceRelease> _pendingTextureResourceReleases = new();
	private readonly ConcurrentQueue<Mesh> _ensureMeshQueue = new();

	private readonly record struct PendingTextureResourceRelease(
		ITextureResources Resources,
		ulong ReleaseAfterSubmissionId);

	public RenderGraph(
		RenderGraphResourceRegistry resourceRegistry,
		IRenderer renderer,
		IArenaAllocator arenaAllocator,
		GpuDrawResources gpuDrawResources,
		GpuDrawHardeningStats hardeningStats,
		GpuProfiler gpuProfiler,
		IUiFrameProvider uiFrameProvider,
		EditorViewportStateBus viewportStateBus,
		EditorFrameCoordinator editorFrameCoordinator,
		RenderFrameCoordinator renderFrameCoordinator,
		IMainThreadDispatcher mainThreadDispatcher,
		IImGuiRenderer imGuiRenderer,
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
			shaderProvider);
		_gpuDrawResources = gpuDrawResources;
		_hardeningStats = hardeningStats ?? throw new ArgumentNullException(nameof(hardeningStats));
		_gpuProfiler = gpuProfiler ?? throw new ArgumentNullException(nameof(gpuProfiler));
		_imGuiRenderer = imGuiRenderer ?? throw new ArgumentNullException(nameof(imGuiRenderer));
		_uiFrameProvider = uiFrameProvider;
		_viewportStateBus = viewportStateBus ?? throw new ArgumentNullException(nameof(viewportStateBus));
		_presentationOptions = presentationOptions ?? new RenderPresentationOptions();
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
		pass.Configure(name, kind);
		_passes.Add(pass);
		return new(pass, _resourceRegistry);
	}

	public void Execute()
	{
		ApplyPendingShaderReload();
		// Compile barriers before execution
		_compiler.Compile(_passes);
		_frameBuilder.PrepareSceneViewport();

		var device = _renderer.GetGfxDevice();
		var profilerBackend = (device as IGpuProfilerDevice)?.GpuProfilerBackend;

		var snapshot = _activeSnapshot;
		if (snapshot is null)
		{
			ReleasePasses();
			return;
		}

		// Build scene data from snapshot
		SceneDrawData sceneData = null;
		var world = snapshot.CameraWorldTransform.LocalToWorld;
		var taaEnabled = snapshot.Config.TemporalAntiAliasing.Enabled;
		var taaPhaseCount = snapshot.Config.TemporalAntiAliasing.PhaseCount > 0
			? snapshot.Config.TemporalAntiAliasing.PhaseCount
			: TemporalJitter.DefaultPhaseCount;
		var jitterPixels = taaEnabled
			? TemporalJitter.GetHaltonJitterPixels(
				(ulong)_frameIndex,
				taaPhaseCount)
			: Vector2.Zero;
		var jitterNdc = TemporalJitter.GetJitterNdc(jitterPixels, _currentSceneRenderSize);
		var jitteredProjection = taaEnabled
			? TemporalJitter.ApplyProjectionJitter(snapshot.Camera.Perspective, jitterNdc)
			: snapshot.Camera.Perspective;
		if (Matrix4x4.Invert(world, out var view) &&
		    Matrix4x4.Decompose(world, out _, out _, out var cameraPosition) &&
		    Matrix4x4.Invert(jitteredProjection, out var invProjection))
		{
			_renderLights.Clear();
			for (var i = 0; i < snapshot.LightPackets.Count; i++)
			{
				var lightPacket = snapshot.LightPackets[i];
				var lightTransform = lightPacket.Transform;
				lightTransform.Translation -= cameraPosition;
				_renderLights.Add(new LightPacket(lightPacket.Light, lightTransform));
			}

			// Remove camera translation from the view matrix since objects are camera-relative
			view.Translation = Vector3.Zero;
			var viewProjection = view * jitteredProjection;
			var unjitteredViewProjection = view * snapshot.Camera.Perspective;
			if (Matrix4x4.Invert(viewProjection, out var invViewProjection) == false)
			{
				ReleasePasses();
				return;
			}

			var hasPreviousCameraState = TryCreatePreviousCameraState(
				snapshot,
				unjitteredViewProjection,
				cameraPosition,
				out var previousViewProjection,
				out var previousCameraOrigin);

			sceneData = new(
				view,
				viewProjection,
				snapshot.Camera.Perspective,
				unjitteredViewProjection,
				previousViewProjection,
				invProjection,
				invViewProjection,
				cameraPosition,
				previousCameraOrigin,
				_currentSceneRenderSize,
				snapshot.Camera.NearPlane > 0.0f ? snapshot.Camera.NearPlane : Camera.DefaultNearPlane,
				snapshot.Camera.FarPlane > 0.0f ? snapshot.Camera.FarPlane : Camera.DefaultFarPlane,
				jitterPixels,
				jitterNdc,
				hasPreviousCameraState == false || (taaEnabled && _previousTaaEnabled == false),
				_renderLights,
				snapshot.DecalPackets);

			_previousTaaEnabled = taaEnabled;
		}

		if (sceneData is null &&
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

				// Inject barriers before the pass executes
				for (var i = 0; i < pass.Barriers.Count; i++)
				{
					commandList.Barrier(pass.Barriers[i]);
				}

				// Execute the pass with the command list and scene data
				var context = new RenderGraphContext(_resourceRegistry, pass.Name)
				{
					CommandList = commandList,
					SceneData = sceneData,
					GpuDrawDatabase = snapshot.GpuDrawDatabase,
					FrameSnapshot = snapshot
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

	private static bool TryCreatePreviousCameraState(
		FrameSnapshot snapshot,
		in Matrix4x4 fallbackViewProjection,
		in Vector3 fallbackCameraOrigin,
		out Matrix4x4 previousViewProjection,
		out Vector3 previousCameraOrigin)
	{
		if (snapshot.HasPreviousCameraState == false ||
		    Matrix4x4.Invert(snapshot.PreviousCameraWorldTransform.LocalToWorld, out var previousView) == false ||
		    Matrix4x4.Decompose(snapshot.PreviousCameraWorldTransform.LocalToWorld, out _, out _,
			    out previousCameraOrigin) == false)
		{
			previousViewProjection = fallbackViewProjection;
			previousCameraOrigin = fallbackCameraOrigin;
			return false;
		}

		previousView.Translation = Vector3.Zero;
		previousViewProjection = previousView * snapshot.PreviousCamera.Perspective;
		return true;
	}

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
		ReleaseRetiredTextureResources();

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
		_sceneRenderTargetManager.Advance(_renderer.GetGfxDevice());

		using (FrameProfiler.Instance.Measure("Begin Frame"))
		{
			_renderer.BeginFrame();
		}


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
				for (var i = 0; i < changedMaterials.Count; i++)
				{
					snapshot.GpuDrawDatabase.NotifyMaterialChanged(changedMaterials[i]);
				}

				var frameBufferSize = _renderer.GetFrameBufferSize();
				var sceneViewportState = _viewportStateBus.GetUiState();
				var renderSceneToWindow = _presentationOptions.OutputMode == RenderOutputMode.FullWindow;
				var sceneEnabled = renderSceneToWindow
					? TryComputeFullWindowSceneRenderSize(frameBufferSize, out var sceneRenderSize)
					: TryComputeSceneRenderSize(sceneViewportState, out sceneRenderSize);
				var currentResolution = sceneEnabled ? sceneRenderSize : frameBufferSize;
				if (currentResolution.X > 0 && currentResolution.Y > 0)
				{
					Screen.CurrentResolution = currentResolution;
				}

				_currentSceneRenderSize = sceneRenderSize;
				var renderSceneToViewport = sceneEnabled && !renderSceneToWindow;
				var sceneColorHandle = default(RenderGraphResourceHandle);
				if (renderSceneToViewport)
				{
					var sceneTarget = _sceneRenderTargetManager.EnsureTarget(_renderer.GetGfxDevice(), sceneRenderSize);
					sceneColorHandle = _resourceRegistry.ImportTexture(
						sceneTarget,
						takeOwnership: false,
						initialState: _sceneRenderTargetManager.CurrentState);
				}

				if (!Matrix4x4.Decompose(
					    snapshot.CameraWorldTransform.LocalToWorld,
					    out _,
					    out _,
					    out var frameCameraPosition))
				{
					frameCameraPosition = snapshot.Config.DiffuseGlobalIllumination.Origin;
				}

				_frameBuilder.SetSceneViewportSelection(sceneViewportState.RequestedDebugViewId);
				_frameBuilder.BeginFrame(
					frameBufferSize,
					sceneRenderSize,
					sceneColorHandle,
					renderSceneToViewport || renderSceneToWindow,
					snapshot.DecalPackets.Count > 0,
					snapshot.SunDirection,
					snapshot.SunIntensityScale,
					snapshot.Config,
					frameCameraPosition);
				_frameBuilder.SetUiFrame(uiFrame);

				_frameBuilder.Build(this);
				Execute();
				var captureColorHandle = _frameBuilder.GetCaptureColorHandle();
				if (captureColorHandle.IsValid)
				{
					_renderer.CompletePendingFrameCapture(_resourceRegistry, captureColorHandle);
				}

				_frameBuilder.CompleteFrame();
				if (sceneColorHandle.IsValid)
				{
					_sceneRenderTargetManager.SetCurrentState(_resourceRegistry.GetResourceState(sceneColorHandle));
				}

				_renderer.Render(_resourceRegistry, _frameBuilder.GetFinalColorHandle());
				_viewportStateBus.PublishRenderState(_frameBuilder.GetSceneViewportRenderState());

				_resourceRegistry.EndFrame();
			}
			finally
			{
				uiFrame.Release();
			}
		}

		// Clear for next frame
		_arenaAllocator.Reset();
		_frameIndex++;
		_hardeningStats.SetDeferredReleaseBacklog(_resourceRegistry.PendingDeferredReleaseCount);
		LogGpuHardeningStatsIfNeeded();
		PublishRayTracingSceneState();
		_renderFrameCoordinator.PublishCompletedFrame();
		FrameProfiler.Instance.EndFrame();
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

			var resources = _renderer.CreateTextureResources(texture);
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
		var releaseAfterSubmissionId = _renderer.GetGfxDevice() is IGpuSubmissionTimeline submissionTimeline
			? submissionTimeline.LastSubmittedId
			: 0UL;
		lock (_resourceSync)
		{
			_pendingTextureResourceReleases.Add(new PendingTextureResourceRelease(resources, releaseAfterSubmissionId));
		}
	}

	private void ReleaseRetiredTextureResources()
	{
		var completedSubmissionId = _renderer.GetGfxDevice() is IGpuSubmissionTimeline submissionTimeline
			? submissionTimeline.CompletedId
			: ulong.MaxValue;

		lock (_resourceSync)
		{
			for (var i = _pendingTextureResourceReleases.Count - 1; i >= 0; i--)
			{
				var pending = _pendingTextureResourceReleases[i];
				if (pending.ReleaseAfterSubmissionId > completedSubmissionId)
				{
					continue;
				}

				(pending.Resources.Texture as IDisposable)?.Dispose();
				(pending.Resources as IDisposable)?.Dispose();
				_pendingTextureResourceReleases.RemoveAt(i);
			}
		}
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
				if (textures[textureIndex] is not null)
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
