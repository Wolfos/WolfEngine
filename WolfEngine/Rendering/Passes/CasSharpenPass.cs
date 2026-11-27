using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

public sealed class CasSharpenPass
{
	private readonly ComputeKernel _kernel;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly Action<RenderGraphContext> _execute;

	public CasSharpenPass(ShaderSource shaderSource, BindlessResourceRegistry bindlessRegistry)
	{
		ArgumentNullException.ThrowIfNull(shaderSource);

		_kernel = new ComputeKernel(shaderSource, EngineShaderPrograms.CasSharpen, "CasSharpenCS");
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_execute = Execute;
	}

	/// <summary>Adds the CAS Sharpen node: tonemapped scene colour in, sharpened display colour out.</summary>
	internal void AddNodes(RenderGraph graph, in RenderViewResources resources)
	{
		graph.AddPass("CAS Sharpen", PassKind.Compute)
			.ReadTexture(resources.TonemappedLinearSceneColor, ResourceState.ShaderResource)
			.WriteTexture(resources.DisplayLinearSceneColor, ResourceState.UnorderedAccess)
			.SetExecute(_execute);
	}

	private void Execute(RenderGraphContext context)
	{
		var config = BuildConfig(context, context.ViewState.FrameResources, context.Device);
		Record(context, in config);
	}

	private CasSharpenPassConfig BuildConfig(
		RenderGraphContext context,
		RenderViewResources resources,
		IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = _kernel.GetPipeline(device);
		_bindlessRegistry.EnsureInitialized(device);

		var input = context.GetTexture(resources.TonemappedLinearSceneColor);
		var output = context.GetTexture(resources.DisplayLinearSceneColor);
		return new CasSharpenPassConfig
		{
			Pipeline = pipeline,
			InputHandle = _bindlessRegistry.GetTextureHandle(input),
			OutputHandle = _bindlessRegistry.RegisterRwTexture(output),
			RenderSize = resources.FramebufferSize,
			SharpenEnabled = resources.Config.AntiAliasing.UsesCasSharpening,
			Sharpness = Math.Clamp(resources.Config.AntiAliasing.Taa.CasSharpness, 0.0f, 1.0f)
		};
	}

	private void Record(RenderGraphContext context, in CasSharpenPassConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _kernel.Writer("BindlessHandles");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("inputHandle", config.InputHandle.Value);
		bindlessWriter.SetUInt("outputHandle", config.OutputHandle.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var settingsWriter = _kernel.Writer("CasSettings");
		settingsWriter.Clear();
		settingsWriter.SetUInt("renderSizeX", (uint)Math.Max(config.RenderSize.X, 1));
		settingsWriter.SetUInt("renderSizeY", (uint)Math.Max(config.RenderSize.Y, 1));
		settingsWriter.SetUInt("sharpenEnabled", config.SharpenEnabled ? 1u : 0u);
		settingsWriter.SetUInt("casConst0X", BitConverter.SingleToUInt32Bits(1.0f));
		settingsWriter.SetUInt("casConst0Y", BitConverter.SingleToUInt32Bits(1.0f));
		settingsWriter.SetUInt("casConst0Z", BitConverter.SingleToUInt32Bits(0.0f));
		settingsWriter.SetUInt("casConst0W", BitConverter.SingleToUInt32Bits(0.0f));

		var peak = -1.0f / Lerp(8.0f, 5.0f, config.Sharpness);
		settingsWriter.SetUInt("casConst1X", BitConverter.SingleToUInt32Bits(peak));
		settingsWriter.SetUInt("casConst1Y", BitConverter.SingleToUInt32Bits(peak));
		settingsWriter.SetUInt("casConst1Z", BitConverter.SingleToUInt32Bits(8.0f));
		settingsWriter.SetUInt("casConst1W", 0u);
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

		var threadGroupSize = _kernel.ThreadGroupSize;
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(
			(uint)Math.Max(config.RenderSize.X, 1),
			(uint)Math.Max(config.RenderSize.Y, 1));
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private static float Lerp(float start, float end, float amount)
	{
		return start + ((end - start) * amount);
	}
}
