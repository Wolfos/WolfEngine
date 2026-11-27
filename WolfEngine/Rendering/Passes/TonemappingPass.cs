using System.Numerics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

public sealed class TonemappingPass
{
	private const int ModeCount = 3;

	private readonly ComputeKernel[] _kernels;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;
	private readonly Action<RenderGraphContext> _execute;

	public TonemappingPass(ShaderSource shaderSource, BindlessResourceRegistry bindlessRegistry)
	{
		ArgumentNullException.ThrowIfNull(shaderSource);

		_kernels = new ComputeKernel[ModeCount];
		for (var i = 0; i < ModeCount; i++)
		{
			_kernels[i] = new ComputeKernel(shaderSource, EngineShaderPrograms.Tonemapping, GetEntryPoint((TonemappingMode)i));
		}

		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_execute = Execute;
	}

	/// <summary>Adds the Tonemapping node: the bloom composite, or the resolved scene colour without bloom, to display range.</summary>
	internal void AddNodes(RenderGraph graph, in RenderViewResources resources)
	{
		graph.AddPass("Tonemapping", PassKind.Compute)
			.ReadTexture(
				resources.BloomCompositeSceneColor.IsValid ? resources.BloomCompositeSceneColor : resources.ResolvedSceneColor,
				ResourceState.ShaderResource)
			.WriteTexture(resources.TonemappedLinearSceneColor, ResourceState.UnorderedAccess)
			.SetExecute(_execute);
	}

	private void Execute(RenderGraphContext context)
	{
		var config = BuildConfig(context, context.ViewState.FrameResources, context.Device);
		Record(context, in config);
	}

	public static string GetEntryPoint(TonemappingMode mode) => mode switch
	{
		TonemappingMode.Aces => "TonemappingAces",
		TonemappingMode.AgX => "TonemappingAgX",
		TonemappingMode.KhronosPbrNeutral => "TonemappingPbrNeutral",
		_ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown tonemapping mode.")
	};

	private TonemappingPassConfig BuildConfig(
		RenderGraphContext context,
		RenderViewResources resources,
		IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var settings = resources.Config.Tonemapping;
		var pipeline = _kernels[GetModeIndex(settings.Mode)].GetPipeline(device);
		_bindlessRegistry.EnsureInitialized(device);
		if (_linearSampler.IsValid == false)
		{
			_linearSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
				FilterMode.Bilinear,
				AddressMode.Clamp,
				AddressMode.Clamp,
				AddressMode.Clamp));
		}

		var input = context.GetTexture(resources.BloomCompositeSceneColor.IsValid
			? resources.BloomCompositeSceneColor
			: resources.ResolvedSceneColor);
		var output = context.GetTexture(resources.TonemappedLinearSceneColor);

		// The LUT is a persistent asset texture rather than a graph resource. Until its upload has published a
		// volume SRV, grading is skipped: the bindless error texture is 2D and cannot stand in for it.
		var colorGrading = resources.Config.ColorGrading;
		var lookupTable = context.ViewSnapshot.ColorGradingLookupTable;
		var lookupTableResources = lookupTable is not null && lookupTable.Texture.HasGpuResources
			? lookupTable.Texture.Resources
			: null;
		var gradingActive = colorGrading.Contribution > 0.0f &&
		                    lookupTableResources is not null &&
		                    lookupTableResources.ShaderResourceView.IsValid;

		return new TonemappingPassConfig
		{
			Pipeline = pipeline,
			InputHandle = _bindlessRegistry.GetTextureHandle(input),
			OutputHandle = _bindlessRegistry.RegisterRwTexture(output),
			LinearSampler = _linearSampler,
			RenderSize = resources.FramebufferSize,
			Settings = settings,
			LookupTableHandle = gradingActive ? lookupTableResources!.ShaderResourceView : DescriptorHandle.Invalid,
			LookupTableContribution = gradingActive ? MathF.Min(colorGrading.Contribution, 1.0f) : 0.0f,
			LookupTableSize = lookupTable?.Size ?? 2,
			LookupTableDomainMin = lookupTable?.DomainMin ?? Vector3.Zero,
			LookupTableDomainMax = lookupTable?.DomainMax ?? Vector3.One
		};
	}

	private void Record(RenderGraphContext context, in TonemappingPassConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var index = GetModeIndex(config.Settings.Mode);
		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _kernels[index].Writer("BindlessHandles");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("inputHandle", config.InputHandle.Value);
		bindlessWriter.SetUInt("outputHandle", config.OutputHandle.Value);
		bindlessWriter.SetUInt("samplerHandle", config.LinearSampler.Value);
		bindlessWriter.SetUInt("lutHandle", config.LookupTableHandle.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var settingsWriter = _kernels[index].Writer("TonemappingSettings");
		settingsWriter.Clear();
		settingsWriter.SetUInt("renderSizeX", (uint)Math.Max(config.RenderSize.X, 1));
		settingsWriter.SetUInt("renderSizeY", (uint)Math.Max(config.RenderSize.Y, 1));
		settingsWriter.SetFloat("exposure", MathF.Max(config.Settings.Exposure, 0.0f));
		var lutSize = (float)Math.Max(config.LookupTableSize, 2);
		var domainExtent = Vector3.Max(config.LookupTableDomainMax - config.LookupTableDomainMin, new Vector3(1e-6f));
		settingsWriter.SetFloat("lutContribution", config.LookupTableContribution);
		settingsWriter.SetFloat("lutScale", (lutSize - 1.0f) / lutSize);
		settingsWriter.SetFloat("lutOffset", 0.5f / lutSize);
		settingsWriter.SetVector3("lutDomainMin", config.LookupTableDomainMin);
		settingsWriter.SetVector3("lutInverseDomainExtent", Vector3.One / domainExtent);
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

		var threadGroupSize = _kernels[index].ThreadGroupSize;
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(
			(uint)Math.Max(config.RenderSize.X, 1),
			(uint)Math.Max(config.RenderSize.Y, 1));
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private static int GetModeIndex(TonemappingMode mode)
	{
		var index = (int)mode;
		if (index < 0 || index >= ModeCount)
		{
			throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown tonemapping mode.");
		}

		return index;
	}
}
