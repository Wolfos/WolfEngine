using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Atmosphere;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Clouds;

/// <summary>
/// Records the volumetric cloud layer: the noise textures it is shaped from, generated once and shared by every
/// view; and per view, its sun shadow map, its reduced-resolution march and temporal accumulation, and its clouds
/// in the sky environment.
/// </summary>
internal sealed class CloudPass
{
	public const int ShapeNoiseSize = 128;
	public const int DetailNoiseSize = 64;
	public const int WeatherSize = 512;

	private const string ShaderVariant = "clouds.compute.slang";
	private const string NoiseShaderVariant = "cloud_noise.compute.slang";

	private readonly IRenderer _renderer;
	private readonly IShaderProvider _shaderProvider;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly ComputeKernel _noiseShape = new(EngineShaderPrograms.CloudNoise, "CloudNoiseShapeCS", NoiseShaderVariant);
	private readonly ComputeKernel _noiseDetail = new(EngineShaderPrograms.CloudNoise, "CloudNoiseDetailCS", NoiseShaderVariant);
	private readonly ComputeKernel _noiseWeather = new(EngineShaderPrograms.CloudNoise, "CloudNoiseWeatherCS", NoiseShaderVariant);
	private readonly ComputeKernel _march = new(EngineShaderPrograms.Clouds, "CloudMarchCS", ShaderVariant);
	private readonly ComputeKernel _temporal = new(EngineShaderPrograms.Clouds, "CloudTemporalCS", ShaderVariant);
	private readonly ComputeKernel _shadow = new(EngineShaderPrograms.Clouds, "CloudShadowCS", ShaderVariant);
	private readonly ComputeKernel _environmentMarch = new(EngineShaderPrograms.Clouds, "CloudEnvironmentMarchCS", ShaderVariant);
	private readonly ComputeKernel _environmentComposite = new(EngineShaderPrograms.Clouds, "CloudEnvironmentCompositeCS", ShaderVariant);
	private DescriptorHandle _wrapSampler = DescriptorHandle.Invalid;
	private DescriptorHandle _clampSampler = DescriptorHandle.Invalid;
	private IGfxTexture? _weather;
	private IGfxTexture? _shape;
	private IGfxTexture? _detail;
	private IGfxDevice? _noiseDevice;
	private bool _noiseGenerated;
	private readonly Action<RenderGraphContext> _noiseExecute;
	private readonly Action<RenderGraphContext> _shadowMapExecute;
	private readonly Action<RenderGraphContext> _marchExecute;
	private readonly Action<RenderGraphContext> _temporalExecute;
	private readonly Action<RenderGraphContext> _environmentMarchExecute;
	private readonly Action<RenderGraphContext> _environmentCompositeExecute;

	public CloudPass(IRenderer renderer, IShaderProvider shaderProvider, BindlessResourceRegistry bindlessRegistry)
	{
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
		_shaderProvider = shaderProvider ?? throw new ArgumentNullException(nameof(shaderProvider));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_noiseExecute = RecordNoise;
		_shadowMapExecute = context => RecordShadowMap(context, context.ViewState.FrameResources);
		_marchExecute = context => RecordMarch(context, context.ViewState.FrameResources);
		_temporalExecute = context => RecordTemporal(context, context.ViewState.FrameResources);
		_environmentMarchExecute = context => RecordEnvironmentMarch(context, context.ViewState.FrameResources);
		_environmentCompositeExecute = context => RecordEnvironmentComposite(context, context.ViewState.FrameResources);
	}

	/// <summary>Adds the Cloud Noise node, which generates the noise every view's clouds share.</summary>
	internal void AddNoiseNodes(RenderGraph graph, in CloudViewResources clouds)
	{
		graph.AddPass("Cloud Noise", PassKind.Compute)
			.WriteTexture(clouds.Weather, ResourceState.UnorderedAccess)
			.WriteTexture(clouds.Shape, ResourceState.UnorderedAccess)
			.WriteTexture(clouds.Detail, ResourceState.UnorderedAccess)
			.SetExecute(_noiseExecute);
	}

