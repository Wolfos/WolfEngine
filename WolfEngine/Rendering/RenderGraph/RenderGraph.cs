using System.Collections.Concurrent;
using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

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
	private readonly ConcurrentQueue<RenderCommand> _pendingCommands = new();
	private readonly List<DrawPacket> _drawPackets = new();
	private Camera _camera;
	private Transform _cameraTransform;
	private bool _hasCamera;

	public RenderGraph(
		RenderGraphResourceRegistry resourceRegistry,
		IRenderer renderer,
		IArenaAllocator arenaAllocator,
		DeferredLightingPass deferredLightingPass)
	{
		_resourceRegistry = resourceRegistry;
		_renderer = renderer;
		_arenaAllocator = arenaAllocator;
		_frameBuilder = new(resourceRegistry, renderer, deferredLightingPass);
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

		// Build scene data from camera and draw packets
		SceneDrawData sceneData = null;
		if (_hasCamera)
		{
			var world = _cameraTransform.GetTransform();
			if (Matrix4x4.Invert(world, out var view) &&
			    Matrix4x4.Decompose(world, out _, out _, out var cameraPosition) &&
			    Matrix4x4.Invert(_camera.Perspective, out var invProjection))
			{
				// Use camera-relative space for draw packets
				for (var i = 0; i < _drawPackets.Count; i++)
				{
					var packet = _drawPackets[i];
					var relative = packet.Transform;
					var translation = relative.Translation - cameraPosition;
					relative.Translation = translation;
					_drawPackets[i] = new(packet.Mesh, packet.Material, relative);
				}

				// Remove camera translation from the view matrix since objects are now camera-relative
				view.Translation = Vector3.Zero;
				var viewProjection = view * _camera.Perspective;
				sceneData = new(viewProjection, invProjection, cameraPosition, _drawPackets);
			}
		}

		// Skip all rendering if there's no scene data yet (no camera set)
		if (sceneData == null)
		{
			ReleasePasses();
			return;
		}

		foreach (var pass in _passes)
		{
			// Materialize resources used by this pass
			foreach (var read in pass.Reads)
			{
				_resourceRegistry.GetTexture(read);
			}

			foreach (var write in pass.Writes)
			{
				_resourceRegistry.GetTexture(write);
			}

			// Create command list for this pass based on its kind
			var commandList = pass.Kind == PassKind.Graphics
				? device.BeginGraphics()
				: device.BeginCompute();

			// Inject barriers before the pass executes
			foreach (var barrier in pass.Barriers)
			{
				commandList.Barrier(barrier);
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

		ReleasePasses();
	}

	public void Startup(Action startup, Action<float> update)
	{
		_renderer.Run(startup, update, OnRender);
	}

	public void OnRender(float deltaTime)
	{
		_resourceRegistry.SetDevice(_renderer.GetGfxDevice());

		_renderer.BeginFrame();

		_resourceRegistry.BeginFrame();
		ReleasePasses();

		// Process pending render commands to build scene data
		ProcessCommands();

		var frameBufferSize = _renderer.GetFrameBufferSize();
		var backBuffer = _renderer.ImportBackbuffer(_resourceRegistry, frameBufferSize.X, frameBufferSize.Y);
		var frameResources = _frameBuilder.BeginFrame(frameBufferSize, backBuffer);

		_frameBuilder.Build(this);
		Execute();

		_renderer.Render(deltaTime, _resourceRegistry, backBuffer, frameResources.LightingBuffer);

		_resourceRegistry.EndFrame();

		// Clear for next frame
		_drawPackets.Clear();
		_arenaAllocator.Reset();
	}

	private void ProcessCommands()
	{
		while (_pendingCommands.TryDequeue(out var command))
		{
			switch (command.Type)
			{
				case RenderCommandType.CreateMesh:
					HandleCreateMesh(command);
					break;
				case RenderCommandType.DrawMesh:
					HandleDrawMesh(command);
					break;
				case RenderCommandType.SetCamera:
					HandleSetCamera(command);
					break;
			}
		}
	}

	private void HandleCreateMesh(RenderCommand command)
	{
		var payload = command.ReadPayload<RenderCommand.CreateMeshPayload>();
		if (payload.MeshHandle.Target is Mesh mesh)
		{
			_renderer.EnsureMeshResources(mesh);
		}

		payload.MeshHandle.Free();
	}

	private void HandleDrawMesh(RenderCommand command)
	{
		var payload = command.ReadPayload<RenderCommand.DrawMeshPayload>();
		if (payload.MeshHandle.Target is Mesh mesh && payload.MaterialHandle.Target is Material material)
		{
			_drawPackets.Add(new DrawPacket(mesh, material, payload.Transform));
		}

		payload.MeshHandle.Free();
		payload.MaterialHandle.Free();
	}

	private void HandleSetCamera(RenderCommand command)
	{
		var payload = command.ReadPayload<RenderCommand.SetCameraPayload>();
		_camera = payload.Camera;
		_cameraTransform = payload.Transform;
		_hasCamera = true;
	}

	public IMaterialResources EnsureMaterialResources(Material material)
	{
		// TODO: Should probably be handled in resource registry
		return _renderer.CreateMaterialResources(material);
	}

	public void SubmitCommand(RenderCommand command)
	{
		_pendingCommands.Enqueue(command);
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
