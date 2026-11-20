using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Atmosphere;

/// <summary>
/// Records the Procedural sky's atmosphere (Hillaire 2020) for one view: its transmittance and multiple-scattering
/// LUTs, its Sky View LUT and aerial perspective volume, and the equirectangular environment that image-based
/// lighting and DDGI read. Textures live on the view's <see cref="ViewSky"/>.
/// </summary>
internal sealed class AtmospherePass
{
	public const int TransmittanceWidth = 256;
	public const int TransmittanceHeight = 64;
	public const int MultipleScatteringSize = 32;
	public const int SkyViewWidth = 192;
	public const int SkyViewHeight = 108;
	public const int AerialPerspectiveSize = 32;

	private readonly IRenderer _renderer;
	private readonly IShaderProvider _shaderProvider;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly ComputeKernel _transmittance = new(EngineShaderPrograms.AtmosphereTransmittanceLut, "AtmosphereTransmittanceLutCSMain");
	private readonly ComputeKernel _multipleScattering = new(EngineShaderPrograms.AtmosphereMultipleScatteringLut, "AtmosphereMultipleScatteringLutCSMain");
	private readonly ComputeKernel _skyView = new(EngineShaderPrograms.AtmosphereSkyViewLut, "AtmosphereSkyViewLutCSMain");
	private readonly ComputeKernel _aerialPerspective = new(EngineShaderPrograms.AtmosphereAerialPerspective, "AtmosphereAerialPerspectiveCSMain");
	private readonly ComputeKernel _environment = new(EngineShaderPrograms.AtmosphereEnvironment, "AtmosphereEnvironmentCSMain");
	private DescriptorHandle _linearClampSampler = DescriptorHandle.Invalid;

