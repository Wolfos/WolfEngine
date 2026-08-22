using Microsoft.AspNetCore.Components;
using WolfEngine.Rendering;

namespace WolfEngine.UI;

public enum UiSurfaceKind
{
	Screen,
	Texture
}

public sealed record UiSurfaceOptions
{
	public UiSurfaceKind Kind { get; init; } = UiSurfaceKind.Screen;
	public int Width { get; init; } = 512;
	public int Height { get; init; } = 512;
	public int Layer { get; init; }
	public string? Name { get; init; }
	public ColorRGBA ClearColor { get; init; } = new(0, 0, 0, 0);
}

public readonly record struct UiPerformanceSnapshot(
	long Revision,
	int NodeCount,
	int VertexCount,
	int IndexCount,
	int DrawCalls,
	double BuildMilliseconds,
	long ManagedBytesAllocated,
	bool LayoutRan);

public interface IGameplayUiSurface : IDisposable
{
	long Id { get; }
	Texture? Texture { get; }
	UiPerformanceSnapshot Performance { get; }
	void SetParameters(IReadOnlyDictionary<string, object?> parameters);
	void Invalidate();
}

public interface IGameplayUiHost
{
	IGameplayUiSurface Create<TComponent>(UiSurfaceOptions options, string? cssResourceName = null,
		IReadOnlyDictionary<string, object?>? initialParameters = null)
		where TComponent : IComponent;

	UiPerformanceSnapshot AggregatePerformance { get; }
}
