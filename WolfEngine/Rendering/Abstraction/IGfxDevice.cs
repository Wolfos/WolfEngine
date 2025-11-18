#nullable enable

namespace WolfEngine.Rendering.Abstraction;

/// <summary>
/// API-agnostic device interface used by the render graph to allocate resources and create command lists.
/// </summary>
public interface IGfxDevice
{
	/// <summary>
	/// Begins recording a graphics command list targeting the primary graphics queue.
	/// </summary>
	IGfxCommandList BeginGraphics();

	/// <summary>
	/// Begins recording a compute command list targeting the compute queue.
	/// </summary>
	IGfxCommandList BeginCompute();

	/// <summary>
	/// Submits a previously recorded command list to the appropriate GPU queue.
	/// </summary>
	void Submit(IGfxCommandList commandList);

	/// <summary>
	/// Allocates a new GPU texture from the supplied abstract descriptor.
	/// </summary>
	IGfxTexture CreateTexture(in TextureDescriptor descriptor);

	/// <summary>
	/// Allocates a new GPU buffer from the supplied descriptor.
	/// </summary>
	IGfxBuffer CreateBuffer(in BufferDescriptor descriptor);

	/// <summary>
	/// Retrieves an existing pipeline matching the key or creates one using the provided shader bytecodes.
	/// </summary>
	IGfxPipeline GetOrCreatePipeline(PipelineKey key, in ShaderBytecodeSet shaders);

	/// <summary>
	/// Global bindless descriptor table shared across passes and command lists.
	/// </summary>
	IGfxDescriptorTable GlobalTable { get; }

	/// <summary>
	/// Creates a backend-specific descriptor set builder.
	/// </summary>
	IGfxDescriptorSetBuilder CreateDescriptorSetBuilder();
}
