using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering;

/// <summary>
/// Writes draw records into a backend's indirect command buffers. Deciding when a record is stale is not part of
/// this port: an encoder writes what it is given and keeps no memory of what it wrote before.
/// </summary>
public interface IDrawRecordEncoder
{
	void ResetCommand(IGfxIndirectCommandBuffer commandBuffer, uint commandIndex);

	/// <param name="pipeline">
	/// The pipeline that will execute the record. A backend whose records capture its bindless tables uses it to
	/// make those tables current before they are captured.
	/// </param>
	/// <returns>False when the resources cannot be encoded on this backend; the caller then resets the record.</returns>
	bool TryEncodeIndexedDrawCommand(
		IGfxDevice device,
		IGfxPipeline pipeline,
		IGfxIndirectCommandBuffer commandBuffer,
		uint commandIndex,
		uint drawArgsCommandIndex,
		Mesh mesh,
		in SharedDrawIndirectEncodeResources resources,
		GraphicsPassBindingSet passBindings,
		in SharedDrawPerDrawBindings perDrawBindings);
}
