using WolfEngine.Profiling;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.UI;

/// <summary>
/// Draws the editor UI into the window's image, in a submission of its own after the render graph's.
/// </summary>
/// <remarks>
/// The UI used to be the graph's last pass, which tied it to the scene: the UI could only be drawn on a frame that
/// also drew every view. Kept apart, the render thread can present a frame that runs no graph at all, and the UI
/// keeps updating while the scene is drawn less often, as when the editor limits the viewport's frame rate.
/// <para>
/// It depends on the graph only through the window target and the views' outputs. When a graph ran this frame,
/// its last pass left those in the states drawn here: the window target as a render target and every view's
/// output as a shader resource. When none ran, they are still in the states the last graph left them in.
/// </para>
/// </remarks>
internal sealed class UiCompositor
{
	private const string UiPassName = "UI";

	private readonly IImGuiRenderer _imGuiRenderer;

	public UiCompositor(IImGuiRenderer imGuiRenderer)
	{
		_imGuiRenderer = imGuiRenderer ?? throw new ArgumentNullException(nameof(imGuiRenderer));
	}

	/// <summary>
	/// Records and submits <paramref name="uiFrame"/> into <paramref name="windowTarget"/>. The frame's viewport
	/// sentinels must already be resolved to texture ids.
	/// </summary>
	/// <param name="clearTarget">
	/// False when a backbuffer view drew the scene into the window target this frame and the UI goes over it.
	/// </param>
	/// <param name="gpuFrameCapture">The GPU profiler's capture of this frame, or null when it is not profiled.</param>
	public void Compose(
		IGfxDevice device,
		UiFrameData uiFrame,
		IGfxTexture windowTarget,
		bool clearTarget,
		GpuProfileFrameCapture? gpuFrameCapture)
	{
		ArgumentNullException.ThrowIfNull(device);
		ArgumentNullException.ThrowIfNull(windowTarget);

		_imGuiRenderer.EnsureResources(device, uiFrame);

		var commandList = device.BeginGraphics();
		commandList.SetBindlessTable(device.GlobalTable);
		if (gpuFrameCapture is not null &&
		    (device as IGpuProfilerDevice)?.GpuProfilerBackend is IGpuProfilerCaptureBackend captureBackend)
		{
			captureBackend.Attach(commandList, gpuFrameCapture.AddPass(UiPassName));
		}

		commandList.BeginEvent(UiPassName);
		_imGuiRenderer.Record(commandList, uiFrame, windowTarget, clearTarget);
		commandList.EndEvent();
		device.Submit(commandList);
	}

	public void InvalidateShaderPipeline() => _imGuiRenderer.InvalidateShaderPipeline();
}
