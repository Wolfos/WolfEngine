using System.Numerics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public sealed class VBAOPass
{
	public enum AmbientOcclusionResolution
	{
		Full,
		Half
	}

	public struct Config
	{
		public Config()
		{
		}

		public bool Enabled { get; set; } = false;
		public AmbientOcclusionResolution Resolution { get; set; } = AmbientOcclusionResolution.Full;
		public int SliceCount { get; set; } = 2;
		public int StepCount { get; set; } = 8;
		public float Radius { get; set; } = 1.2f;
		public float Thickness { get; set; } = 0.2f;
		public float Bias { get; set; } = 0.03f;
		public float Strength { get; set; } = 1.0f;
		public float Power { get; set; } = 1.5f;
		public float BlurSharpness { get; set; } = 16.0f;
	}

	private readonly IShaderCompiler _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxPipeline _pipeline;
	private ReadOnlyMemory<byte> _computeShader;
	private GraphicsBackendKind? _compiledBackendKind;
	private ShaderPropertyWriter? _bindlessWriter;
	private ShaderPropertyWriter? _cameraWriter;
	private ShaderPropertyWriter? _settingsWriter;
	public VBAOPass(IShaderCompiler shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public VisibilityBitmaskAmbientOcclusionPassConfig BuildConfig(
		RenderGraphContext context,
		RenderGraphFrameResources resources,
		IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = EnsurePipeline(device);
		_bindlessRegistry.EnsureInitialized(device);

		var depth = context.GetTexture(resources.GBufferDepth);
		var normal = context.GetTexture(resources.GBufferNormal);
		var output = context.GetTexture(resources.AmbientOcclusionRaw);
		var settings = resources.Config.VBAOConfig;
		var depthHandle = _bindlessRegistry.RegisterDepthTexture(depth);
		var normalHandle = _bindlessRegistry.GetTextureHandle(normal);
		var outputHandle = _bindlessRegistry.RegisterRwTexture(output);

		return new VisibilityBitmaskAmbientOcclusionPassConfig
		{
			Pipeline = pipeline,
			DepthHandle = depthHandle,
			NormalHandle = normalHandle,
			OutputHandle = outputHandle,
			FullResolution = resources.SceneFramebufferSize,
			OutputResolution = new(output.Descriptor.Width, output.Descriptor.Height),
			SliceCount = Math.Max(1, settings.SliceCount),
			StepCount = Math.Max(1, settings.StepCount),
			Radius = Math.Max(settings.Radius, 0.001f),
			Thickness = Math.Max(settings.Thickness, 0.0f),
			Bias = Math.Max(settings.Bias, 0.0f),
			Strength = Math.Max(settings.Strength, 0.0f),
			Power = Math.Max(settings.Power, 0.001f)
		};
	}

	public void Record(RenderGraphContext context, in VisibilityBitmaskAmbientOcclusionPassConfig config, SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sceneData);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _bindlessWriter
			?? throw new InvalidOperationException("Ambient occlusion bindless writer was not initialized.");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("depthHandle", config.DepthHandle.Value);
		bindlessWriter.SetUInt("normalHandle", config.NormalHandle.Value);
		bindlessWriter.SetUInt("outputHandle", config.OutputHandle.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var cameraWriter = _cameraWriter
			?? throw new InvalidOperationException("Ambient occlusion camera writer was not initialized.");
		cameraWriter.Clear();
		cameraWriter.SetMatrix4x4("camera.invProjection", sceneData.InverseProjection);
		cameraWriter.SetMatrix4x4("camera.invViewProjection", sceneData.InverseViewProjection);
		cameraWriter.SetVector3("camera.cameraOrigin", sceneData.CameraOrigin);
		cameraWriter.SetMatrix4x4("camera.viewMatrix", sceneData.ViewMatrix);
		commandList.SetComputeConstants(cameraWriter.RegisterIndex, cameraWriter.AsBytes());

		var settingsWriter = _settingsWriter
			?? throw new InvalidOperationException("Ambient occlusion settings writer was not initialized.");
		settingsWriter.Clear();
		settingsWriter.SetUInt("fullResolutionX", (uint)Math.Max(config.FullResolution.X, 1));
		settingsWriter.SetUInt("fullResolutionY", (uint)Math.Max(config.FullResolution.Y, 1));
		settingsWriter.SetUInt("outputResolutionX", (uint)Math.Max(config.OutputResolution.X, 1));
		settingsWriter.SetUInt("outputResolutionY", (uint)Math.Max(config.OutputResolution.Y, 1));
		settingsWriter.SetUInt("sliceCount", (uint)config.SliceCount);
		settingsWriter.SetUInt("stepCount", (uint)config.StepCount);
		settingsWriter.SetFloat("radius", config.Radius);
		settingsWriter.SetFloat("thickness", config.Thickness);
		settingsWriter.SetFloat("bias", config.Bias);
		settingsWriter.SetFloat("strength", config.Strength);
		settingsWriter.SetFloat("power", config.Power);
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

		var dispatchX = (uint)((config.OutputResolution.X + 7) / 8);
		var dispatchY = (uint)((config.OutputResolution.Y + 7) / 8);
		commandList.Dispatch(dispatchX, dispatchY, 1);
	}

	private IGfxPipeline EnsurePipeline(IGfxDevice device)
	{
		if (_pipeline is not null)
		{
			if (_compiledBackendKind.HasValue && _compiledBackendKind.Value != device.BackendKind)
			{
				throw new InvalidOperationException(
					$"VisibilityBitmaskAmbientOcclusionPass is already compiled for backend '{_compiledBackendKind.Value}', but was requested for '{device.BackendKind}'.");
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
			shaderVariant: "ao_vbao.compute.slang");
		_pipeline = device.GetOrCreatePipeline(pipelineKey, new ShaderBytecodeSet(compute: _computeShader));
		return _pipeline;
	}

	private void EnsureReflectionWriters(GraphicsBackendKind backendKind)
	{
		if (_compiledBackendKind.HasValue &&
		    _compiledBackendKind.Value == backendKind &&
		    _computeShader.IsEmpty == false &&
		    _bindlessWriter is not null &&
		    _cameraWriter is not null &&
		    _settingsWriter is not null)
		{
			return;
		}

		var compiled = _shaderCompiler.GetComputeShaderWithReflection(
			"ao_vbao.compute.slang",
			"CSMain",
			backendKind);

		_computeShader = compiled.Bytecode;
		var reflection = compiled.ReflectionLayout;
		_bindlessWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("BindlessHandles"));
		_cameraWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("CameraParams"));
		_settingsWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("AoSettings"));
		_compiledBackendKind = backendKind;
	}
}