	/// <summary>Adds the nodes that draw the clouds into the view's sky environment, before image-based lighting is built from it.</summary>
	internal void AddEnvironmentNodes(RenderGraph graph, in RenderViewResources resources)
	{
		var clouds = resources.Clouds;
		var marchBuilder = graph.AddPass("Cloud Environment", PassKind.Compute);
		ReadInputs(marchBuilder, resources);
		marchBuilder
			.WriteTexture(clouds.Environment, ResourceState.UnorderedAccess)
			.SetExecute(_environmentMarchExecute);

		var compositeBuilder = graph.AddPass("Cloud Environment Composite", PassKind.Compute);
		ReadInputs(compositeBuilder, resources);
		compositeBuilder
			.ReadTexture(clouds.Environment, ResourceState.ShaderResource)
			.WriteTexture(resources.SkyEnvironment.ViewEnvironment, ResourceState.UnorderedAccess)
			.SetExecute(_environmentCompositeExecute);
	}

	/// <summary>Adds the view's cloud shadow map, march and temporal accumulation, ahead of fog and lighting.</summary>
	internal void AddNodes(RenderGraph graph, in RenderViewResources resources)
	{
		var clouds = resources.Clouds;
		if (clouds.CastsShadows)
		{
			var shadowBuilder = graph.AddPass("Cloud Shadow Map", PassKind.Compute);
			ReadInputs(shadowBuilder, resources);
			shadowBuilder
				.WriteTexture(clouds.ShadowMap, ResourceState.UnorderedAccess)
				.SetExecute(_shadowMapExecute);
		}

		var marchBuilder = graph.AddPass("Cloud March", PassKind.Compute);
		ReadInputs(marchBuilder, resources);
		marchBuilder
			.ReadTexture(resources.GBufferDepth, ResourceState.ShaderResource)
			.WriteTexture(clouds.CurrentColor, ResourceState.UnorderedAccess)
			.WriteTexture(clouds.CurrentDepth, ResourceState.UnorderedAccess)
			.SetExecute(_marchExecute);

		var temporalBuilder = graph.AddPass("Cloud Temporal", PassKind.Compute);
		ReadInputs(temporalBuilder, resources);
		temporalBuilder
			.ReadTexture(clouds.CurrentColor, ResourceState.ShaderResource)
			.ReadTexture(clouds.CurrentDepth, ResourceState.ShaderResource)
			.ReadTexture(clouds.HistoryColorRead, ResourceState.ShaderResource)
			.ReadTexture(clouds.HistoryDepthRead, ResourceState.ShaderResource)
			.WriteTexture(clouds.HistoryColorWrite, ResourceState.UnorderedAccess)
			.WriteTexture(clouds.HistoryDepthWrite, ResourceState.UnorderedAccess)
			.SetExecute(_temporalExecute);
	}

	/// <summary>What every cloud kernel binds: the noise, the atmosphere's LUTs and the sky's irradiance.</summary>
	private static void ReadInputs(RenderGraphBuilder builder, in RenderViewResources resources)
	{
		var clouds = resources.Clouds;
		var atmosphere = resources.Atmosphere;
		builder
			.ReadTexture(clouds.Weather, ResourceState.ShaderResource)
			.ReadTexture(clouds.Shape, ResourceState.ShaderResource)
			.ReadTexture(clouds.Detail, ResourceState.ShaderResource)
			.ReadTexture(atmosphere.Transmittance, ResourceState.ShaderResource)
			.ReadTexture(atmosphere.SkyView, ResourceState.ShaderResource)
			.ReadTexture(atmosphere.AerialPerspective, ResourceState.ShaderResource)
			.ReadTexture(resources.SkyboxIrradiance, ResourceState.ShaderResource);
	}

