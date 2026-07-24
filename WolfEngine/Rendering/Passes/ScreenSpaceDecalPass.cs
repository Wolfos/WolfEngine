#nullable enable

using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

public sealed class ScreenSpaceDecalPass
{
	private readonly IRenderer _renderer;
	private readonly IShaderProvider _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly DebugPrimitiveMeshFactory _meshFactory = new();
	private readonly List<GpuDecalProjectorData> _packedProjectors = new();
	private IGfxPipeline? _pipeline;
	private GraphicsBackendKind? _compiledBackendKind;
	private ShaderPropertyWriter? _bindlessWriter;
	private ShaderPropertyWriter? _cameraWriter;
	private ShaderPropertyWriter? _drawWriter;
	private uint _decalProjectorBufferRegisterIndex = 20;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;
	private Mesh? _projectorMesh;
	private IGfxBuffer? _projectorVertexBuffer;
	private IGfxBuffer? _projectorIndexBuffer;
	private uint _projectorIndexCount;
	private GraphicsBackendKind? _projectorGeometryBackendKind;

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	private struct VertexData
	{
		public Vector3 Position;
		public Vector3 Normal;
		public Vector2 UV;
		public Vector4 Tangent;
	}

	public ScreenSpaceDecalPass(
		IRenderer renderer,
		IShaderProvider shaderCompiler,
		BindlessResourceRegistry bindlessRegistry)
	{
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public ScreenSpaceDecalPassConfig BuildConfig(
		RenderGraphContext context,
		RenderGraphFrameResources resources,
		IGfxDevice device,
		GpuDrawResources gpuDrawResources,
		SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);
		ArgumentNullException.ThrowIfNull(gpuDrawResources);
		ArgumentNullException.ThrowIfNull(sceneData);

		EnsurePipeline(device);
		_bindlessRegistry.EnsureInitialized(device);

		if (_linearSampler.IsValid == false)
		{
			var sampler = new SamplerDescriptor(FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp);
			_linearSampler = _bindlessRegistry.GetSamplerHandle(sampler);
		}

		EnsureProjectorGeometry(device);

		var maxProjectorCount = Math.Max(resources.Config.Decals.MaxProjectorCount, 1);
		var decalCount = Math.Min(sceneData.Decals.Count, maxProjectorCount);
		gpuDrawResources.EnsureDecalCapacity(device, maxProjectorCount);
		UploadDecals(sceneData, gpuDrawResources.DecalProjectorBuffer, decalCount);

		return new ScreenSpaceDecalPassConfig
		{
			FramebufferWidth = resources.SceneFramebufferSize.X,
			FramebufferHeight = resources.SceneFramebufferSize.Y,
			SourceAlbedo = context.GetTexture(resources.DecalSourceGBufferAlbedo),
			SourceNormal = context.GetTexture(resources.DecalSourceGBufferNormal),
			SourceMaterial = context.GetTexture(resources.DecalSourceGBufferMaterial),
			SourceEmissive = context.GetTexture(resources.DecalSourceGBufferEmissive),
			DepthTexture = context.GetTexture(resources.GBufferDepth),
			TargetAlbedo = context.GetTexture(resources.GBufferAlbedo),
			TargetNormal = context.GetTexture(resources.GBufferNormal),
			TargetMaterial = context.GetTexture(resources.GBufferMaterial),
			TargetEmissive = context.GetTexture(resources.GBufferEmissive),
			Pipeline = _pipeline ?? throw new InvalidOperationException("Screen-space decal pipeline was not initialized."),
			DecalProjectorBuffer = gpuDrawResources.DecalProjectorBuffer,
			DecalProjectorCount = (uint)decalCount
		};
	}

	public void Record(RenderGraphContext context, in ScreenSpaceDecalPassConfig config, SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sceneData);

		var commandList = context.CommandList;
		var targets = new PassTargets(
			new[]
			{
				new ColorTargetBinding(config.TargetAlbedo),
				new ColorTargetBinding(config.TargetNormal),
				new ColorTargetBinding(config.TargetMaterial),
				new ColorTargetBinding(config.TargetEmissive)
			},
			new DepthTargetBinding(config.DepthTexture, readOnlyDepth: true));
		var viewport = new Viewport(0.0f, 0.0f, config.FramebufferWidth, config.FramebufferHeight);
		commandList.BeginPass(targets, viewport);
		commandList.SetScissorRect(new RectInt(0, 0, config.FramebufferWidth, config.FramebufferHeight));

