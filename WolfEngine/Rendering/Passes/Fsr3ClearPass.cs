using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>Clears FSR3 UAV resources that upstream requires to be initialized explicitly.</summary>
public sealed class Fsr3ClearPass
{
	private readonly IShaderProvider _shaderProvider;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly Dictionary<bool, ClearPipeline> _pipelines = new();

	private sealed record ClearPipeline(IGfxPipeline Pipeline, ShaderPropertyWriter Handles,
		ShaderPropertyWriter Constants, GraphicsBackendKind Backend);

	public Fsr3ClearPass(IShaderProvider shaderProvider, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderProvider = shaderProvider;
		_bindlessRegistry = bindlessRegistry;
	}

	public void Record(RenderGraphContext context, IGfxDevice device, RenderGraphResourceHandle output,
		Int2 size, uint clearValueBits, bool uintTexture)
	{
		var compiled = EnsurePipeline(device, uintTexture);
		var texture = context.GetTexture(output);
		var handle = _bindlessRegistry.RegisterRwTexture(texture);
		compiled.Handles.Clear();
		compiled.Handles.SetUInt("outputHandle", handle.Value);
		compiled.Constants.Clear();
		compiled.Constants.SetUInt("clearWidth", (uint)Math.Max(size.X, 1));
		compiled.Constants.SetUInt("clearHeight", (uint)Math.Max(size.Y, 1));
		compiled.Constants.SetUInt("clearValueBits", clearValueBits);
		context.CommandList.BindPipeline(compiled.Pipeline);
		context.CommandList.SetComputeConstants(compiled.Handles.RegisterIndex, compiled.Handles.AsBytes());
		context.CommandList.SetComputeConstants(compiled.Constants.RegisterIndex, compiled.Constants.AsBytes());
		context.CommandList.Dispatch((uint)Math.Max((size.X + 7) / 8, 1),
			(uint)Math.Max((size.Y + 7) / 8, 1), 1);
	}

	private ClearPipeline EnsurePipeline(IGfxDevice device, bool uintTexture)
	{
		if (_pipelines.TryGetValue(uintTexture, out var result))
		{
			if (result.Backend != device.BackendKind) throw new InvalidOperationException("FSR3 clear backend changed.");
			return result;
		}
		_bindlessRegistry.EnsureInitialized(device);
		var entry = uintTexture ? "Fsr3ClearUintCS" : "Fsr3ClearFloatCS";
		var shader = _shaderProvider.GetComputeShaderWithReflection(EngineShaderPrograms.Fsr3Clear, entry, device.BackendKind);
		var key = new PipelineKey(PassKind.Compute, vertexEntryPoint: null, pixelEntryPoint: null,
			computeEntryPoint: entry, renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown), renderState: default,
			shaderVariant: $"fsr3_clear.compute.slang:{entry}");
		var pipeline = device.GetOrCreatePipeline(key,
			new ShaderBytecodeSet(compute: shader.Bytecode, computeThreadGroupSize: shader.ThreadGroupSize));
		result = new ClearPipeline(pipeline,
			new ShaderPropertyWriter(shader.ReflectionLayout.GetConstantBuffer("Fsr3ClearHandles")),
			new ShaderPropertyWriter(shader.ReflectionLayout.GetConstantBuffer("Fsr3ClearConstants")), device.BackendKind);
		_pipelines.Add(uintTexture, result);
		return result;
	}
}