	public IGfxTexture Weather => _weather ?? throw new InvalidOperationException("Cloud noise was not created.");
	public IGfxTexture Shape => _shape ?? throw new InvalidOperationException("Cloud noise was not created.");
	public IGfxTexture Detail => _detail ?? throw new InvalidOperationException("Cloud noise was not created.");

	/// <summary>Whether the noise textures still have to be generated.</summary>
	public bool ShouldRecordNoise => _noiseGenerated == false;

	/// <summary>The state the noise textures are in when a frame starts.</summary>
	public ResourceState NoiseInitialState => _noiseGenerated
		? ResourceState.ShaderResource
		: ResourceState.UnorderedAccess;

	/// <summary>Creates the shared noise textures on first use, or again on a new device.</summary>
	public void EnsureNoiseTextures(IGfxDevice device)
	{
		if (ReferenceEquals(_noiseDevice, device) && _weather is not null)
		{
			return;
		}

		ReleaseNoise();
		_weather = device.CreateTexture(new TextureDescriptor(
			WeatherSize,
			WeatherSize,
			TextureFormat.Rgba8Unorm,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess));
		_shape = device.CreateTexture(CreateVolumeDescriptor(ShapeNoiseSize));
		_detail = device.CreateTexture(CreateVolumeDescriptor(DetailNoiseSize));
		_noiseDevice = device;
		_noiseGenerated = false;
	}

	public void RecordNoise(RenderGraphContext context)
	{
		var commandList = context.CommandList;

		var shape = Bind(commandList, _noiseShape);
		WriteNoiseHandles(commandList, shape, Shape, ShapeNoiseSize);
		shape.Dispatch(commandList, ShapeNoiseSize, ShapeNoiseSize, ShapeNoiseSize);

		var detail = Bind(commandList, _noiseDetail);
		WriteNoiseHandles(commandList, detail, Detail, DetailNoiseSize);
		detail.Dispatch(commandList, DetailNoiseSize, DetailNoiseSize, DetailNoiseSize);

		var weather = Bind(commandList, _noiseWeather);
		WriteNoiseHandles(commandList, weather, Weather, WeatherSize);
		weather.Dispatch(commandList, WeatherSize, WeatherSize, 1);

		_noiseGenerated = true;
	}

	public void RecordShadowMap(RenderGraphContext context, RenderViewResources resources)
	{
		var clouds = resources.Clouds;
		var commandList = context.CommandList;
		var kernel = Bind(commandList, _shadow);
		var output = context.GetTexture(clouds.ShadowMap);

		var bindless = BeginHandles(kernel, context, resources);
		bindless.SetUInt("outputColorHandle", _bindlessRegistry.RegisterRwTexture(output).Value);
		commandList.SetComputeConstants(bindless.RegisterIndex, bindless.AsBytes());

		WriteFrameConstants(commandList, kernel, resources, environment: false);
		var view = BeginView(kernel, context, resources);
		view.SetUInt("outputSizeX", CloudViewResources.ShadowMapSize);
		view.SetUInt("outputSizeY", CloudViewResources.ShadowMapSize);
		view.SetVector4(
			"shadowMap",
			new Vector4(
				clouds.ShadowMapCentre.X,
				clouds.ShadowMapCentre.Y,
				CloudViewResources.ShadowMapExtentMetres,
				0.0f));
		commandList.SetComputeConstants(view.RegisterIndex, view.AsBytes());

		kernel.Dispatch(commandList, CloudViewResources.ShadowMapSize, CloudViewResources.ShadowMapSize, 1);
	}

