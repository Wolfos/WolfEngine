using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering;

/// <summary>
/// Writes draw records into a backend's indirect command buffers. Deciding when a record is stale is not part of
/// this port: an encoder writes what it is given and keeps no memory of what it wrote before.
/// </summary>
public interface IDrawRecordEncoder
{
	/// <summary>Creates one page of draw records, which the caller owns and disposes.</summary>
	IGfxIndirectCommandBuffer CreatePage(IGfxDevice device, uint commandCapacity, string name)
	{
		ArgumentNullException.ThrowIfNull(device);
		return device.CreateIndirectCommandBuffer(new IndirectCommandBufferDescriptor(
			PassKind.Graphics,
			commandCapacity,
			name: name));
	}

	/// <summary>
	/// Makes current whatever a record executed by <paramref name="pipeline"/> captures from outside the encoded
	/// resources, such as bindless argument buffers. Called once per drawn lane before a set is checked for
	/// staleness and encoded, so anything this replaces is seen by that check rather than after it.
	/// </summary>
	void PrepareForPipeline(IGfxDevice device, IGfxPipeline pipeline);

	void ResetCommand(IGfxIndirectCommandBuffer commandBuffer, uint commandIndex);

	/// <returns>False when the resources cannot be encoded on this backend; the caller then resets the record.</returns>
	bool TryEncodeIndexedDrawCommand(
		IGfxDevice device,
		IGfxIndirectCommandBuffer commandBuffer,
		uint commandIndex,
		uint drawArgsCommandIndex,
		Mesh mesh,
		in SharedDrawIndirectEncodeResources resources,
		GraphicsPassBindingSet passBindings,
		in SharedDrawPerDrawBindings perDrawBindings);
}