		if (config.DecalProjectorBuffer is null || config.DecalProjectorCount == 0)
		{
			commandList.EndPass();
			return;
		}

		if (_projectorVertexBuffer is null ||
		    _projectorIndexBuffer is null ||
		    _projectorIndexCount == 0)
		{
			commandList.EndPass();
			return;
		}

		commandList.BindPipeline(config.Pipeline);
		commandList.SetPrimitiveTopology(PrimitiveTopology.TriangleList);
		commandList.SetVertexBuffer(new VertexBufferView(_projectorVertexBuffer, (uint)Marshal.SizeOf<VertexData>(), 0));
		commandList.SetIndexBuffer(new IndexBufferView(_projectorIndexBuffer, IndexFormat.UInt32, 0));
		commandList.BindConstantBuffer(_decalProjectorBufferRegisterIndex, config.DecalProjectorBuffer);

		var bindlessWriter = _bindlessWriter
			?? throw new InvalidOperationException("Screen-space decal bindless writer was not initialized.");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("sourceAlbedoHandle", _bindlessRegistry.RegisterTexture(config.SourceAlbedo).Value);
		bindlessWriter.SetUInt("sourceNormalHandle", _bindlessRegistry.RegisterTexture(config.SourceNormal).Value);
		bindlessWriter.SetUInt("sourceMaterialHandle", _bindlessRegistry.RegisterTexture(config.SourceMaterial).Value);
		bindlessWriter.SetUInt("sourceEmissiveHandle", _bindlessRegistry.RegisterTexture(config.SourceEmissive).Value);
		bindlessWriter.SetUInt("depthHandle", _bindlessRegistry.RegisterDepthTexture(config.DepthTexture).Value);
		commandList.SetGraphicsConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var cameraWriter = _cameraWriter
			?? throw new InvalidOperationException("Screen-space decal camera writer was not initialized.");
		cameraWriter.Clear();
		cameraWriter.SetMatrix4x4("viewProjection", sceneData.ViewProjection);
		cameraWriter.SetMatrix4x4("invViewProjection", sceneData.InverseViewProjection);
		cameraWriter.SetUInt("frameSizeX", (uint)Math.Max(config.FramebufferWidth, 1));
		cameraWriter.SetUInt("frameSizeY", (uint)Math.Max(config.FramebufferHeight, 1));
		commandList.SetGraphicsConstants(cameraWriter.RegisterIndex, cameraWriter.AsBytes());

		var drawWriter = _drawWriter
			?? throw new InvalidOperationException("Screen-space decal draw writer was not initialized.");
		for (var decalIndex = 0u; decalIndex < config.DecalProjectorCount; decalIndex++)
		{
			drawWriter.Clear();
			drawWriter.SetUInt("decalIndex", decalIndex);
			commandList.SetGraphicsConstants(drawWriter.RegisterIndex, drawWriter.AsBytes());
			commandList.Draw(new DrawArguments(_projectorIndexCount, 1, 0, 0, 0));
		}

