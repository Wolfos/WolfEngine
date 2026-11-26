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
	private RenderViewState? _viewState;
	private IGfxDevice? _device;

	internal RenderGraphContext(RenderGraphResourceRegistry resourceRegistry, string passName)
	{
		ResourceRegistry = resourceRegistry ?? throw new ArgumentNullException(nameof(resourceRegistry));
		PassName = passName ?? throw new ArgumentNullException(nameof(passName));
	}

	internal RenderGraphContext(RenderGraphResourceRegistry resourceRegistry, RenderGraphPass node)
		: this(resourceRegistry, node.Name)
	{
		Node = node;
	}

	/// <summary>
	/// The graph node being executed, whose declaration <see cref="GetTexture"/> and <see cref="GetBuffer"/> check
	/// in Debug builds. Null for a context built outside the graph, such as in a test.
	/// </summary>
	internal RenderGraphPass? Node { get; }

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

	/// <summary>
	/// The state of the view this node runs for: its frame resources, histories and sky. A shared node runs
	/// against the bound view. Passes read per-view state here when their nodes execute, because one pass
	/// records nodes for several views into the same graph.
	/// </summary>
	internal RenderViewState ViewState
	{
		get => _viewState ?? throw new InvalidOperationException("ViewState has not been set for this pass.");
		set => _viewState = value;
	}

	/// <summary>The graphics device the frame is recorded on.</summary>
	internal IGfxDevice Device
	{
		get => _device ?? throw new InvalidOperationException("Device has not been set for this pass.");
		set => _device = value;
	}

	/// <summary>What every view in the frame shares: the final presentation target and the BRDF LUT.</summary>
	internal RenderFrameSharedResources SharedResources { get; set; }

	/// <summary>
	/// Resolves a texture this graph node declared as a read or a write. Debug builds throw for a handle the node
	/// did not declare, because the graph derives barriers and transient memory sharing from declarations alone.
	/// </summary>
	public IGfxTexture GetTexture(RenderGraphResourceHandle handle)
	{
		ThrowIfUndeclared(handle);

		return ResourceRegistry.GetTexture(handle);
	}

	/// <summary>
	/// Resolves a buffer this graph node declared as a read or a write. Debug builds throw for a handle the node
	/// did not declare.
	/// </summary>
	public IGfxBuffer GetBuffer(RenderGraphResourceHandle handle)
	{
		ThrowIfUndeclared(handle);

		return ResourceRegistry.GetBuffer(handle);
	}

	[System.Diagnostics.Conditional("DEBUG")]
	private void ThrowIfUndeclared(RenderGraphResourceHandle handle)
	{
		if (Node is null) return;

		if (Node.Declares(handle) == false)
		{
			throw new InvalidOperationException(
				$"Graph node '{PassName}' fetched a resource it did not declare ({ResourceRegistry.Describe(handle)}). " +
				"Declare every resource a node binds as a read or a write when the node is added: the graph places " +
				"barriers and shares transient memory from declarations alone.");
		}
	}
}
