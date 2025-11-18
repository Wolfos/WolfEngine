#nullable enable

namespace WolfEngine.Rendering.Abstraction;

/// <summary>
/// API-neutral descriptor set used to bind SRVs/UAVs/CBVs and samplers for a shader stage.
/// </summary>
public interface IGfxDescriptorSet
{
}

/// <summary>
/// Builds descriptor sets for a specific backend.
/// </summary>
public interface IGfxDescriptorSetBuilder
{
	/// <summary>
	/// Adds a shader resource view binding at the given slot.
	/// </summary>
	void AddShaderResource(uint slot, IGfxResource resource);

	/// <summary>
	/// Adds an unordered access view binding at the given slot.
	/// </summary>
	void AddUnorderedAccess(uint slot, IGfxResource resource);

	/// <summary>
	/// Adds a constant buffer view binding at the given slot.
	/// </summary>
	void AddConstantBuffer(uint slot, IGfxBuffer buffer);

	/// <summary>
	/// Finalises the descriptor set.
	/// </summary>
	IGfxDescriptorSet Build();
}
