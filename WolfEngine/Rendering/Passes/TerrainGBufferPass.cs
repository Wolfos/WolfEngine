using System;
using System.Collections.Generic;
using System.Numerics;
using WolfEngine.Profiling;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public sealed class TerrainGBufferPass
{
	private readonly IShaderCompiler _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly IRenderer _renderer;
	private IGfxPipeline? _pipeline;
	private GraphicsBackendKind? _reflectionBackendKind;
	private ShaderPropertyWriter? _cameraWriter;
	private ShaderPropertyWriter? _drawWriter;
	private DescriptorHandle _layerSampler = DescriptorHandle.Invalid;
	private DescriptorHandle _controlSampler = DescriptorHandle.Invalid;

	public TerrainGBufferPass(IShaderCompiler shaderCompiler, BindlessResourceRegistry bindlessRegistry, IRenderer renderer)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
	}

	public TerrainGBufferPassConfig BuildConfig(
		RenderGraphContext context,
		RenderGraphFrameResources resources,
		IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);
		_bindlessRegistry.EnsureInitialized(device);
		EnsurePipeline(device);
		EnsureReflectionWriters(device.BackendKind);
		if (_layerSampler.IsValid == false)
		{
			_layerSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
				FilterMode.Anisotropic,
				AddressMode.Wrap,
				AddressMode.Wrap,
				AddressMode.Wrap,
				maxAnisotropy: 8.0f));
		}

		if (_controlSampler.IsValid == false)
		{
			_controlSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
				FilterMode.Bilinear,
				AddressMode.Clamp,
				AddressMode.Clamp,
				AddressMode.Clamp));
		}

		return new TerrainGBufferPassConfig
		{
			FramebufferWidth = resources.SceneFramebufferSize.X,
			FramebufferHeight = resources.SceneFramebufferSize.Y,
			AlbedoTarget = context.GetTexture(resources.GBufferAlbedo),
			NormalTarget = context.GetTexture(resources.GBufferNormal),
			MaterialTarget = context.GetTexture(resources.GBufferMaterial),
			EmissiveTarget = context.GetTexture(resources.GBufferEmissive),
			VelocityTarget = context.GetTexture(resources.GBufferVelocity),
			DepthTarget = context.GetTexture(resources.GBufferDepth),
			Pipeline = _pipeline ?? throw new InvalidOperationException("Terrain pipeline was not initialized."),
			Records = context.FrameSnapshot.TerrainRecords,
			LayerSampler = _layerSampler,
			ControlSampler = _controlSampler
		};
	}

	public void Record(RenderGraphContext context, in TerrainGBufferPassConfig config, SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sceneData);
		var records = config.Records;
		if (records is null || records.Count == 0)
		{
			return;
		}

		var commandList = context.CommandList;
		var targets = new PassTargets(
			new[]
			{
				new ColorTargetBinding(config.AlbedoTarget),
				new ColorTargetBinding(config.NormalTarget),
				new ColorTargetBinding(config.MaterialTarget),
				new ColorTargetBinding(config.EmissiveTarget),
				new ColorTargetBinding(config.VelocityTarget)
			},
			new DepthTargetBinding(config.DepthTarget, readOnlyDepth: false));
		var viewport = new Viewport(0.0f, 0.0f, config.FramebufferWidth, config.FramebufferHeight);
		commandList.BeginPass(targets, viewport);
		commandList.SetScissorRect(new RectInt(0, 0, config.FramebufferWidth, config.FramebufferHeight));
		commandList.SetPrimitiveTopology(PrimitiveTopology.TriangleList);

		var cameraWriter = _cameraWriter ?? throw new InvalidOperationException("Terrain camera writer was not initialized.");
		cameraWriter.Clear();
		cameraWriter.SetMatrix4x4("viewProjection", sceneData.ViewProjection);
		cameraWriter.SetVector3("cameraPosition", sceneData.CameraOrigin);
		cameraWriter.SetVector3("previousCameraPosition", sceneData.PreviousCameraOrigin);
		cameraWriter.SetFloat("currentJitterPixelsX", sceneData.JitterPixels.X);
		cameraWriter.SetFloat("currentJitterPixelsY", sceneData.JitterPixels.Y);
		cameraWriter.SetMatrix4x4("unjitteredViewProjection", sceneData.UnjitteredViewProjection);
		cameraWriter.SetMatrix4x4("previousViewProjection", sceneData.PreviousViewProjection);
		cameraWriter.SetVector2("currentJitterNdc", sceneData.JitterNdc);
		cameraWriter.SetUInt("frameSizeX", (uint)Math.Max(config.FramebufferWidth, 1));
		cameraWriter.SetUInt("frameSizeY", (uint)Math.Max(config.FramebufferHeight, 1));
		cameraWriter.SetMatrix4x4("viewMatrix", sceneData.ViewMatrix);
		cameraWriter.SetFloat("nearPlane", sceneData.NearPlane);
		cameraWriter.SetFloat("farPlane", sceneData.FarPlane);
		commandList.BindPipeline(config.Pipeline);
		commandList.SetGraphicsConstants(cameraWriter.RegisterIndex, cameraWriter.AsBytes());

		var drawWriter = _drawWriter ?? throw new InvalidOperationException("Terrain draw writer was not initialized.");
		for (var i = 0; i < records.Count; i++)
		{
			var record = records[i];
			if (record.Mesh.VertexBuffer is null || record.Mesh.IndexBuffer is null || record.Mesh.IndexCount == 0)
			{
				continue;
			}

			using (FrameProfiler.Instance.Measure("Terrain.Draw"))
			{
				drawWriter.Clear();
				drawWriter.SetMatrix4x4("world", record.WorldTransform);
				drawWriter.SetUInt("controlMapHandle", ResolveTextureHandle(record.ControlMap).Value);
				drawWriter.SetUInt("hasControlMap", record.ControlMap is null ? 0u : 1u);
				drawWriter.SetUInt("layerSamplerHandle", config.LayerSampler.Value);
				drawWriter.SetUInt("controlSamplerHandle", config.ControlSampler.Value);
				drawWriter.SetUInt("layerCount", (uint)Math.Clamp(record.LayerCount, 1, 4));
				drawWriter.SetFloat("heightBlendSharpness", record.HeightBlendSharpness);
				WriteLayer(drawWriter, 0, record.Layer0);
				WriteLayer(drawWriter, 1, record.Layer1);
				WriteLayer(drawWriter, 2, record.Layer2);
				WriteLayer(drawWriter, 3, record.Layer3);
				commandList.SetGraphicsConstants(drawWriter.RegisterIndex, drawWriter.AsBytes());
				commandList.SetVertexBuffers(new[] { new VertexBufferView(record.Mesh.VertexBuffer, record.Mesh.StrideInBytes, checked((uint)record.Mesh.PackedVertexOffsetBytes)) });
				commandList.SetIndexBuffer(new IndexBufferView(record.Mesh.IndexBuffer, IndexFormat.UInt32, checked((uint)record.Mesh.PackedIndexOffsetBytes)));
				commandList.Draw(new DrawArguments(record.Mesh.IndexCount, 1, 0, 0, 0));
			}
		}

		commandList.EndPass();
	}

	private void EnsurePipeline(IGfxDevice device)
	{
		if (_pipeline is not null)
		{
			return;
		}

		var renderState = new RenderStateDescriptor(
			FillMode.Solid,
			CullMode.None,
			depthTestEnabled: true,
			depthWriteEnabled: true,
			BlendMode.Opaque);
		var shaderSet = GraphicsShaderCompiler.Compile(
			_shaderCompiler,
			device.BackendKind,
			"terrain_gbuffer.slang",
			"vertexShader",
			"fragmentShader");
		var pipelineKey = new PipelineKey(
			PassKind.Graphics,
			"vertexShader",
			"fragmentShader",
			null,
			new RenderTargetFormats(new[]
			{
				TextureFormat.Bgra8Unorm,
				TextureFormat.Rgba16Float,
				TextureFormat.Rgba8Unorm,
				TextureFormat.Rgba8Unorm,
				TextureFormat.Rgba16Float
			}),
			new DepthStencilFormat(TextureFormat.D32Float),
			renderState,
			GraphicsLayoutKind.Material,
			"TerrainGBuffer");
		_pipeline = device.GetOrCreatePipeline(pipelineKey, shaderSet);
	}

	private void EnsureReflectionWriters(GraphicsBackendKind backendKind)
	{
		if (_reflectionBackendKind == backendKind &&
		    _cameraWriter is not null &&
		    _drawWriter is not null)
		{
			return;
		}

		var compiled = GraphicsShaderCompiler.CompileWithReflection(
			_shaderCompiler,
			backendKind,
			"terrain_gbuffer.slang",
			"vertexShader",
			"fragmentShader");
		_cameraWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("CameraParams"));
		_drawWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("TerrainDrawParams"));
		_reflectionBackendKind = backendKind;
	}

	private DescriptorHandle ResolveTextureHandle(Texture? texture)
	{
		return texture?.Resources is null
			? _bindlessRegistry.ErrorTextureHandle
			: _bindlessRegistry.GetTextureHandle(texture.Resources);
	}

	private void WriteLayer(ShaderPropertyWriter writer, int index, in TerrainResolvedLayer layer)
	{
		writer.SetUInt($"layer{index}AlbedoHandle", ResolveTextureHandle(layer.Albedo).Value);
		writer.SetUInt($"layer{index}NormalHandle", ResolveTextureHandle(layer.Normal).Value);
		writer.SetUInt($"layer{index}MetallicRoughnessHandle", ResolveTextureHandle(layer.MetallicRoughness).Value);
		writer.SetUInt($"layer{index}OcclusionHandle", ResolveTextureHandle(layer.Occlusion).Value);
		var hasHeight = layer.Height is not null;
		writer.SetUInt($"layer{index}HeightHandle", ResolveTextureHandle(layer.Height).Value);
		writer.SetUInt($"layer{index}HasHeight", hasHeight ? 1u : 0u);
		writer.SetFloat($"layer{index}Scale", Math.Max(layer.Scale, 0.001f));
	}
}

public sealed class TerrainGBufferPassConfig
{
	public required int FramebufferWidth { get; init; }
	public required int FramebufferHeight { get; init; }
	public required IGfxTexture AlbedoTarget { get; init; }
	public required IGfxTexture NormalTarget { get; init; }
	public required IGfxTexture MaterialTarget { get; init; }
	public required IGfxTexture EmissiveTarget { get; init; }
	public required IGfxTexture VelocityTarget { get; init; }
	public required IGfxTexture DepthTarget { get; init; }
	public required IGfxPipeline Pipeline { get; init; }
	public required IReadOnlyList<TerrainSnapshotRecord> Records { get; init; }
	public required DescriptorHandle LayerSampler { get; init; }
	public required DescriptorHandle ControlSampler { get; init; }
}
