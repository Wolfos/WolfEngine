using System.Collections.Concurrent;
using System.Numerics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.UI;
using WolfEngine.Mathematics;
using WolfEngine.Profiling;
using WolfEngine.Utility;
using WolfEngine.Rendering.Backend.Metal;

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
	private readonly IMainThreadDispatcher _mainThreadDispatcher;
	private readonly GpuDrawResources _gpuDrawResources;
	private readonly GpuDrawHardeningStats _hardeningStats;
	private readonly bool _metalLeakDiagnosticsEnabled;
	private readonly int _metalLeakDiagnosticsInterval;
	private readonly int _gpuHardeningLogInterval;
	private FrameSnapshot _currentSnapshot;
	private FrameSnapshot _activeSnapshot;
	private long _lastProcessWorkingSetBytes;
	private bool _hasLastProcessMemorySnapshot;
	private int _frameIndex;
	public event Action? FrameCompleted;

	private readonly ConcurrentQueue<Material> _ensureMaterialQueue = new();
	private readonly ConcurrentQueue<Texture> _ensureTextureQueue = new();
	private readonly ConcurrentQueue<Mesh> _ensureMeshQueue = new();

	public RenderGraph(
		RenderGraphResourceRegistry resourceRegistry,
		IRenderer renderer,
		IArenaAllocator arenaAllocator,
		DeferredLightingPass deferredLightingPass,
		GpuDrawPass gpuDrawPass,
		GpuDrawResources gpuDrawResources,
		GpuDrawHardeningStats hardeningStats,
		IUiFrameProvider uiFrameProvider,
		IMainThreadDispatcher mainThreadDispatcher,
		IImGuiRenderer imGuiRenderer)
	{
		_resourceRegistry = resourceRegistry;
		_renderer = renderer;
		_arenaAllocator = arenaAllocator;
		_frameBuilder = new(resourceRegistry, renderer, deferredLightingPass, gpuDrawPass, gpuDrawResources,
			imGuiRenderer);
		_gpuDrawResources = gpuDrawResources;
		_hardeningStats = hardeningStats ?? throw new ArgumentNullException(nameof(hardeningStats));
		_uiFrameProvider = uiFrameProvider;
		_mainThreadDispatcher = mainThreadDispatcher;
		_compiler = new(resourceRegistry);
		_metalLeakDiagnosticsEnabled = string.Equals(
			Environment.GetEnvironmentVariable("WOLF_METAL_LEAK_DIAG"),
			"1",
			StringComparison.Ordinal);
		_metalLeakDiagnosticsInterval = ParsePositiveIntEnvironmentVariable("WOLF_METAL_LEAK_DIAG_INTERVAL", 120);
		_gpuHardeningLogInterval = GraphicsConfig.GpuHardeningLogIntervalFrames;
	}


	public RenderGraphBuilder AddPass(string name, PassKind kind = PassKind.Graphics)
	{
		var pass = _passPool.Count > 0 ? _passPool.Dequeue() : new RenderGraphPass();
		pass.Configure(name, kind);
		_passes.Add(pass);
		return new(pass, _resourceRegistry);
	}

	public void Execute()
	{
		// Compile barriers before execution
		_compiler.Compile(_passes);

		var device = _renderer.GetGfxDevice();

		var snapshot = _activeSnapshot;
		if (snapshot is null)
		{
			ReleasePasses();
			return;
		}

		// Build scene data from snapshot
		SceneDrawData sceneData = null;
		var world = snapshot.CameraWorldTransform.LocalToWorld;
		if (Matrix4x4.Invert(world, out var view) &&
		    Matrix4x4.Decompose(world, out _, out _, out var cameraPosition) &&
		    Matrix4x4.Invert(snapshot.Camera.Perspective, out var invProjection))
		{
			_renderLights.Clear();
			for (var i = 0; i < snapshot.LightPackets.Count; i++)
			{
				var lightPacket = snapshot.LightPackets[i];
				var lightTransform = lightPacket.Transform;
				lightTransform.Translation -= cameraPosition;
				_renderLights.Add(new LightPacket(lightPacket.Light, lightTransform));
			}

			// Remove camera translation from the view matrix since objects are now camera-relative
			view.Translation = Vector3.Zero;
			var viewProjection = view * snapshot.Camera.Perspective;
			if (Matrix4x4.Invert(viewProjection, out var invViewProjection) == false)
			{
				ReleasePasses();
				return;
			}

			sceneData = new(viewProjection, invProjection, invViewProjection, cameraPosition, _renderLights);
		}

		if (sceneData is null)
		{
			ReleasePasses();
			return;
		}

		foreach (var pass in _passes)
		{
			using (FrameProfiler.Instance.Measure($"Pass: {pass.Name}"))
			{
				// Materialize resources used by this pass
				for (var i = 0; i < pass.Reads.Count; i++)
				{
					_resourceRegistry.GetTexture(pass.Reads[i]);
				}

				for (var i = 0; i < pass.Writes.Count; i++)
				{
					_resourceRegistry.GetTexture(pass.Writes[i]);
				}

				// Create command list for this pass based on its kind
				var commandList = pass.Kind == PassKind.Graphics
					? device.BeginGraphics()
					: device.BeginCompute();

				commandList.SetBindlessTable(device.GlobalTable);

				// Inject barriers before the pass executes
				for (var i = 0; i < pass.Barriers.Count; i++)
				{
					commandList.Barrier(pass.Barriers[i]);
				}

				// Execute the pass with the command list and scene data
				var context = new RenderGraphContext(_resourceRegistry, pass.Name)
				{
					CommandList = commandList,
					SceneData = sceneData
				};
				pass.Execute(context);

				// Submit the command list
				device.Submit(commandList);
			}
		}

		ReleasePasses();
	}

	public void Startup(Action startup, Action<float> update)
	{
		_renderer.Run(startup, update, OnRender);
	}

	public FrameSnapshot BeginSnapshotWrite()
	{
		return _snapshotBuffer.BeginWrite();
	}

	public void PublishSnapshot()
	{
		_snapshotBuffer.PublishWrite();
	}

	public void SetSkybox(SkyboxResources skybox)
	{
		_frameBuilder.SetSkybox(skybox);
	}

	public void OnRender(float deltaTime)
	{
		FrameProfiler.Instance.BeginFrame("Render Frame");
		if (_renderer.GetGfxDevice() is IGpuSubmissionTimeline submissionTimeline)
		{
			submissionTimeline.PumpCompleted();
		}

		using (FrameProfiler.Instance.Measure("Upload resources"))
		{
			while (_ensureTextureQueue.TryDequeue(out var texture))
			{
				EnsureTextureResource(texture);
			}

			while (_ensureMaterialQueue.TryDequeue(out var material))
			{
				if (material is null || material.Resources is not null)
				{
					continue;
				}

				material.Resources = _renderer.CreateMaterialResources(material);
			}

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
		_resourceRegistry.SetDevice(_renderer.GetGfxDevice());
		_gpuDrawResources.EnsureCreated(_renderer.GetGfxDevice());

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

				var frameBufferSize = _renderer.GetFrameBufferSize();
				_frameBuilder.BeginFrame(frameBufferSize);
				_frameBuilder.SetUiFrame(uiFrame);

				_frameBuilder.Build(this);
				Execute();

				_renderer.Render(_resourceRegistry, _frameBuilder.GetFinalColorHandle());

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
		LogMetalLeakDiagnosticsIfNeeded();
		FrameCompleted?.Invoke();
		FrameProfiler.Instance.EndFrame();
	}

	public Int2 GetFrameBufferSize() => _renderer.GetFrameBufferSize();

	public void EnsureMaterialResources(Material material)
	{
		if (material is null)
		{
			throw new ArgumentNullException(nameof(material));
		}

		_ensureMaterialQueue.Enqueue(material);
	}

	public void EnsureTextureResources(Texture texture)
	{
		if (texture is null)
		{
			throw new ArgumentNullException(nameof(texture));
		}

		_ensureTextureQueue.Enqueue(texture);
	}

	public void EnsureMeshResources(Mesh mesh)
	{
		if (mesh is null)
		{
			throw new ArgumentNullException(nameof(mesh));
		}

		_ensureMeshQueue.Enqueue(mesh);
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
		if (GraphicsConfig.GpuHardeningStressEnabled == false || _gpuHardeningLogInterval <= 0 ||
		    (_frameIndex % _gpuHardeningLogInterval) != 0)
		{
			return;
		}

		var snapshot = _hardeningStats.Snapshot();
		Console.WriteLine(
			$"[GpuHardening] frame={_frameIndex} staleRejects={snapshot.StaleHandleRejects} " +
			$"fallbackSubs={snapshot.FallbackProxySubstitutions} overflowRecoveries={snapshot.UpdateOverflowRecoveries} " +
			$"packedCapacityFailures={snapshot.PackedCapacityFailures} visibleClampHits={snapshot.VisibleListClampHits} " +
			$"materialFallbackDrawHits={snapshot.MaterialFallbackDrawHits} " +
			$"deferredBacklog={snapshot.DeferredReleaseBacklog} icbStarvationStalls={snapshot.IcbSlotStarvationStalls}");
	}

	private void EnsureTextureResource(Texture? texture)
	{
		if (texture is null || texture.Resources is not null)
		{
			return;
		}

		texture.Resources = _renderer.CreateTextureResources(texture);
	}

#pragma warning disable CA1416
	private void LogMetalLeakDiagnosticsIfNeeded()
	{
		if (_metalLeakDiagnosticsEnabled == false)
		{
			return;
		}

		if ((_frameIndex % _metalLeakDiagnosticsInterval) != 0)
		{
			return;
		}

		if (_renderer.GetGfxDevice() is not MetalDevice)
		{
			return;
		}

		var workingSetBytes = Environment.WorkingSet;
		if (_hasLastProcessMemorySnapshot)
		{
			Console.WriteLine(
				$"[MetalLeakDiag] frame={_frameIndex} " +
				$"procWorkingSetMiB={(workingSetBytes / (1024.0 * 1024.0)):F2} " +
				$"({(workingSetBytes - _lastProcessWorkingSetBytes) / (1024.0 * 1024.0):+#.##;-#.##;0.00})");
		}
		else
		{
			Console.WriteLine(
				$"[MetalLeakDiag] frame={_frameIndex} baseline " +
				$"procWorkingSetMiB={(workingSetBytes / (1024.0 * 1024.0)):F2}");
		}

		_lastProcessWorkingSetBytes = workingSetBytes;
		_hasLastProcessMemorySnapshot = true;
	}
#pragma warning restore CA1416
}