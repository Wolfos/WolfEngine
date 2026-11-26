using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

public sealed class CopyToFinalPass
{
	private readonly IShaderProvider _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxPipeline? _pipeline;
	private ReadOnlyMemory<byte> _computeShader;
	private ComputeThreadGroupSize? _threadGroupSize;
	private GraphicsBackendKind? _compiledBackendKind;
	private ShaderPropertyWriter? _bindlessWriter;
	private ShaderPropertyWriter? _settingsWriter;
	private readonly Action<RenderGraphContext> _execute;

	public CopyToFinalPass(IShaderProvider shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_execute = Execute;
	}

	/// <summary>
	/// Adds the Copy To Final node: display colour into the view's encoded scene colour and, for a view presented
	/// straight to the window, into the shared final colour as well.
	/// </summary>
	internal void AddNodes(
		RenderGraph graph,
		in RenderViewResources resources,
		in RenderFrameSharedResources sharedResources,
		RenderViewOutput output)
	{
		var builder = graph.AddPass("Copy To Final", PassKind.Compute)
			.ReadTexture(resources.DisplayLinearSceneColor, ResourceState.ShaderResource)
			.WriteTexture(resources.EncodedSceneColor, ResourceState.UnorderedAccess);
		// Only a backbuffer view copies directly into the window. Editor views are
		// displayed by ImGui from their independently sized encoded scene texture.
		if (output == RenderViewOutput.Backbuffer)
		{
			builder.WriteTexture(sharedResources.FinalColor, ResourceState.UnorderedAccess);
		}

		builder.SetExecute(_execute);
	}

	private void Execute(RenderGraphContext context)
	{
		var config = BuildConfig(
			context,
			context.ViewState.FrameResources,
			context.SharedResources,
			context.Device,
			context.ViewState.Output == RenderViewOutput.Backbuffer);
		Record(context, in config);
	}

	private CopyToFinalPassConfig BuildConfig(
		RenderGraphContext context,
		RenderViewResources resources,
		RenderFrameSharedResources sharedResources,
		IGfxDevice device,
		bool writeFinalOutput = true)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = EnsurePipeline(device);
		_bindlessRegistry.EnsureInitialized(device);
		// DisplayLinearSceneColor aliases tonemapping when CAS is disabled.
		var input = context.GetTexture(resources.DisplayLinearSceneColor);
		var encodedSceneOutput = context.GetTexture(resources.EncodedSceneColor);
		// A view that does not own the presentation never touches the shared final target; the shader skips the
		// write, and the handle only has to be a valid binding.
		var output = writeFinalOutput ? context.GetTexture(sharedResources.FinalColor) : encodedSceneOutput;

		return new CopyToFinalPassConfig
		{
			Pipeline = pipeline,
			InputHandle = _bindlessRegistry.GetTextureHandle(input),
			OutputHandle = _bindlessRegistry.RegisterRwTexture(output),
			EncodedSceneOutputHandle = _bindlessRegistry.RegisterRwTexture(encodedSceneOutput),
			RenderSize = resources.FramebufferSize,
			WriteFinalOutput = writeFinalOutput
		};
	}

	private void Record(RenderGraphContext context, in CopyToFinalPassConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _bindlessWriter
		                     ?? throw new InvalidOperationException(
			                     "Copy-to-final bindless writer was not initialized.");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("inputHandle", config.InputHandle.Value);
		bindlessWriter.SetUInt("outputHandle", config.OutputHandle.Value);
		bindlessWriter.SetUInt("encodedSceneOutputHandle", config.EncodedSceneOutputHandle.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var settingsWriter = _settingsWriter
		                     ?? throw new InvalidOperationException(
			                     "Copy-to-final settings writer was not initialized.");
		settingsWriter.Clear();
		settingsWriter.SetUInt("renderSizeX", (uint)Math.Max(config.RenderSize.X, 1));
		settingsWriter.SetUInt("renderSizeY", (uint)Math.Max(config.RenderSize.Y, 1));
		settingsWriter.SetUInt("writeFinalOutput", config.WriteFinalOutput ? 1u : 0u);
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

		var threadGroupSize = _threadGroupSize
		                      ?? throw new InvalidOperationException(
			                      "Copy-to-final threadgroup size was not initialized.");
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
					$"CopyToFinalPass is already compiled for backend '{_compiledBackendKind.Value}', but was requested for '{device.BackendKind}'.");
			}

			return _pipeline;
		}

		EnsureReflectionWriters(device.BackendKind);
		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: "CopyToFinalCS",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: "copy_to_final.compute.slang");
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
			EngineShaderPrograms.CopyToFinal,
			"CopyToFinalCS",
			backendKind);
		_computeShader = compiled.Bytecode;
		_threadGroupSize = compiled.ThreadGroupSize;
		var reflection = compiled.ReflectionLayout;
		_bindlessWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("BindlessHandles"));
		_settingsWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("CopySettings"));
		_compiledBackendKind = backendKind;
	}
}