		commandList.EndPass();
	}

	private void UploadDecals(SceneDrawData sceneData, IGfxBuffer? buffer, int decalCount)
	{
		if (buffer is not IWritableGpuBuffer writableBuffer)
		{
			return;
		}

		_packedProjectors.Clear();
		for (var i = 0; i < decalCount; i++)
		{
			var packet = sceneData.Decals[i];
			var handles = ResolveHandles(packet.Projector);
			_packedProjectors.Add(DecalProjectorGpuPacker.CreateGpuData(packet, sceneData.CameraOrigin, handles));
		}

		if (_packedProjectors.Count > 0)
		{
			writableBuffer.Write<GpuDecalProjectorData>(_packedProjectors.ToArray());
		}
	}

	private DecalProjectorResolvedHandles ResolveHandles(in DecalProjector projector)
	{
		return new DecalProjectorResolvedHandles(
			ResolveTextureHandle(projector.AlbedoTexture),
			ResolveTextureHandle(projector.NormalTexture),
			ResolveTextureHandle(projector.MaterialTexture),
			ResolveTextureHandle(projector.EmissiveTexture),
			_linearSampler);
	}

	private DescriptorHandle ResolveTextureHandle(Texture? texture)
	{
		if (texture?.Resources is ITextureResources resources)
		{
			return _bindlessRegistry.GetTextureHandle(resources);
		}

		return _bindlessRegistry.ErrorTextureHandle;
	}

	private Mesh EnsureProjectorMesh()
	{
		_projectorMesh ??= _meshFactory.GetMesh(DebugPrimitiveType.Box);
		return _projectorMesh;
	}

	private void EnsureProjectorGeometry(IGfxDevice device)
	{
		if (_projectorVertexBuffer is not null &&
		    _projectorIndexBuffer is not null &&
		    _projectorIndexCount > 0 &&
		    _projectorGeometryBackendKind == device.BackendKind)
		{
			return;
		}

		var mesh = EnsureProjectorMesh();
		var vertexData = new VertexData[mesh.Vertices.Length];
		for (var i = 0; i < mesh.Vertices.Length; i++)
		{
			vertexData[i] = new VertexData
			{
				Position = new Vector3(mesh.Vertices[i].X, mesh.Vertices[i].Y, mesh.Vertices[i].Z),
				Normal = mesh.Normals[i],
				UV = mesh.UVs[i],
				Tangent = mesh.Tangents[i]
			};
		}

		var vertexBuffer = device.CreateBuffer(new BufferDescriptor(
			(ulong)(vertexData.Length * Marshal.SizeOf<VertexData>()),
			BufferUsage.Vertex));
		var indexBuffer = device.CreateBuffer(new BufferDescriptor(
			(ulong)(mesh.Indices.Length * sizeof(uint)),
			BufferUsage.Index));
		if (vertexBuffer is not IWritableGpuBuffer writableVertexBuffer ||
		    indexBuffer is not IWritableGpuBuffer writableIndexBuffer)
		{
			throw new InvalidOperationException("Screen-space decal projector buffers were not writable.");
		}

		writableVertexBuffer.Write<VertexData>(vertexData);
		writableIndexBuffer.Write<uint>(mesh.Indices);
		_projectorVertexBuffer = vertexBuffer;
		_projectorIndexBuffer = indexBuffer;
		_projectorIndexCount = (uint)mesh.Indices.Length;
		_projectorGeometryBackendKind = device.BackendKind;
	}

	private void EnsurePipeline(IGfxDevice device)
	{
		if (_pipeline is not null)
		{
			if (_compiledBackendKind.HasValue && _compiledBackendKind.Value != device.BackendKind)
			{
				throw new InvalidOperationException(
					$"ScreenSpaceDecalPass is already compiled for backend '{_compiledBackendKind.Value}', " +
					$"but was requested for '{device.BackendKind}'.");
			}

			return;
		}

		var compiled = GraphicsShaderCompiler.CompileWithReflection(
			_shaderCompiler,
			device.BackendKind,
			EngineShaderPrograms.ScreenSpaceDecal,
			"vertexShader",
			"fragmentShader");
		var renderState = new RenderStateDescriptor(
			FillMode.Solid,
			CullMode.Front,
			depthTestEnabled: false,
			depthWriteEnabled: false,
			BlendMode.Opaque);
		var key = new PipelineKey(
			PassKind.Graphics,
			vertexEntryPoint: "vertexShader",
			pixelEntryPoint: "fragmentShader",
			computeEntryPoint: null,
			renderTargets: new(new[]
			{
				TextureFormat.Bgra8Unorm,
				TextureFormat.Rgba16Float,
				TextureFormat.Rgba8Unorm,
				TextureFormat.Rgba16Float
			}),
			depthStencil: new DepthStencilFormat(TextureFormat.D32Float, readOnlyDepth: true),
			renderState: renderState,
			shaderVariant: "ScreenSpaceDecal");
		_pipeline = device.GetOrCreatePipeline(key, compiled.Bytecode);

		var reflection = compiled.ReflectionLayout;
		_bindlessWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("BindlessHandles"));
		_cameraWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("CameraParams"));
		_drawWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("DrawParams"));
		if (reflection.TryGetResource("g_Decals", out var decalResource))
		{
			_decalProjectorBufferRegisterIndex = decalResource.RegisterIndex;
		}

		_compiledBackendKind = device.BackendKind;
	}
}
