using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>Clears FSR3 UAV resources that upstream requires to be initialized explicitly.</summary>
public sealed class Fsr3ClearPass
{
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly ComputeKernel _clearFloat;
	private readonly ComputeKernel _clearUint;

	public Fsr3ClearPass(ShaderSource shaderSource, BindlessResourceRegistry bindlessRegistry)
	{
		ArgumentNullException.ThrowIfNull(shaderSource);

		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_clearFloat = new ComputeKernel(shaderSource, EngineShaderPrograms.Fsr3Clear, "Fsr3ClearFloatCS");
		_clearUint = new ComputeKernel(shaderSource, EngineShaderPrograms.Fsr3Clear, "Fsr3ClearUintCS");
	}

	public void Record(RenderGraphContext context, IGfxDevice device, RenderGraphResourceHandle output,
		Int2 size, uint clearValueBits, bool uintTexture)
	{
		var kernel = uintTexture ? _clearUint : _clearFloat;
		var commandList = context.CommandList;
		_bindlessRegistry.EnsureInitialized(device);
		kernel.Bind(commandList, device);

		var texture = context.GetTexture(output);
		var handle = _bindlessRegistry.RegisterRwTexture(texture);
		var handles = kernel.Writer("Fsr3ClearHandles");
		handles.Clear();
		handles.SetUInt("outputHandle", handle.Value);
		commandList.SetComputeConstants(handles.RegisterIndex, handles.AsBytes());

		var constants = kernel.Writer("Fsr3ClearConstants");
		constants.Clear();
		constants.SetUInt("clearWidth", (uint)Math.Max(size.X, 1));
		constants.SetUInt("clearHeight", (uint)Math.Max(size.Y, 1));
		constants.SetUInt("clearValueBits", clearValueBits);
		commandList.SetComputeConstants(constants.RegisterIndex, constants.AsBytes());

		kernel.Dispatch(commandList, size.X, size.Y);
	}
}
