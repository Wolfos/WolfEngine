using WolfEngine.ECS;

namespace WolfEngine.Rendering.Features;

/// <summary>Where in a view's frame a render feature's passes run.</summary>
public enum RenderInjectionPoint
{
	/// <summary>
	/// After opaque lighting and transparent objects, before TAA or FSR3 resolves the frame. Scene colour is linear
	/// HDR at render resolution, so the temporal resolve smooths whatever the feature draws.
	/// </summary>
	BeforeTemporalResolve
}

/// <summary>
/// Game-defined GPU work that runs inside the engine's frame. Derive from <see cref="RenderFeature{TFrame}"/>
/// and register an instance with <see cref="IRenderFeatureRegistry"/>.
/// </summary>
public abstract class RenderFeature
{
	private volatile bool _retired;
	private int _faulted;

	private protected RenderFeature()
	{
	}

	public abstract RenderInjectionPoint InjectionPoint { get; }

	/// <summary>Set once the feature is unregistered. The render thread then stops recording it.</summary>
	internal bool IsRetired => _retired;

	/// <summary>Set when the feature's own code threw. It is skipped from then on rather than failing every frame.</summary>
	internal bool IsFaulted => Volatile.Read(ref _faulted) != 0;

	internal void MarkRetired() => _retired = true;

	/// <summary>Returns true for the one caller that faulted the feature, so a failure is reported once.</summary>
	internal bool TryMarkFaulted() => Interlocked.Exchange(ref _faulted, 1) == 0;

	internal abstract object CreateFrame();

	internal abstract void ExtractFrame(World world, object frame);

	internal abstract void RecordFrame(RenderFeatureContext context, object frame);

	internal void InitializeOnRenderThread(RenderFeatureResources resources) => Initialize(resources);

	/// <summary>
	/// Render thread, once, before the feature first records. Create the feature's textures here; the engine
	/// releases them when the feature is unregistered.
	/// </summary>
	protected virtual void Initialize(RenderFeatureResources resources)
	{
	}
}

/// <summary>
/// A render feature whose game-thread data reaches the render thread as a <typeparamref name="TFrame"/>.
/// </summary>
/// <remarks>
/// <see cref="Extract"/> runs on the game thread each time a frame is published, and <see cref="Record"/> on the
/// render thread for each view that renders the feature's world. They never see the same <typeparamref name="TFrame"/>
/// at the same time: frame objects are pooled per published frame and reused, so <see cref="Extract"/> must
/// overwrite every field, and nothing but the frame object may cross between the two methods.
/// </remarks>
public abstract class RenderFeature<TFrame> : RenderFeature where TFrame : class, new()
{
	internal sealed override object CreateFrame() => new TFrame();

	internal sealed override void ExtractFrame(World world, object frame) => Extract(world, (TFrame)frame);

	internal sealed override void RecordFrame(RenderFeatureContext context, object frame) => Record(context, (TFrame)frame);

	/// <summary>Game thread: copy what <see cref="Record"/> needs into <paramref name="frame"/>.</summary>
	protected abstract void Extract(World world, TFrame frame);

	/// <summary>Render thread: add this view's passes. Read only <paramref name="frame"/> and render-thread state.</summary>
	protected abstract void Record(RenderFeatureContext context, TFrame frame);
}
