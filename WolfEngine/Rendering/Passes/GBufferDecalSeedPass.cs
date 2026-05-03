using System;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public sealed class GBufferDecalSeedPass
{
	private readonly IShaderCompiler _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxPipeline? _pipeline;
	private ReadOnlyMemory<byte> _computeShader;
	private ComputeThreadGroupSize? _threadGroupSize;
	private GraphicsBackendKind? _compiledBackendKind;
	private ShaderPropertyWriter? _bindlessWriter;
	private ShaderPropertyWriter? _settingsWriter;

	public GBufferDecalSeedPass(IShaderCompiler shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public GBufferDecalSeedPassConfig BuildConfig(
		RenderGraphContext context,
		RenderGraphFrameResources resources,
		IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = EnsurePipeline(device);
		_bindlessRegistry.EnsureInitialized(device);
		var sourceAlbedo = context.GetTexture(resources.DecalSourceGBufferAlbedo);
		var sourceNormal = context.GetTexture(resources.DecalSourceGBufferNormal);
		var sourceMaterial = context.GetTexture(resources.DecalSourceGBufferMaterial);
		var sourceEmissive = context.GetTexture(resources.DecalSourceGBufferEmissive);
		var targetAlbedo = context.GetTexture(resources.GBufferAlbedo);
		var targetNormal = context.GetTexture(resources.GBufferNormal);
		var targetMaterial = context.GetTexture(resources.GBufferMaterial);
		var targetEmissive = context.GetTexture(resources.GBufferEmissive);

		return new GBufferDecalSeedPassConfig
		{
			Pipeline = pipeline,
			SourceAlbedoHandle = _bindlessRegistry.GetTextureHandle(sourceAlbedo),
			SourceNormalHandle = _bindlessRegistry.GetTextureHandle(sourceNormal),
			SourceMaterialHandle = _bindlessRegistry.GetTextureHandle(sourceMaterial),
			SourceEmissiveHandle = _bindlessRegistry.GetTextureHandle(sourceEmissive),
			TargetAlbedoHandle = _bindlessRegistry.RegisterRwTexture(targetAlbedo),
			TargetNormalHandle = _bindlessRegistry.RegisterRwTexture(targetNormal),
			TargetMaterialHandle = _bindlessRegistry.RegisterRwTexture(targetMaterial),
			TargetEmissiveHandle = _bindlessRegistry.RegisterRwTexture(targetEmissive),
			RenderSize = resources.SceneFramebufferSize
		};
	}

	public void Record(RenderGraphContext context, in GBufferDecalSeedPassConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _bindlessWriter
			?? throw new InvalidOperationException("GBuffer decal seed bindless writer was not initialized.");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("sourceAlbedoHandle", config.SourceAlbedoHandle.Value);
		bindlessWriter.SetUInt("sourceNormalHandle", config.SourceNormalHandle.Value);
		bindlessWriter.SetUInt("sourceMaterialHandle", config.SourceMaterialHandle.Value);
		bindlessWriter.SetUInt("sourceEmissiveHandle", config.SourceEmissiveHandle.Value);
		bindlessWriter.SetUInt("targetAlbedoHandle", config.TargetAlbedoHandle.Value);
		bindlessWriter.SetUInt("targetNormalHandle", config.TargetNormalHandle.Value);
		bindlessWriter.SetUInt("targetMaterialHandle", config.TargetMaterialHandle.Value);
		bindlessWriter.SetUInt("targetEmissiveHandle", config.TargetEmissiveHandle.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var settingsWriter = _settingsWriter
			?? throw new InvalidOperationException("GBuffer decal seed settings writer was not initialized.");
		settingsWriter.Clear();
		settingsWriter.SetUInt("renderSizeX", (uint)Math.Max(config.RenderSize.X, 1));
		settingsWriter.SetUInt("renderSizeY", (uint)Math.Max(config.RenderSize.Y, 1));
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

		var threadGroupSize = _threadGroupSize
			?? throw new InvalidOperationException("GBuffer decal seed threadgroup size was not initialized.");
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(
			(uint)Math.Max(config.RenderSize.X, 1),
			(uint)Math.Max(config.RenderSize.Y, 1));
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private IGfxPipeline EnsurePipeline(IGfxDevice device)
	{
		if (_pipeline is not null)
		{
			if (_compiledBackendKind.HasValue && _compiledBackendKind.Value != device.BackendKind)
			{
				throw new InvalidOperationException(
					$"GBufferDecalSeedPass is already compiled for backend '{_compiledBackendKind.Value}', but was requested for '{device.BackendKind}'.");
			}

			return _pipeline;
		}

		EnsureReflectionWriters(device.BackendKind);
		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: "CSMain",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: "gbuffer_decal_seed.compute.slang");
		_pipeline = device.GetOrCreatePipeline(
			pipelineKey,
			new ShaderBytecodeSet(compute: _computeShader, computeThreadGroupSize: _threadGroupSize));
		return _pipeline;
	}

	private void EnsureReflectionWriters(GraphicsBackendKind backendKind)
	{
		if (_compiledBackendKind.HasValue &&
		    _compiledBackendKind.Value == backendKind &&
		    _bindlessWriter is not null &&
		    _settingsWriter is not null &&
		    _computeShader.IsEmpty == false &&
		    _threadGroupSize.HasValue)
		{
			return;
		}

		var compiled = _shaderCompiler.GetComputeShaderWithReflection(
			"gbuffer_decal_seed.compute.slang",
			"CSMain",
			backendKind);
		_computeShader = compiled.Bytecode;
		_threadGroupSize = compiled.ThreadGroupSize;
		var reflection = compiled.ReflectionLayout;
		_bindlessWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("BindlessHandles"));
		_settingsWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("CopySettings"));
		_compiledBackendKind = backendKind;
	}
}
