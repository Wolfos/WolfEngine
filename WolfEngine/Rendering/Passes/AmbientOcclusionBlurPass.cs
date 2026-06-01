using System;
using WolfEngine.Rendering.Abstraction;
using System.Numerics;

namespace WolfEngine.Rendering.Passes;

public sealed class AmbientOcclusionBlurPass
{
	private readonly IShaderCompiler _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxPipeline _pipeline;
	private ReadOnlyMemory<byte> _computeShader;
	private ComputeThreadGroupSize? _threadGroupSize;
	private GraphicsBackendKind? _compiledBackendKind;
	private ShaderPropertyWriter? _bindlessWriter;
	private ShaderPropertyWriter? _settingsWriter;

	public AmbientOcclusionBlurPass(IShaderCompiler shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public AmbientOcclusionBlurPassConfig BuildConfig(
		RenderGraphContext context,
		RenderGraphFrameResources resources,
		IGfxDevice device,
		bool blurHorizontally)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = EnsurePipeline(device);
		_bindlessRegistry.EnsureInitialized(device);

		var depth = context.GetTexture(resources.GBufferDepth);
		var normal = context.GetTexture(resources.GBufferNormal);
		var source = context.GetTexture(blurHorizontally ? resources.AmbientOcclusionRaw : resources.AmbientOcclusionTemp);
		var destinationHandle = blurHorizontally
			? resources.AmbientOcclusionTemp
			: resources.Config.AmbientOcclusion.Resolution == AmbientOcclusionResolution.Half
				? resources.AmbientOcclusionRaw
				: resources.AmbientOcclusionFinal;
		var destination = context.GetTexture(destinationHandle);
		var depthHandle = _bindlessRegistry.RegisterDepthTexture(depth);
		var normalHandle = _bindlessRegistry.GetTextureHandle(normal);
		var sourceHandle = _bindlessRegistry.GetTextureHandle(source);
		var outputHandle = _bindlessRegistry.RegisterRwTexture(destination);

		return new AmbientOcclusionBlurPassConfig
		{
			Pipeline = pipeline,
			DepthHandle = depthHandle,
			NormalHandle = normalHandle,
			SourceHandle = sourceHandle,
			OutputHandle = outputHandle,
			FullResolution = resources.SceneFramebufferSize,
			AoResolution = new(source.Descriptor.Width, source.Descriptor.Height),
			BlurSharpness = Math.Max(resources.Config.AmbientOcclusion.BlurSharpness, 0.001f),
			BlurHorizontally = blurHorizontally
		};
	}

	public void Record(RenderGraphContext context, in AmbientOcclusionBlurPassConfig config, SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _bindlessWriter
			?? throw new InvalidOperationException("Ambient occlusion blur bindless writer was not initialized.");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("depthHandle", config.DepthHandle.Value);
		bindlessWriter.SetUInt("normalHandle", config.NormalHandle.Value);
		bindlessWriter.SetUInt("sourceHandle", config.SourceHandle.Value);
		bindlessWriter.SetUInt("outputHandle", config.OutputHandle.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var settingsWriter = _settingsWriter
			?? throw new InvalidOperationException("Ambient occlusion blur settings writer was not initialized.");
		if (Matrix4x4.Invert(sceneData.InverseProjection, out var projectionMatrix) == false)
		{
			throw new InvalidOperationException("Ambient occlusion blur projection parameters could not be reconstructed.");
		}

		settingsWriter.Clear();
		settingsWriter.SetUInt("fullResolutionX", (uint)Math.Max(config.FullResolution.X, 1));
		settingsWriter.SetUInt("fullResolutionY", (uint)Math.Max(config.FullResolution.Y, 1));
		settingsWriter.SetUInt("aoResolutionX", (uint)Math.Max(config.AoResolution.X, 1));
		settingsWriter.SetUInt("aoResolutionY", (uint)Math.Max(config.AoResolution.Y, 1));
		settingsWriter.SetUInt("blurHorizontally", config.BlurHorizontally ? 1u : 0u);
		settingsWriter.SetFloat("blurSharpness", config.BlurSharpness);
		settingsWriter.SetFloat("projZBias", projectionMatrix.M33);
		settingsWriter.SetFloat("projZScale", projectionMatrix.M43);
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

		var threadGroupSize = _threadGroupSize
			?? throw new InvalidOperationException("Ambient occlusion blur threadgroup size was not initialized.");
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(
			(uint)Math.Max(config.AoResolution.X, 1),
			(uint)Math.Max(config.AoResolution.Y, 1));
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private IGfxPipeline EnsurePipeline(IGfxDevice device)
	{
		if (_pipeline is not null)
		{
			if (_compiledBackendKind.HasValue && _compiledBackendKind.Value != device.BackendKind)
			{
				throw new InvalidOperationException(
					$"AmbientOcclusionBlurPass is already compiled for backend '{_compiledBackendKind.Value}', but was requested for '{device.BackendKind}'.");
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
			shaderVariant: "ao_blur.compute.slang");
		_pipeline = device.GetOrCreatePipeline(
			pipelineKey,
			new ShaderBytecodeSet(compute: _computeShader, computeThreadGroupSize: _threadGroupSize));
		return _pipeline;
	}

	private void EnsureReflectionWriters(GraphicsBackendKind backendKind)
	{
		if (_compiledBackendKind.HasValue &&
		    _compiledBackendKind.Value == backendKind &&
		    _computeShader.IsEmpty == false &&
		    _threadGroupSize.HasValue &&
		    _bindlessWriter is not null &&
		    _settingsWriter is not null)
		{
			return;
		}

		var compiled = _shaderCompiler.GetComputeShaderWithReflection(
			"ao_blur.compute.slang",
			"CSMain",
			backendKind);

		_computeShader = compiled.Bytecode;
		_threadGroupSize = compiled.ThreadGroupSize;
		var reflection = compiled.ReflectionLayout;
		_bindlessWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("BindlessHandles"));
		_settingsWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("BlurSettings"));
		_compiledBackendKind = backendKind;
	}
}
