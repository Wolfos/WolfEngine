using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

/// <summary>
/// Runtime data handed to a pass while it is executing.
/// Responsible for exposing resolved GPU resources and frame-level services.
/// </summary>
public sealed class RenderGraphContext
{
	// Assigned by the render graph once per pass, after the context is constructed.
	private IGfxCommandList? _commandList;
	private SceneDrawData? _sceneData;
	private GpuDrawDatabase? _gpuDrawDatabase;
	private FrameSnapshot? _frameSnapshot;
	private RenderViewSnapshot? _viewSnapshot;

	internal RenderGraphContext(RenderGraphResourceRegistry resourceRegistry, string passName)
	{
		ResourceRegistry = resourceRegistry ?? throw new ArgumentNullException(nameof(resourceRegistry));
		PassName = passName ?? throw new ArgumentNullException(nameof(passName));
	}

	public string PassName { get; }

	public RenderGraphResourceRegistry ResourceRegistry { get; }
	
	public IGfxCommandList CommandList
	{
		get => _commandList ?? throw new InvalidOperationException("Command list has not been set for this pass.");
		internal set => _commandList = value;
	}
	
	public SceneDrawData SceneData
	{
		get => _sceneData ?? throw new InvalidOperationException("SceneData has not been set for this pass.");
		internal set => _sceneData = value;
	}

	public GpuDrawDatabase GpuDrawDatabase
	{
		get => _gpuDrawDatabase ?? throw new InvalidOperationException("GpuDrawDatabase has not been set for this pass.");
		internal set => _gpuDrawDatabase = value;
	}

	public FrameSnapshot FrameSnapshot
	{
		get => _frameSnapshot ?? throw new InvalidOperationException("FrameSnapshot has not been set for this pass.");
		internal set => _frameSnapshot = value;
	}

	/// <summary>
	/// The view this pass runs for. A pass given no view snapshot — a shared pass on a single-view frame, or a
	/// test — belongs to the primary view.
	/// </summary>
	public RenderViewId View => _viewSnapshot?.View ?? RenderViewId.Primary;

	/// <summary>The scene packets and camera state for the view this pass is recording.</summary>
	public RenderViewSnapshot ViewSnapshot
	{
		get => _viewSnapshot ?? throw new InvalidOperationException("ViewSnapshot has not been set for this pass.");
		internal set => _viewSnapshot = value;
	}

	public IGfxTexture GetTexture(RenderGraphResourceHandle handle)
	{
		return ResourceRegistry.GetTexture(handle);
	}
	
	public IGfxBuffer GetBuffer(RenderGraphResourceHandle handle)
	{
		return ResourceRegistry.GetBuffer(handle);
	}
}
