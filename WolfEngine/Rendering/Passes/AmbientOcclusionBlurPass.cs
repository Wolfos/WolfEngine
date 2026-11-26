using WolfEngine.Rendering.Abstraction;
using System.Numerics;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

public sealed class AmbientOcclusionBlurPass
{
	private readonly IShaderProvider _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxPipeline? _pipeline;
	private ReadOnlyMemory<byte> _computeShader;
	private ComputeThreadGroupSize? _threadGroupSize;
	private GraphicsBackendKind? _compiledBackendKind;
	private ShaderPropertyWriter? _bindlessWriter;
	private ShaderPropertyWriter? _settingsWriter;
	private readonly Action<RenderGraphContext> _horizontalExecute;
	private readonly Action<RenderGraphContext> _verticalExecute;

	public AmbientOcclusionBlurPass(IShaderProvider shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_horizontalExecute = context => Execute(context, blurHorizontally: true);
		_verticalExecute = context => Execute(context, blurHorizontally: false);
	}

	/// <summary>The view's intermediate between the two blur directions; only this pass reads or writes it.</summary>
	private sealed class ViewHandles
	{
		public RenderGraphResourceHandle Temporary;
	}

	/// <summary>
	/// Adds the separable blur's two nodes, through an intermediate of the raw occlusion's size. At half
	/// resolution the vertical blur writes back into the raw target for the upsample to read; at full resolution
	/// it writes the final occlusion directly.
	/// </summary>
	internal void AddNodes(RenderGraph graph, RenderViewState view)
	{
		var resources = view.FrameResources;
		// The intermediate has the raw occlusion's shape: the same size, format and clear value.
		var temporary = graph.Resources.CreateTransientTexture(
			graph.Resources.GetTextureDescriptor(resources.AmbientOcclusionRaw));
		view.GetPassState<ViewHandles>().Temporary = temporary;

		graph.AddPass("Ambient Occlusion Blur X", PassKind.Compute)
			.ReadTexture(resources.GBufferDepth, ResourceState.ShaderResource)
			.ReadTexture(resources.GBufferNormal, ResourceState.ShaderResource)
			.ReadTexture(resources.AmbientOcclusionRaw, ResourceState.ShaderResource)
			.WriteTexture(temporary, ResourceState.UnorderedAccess)
			.SetExecute(_horizontalExecute);

		var output = resources.Config.AmbientOcclusion.Resolution == AmbientOcclusionResolution.Half
			? resources.AmbientOcclusionRaw
			: resources.AmbientOcclusionFinal;
		graph.AddPass("Ambient Occlusion Blur Y", PassKind.Compute)
			.ReadTexture(resources.GBufferDepth, ResourceState.ShaderResource)
			.ReadTexture(resources.GBufferNormal, ResourceState.ShaderResource)
			.ReadTexture(temporary, ResourceState.ShaderResource)
			.WriteTexture(output, ResourceState.UnorderedAccess)
			.SetExecute(_verticalExecute);
	}

	private void Execute(RenderGraphContext context, bool blurHorizontally)
	{
		var view = context.ViewState;
		var config = BuildConfig(
			context,
			view.FrameResources,
			context.Device,
			view.GetPassState<ViewHandles>().Temporary,
			blurHorizontally);
		Record(context, in config, context.SceneData);
	}

	private AmbientOcclusionBlurPassConfig BuildConfig(
		RenderGraphContext context,
		RenderViewResources resources,
		IGfxDevice device,
		RenderGraphResourceHandle temporary,
		bool blurHorizontally)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = EnsurePipeline(device);
		_bindlessRegistry.EnsureInitialized(device);

		var depth = context.GetTexture(resources.GBufferDepth);
		var normal = context.GetTexture(resources.GBufferNormal);
		var source = context.GetTexture(blurHorizontally ? resources.AmbientOcclusionRaw : temporary);
		var destinationHandle = blurHorizontally
			? temporary
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

	private void Record(RenderGraphContext context, in AmbientOcclusionBlurPassConfig config, SceneDrawData sceneData)
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
			computeEntryPoint: "AmbientOcclusionBlurCS",
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
			EngineShaderPrograms.AmbientOcclusionBlur,
			"AmbientOcclusionBlurCS",
			backendKind);

		_computeShader = compiled.Bytecode;
		_threadGroupSize = compiled.ThreadGroupSize;
		var reflection = compiled.ReflectionLayout;
		_bindlessWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("BindlessHandles"));
		_settingsWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("BlurSettings"));
		_compiledBackendKind = backendKind;
	}
}