	public void RecordMarch(RenderGraphContext context, RenderViewResources resources)
	{
		var clouds = resources.Clouds;
		var commandList = context.CommandList;
		var kernel = Bind(commandList, _march);

		var bindless = BeginHandles(kernel, context, resources);
		bindless.SetUInt("sceneDepthHandle", _bindlessRegistry.RegisterDepthTexture(context.GetTexture(resources.GBufferDepth)).Value);
		bindless.SetUInt("outputColorHandle", RegisterOutput(context, clouds.CurrentColor));
		bindless.SetUInt("outputDepthHandle", RegisterOutput(context, clouds.CurrentDepth));
		commandList.SetComputeConstants(bindless.RegisterIndex, bindless.AsBytes());

		WriteFrameConstants(commandList, kernel, resources, environment: false);
		var view = BeginView(kernel, context, resources);
		commandList.SetComputeConstants(view.RegisterIndex, view.AsBytes());

		kernel.Dispatch(commandList, clouds.TraceSize.X, clouds.TraceSize.Y, 1);
	}

	public void RecordTemporal(RenderGraphContext context, RenderViewResources resources)
	{
		var clouds = resources.Clouds;
		var commandList = context.CommandList;
		var kernel = Bind(commandList, _temporal);

		var bindless = BeginHandles(kernel, context, resources);
		bindless.SetUInt("currentColorHandle", RegisterInput(context, clouds.CurrentColor));
		bindless.SetUInt("currentDepthHandle", RegisterInput(context, clouds.CurrentDepth));
		bindless.SetUInt("historyColorHandle", RegisterInput(context, clouds.HistoryColorRead));
		bindless.SetUInt("historyDepthHandle", RegisterInput(context, clouds.HistoryDepthRead));
		bindless.SetUInt("outputColorHandle", RegisterOutput(context, clouds.HistoryColorWrite));
		bindless.SetUInt("outputDepthHandle", RegisterOutput(context, clouds.HistoryDepthWrite));
		commandList.SetComputeConstants(bindless.RegisterIndex, bindless.AsBytes());

		WriteFrameConstants(commandList, kernel, resources, environment: false);
		var view = BeginView(kernel, context, resources);
		var historyValid = clouds.HistoryValid && context.SceneData.ResetHistory == false;
		view.SetUInt("historyValid", historyValid ? 1u : 0u);
		commandList.SetComputeConstants(view.RegisterIndex, view.AsBytes());

		kernel.Dispatch(commandList, clouds.TraceSize.X, clouds.TraceSize.Y, 1);
	}

	/// <summary>Marches the clouds seen from the camera in every direction into the view's cloud environment.</summary>
	public void RecordEnvironmentMarch(RenderGraphContext context, RenderViewResources resources)
	{
		var clouds = resources.Clouds;
		var commandList = context.CommandList;
		var kernel = Bind(commandList, _environmentMarch);
		var output = context.GetTexture(clouds.Environment);

		var bindless = BeginHandles(kernel, context, resources);
		bindless.SetUInt("outputColorHandle", _bindlessRegistry.RegisterRwTexture(output).Value);
		commandList.SetComputeConstants(bindless.RegisterIndex, bindless.AsBytes());

		WriteFrameConstants(commandList, kernel, resources, environment: true);
		var view = BeginView(kernel, context, resources);
		view.SetUInt("outputSizeX", (uint)output.Descriptor.Width);
		view.SetUInt("outputSizeY", (uint)output.Descriptor.Height);
		commandList.SetComputeConstants(view.RegisterIndex, view.AsBytes());

		kernel.Dispatch(commandList, output.Descriptor.Width, output.Descriptor.Height, 1);
	}