	public AtmospherePass(IRenderer renderer, IShaderProvider shaderProvider, BindlessResourceRegistry bindlessRegistry)
	{
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
		_shaderProvider = shaderProvider ?? throw new ArgumentNullException(nameof(shaderProvider));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	/// <summary>Creates the view's LUT textures on first use. Returns whether any were created.</summary>
	public static bool EnsureTextures(IGfxDevice device, ViewSky sky)
	{
		if (sky.HasAtmosphereTextures)
		{
			return false;
		}

		sky.ReleaseAtmosphere();
		sky.TransmittanceLut = device.CreateTexture(CreateLutDescriptor(TransmittanceWidth, TransmittanceHeight));
		sky.MultipleScatteringLut = device.CreateTexture(CreateLutDescriptor(MultipleScatteringSize, MultipleScatteringSize));
		sky.SkyViewLut = device.CreateTexture(CreateLutDescriptor(SkyViewWidth, SkyViewHeight));
		sky.AerialPerspective = device.CreateTexture(new TextureDescriptor(
			AerialPerspectiveSize,
			AerialPerspectiveSize,
			TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			dimension: TextureDimension.Texture3D,
			depth: AerialPerspectiveSize));

		return true;
	}

	public void RecordTransmittance(RenderGraphContext context, ViewSky sky)
	{
		var commandList = context.CommandList;
		var kernel = Bind(commandList, _transmittance);
		var bindless = kernel.Writer("BindlessHandles");
		bindless.Clear();
		bindless.SetUInt("outputHandle", Required(sky.TransmittanceLut).UnorderedAccessView.Value);
		bindless.SetUInt("samplerHandle", GetLinearClampSampler().Value);
		commandList.SetComputeConstants(bindless.RegisterIndex, bindless.AsBytes());
		WriteAtmosphere(commandList, kernel, sky.Atmosphere);
		Dispatch(commandList, kernel, TransmittanceWidth, TransmittanceHeight, 1);
	}

	public void RecordMultipleScattering(RenderGraphContext context, ViewSky sky)
	{
		var commandList = context.CommandList;
		var kernel = Bind(commandList, _multipleScattering);
		var bindless = kernel.Writer("BindlessHandles");
		bindless.Clear();
		bindless.SetUInt("outputHandle", Required(sky.MultipleScatteringLut).UnorderedAccessView.Value);
		bindless.SetUInt("transmittanceLutHandle", Required(sky.TransmittanceLut).ShaderResourceView.Value);
		bindless.SetUInt("samplerHandle", GetLinearClampSampler().Value);
		commandList.SetComputeConstants(bindless.RegisterIndex, bindless.AsBytes());
		WriteAtmosphere(commandList, kernel, sky.Atmosphere);

		// One 1x1x64 group per texel, as in the reference.
		commandList.Dispatch(MultipleScatteringSize, MultipleScatteringSize, 1);

		sky.HasGeneratedAtmosphereLuts = true;
		sky.GeneratedAtmosphereLutHash = sky.Atmosphere.Model.GetLutHash();
	}

	public void RecordSkyView(RenderGraphContext context, ViewSky sky)
	{
		var commandList = context.CommandList;
		var kernel = Bind(commandList, _skyView);
		var bindless = kernel.Writer("BindlessHandles");
		bindless.Clear();
		bindless.SetUInt("outputHandle", Required(sky.SkyViewLut).UnorderedAccessView.Value);
		bindless.SetUInt("transmittanceLutHandle", Required(sky.TransmittanceLut).ShaderResourceView.Value);
		bindless.SetUInt("multiScatteringLutHandle", Required(sky.MultipleScatteringLut).ShaderResourceView.Value);
		bindless.SetUInt("samplerHandle", GetLinearClampSampler().Value);
		commandList.SetComputeConstants(bindless.RegisterIndex, bindless.AsBytes());
		WriteAtmosphere(commandList, kernel, sky.Atmosphere);
		Dispatch(commandList, kernel, SkyViewWidth, SkyViewHeight, 1);
	}

	public void RecordAerialPerspective(RenderGraphContext context, ViewSky sky)
	{
		var commandList = context.CommandList;
		var kernel = Bind(commandList, _aerialPerspective);
		var bindless = kernel.Writer("BindlessHandles");
		bindless.Clear();
		bindless.SetUInt("outputHandle", Required(sky.AerialPerspective).UnorderedAccessView.Value);
		bindless.SetUInt("transmittanceLutHandle", Required(sky.TransmittanceLut).ShaderResourceView.Value);
		bindless.SetUInt("multiScatteringLutHandle", Required(sky.MultipleScatteringLut).ShaderResourceView.Value);
		bindless.SetUInt("samplerHandle", GetLinearClampSampler().Value);
		commandList.SetComputeConstants(bindless.RegisterIndex, bindless.AsBytes());
		WriteAtmosphere(commandList, kernel, sky.Atmosphere);

		var view = kernel.Writer("ViewParams");
		view.Clear();
		view.SetMatrix4x4("invViewProjection", context.SceneData.InverseViewProjection);
		commandList.SetComputeConstants(view.RegisterIndex, view.AsBytes());

		Dispatch(commandList, kernel, AerialPerspectiveSize, AerialPerspectiveSize, AerialPerspectiveSize);

		sky.HasGeneratedAtmosphereViewLuts = true;
	}

	/// <summary>Draws the view's equirectangular environment from its Sky View LUT.</summary>
	public void RecordEnvironment(IGfxCommandList commandList, ViewSky sky, IGfxTexture environment)
	{
		var kernel = Bind(commandList, _environment);
		var bindless = kernel.Writer("BindlessHandles");
		bindless.Clear();
		bindless.SetUInt("environmentHandle", environment.UnorderedAccessView.Value);
		bindless.SetUInt("skyViewLutHandle", Required(sky.SkyViewLut).ShaderResourceView.Value);
		bindless.SetUInt("samplerHandle", GetLinearClampSampler().Value);
		bindless.SetUInt("width", (uint)environment.Descriptor.Width);
		bindless.SetUInt("height", (uint)environment.Descriptor.Height);
		commandList.SetComputeConstants(bindless.RegisterIndex, bindless.AsBytes());
		WriteAtmosphere(commandList, kernel, sky.Atmosphere);
		Dispatch(commandList, kernel, environment.Descriptor.Width, environment.Descriptor.Height, 1);
	}

	private static TextureDescriptor CreateLutDescriptor(int width, int height)
	{
		return new TextureDescriptor(
			width,
			height,
			TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess);
	}

	private static IGfxTexture Required(IGfxTexture? texture)
	{
		return texture ?? throw new InvalidOperationException("Atmosphere texture was not created.");
	}

	private static void WriteAtmosphere(IGfxCommandList commandList, ComputeKernel kernel, in AtmosphereFrame frame)
	{
		var writer = kernel.Writer("AtmosphereParams");
		writer.Clear();
		AtmosphereConstants.Write(writer, frame);
		commandList.SetComputeConstants(writer.RegisterIndex, writer.AsBytes());
	}

	private static void Dispatch(IGfxCommandList commandList, ComputeKernel kernel, int width, int height, int depth)
	{
		var (x, y, z) = kernel.ThreadGroupSize.GetDispatchGroupCount((uint)width, (uint)height, (uint)depth);
		commandList.Dispatch(x, y, z);
	}

	private ComputeKernel Bind(IGfxCommandList commandList, ComputeKernel kernel)
	{
		var device = _renderer.GetGfxDevice() ?? throw new InvalidOperationException("Graphics device is not initialized.");
		kernel.EnsureCreated(device, _shaderProvider);
		commandList.BindPipeline(kernel.Pipeline);

		return kernel;
	}

	private DescriptorHandle GetLinearClampSampler()
	{
		if (_linearClampSampler.IsValid == false)
		{
			var device = _renderer.GetGfxDevice() ?? throw new InvalidOperationException("Graphics device is not initialized.");
			_bindlessRegistry.EnsureInitialized(device);
			_linearClampSampler = _bindlessRegistry.GetSamplerHandle(
				new SamplerDescriptor(FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp));
		}

		return _linearClampSampler;
	}
}
