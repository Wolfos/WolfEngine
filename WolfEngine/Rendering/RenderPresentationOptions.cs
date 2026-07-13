namespace WolfEngine.Rendering;

public enum RenderOutputMode
{
	EditorViewport,
	FullWindow
}

public sealed class RenderPresentationOptions
{
	public RenderOutputMode OutputMode { get; init; } = RenderOutputMode.EditorViewport;
}
