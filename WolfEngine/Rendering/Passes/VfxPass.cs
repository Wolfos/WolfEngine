using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine.ECS;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;
using WolfEngine.Vfx;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// Simulates and draws visual effects. Each world gets a fixed-size particle pool on the GPU; the game thread decides
/// which pool slots each emitter owns, so the GPU never allocates and nothing is read back.
/// </summary>
/// <remarks>
/// <para>
/// Simulation runs once per published frame per world, keyed by <see cref="VfxWorldFrame.Sequence"/>. A snapshot can be
/// drawn more than once, and a world can be shown by several views, so the simulate pass of every view checks the
/// sequence and only the first one for a new frame does any work.
/// </para>
/// <para>
/// Effects draw after transparent geometry and before temporal resolve, unlit, into the lighting buffer. Emitters
/// are sorted back to front by their position; particles within an emitter draw oldest first. That keeps alpha
/// blending stable, but two overlapping alpha-blended emitters, or an effect inside a transparent mesh, can sort
/// wrongly. The scene depth is sampled rather than bound, so the same read depth-tests and softens effects.
/// </para>
/// </remarks>
public sealed class VfxPass
{
	private const int MinimumUploadElements = 16;

	// Matches VFX_NO_TEXTURE in vfx_common.slang: the effect draws a soft procedural shape instead.
	private const uint NoTexture = uint.MaxValue;

	// A world's pool is released once no view has drawn it for this long, such as a closed preview or a
	// discarded Play-mode world.
	private static readonly long UnusedWorldTimeout = Stopwatch.Frequency * 10;

	private static readonly ushort[] QuadIndices = [0, 1, 2, 2, 1, 3];

	private readonly IShaderProvider _shaderProvider;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly Dictionary<World, WorldResources> _worlds = new(ReferenceEqualityComparer.Instance);
	private readonly List<World> _releaseScratch = new();
	private readonly List<VfxDrawPacket> _sortedDraws = new();
	private IGfxPipeline? _simulatePipeline;
	private IGfxPipeline? _emitPipeline;
	private IGfxPipeline? _spriteAlphaPipeline;
	private IGfxPipeline? _spriteAdditivePipeline;
	private IGfxPipeline? _ribbonAlphaPipeline;
	private IGfxPipeline? _ribbonAdditivePipeline;
	private ComputeThreadGroupSize _simulateGroupSize;
	private ComputeThreadGroupSize _emitGroupSize;
	private ShaderPropertyWriter? _simulateWriter;
	private ShaderPropertyWriter? _spriteViewWriter;
	private ShaderPropertyWriter? _spriteDrawWriter;
	private ShaderPropertyWriter? _ribbonViewWriter;
	private ShaderPropertyWriter? _ribbonDrawWriter;
	private uint _spriteParticlesRegister;
	private uint _spriteEmittersRegister;
	private uint _ribbonEmittersRegister;
	private uint _ribbonPointsRegister;
	private IGfxBuffer? _quadIndexBuffer;
	private IGfxBuffer? _stripIndexBuffer;
	private DescriptorHandle _sampler = DescriptorHandle.Invalid;

	public VfxPass(IShaderProvider shaderProvider, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderProvider = shaderProvider ?? throw new ArgumentNullException(nameof(shaderProvider));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	/// <summary>Whether a view's frame has anything to simulate or draw.</summary>
	internal static bool HasWork(VfxWorldFrame? frame) =>
		frame is not null &&
		frame.World is not null &&
		(frame.EmitterCount > 0 || frame.Draws.Count > 0);

	/// <summary>
	/// Uploads this frame's emitters, spawns and ribbon points, then advances and spawns particles. Does nothing
	/// if another view already simulated this frame of the world.
	/// </summary>
	internal void Simulate(RenderGraphContext context, IGfxDevice device, VfxWorldFrame frame)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);
		ReleaseUnusedWorlds(device);
		if (HasWork(frame) == false)
		{
			return;
		}

