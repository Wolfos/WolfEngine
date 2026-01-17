using System.Numerics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.UI;
using WolfEngine.Mathematics;
using WolfEngine.Profiling;
using WolfEngine.Utility;

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
	private readonly List<DrawPacket> _renderPackets = new();
	private readonly List<LightPacket> _renderLights = new();
	private readonly IUiFrameProvider _uiFrameProvider;
	private readonly IMainThreadDispatcher _mainThreadDispatcher;
	private FrameSnapshot _currentSnapshot;
	private FrameSnapshot _activeSnapshot;
	public event Action? FrameCompleted;

	public RenderGraph(
		RenderGraphResourceRegistry resourceRegistry,
		IRenderer renderer,
		IArenaAllocator arenaAllocator,
		DeferredLightingPass deferredLightingPass,
		IUiFrameProvider uiFrameProvider,
		IMainThreadDispatcher mainThreadDispatcher,
		IImGuiRenderer imGuiRenderer)
	{
		_resourceRegistry = resourceRegistry;
		_renderer = renderer;
		_arenaAllocator = arenaAllocator;
		_frameBuilder = new(resourceRegistry, renderer, deferredLightingPass, imGuiRenderer);
		_uiFrameProvider = uiFrameProvider;
		_mainThreadDispatcher = mainThreadDispatcher;
		_compiler = new(resourceRegistry);
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
			_renderPackets.Clear();
			for (var i = 0; i < snapshot.DrawPackets.Count; i++)
			{
				var packet = snapshot.DrawPackets[i];
				var relative = packet.Transform;
				relative.Translation -= cameraPosition;
				_renderPackets.Add(new DrawPacket(packet.Mesh, packet.Material, relative));
			}

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
			sceneData = new(viewProjection, invProjection, invViewProjection, cameraPosition, _renderPackets, _renderLights);
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

		_mainThreadDispatcher.ExecutePending();
		_resourceRegistry.SetDevice(_renderer.GetGfxDevice());

		using (FrameProfiler.Instance.Measure("Begin Frame"))
		{
			_renderer.BeginFrame();
		}

		using (FrameProfiler.Instance.Measure("Build Frame"))
		{
			_resourceRegistry.BeginFrame();
			ReleasePasses();

			UiFrameData uiFrame;
			if (_uiFrameProvider.TryConsumeLatest(out var latestUi))
			{
				uiFrame = latestUi;
			}
			else
			{
				uiFrame = UiFrameData.Empty;
			}

			if (_snapshotBuffer.TryConsumeLatest(out var snapshot) == false)
			{
				snapshot = _currentSnapshot;
			}
			_currentSnapshot = snapshot;
			_activeSnapshot = snapshot;
			
			var frameBufferSize = _renderer.GetFrameBufferSize();
			var backBuffer = _renderer.ImportBackbuffer(_resourceRegistry, frameBufferSize.X, frameBufferSize.Y);
			var backbufferTexture = _resourceRegistry.GetTexture(backBuffer);
			var actualFrameSize = new Int2(backbufferTexture.Descriptor.Width, backbufferTexture.Descriptor.Height);
			_frameBuilder.BeginFrame(actualFrameSize, backBuffer);
			_frameBuilder.SetUiFrame(uiFrame);

			_frameBuilder.Build(this);
			Execute();

			_renderer.Render(_resourceRegistry, backBuffer);

			_resourceRegistry.EndFrame();
		}

		// Clear for next frame
		_arenaAllocator.Reset();
		FrameCompleted?.Invoke();
		FrameProfiler.Instance.EndFrame();
	}

	public Int2 GetFrameBufferSize() => _renderer.GetFrameBufferSize();

	public IMaterialResources EnsureMaterialResources(Material material)
	{
		return _renderer.CreateMaterialResources(material);
	}

	public ITextureResources EnsureTextureResources(Texture texture)
	{
		return _renderer.CreateTextureResources(texture);
	}

	public void EnsureMeshResources(Mesh mesh)
	{
		_renderer.EnsureMeshResources(mesh);
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
}
