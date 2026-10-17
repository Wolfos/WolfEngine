namespace WolfEngine.Rendering;

/// <summary>CPU-readable pixels from a completed renderer frame.</summary>
public sealed class FrameCapture
{
	public FrameCapture(int width, int height, byte[] rgba8)
	{
		Width = width;
		Height = height;
		Rgba8 = rgba8;
	}

	public int Width { get; }
	public int Height { get; }
	public byte[] Rgba8 { get; }
}

/// <summary>Which composited target a frame capture reads back.</summary>
public enum FrameCaptureTarget
{
	/// <summary>The rendered scene on its own, without the editor's UI composited over it.</summary>
	SceneColor,

	/// <summary>The presented window, including the editor's ImGui panels, docking and menus.</summary>
	Window
}

public interface IFrameCaptureSource
{
	Task<FrameCapture> CaptureNextFrameAsync(CancellationToken cancellationToken = default) =>
		CaptureNextFrameAsync(FrameCaptureTarget.SceneColor, cancellationToken);

	Task<FrameCapture> CaptureNextFrameAsync(FrameCaptureTarget target, CancellationToken cancellationToken = default) =>
		Task.FromException<FrameCapture>(new PlatformNotSupportedException("Frame capture is not supported by this renderer."));
	void RequestShutdown() { }
}