		var resources = GetWorldResources(device, frame.World!);
		resources.LastUsed = Stopwatch.GetTimestamp();
		if (resources.SimulatedSequence == frame.Sequence)
		{
			return;
		}

		resources.SimulatedSequence = frame.Sequence;
		resources.FrameSlot = (resources.FrameSlot + 1) % GpuDrawResources.MaxFramesInFlight;
		Upload(device, resources, frame);

		if (frame.EmitterCount == 0)
		{
			return;
		}

		EnsureComputePipelines(device);
		var writer = _simulateWriter!;
		var commandList = context.CommandList;
		var slot = resources.FrameSlot;
		writer.Clear();
		writer.SetFloat("deltaTime", frame.DeltaTime);
		writer.SetUInt("particleCount", (uint)VfxLimits.ParticlePoolCapacity);
		writer.SetUInt("emitterCount", (uint)frame.EmitterCount);
		writer.SetUInt("spawnCount", (uint)frame.SpawnedParticleCount);
		writer.SetUInt("batchCount", (uint)frame.Spawns.Count);

		if (frame.DeltaTime > 0.0f)
		{
			commandList.BindPipeline(_simulatePipeline!);
			commandList.SetComputeBuffer(0, resources.Particles);
			commandList.SetComputeReadOnlyBuffer(2, resources.Emitters[slot]!);
			commandList.SetComputeReadOnlyBuffer(3, resources.Spawns[slot]!);
			commandList.SetComputeConstants(writer.RegisterIndex, writer.AsBytes());
			var (groups, _, _) = _simulateGroupSize.GetDispatchGroupCount((uint)VfxLimits.ParticlePoolCapacity);
			commandList.Dispatch(groups, 1, 1);
			commandList.SynchronizeUnorderedAccess(resources.Particles);
		}

