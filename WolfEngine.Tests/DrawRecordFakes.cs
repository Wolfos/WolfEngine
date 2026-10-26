using Moq;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

/// <summary>Test adapters for the ports of <see cref="IndirectDrawRecords"/>.</summary>
internal static class DrawRecordFakes
{
	public static IndirectDrawRecords CreateRecords() =>
		new(new FakeDrawRecordEncoder(), new FakeLiveDrawSource(), new FakeStalenessInputSource());

	public static ShadowMapPass CreateShadowMapPass()
	{
		var shaderCompiler = new ShaderCompiler();
		return new ShadowMapPass(shaderCompiler, CreateRecords(), new GpuDrawResources(shaderCompiler));
	}
}

/// <summary>A page that remembers which draw each of its records was last encoded for.</summary>
internal sealed class FakeRecordPage : IGfxIndirectCommandBuffer
{
	public FakeRecordPage(string name, uint commandCapacity)
	{
		Name = name;
		Descriptor = new IndirectCommandBufferDescriptor(PassKind.Graphics, commandCapacity, name: name);
	}

	public string? Name { get; }

	public IndirectCommandBufferDescriptor Descriptor { get; }

	/// <summary>Draw index per encoded record, keyed by the record's index within the page.</summary>
	public Dictionary<uint, uint> Records { get; } = new();
}

internal sealed class FakeDrawRecordEncoder : IDrawRecordEncoder
{
	public List<FakeRecordPage> Pages { get; } = new();

	/// <summary>The draw index of every record encoded since the last <see cref="ClearLog"/>, in order.</summary>
	public List<uint> EncodedDraws { get; } = new();

	public IGfxIndirectCommandBuffer CreatePage(IGfxDevice device, uint commandCapacity, string name)
	{
		var page = new FakeRecordPage(name, commandCapacity);
		Pages.Add(page);
		return page;
	}

	public void PrepareForPipeline(IGfxDevice device, IGfxPipeline pipeline)
	{
	}

	public void ResetCommand(IGfxIndirectCommandBuffer commandBuffer, uint commandIndex)
	{
		((FakeRecordPage)commandBuffer).Records.Remove(commandIndex);
	}

	public bool TryEncodeIndexedDrawCommand(
		IGfxDevice device,
		IGfxIndirectCommandBuffer commandBuffer,
		uint commandIndex,
		uint drawArgsCommandIndex,
		Mesh mesh,
		in SharedDrawIndirectEncodeResources resources,
		GraphicsPassBindingSet passBindings,
		in SharedDrawPerDrawBindings perDrawBindings)
	{
		((FakeRecordPage)commandBuffer).Records[commandIndex] = drawArgsCommandIndex;
		EncodedDraws.Add(drawArgsCommandIndex);
		return true;
	}

	public void ClearLog()
	{
		EncodedDraws.Clear();
	}
}

internal sealed class FakeLiveDrawSource : ILiveDrawSource
{
	public List<LiveDraw> Draws { get; } = new();

	public HashSet<uint> CurrentHandles { get; } = new();

	public List<Mesh> MadeResident { get; } = new();

	public bool IsCurrent(in GpuDrawHandle drawHandle) => CurrentHandles.Contains(drawHandle.Value);

	public void CollectResidentDraws(List<LiveDraw> destination)
	{
		destination.Clear();
		destination.AddRange(Draws);
	}

	public void EnsureResident(Mesh mesh)
	{
		MadeResident.Add(mesh);
	}
}

internal sealed class FakeStalenessInputSource : IDrawRecordStalenessInputSource
{
	public DrawRecordStalenessInputs Inputs { get; set; } = new(1, 1, 1);

	public DrawRecordStalenessInputs Read() => Inputs;
}

internal sealed class FakeLaneBindingProvider : ILaneBindingProvider
{
	private readonly IGfxPipeline _pipeline = Mock.Of<IGfxPipeline>();

	public ulong Generation { get; set; } = 1;

	public bool IsLaneDrawn(GpuDrawExecutionLaneDefinition lane) => true;

	public SharedDrawGraphicsBufferBindings? GetBufferBindings(GpuDrawExecutionLaneDefinition lane) =>
		new SharedDrawGraphicsBufferBindings(0, 10, 11, 12, 13);

	public GraphicsPassBindingSet? GetPassBindings(GpuDrawExecutionLaneDefinition lane) =>
		new GraphicsPassBindingSet(Array.Empty<GraphicsPassBinding>());

	public IGfxPipeline? GetPipeline(GpuDrawExecutionLaneDefinition lane) => _pipeline;
}
