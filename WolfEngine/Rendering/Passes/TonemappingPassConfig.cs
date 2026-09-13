using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct TonemappingPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }
	public required DescriptorHandle InputHandle { get; init; }
	public required DescriptorHandle OutputHandle { get; init; }
	public required DescriptorHandle LinearSampler { get; init; }
	public required Int2 RenderSize { get; init; }
	public required TonemappingConfig Settings { get; init; }

	/// <summary>Volume SRV of the grading LUT, or invalid when grading is inactive this frame.</summary>
	public required DescriptorHandle LookupTableHandle { get; init; }

	/// <summary>Zero whenever <see cref="LookupTableHandle"/> is invalid.</summary>
	public required float LookupTableContribution { get; init; }
	public required int LookupTableSize { get; init; }
	public required Vector3 LookupTableDomainMin { get; init; }
	public required Vector3 LookupTableDomainMax { get; init; }
}