		if (frame.SpawnedParticleCount > 0)
		{
			commandList.BindPipeline(_emitPipeline!);
			commandList.SetComputeBuffer(0, resources.Particles);
			commandList.SetComputeReadOnlyBuffer(2, resources.Emitters[slot]!);
			commandList.SetComputeReadOnlyBuffer(3, resources.Spawns[slot]!);
			commandList.SetComputeConstants(writer.RegisterIndex, writer.AsBytes());
			var (groups, _, _) = _emitGroupSize.GetDispatchGroupCount((uint)frame.SpawnedParticleCount);
			commandList.Dispatch(groups, 1, 1);
			commandList.SynchronizeUnorderedAccess(resources.Particles);
		}
	}

	/// <summary>Draws a view's effects over the lit scene.</summary>
	internal void Draw(
		RenderGraphContext context,
		IGfxDevice device,
		VfxWorldFrame frame,
		IGfxTexture lightingTarget,
		IGfxTexture depthTexture,
		Int2 framebufferSize)
	{
		ArgumentNullException.ThrowIfNull(context);
		var commandList = context.CommandList;
		var targets = new PassTargets(new[]
		{
			new ColorTargetBinding(lightingTarget)
		});
		var viewport = new Viewport(0.0f, 0.0f, framebufferSize.X, framebufferSize.Y);
		commandList.BeginPass(targets, viewport);
		commandList.SetScissorRect(new RectInt(0, 0, framebufferSize.X, framebufferSize.Y));
		try
		{
			if (HasWork(frame) == false ||
			    frame.Draws.Count == 0 ||
			    _worlds.TryGetValue(frame.World!, out var resources) == false ||
			    resources.SimulatedSequence != frame.Sequence)
			{
				return;
			}

			EnsureGraphicsPipelines(device);
			EnsureIndexBuffers(device);
			var sceneData = context.SceneData;
			var depthHandle = _bindlessRegistry.RegisterDepthTexture(depthTexture).Value;
			WriteViewParams(_spriteViewWriter!, sceneData, depthHandle);
			WriteViewParams(_ribbonViewWriter!, sceneData, depthHandle);
			SortDraws(frame, sceneData.CameraOrigin);

			var slot = resources.FrameSlot;
			IGfxPipeline? boundPipeline = null;
			for (var i = 0; i < _sortedDraws.Count; i++)
			{
				var draw = _sortedDraws[i];
				var ribbon = draw.Kind == VfxEmitterKind.Ribbon;
				var additive = draw.Blend == VfxBlendMode.Additive;
				var pipeline = ribbon
					? additive ? _ribbonAdditivePipeline! : _ribbonAlphaPipeline!
					: additive ? _spriteAdditivePipeline! : _spriteAlphaPipeline!;
				var viewWriter = ribbon ? _ribbonViewWriter! : _spriteViewWriter!;
				var drawWriter = ribbon ? _ribbonDrawWriter! : _spriteDrawWriter!;

				if (ReferenceEquals(pipeline, boundPipeline) == false)
				{
					// Bindings belong to the pipeline's layout, so they are restored after every switch.
					commandList.BindPipeline(pipeline);
					commandList.SetPrimitiveTopology(ribbon ? PrimitiveTopology.TriangleStrip : PrimitiveTopology.TriangleList);
					if (ribbon)
					{
						commandList.SetIndexBuffer(new IndexBufferView(_stripIndexBuffer!, IndexFormat.UInt32, 0));
						commandList.BindConstantBuffer(_ribbonEmittersRegister, resources.Emitters[slot]!);
						commandList.BindConstantBuffer(_ribbonPointsRegister, resources.RibbonPoints[slot]!);
					}
					else
					{
						commandList.SetIndexBuffer(new IndexBufferView(_quadIndexBuffer!, IndexFormat.UInt16, 0));
						commandList.BindConstantBuffer(_spriteParticlesRegister, resources.Particles);
						commandList.BindConstantBuffer(_spriteEmittersRegister, resources.Emitters[slot]!);
					}

					commandList.SetGraphicsConstants(viewWriter.RegisterIndex, viewWriter.AsBytes());
					boundPipeline = pipeline;
				}

				drawWriter.Clear();
				drawWriter.SetUInt("emitterSlot", draw.EmitterSlot);
				drawWriter.SetUInt("first", draw.First);
				drawWriter.SetUInt("capacity", draw.Capacity);
				drawWriter.SetUInt("start", draw.Start);
				drawWriter.SetUInt("additive", additive ? 1u : 0u);
				commandList.SetGraphicsConstants(drawWriter.RegisterIndex, drawWriter.AsBytes());

				var arguments = ribbon
					? new DrawArguments(draw.Count * 2u)
					: new DrawArguments((uint)QuadIndices.Length, draw.Count);
				commandList.Draw(in arguments);
			}
		}
		finally
		{
			commandList.EndPass();
		}
	}

	private void SortDraws(VfxWorldFrame frame, Vector3 cameraOrigin)
	{
		_sortedDraws.Clear();
		_sortedDraws.AddRange(frame.Draws);
		_sortedDraws.Sort((a, b) =>
		{
			var distanceA = Vector3.DistanceSquared(a.SortPosition, cameraOrigin);
			var distanceB = Vector3.DistanceSquared(b.SortPosition, cameraOrigin);

			// Farthest first. Ties keep a stable order by slot so equal emitters never swap between frames.
			var byDistance = distanceB.CompareTo(distanceA);
			return byDistance != 0 ? byDistance : a.EmitterSlot.CompareTo(b.EmitterSlot);
		});
	}

	private static void WriteViewParams(ShaderPropertyWriter writer, SceneDrawData sceneData, uint depthHandle)
	{
		var view = sceneData.ViewMatrix;
		writer.Clear();
		writer.SetMatrix4x4("viewProjection", sceneData.ViewProjection);
		writer.SetMatrix4x4("invProjection", sceneData.InverseProjection);
		writer.SetVector4("cameraOrigin", new Vector4(sceneData.CameraOrigin, 0.0f));
		writer.SetVector4("cameraRight", Vector4.Normalize(new Vector4(view.M11, view.M21, view.M31, 0.0f)));
		writer.SetVector4("cameraUp", Vector4.Normalize(new Vector4(view.M12, view.M22, view.M32, 0.0f)));
		writer.SetVector4("cameraForward", Vector4.Normalize(new Vector4(view.M13, view.M23, view.M33, 0.0f)));
		writer.SetUInt("depthHandle", depthHandle);
	}

	private void Upload(IGfxDevice device, WorldResources resources, VfxWorldFrame frame)
	{
		var slot = resources.FrameSlot;
		_bindlessRegistry.EnsureInitialized(device);
		if (_sampler.IsValid == false)
		{
			_sampler = _bindlessRegistry.GetSamplerHandle(
				new SamplerDescriptor(FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp));
		}

		for (var i = 0; i < frame.EmitterCount; i++)
		{
			ref var emitter = ref frame.Emitters[i];
			emitter.TextureHandle = frame.EmitterTextures[i]?.Resources is ITextureResources textureResources
				? _bindlessRegistry.GetTextureHandle(textureResources).Value
				: NoTexture;
			emitter.SamplerHandle = _sampler.Value;
		}

		resources.Emitters[slot] = EnsureUploadCapacity<GpuVfxEmitter>(device, resources.Emitters[slot], frame.EmitterCount, "VfxEmitters");
		resources.Spawns[slot] = EnsureUploadCapacity<GpuVfxSpawnBatch>(device, resources.Spawns[slot], frame.Spawns.Count, "VfxSpawns");
		resources.RibbonPoints[slot] = EnsureUploadCapacity<GpuVfxRibbonPoint>(device, resources.RibbonPoints[slot], frame.RibbonPoints.Count, "VfxRibbonPoints");

		Write<GpuVfxEmitter>(resources.Emitters[slot]!, frame.Emitters.AsSpan(0, frame.EmitterCount));
		Write<GpuVfxSpawnBatch>(resources.Spawns[slot]!, CollectionsMarshal.AsSpan(frame.Spawns));
		Write<GpuVfxRibbonPoint>(resources.RibbonPoints[slot]!, CollectionsMarshal.AsSpan(frame.RibbonPoints));
	}

	private static void Write<T>(IGfxBuffer buffer, ReadOnlySpan<T> data) where T : unmanaged
	{
		if (data.IsEmpty) return;

		((IWritableGpuBuffer)buffer).Write(data);
	}

	/// <summary>
	/// Per-frame data goes into CPU-visible buffers, one per frame in flight. A default-heap buffer would make every
	/// D3D12 write wait for the GPU, which is exactly the per-frame CPU cost this system is meant to avoid.
	/// </summary>
	private static IGfxBuffer EnsureUploadCapacity<T>(IGfxDevice device, IGfxBuffer? existing, int count, string name)
		where T : unmanaged
	{
		var stride = (ulong)Marshal.SizeOf<T>();
		var required = (ulong)Math.Max(count, 1) * stride;
		if (existing is not null && existing.Descriptor.SizeInBytes >= required)
		{
			return existing;
		}

		var capacity = (ulong)MinimumUploadElements;
		while (capacity * stride < required)
		{
			capacity *= 2;
		}

		if (existing is IDisposable disposable)
		{
			device.Retire(disposable, name);
		}

		return device.CreateBuffer(new BufferDescriptor(
			capacity * stride,
			BufferUsage.Constant | BufferUsage.Structured,
			BufferFlags.AllowShaderResource,
			name));
	}

	private WorldResources GetWorldResources(IGfxDevice device, World world)
	{
		if (_worlds.TryGetValue(world, out var resources))
		{
			return resources;
		}

		var particles = device.CreateBuffer(new BufferDescriptor(
			(ulong)VfxLimits.ParticlePoolCapacity * (ulong)Marshal.SizeOf<GpuVfxParticle>(),
			BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource,
			"VfxParticles"));

		// Zero age and zero lifetime is a dead particle, so a zeroed pool starts empty.
		((IWritableGpuBuffer)particles).Write<GpuVfxParticle>(new GpuVfxParticle[VfxLimits.ParticlePoolCapacity]);

		resources = new WorldResources(particles);
		_worlds.Add(world, resources);

		return resources;
	}

	private void ReleaseUnusedWorlds(IGfxDevice device)
	{
		var now = Stopwatch.GetTimestamp();
		_releaseScratch.Clear();
		foreach (var (world, resources) in _worlds)
		{
			if (now - resources.LastUsed > UnusedWorldTimeout)
			{
				_releaseScratch.Add(world);
			}
		}

		for (var i = 0; i < _releaseScratch.Count; i++)
		{
			var world = _releaseScratch[i];
			_worlds[world].Release(device);
			_worlds.Remove(world);
		}
	}

	private void EnsureComputePipelines(IGfxDevice device)
	{
		if (_simulatePipeline is not null && _emitPipeline is not null)
		{
			return;
		}

		var simulate = _shaderProvider.GetComputeShaderWithReflection(EngineShaderPrograms.VfxSimulate, "VfxSimulateCS", device.BackendKind);
		var emit = _shaderProvider.GetComputeShaderWithReflection(EngineShaderPrograms.VfxSimulate, "VfxEmitCS", device.BackendKind);
		_simulateGroupSize = simulate.ThreadGroupSize;
		_emitGroupSize = emit.ThreadGroupSize;
		_simulateWriter = new ShaderPropertyWriter(simulate.ReflectionLayout.GetConstantBuffer("VfxSimulateParams"));
		_simulatePipeline = device.GetOrCreatePipeline(
			new PipelineKey(PassKind.Compute, null, null, "VfxSimulateCS", default, default, default, shaderVariant: "vfx_simulate.compute.slang"),
			new ShaderBytecodeSet(compute: simulate.Bytecode, computeThreadGroupSize: simulate.ThreadGroupSize));
		_emitPipeline = device.GetOrCreatePipeline(
			new PipelineKey(PassKind.Compute, null, null, "VfxEmitCS", default, default, default, shaderVariant: "vfx_simulate.compute.slang"),
			new ShaderBytecodeSet(compute: emit.Bytecode, computeThreadGroupSize: emit.ThreadGroupSize));
	}

	private void EnsureGraphicsPipelines(IGfxDevice device)
	{
		if (_spriteAlphaPipeline is null || _spriteAdditivePipeline is null)
		{
			var sprite = GraphicsShaderCompiler.CompileWithReflection(
				_shaderProvider,
				device.BackendKind,
				EngineShaderPrograms.VfxSprite,
				"vertexShader",
				"fragmentShader");
			_spriteAlphaPipeline = CreateGraphicsPipeline(device, sprite, BlendMode.AlphaBlend, "VfxSprite");
			_spriteAdditivePipeline = CreateGraphicsPipeline(device, sprite, BlendMode.Additive, "VfxSprite");
			var reflection = sprite.ReflectionLayout;
			_spriteViewWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("VfxViewParams"));
			_spriteDrawWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("VfxDrawParams"));
			_spriteParticlesRegister = GetRegister(reflection, "g_Particles");
			_spriteEmittersRegister = GetRegister(reflection, "g_Emitters");
		}

		if (_ribbonAlphaPipeline is null || _ribbonAdditivePipeline is null)
		{
			var ribbon = GraphicsShaderCompiler.CompileWithReflection(
				_shaderProvider,
				device.BackendKind,
				EngineShaderPrograms.VfxRibbon,
				"vertexShader",
				"fragmentShader");
			_ribbonAlphaPipeline = CreateGraphicsPipeline(device, ribbon, BlendMode.AlphaBlend, "VfxRibbon");
			_ribbonAdditivePipeline = CreateGraphicsPipeline(device, ribbon, BlendMode.Additive, "VfxRibbon");
			var reflection = ribbon.ReflectionLayout;
			_ribbonViewWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("VfxViewParams"));
			_ribbonDrawWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("VfxDrawParams"));
			_ribbonEmittersRegister = GetRegister(reflection, "g_Emitters");
			_ribbonPointsRegister = GetRegister(reflection, "g_RibbonPoints");
		}
	}

	private static IGfxPipeline CreateGraphicsPipeline(
		IGfxDevice device,
		CompiledGraphicsShaderWithReflection compiled,
		BlendMode blend,
		string variant)
	{
		var renderState = new RenderStateDescriptor(
			FillMode.Solid,
			CullMode.None,
			depthTestEnabled: false,
			depthWriteEnabled: false,
			blend);
		var key = new PipelineKey(
			PassKind.Graphics,
			vertexEntryPoint: "vertexShader",
			pixelEntryPoint: "fragmentShader",
			computeEntryPoint: null,
			renderTargets: new(new[]
			{
				TextureFormat.Rgba16Float
			}),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: renderState,
			shaderVariant: $"{variant}:{blend}");

		return device.GetOrCreatePipeline(key, compiled.Bytecode);
	}

	private static uint GetRegister(ShaderReflectionLayout reflection, string name)
	{
		if (reflection.TryGetResource(name, out var resource) == false)
		{
			throw new InvalidOperationException($"VFX shader reflection does not expose '{name}'.");
		}

		return resource.RegisterIndex;
	}

	private void EnsureIndexBuffers(IGfxDevice device)
	{
		if (_quadIndexBuffer is null)
		{
			var quad = device.CreateBuffer(new BufferDescriptor(
				(ulong)(QuadIndices.Length * sizeof(ushort)),
				BufferUsage.Index,
				BufferFlags.None,
				"VfxQuadIndices"));
			((IWritableGpuBuffer)quad).Write<ushort>(QuadIndices);
			_quadIndexBuffer = quad;
		}

		if (_stripIndexBuffer is null)
		{
			// Ribbons index their strip vertices directly, so one ascending list serves every ribbon.
			var indices = new uint[VfxLimits.MaxRibbonPoints * 2];
			for (var i = 0; i < indices.Length; i++)
			{
				indices[i] = (uint)i;
			}

			var strip = device.CreateBuffer(new BufferDescriptor(
				(ulong)(indices.Length * sizeof(uint)),
				BufferUsage.Index,
				BufferFlags.None,
				"VfxStripIndices"));
			((IWritableGpuBuffer)strip).Write<uint>(indices);
			_stripIndexBuffer = strip;
		}
	}

	private sealed class WorldResources
	{
		public WorldResources(IGfxBuffer particles)
		{
			Particles = particles;
		}

		public IGfxBuffer Particles { get; }
		public IGfxBuffer?[] Emitters { get; } = new IGfxBuffer?[GpuDrawResources.MaxFramesInFlight];
		public IGfxBuffer?[] Spawns { get; } = new IGfxBuffer?[GpuDrawResources.MaxFramesInFlight];
		public IGfxBuffer?[] RibbonPoints { get; } = new IGfxBuffer?[GpuDrawResources.MaxFramesInFlight];
		public long SimulatedSequence { get; set; } = -1;
		public int FrameSlot { get; set; }
		public long LastUsed { get; set; } = Stopwatch.GetTimestamp();

		public void Release(IGfxDevice device)
		{
			Retire(device, Particles, "VfxParticles");
			for (var i = 0; i < Emitters.Length; i++)
			{
				Retire(device, Emitters[i], "VfxEmitters");
				Retire(device, Spawns[i], "VfxSpawns");
				Retire(device, RibbonPoints[i], "VfxRibbonPoints");
			}
		}

		private static void Retire(IGfxDevice device, IGfxBuffer? buffer, string name)
		{
			if (buffer is IDisposable disposable)
			{
				device.Retire(disposable, name);
			}
		}
	}
}