	/// <summary>Composites the cloud environment over the view's sky environment, in place.</summary>
	public void RecordEnvironmentComposite(RenderGraphContext context, RenderViewResources resources)
	{
		var clouds = resources.Clouds;
		var commandList = context.CommandList;
		var kernel = Bind(commandList, _environmentComposite);
		var environment = context.GetTexture(resources.SkyEnvironment.ViewEnvironment);

		var bindless = BeginHandles(kernel, context, resources);
		bindless.SetUInt("currentColorHandle", RegisterInput(context, clouds.Environment));
		bindless.SetUInt("outputColorHandle", _bindlessRegistry.RegisterRwTexture(environment).Value);
		commandList.SetComputeConstants(bindless.RegisterIndex, bindless.AsBytes());

		WriteFrameConstants(commandList, kernel, resources, environment: true);
		var view = BeginView(kernel, context, resources);
		view.SetUInt("outputSizeX", (uint)environment.Descriptor.Width);
		view.SetUInt("outputSizeY", (uint)environment.Descriptor.Height);
		commandList.SetComputeConstants(view.RegisterIndex, view.AsBytes());

		kernel.Dispatch(commandList, environment.Descriptor.Width, environment.Descriptor.Height, 1);
	}

	/// <summary>The size of the cloud environment for a sky environment: at most the quality's width, 2:1.</summary>
	public static Int2 GetEnvironmentSize(int environmentWidth, in CloudQualitySettings quality)
	{
		var width = Math.Max(Math.Min(environmentWidth, quality.EnvironmentWidth), 2);

		return new Int2(width, width / 2);
	}

	public void ReleaseNoise()
	{
		var state = NoiseInitialState;
		RetireNoise(_weather, state);
		RetireNoise(_shape, state);
		RetireNoise(_detail, state);
		_weather = null;
		_shape = null;
		_detail = null;
		_noiseDevice = null;
		_noiseGenerated = false;
	}

	private void RetireNoise(IGfxTexture? texture, ResourceState state)
	{
		if (texture is null)
		{
			return;
		}

		TemporalResourceRetirement.RetireTexture(_noiseDevice, texture, state);
	}

	private static TextureDescriptor CreateVolumeDescriptor(int size)
	{
		return new TextureDescriptor(
			size,
			size,
			TextureFormat.Rgba8Unorm,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			dimension: TextureDimension.Texture3D,
			depth: size);
	}

	private void WriteNoiseHandles(IGfxCommandList commandList, ComputeKernel kernel, IGfxTexture output, int size)
	{
		var bindless = kernel.Writer("BindlessHandles");
		bindless.Clear();
		bindless.SetUInt("outputHandle", output.UnorderedAccessView.Value);
		bindless.SetUInt("outputSize", (uint)size);
		commandList.SetComputeConstants(bindless.RegisterIndex, bindless.AsBytes());
	}

	/// <summary>Starts the handle constants with what every cloud kernel reads.</summary>
	private ShaderPropertyWriter BeginHandles(ComputeKernel kernel, RenderGraphContext context, RenderViewResources resources)
	{
		var clouds = resources.Clouds;
		var atmosphere = AtmosphereBinding.Resolve(context, _bindlessRegistry, resources.Atmosphere);
		var writer = kernel.Writer("BindlessHandles");
		writer.Clear();
		writer.SetUInt("weatherHandle", RegisterInput(context, clouds.Weather));
		writer.SetUInt("shapeHandle", RegisterInput(context, clouds.Shape));
		writer.SetUInt("detailHandle", RegisterInput(context, clouds.Detail));
		writer.SetUInt("wrapSamplerHandle", GetWrapSampler().Value);
		writer.SetUInt("linearSamplerHandle", GetClampSampler().Value);
		writer.SetUInt("transmittanceLutHandle", atmosphere.TransmittanceLut.Value);
		writer.SetUInt("skyViewLutHandle", atmosphere.SkyViewLut.Value);
		writer.SetUInt("aerialPerspectiveHandle", atmosphere.AerialPerspective.Value);
		writer.SetUInt("irradianceHandle", RegisterInput(context, resources.SkyboxIrradiance));

		return writer;
	}

