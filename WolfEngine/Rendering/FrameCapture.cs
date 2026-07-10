using System.Threading;
using System.Threading.Tasks;

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

public interface IFrameCaptureSource
{
	Task<FrameCapture> CaptureNextFrameAsync(CancellationToken cancellationToken = default) =>
		Task.FromException<FrameCapture>(new PlatformNotSupportedException("Frame capture is not supported by this renderer."));
	void RequestShutdown() { }
}
