using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;
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
private readonly IRenderCommandFactory _renderCommandFactory;
private readonly List<RenderGraphPass> _passes = new();
private readonly Queue<RenderGraphPass> _passPool = new();
private readonly RenderGraphCompiler _compiler;
private readonly ConcurrentQueue<RenderCommand> _pendingCommands = new();
private readonly FrameSnapshotBuffer _snapshotBuffer = new();
private readonly List<DrawPacket> _renderPackets = new();
private FrameSnapshot? _currentSnapshot;
private FrameSnapshot? _activeSnapshot;

	public RenderGraph(
		RenderGraphResourceRegistry resourceRegistry,
		IRenderer renderer,
		IArenaAllocator arenaAllocator,
		DeferredLightingPass deferredLightingPass,
		IRenderCommandFactory renderCommandFactory)
	{
		_resourceRegistry = resourceRegistry;
		_renderer = renderer;
		_arenaAllocator = arenaAllocator;
		_renderCommandFactory = renderCommandFactory;
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

		var snapshot = _activeSnapshot;
		if (snapshot is null)
		{
			ReleasePasses();
			return;
		}

		// Build scene data from snapshot
		SceneDrawData sceneData = null;
		var world = snapshot.CameraTransform.GetTransform();
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

			// Remove camera translation from the view matrix since objects are now camera-relative
			view.Translation = Vector3.Zero;
			var viewProjection = view * snapshot.Camera.Perspective;
			sceneData = new(viewProjection, invProjection, cameraPosition, _renderPackets);
		}

		if (sceneData is null)
		{
			ReleasePasses();
			return;
		}

		foreach (var pass in _passes)
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

	public void OnRender(float deltaTime)
	{
		_resourceRegistry.SetDevice(_renderer.GetGfxDevice());

		_renderer.BeginFrame();

		_resourceRegistry.BeginFrame();
		ReleasePasses();

		// Process pending render commands to build scene data
		ProcessCommands();

		if (_snapshotBuffer.TryConsumeLatest(out var snapshot) == false)
		{
			snapshot = _currentSnapshot;
		}
		_currentSnapshot = snapshot;
		_activeSnapshot = snapshot;
		if (snapshot is not null)
		{
			EmitCommandsFromSnapshot(snapshot);
		}

		var frameBufferSize = _renderer.GetFrameBufferSize();
		var backBuffer = _renderer.ImportBackbuffer(_resourceRegistry, frameBufferSize.X, frameBufferSize.Y);
		var frameResources = _frameBuilder.BeginFrame(frameBufferSize, backBuffer);

		_frameBuilder.Build(this);
		Execute();
		
		_renderer.Render(deltaTime, _resourceRegistry, backBuffer, frameResources.LightingBuffer);

		_resourceRegistry.EndFrame();

		// Clear for next frame
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

	public IMaterialResources EnsureMaterialResources(Material material)
	{
		// TODO: Should probably be handled in resource registry
		return _renderer.CreateMaterialResources(material);
	}

	public void SubmitCommand(RenderCommand command)
	{
		_pendingCommands.Enqueue(command);
	}

	private void EmitCommandsFromSnapshot(FrameSnapshot snapshot)
	{
		var camera = snapshot.Camera;
		var cameraTransform = snapshot.CameraTransform;
		var setCamera = _renderCommandFactory.SetCamera(ref camera, ref cameraTransform);
		SubmitCommand(setCamera);

		for (var i = 0; i < snapshot.DrawPackets.Count; i++)
		{
			var packet = snapshot.DrawPackets[i];
			var meshRenderer = new MeshRenderer {Mesh = packet.Mesh, Material = packet.Material};
			var transform = packet.Transform;
			var draw = _renderCommandFactory.DrawMesh(ref meshRenderer, ref transform);
			SubmitCommand(draw);
		}
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