	private static void WriteFrameConstants(
		IGfxCommandList commandList,
		ComputeKernel kernel,
		RenderViewResources resources,
		bool environment)
	{
		var atmosphereWriter = kernel.Writer("AtmosphereParams");
		atmosphereWriter.Clear();
		AtmosphereConstants.Write(atmosphereWriter, resources.Atmosphere.Frame);
		commandList.SetComputeConstants(atmosphereWriter.RegisterIndex, atmosphereWriter.AsBytes());

		var cloudWriter = kernel.Writer("CloudParams");
		cloudWriter.Clear();
		if (environment)
		{
			resources.Clouds.Frame.WriteForEnvironment(cloudWriter);
		}
		else
		{
			resources.Clouds.Frame.Write(cloudWriter);
		}

		commandList.SetComputeConstants(cloudWriter.RegisterIndex, cloudWriter.AsBytes());
	}

	/// <summary>Starts the view constants; the caller adds what its kernel needs and uploads them.</summary>
	private static ShaderPropertyWriter BeginView(ComputeKernel kernel, RenderGraphContext context, RenderViewResources resources)
	{
		var clouds = resources.Clouds;
		var scene = context.SceneData;
		Matrix4x4.Invert(scene.UnjitteredViewProjection, out var inverseUnjitteredViewProjection);

		var writer = kernel.Writer("CloudViewParams");
		writer.Clear();
		writer.SetMatrix4x4("invViewProjection", inverseUnjitteredViewProjection);
		writer.SetMatrix4x4("depthInvViewProjection", scene.InverseViewProjection);
		writer.SetMatrix4x4("previousViewProjection", scene.PreviousViewProjection);
		writer.SetVector4("cameraDelta", new Vector4(scene.CameraOrigin - scene.PreviousCameraOrigin, CloudViewResources.HistoryWeight));
		writer.SetVector4("windDelta", new Vector4(clouds.Frame.WindDelta, clouds.Frame.JitterOffset, 0.0f));
		writer.SetVector4("subpixelJitter", new Vector4(clouds.Frame.SubpixelJitter, 0.0f, 0.0f));
		writer.SetUInt("traceSizeX", (uint)Math.Max(clouds.TraceSize.X, 1));
		writer.SetUInt("traceSizeY", (uint)Math.Max(clouds.TraceSize.Y, 1));
		writer.SetUInt("renderSizeX", (uint)Math.Max(resources.SceneFramebufferSize.X, 1));
		writer.SetUInt("renderSizeY", (uint)Math.Max(resources.SceneFramebufferSize.Y, 1));
		writer.SetUInt("traceDivisor", (uint)Math.Max(clouds.TraceDivisor, 1));

		return writer;
	}

	private uint RegisterInput(RenderGraphContext context, RenderGraphResourceHandle handle)
	{
		return _bindlessRegistry.GetTextureHandle(context.GetTexture(handle)).Value;
	}

	private uint RegisterOutput(RenderGraphContext context, RenderGraphResourceHandle handle)
	{
		return _bindlessRegistry.RegisterRwTexture(context.GetTexture(handle)).Value;
	}

	private ComputeKernel Bind(IGfxCommandList commandList, ComputeKernel kernel)
	{
		kernel.Bind(commandList, GetDevice(), _shaderProvider);

		return kernel;
	}

	private IGfxDevice GetDevice()
	{
		return _renderer.GetGfxDevice() ?? throw new InvalidOperationException("Graphics device is not initialized.");
	}

	private DescriptorHandle GetWrapSampler()
	{
		if (_wrapSampler.IsValid == false)
		{
			_bindlessRegistry.EnsureInitialized(GetDevice());
			_wrapSampler = _bindlessRegistry.GetSamplerHandle(
				new SamplerDescriptor(FilterMode.Bilinear, AddressMode.Wrap, AddressMode.Wrap, AddressMode.Wrap));
		}

		return _wrapSampler;
	}

	private DescriptorHandle GetClampSampler()
	{
		if (_clampSampler.IsValid == false)
		{
			_bindlessRegistry.EnsureInitialized(GetDevice());
			_clampSampler = _bindlessRegistry.GetSamplerHandle(
				new SamplerDescriptor(FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp));
		}

		return _clampSampler;
	}
}
