using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using WolfEngine.AssetPipeline;
using WolfEngine.Mathematics;
using WolfEngine.Input;
using WolfEngine.Profiling;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.UI;

internal sealed class GameplayUiSurface : IGameplayUiSurface
{
	private readonly object _rebuildSync = new();
	private readonly GameplayUiHost _host;
	private readonly RazorTreeRenderer _renderer;
	private readonly int _rootComponentId;
	private readonly CssStyleSheet _styleSheet;
	private readonly IUiLayoutEngine _layout;
	private readonly UiFrameBuilder _frames;
	private readonly UiTextService _text;
	private readonly string _profilerName;
	private readonly Dictionary<string, object?> _parameters;
	private bool _disposed;
	private long _revision;
	private UiNode? _root;
	private int _layoutWidth;
	private int _layoutHeight;
	private float _layoutScale = 1.0f;
	private readonly List<ComputedStyle> _previousInteractionStyles = [];
	internal UiNode? Root => _root;
	internal float LogicalWidth => _layoutWidth;
	internal float LogicalHeight => _layoutHeight;
	internal UiPointerController Pointer { get; }

	public GameplayUiSurface(
		GameplayUiHost host,
		long id,
		Type componentType,
		UiSurfaceOptions options,
		string? css,
		IReadOnlyDictionary<string, object?> initialParameters,
		IServiceProvider services)
	{
		_host = host;
		Id = id;
		Options = options;
		_profilerName = $"Gameplay UI.Rebuild [{options.Name ?? id.ToString()}]";
		_parameters = new Dictionary<string, object?>(initialParameters);
		_renderer = new RazorTreeRenderer(services, host.Dispatcher);
		Pointer = new UiPointerController(this);
		_rootComponentId = _renderer.AttachRoot(componentType);
		_styleSheet = CssStyleSheet.Parse(css ?? string.Empty);
		_text = new UiTextService(host.Fonts);
		_text.SetFonts(_styleSheet.FontSources);
		_layout = new YogaLayoutEngine(_text);
		_frames = new UiFrameBuilder(_text);
		if (options.Kind == UiSurfaceKind.Texture)
		{
			Texture = Texture.CreateRenderTarget(
				options.Name ?? $"Gameplay UI Surface {id}",
				Math.Max(1, options.Width),
				Math.Max(1, options.Height),
				format: TextureFormat.Bgra8Unorm);
		}
		Rebuild();
	}

	public long Id { get; }
	public UiSurfaceOptions Options { get; }
	public Texture? Texture { get; }
	public UiPerformanceSnapshot Performance { get; private set; }
	internal UiFrameData Frame { get; private set; } = UiFrameData.Empty;
	internal long FrameRevision { get; private set; }

	public void SetParameters(IReadOnlyDictionary<string, object?> parameters)
	{
		_host.Dispatcher.Bind();
		ArgumentNullException.ThrowIfNull(parameters);
		lock (_rebuildSync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			CopyParameters(parameters);
			RebuildCore();
		}
	}

	public void Invalidate()
	{
		_host.Dispatcher.Bind();
		lock (_rebuildSync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			RebuildCore();
		}
	}

	internal void ResizeAndRebuild()
	{
		lock (_rebuildSync)
		{
			if (!_disposed && Options.Kind == UiSurfaceKind.Screen) RebuildCore();
		}
	}


	private void Rebuild()
	{
		lock (_rebuildSync) RebuildCore();
	}

	private void CopyParameters(IReadOnlyDictionary<string, object?> parameters)
	{
		_parameters.Clear();
		if (parameters is Dictionary<string, object?> dictionary)
		{
			foreach (var pair in dictionary) _parameters[pair.Key] = pair.Value;
			return;
		}

		foreach (var pair in parameters) _parameters[pair.Key] = pair.Value;
	}

	internal void DispatchMouse(ulong handler, MouseEventArgs args)
	{
		_renderer.DispatchMouse(handler, args); FlushComponentRender();
	}
	internal void FlushComponentRender()
	{
		if (!_disposed && _renderer.DisplayChanged) RebuildCore(false);
	}
	internal void RefreshInteraction()
	{
		if (!_disposed && _root is not null) RebuildCore(false, interactionOnly: true);
	}
	private void RebuildCore(bool renderComponent = true, bool interactionOnly = false)
	{
		using (FrameProfiler.Instance.Measure(_profilerName))
		{
			var isScreen = Options.Kind == UiSurfaceKind.Screen;
			var scale = isScreen ? _host.DisplayScale : 1.0f;
			var outputWidth = isScreen ? Math.Max(1, _host.ViewportSize.X) : Math.Max(1, Options.Width);
			var outputHeight = isScreen ? Math.Max(1, _host.ViewportSize.Y) : Math.Max(1, Options.Height);
			var width = Math.Max(1, (int)MathF.Round(outputWidth / scale));
			var height = Math.Max(1, (int)MathF.Round(outputHeight / scale));
			var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
			var timer = Stopwatch.StartNew();
			using (FrameProfiler.Instance.Measure("Gameplay UI.Razor Render"))
			{
				if (renderComponent) _renderer.Render(_rootComponentId, _parameters);
			}

			UiNode updatedRoot;
			using (FrameProfiler.Instance.Measure("Gameplay UI.Build Tree"))
			{
				if (interactionOnly)
				{
					_previousInteractionStyles.Clear();
					CaptureStyles(_root!);
					updatedRoot = _root!;
				}
				else updatedRoot = _renderer.BuildTree(_rootComponentId);
				Pointer.ApplyState(updatedRoot);
			}

			using (FrameProfiler.Instance.Measure("Gameplay UI.Apply CSS"))
			{
				_styleSheet.Apply(updatedRoot, width, height);
			}

			UiTreeChanges changes;
			using (FrameProfiler.Instance.Measure("Gameplay UI.Reconcile"))
			{
				var styleIndex = 0;
				changes = interactionOnly ? CompareStyles(updatedRoot, ref styleIndex) : _root is null
					? UiTreeChanges.Rebuild
					: UiTreeReconciler.Reconcile(_root, updatedRoot);
			}

			var topologyChanged = !changes.CanRetain;
			if (topologyChanged)
			{
				var previousRoot = _root;
				_root = updatedRoot;
				_renderer.RecycleTree(previousRoot);
			}
			else if (!interactionOnly)
			{
				_renderer.RecycleTree(updatedRoot);
			}

			var fontsChanged = _text.SetFonts(_styleSheet.FontSources);
			var fullLayoutRequired = topologyChanged || _layoutWidth != width || _layoutHeight != height ||
			                         _layoutScale.Equals(scale) == false || changes.LayoutChanged || fontsChanged;
			var layoutRan = fullLayoutRequired || changes.IntrinsicSizeChanged;
			if (layoutRan)
			{
				using (FrameProfiler.Instance.Measure("Gameplay UI.Yoga Layout"))
				{
					_layout.Layout(_root!, width, height, fullLayoutRequired);
				}
				_layoutWidth = width;
				_layoutHeight = height;
				_layoutScale = scale;
			}
			var geometryChanged = layoutRan || changes.VisualChanged;
			if (geometryChanged)
			{
				using (FrameProfiler.Instance.Measure("Gameplay UI.Build Geometry"))
				{
					var nextFrame = _frames.Build(_root!, outputWidth, outputHeight, scale);
					// Publish/retain and replacement/release share a lock: pooled geometry
					// must not be returned while another surface publishes this one.
					lock (_host.FrameSync)
					{
						var previousFrame = Frame;
						Frame = nextFrame;
						FrameRevision++;
						previousFrame.Release();
					}
				}
			}
			timer.Stop();
			_revision++;
			using (FrameProfiler.Instance.Measure("Gameplay UI.Collect Metrics"))
			{
				Performance = new UiPerformanceSnapshot(
					_revision,
					_root!.CountNodes(),
					Frame.VertexCount,
					Frame.IndexCount,
					Frame.CommandCount,
					timer.Elapsed.TotalMilliseconds,
					GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
					LayoutRan: layoutRan);
			}
			if (geometryChanged) _host.Publish();
		}
	}

	private void CaptureStyles(UiNode node)
	{
		_previousInteractionStyles.Add(node.Style);
		foreach (var child in node.Children) CaptureStyles(child);
	}
	private UiTreeChanges CompareStyles(UiNode node, ref int index)
	{
		var previous = _previousInteractionStyles[index++];
		var layout = !UiTreeReconciler.LayoutStyleEquals(previous, node.Style);
		var paint = previous != node.Style;
		foreach (var child in node.Children)
		{
			var changes = CompareStyles(child, ref index); layout |= changes.LayoutChanged; paint |= changes.VisualChanged;
		}
		return new(true, layout, false, paint);
	}

	public void Dispose()
	{
		lock (_rebuildSync)
		{
			if (_disposed) return;
			_disposed = true;
			Pointer.Cancel();
#pragma warning disable BL0006
			_renderer.Dispose();
#pragma warning restore BL0006
			_layout.Dispose();
			_renderer.RecycleTree(_root);
			_root = null;
			_host.Remove(this);
			Frame.Release();
			Frame = UiFrameData.Empty;
		}
	}
}

public sealed class GameplayUiHost : IGameplayUiHost, IGameplayUiFrameProvider, IPointerInputRouter, IDisposable
{
	private readonly IServiceProvider _services;
	private readonly object _sync = new();
	internal object FrameSync => _sync;
	private readonly List<GameplayUiSurface> _surfaces = [];
	private readonly ConcurrentQueue<GameplayUiRenderFrame> _pendingFrames = new();
	private long _nextSurfaceId;
	private Int2 _viewportSize = new(1280, 720);
	private float _displayScale = 1.0f;
	private Int2 _requestedViewportSize = new(1280, 720);
	private float _requestedScale = 1;
	private bool _resizePending;
	internal UiDispatcher Dispatcher { get; } = new();
	private GameplayUiSurface? _inputSurface;
	private PointerInputContext _inputContext;

	private const float MinDisplayScale = 0.25f;
	private const float MaxDisplayScale = 8.0f;

	internal UiFontCatalog Fonts { get; }
	public GameplayUiHost(IServiceProvider services)
	{
		_services = services;
		Fonts = new UiFontCatalog(services.GetService<IFontContentProvider>());
	}

	/// <summary>Screen render target size, in physical pixels.</summary>
	internal Int2 ViewportSize
	{
		get { lock (_sync) return _viewportSize; }
	}

	/// <summary>Physical pixels per logical pixel. Screen surfaces lay out in logical pixels and scale on output.</summary>
	internal float DisplayScale
	{
		get { lock (_sync) return _displayScale; }
	}

	public UiPerformanceSnapshot AggregatePerformance
	{
		get
		{
			lock (_sync)
			{
				return new UiPerformanceSnapshot(
					_surfaces.Count == 0 ? 0 : _surfaces.Max(x => x.Performance.Revision),
					_surfaces.Sum(x => x.Performance.NodeCount),
					_surfaces.Sum(x => x.Performance.VertexCount),
					_surfaces.Sum(x => x.Performance.IndexCount),
					_surfaces.Sum(x => x.Performance.DrawCalls),
					_surfaces.Sum(x => x.Performance.BuildMilliseconds),
					_surfaces.Sum(x => x.Performance.ManagedBytesAllocated),
					_surfaces.Any(x => x.Performance.LayoutRan));
			}
		}
	}

	public IGameplayUiSurface Create<TComponent>(UiSurfaceOptions options, string? cssResourceName = null,
		IReadOnlyDictionary<string, object?>? initialParameters = null) where TComponent : IComponent
	{
		ArgumentNullException.ThrowIfNull(options);
		Dispatcher.Bind();
		var css = LoadCss(typeof(TComponent).Assembly, cssResourceName);
		GameplayUiSurface surface;
		lock (_sync)
		{
			surface = new GameplayUiSurface(this, ++_nextSurfaceId, typeof(TComponent), options, css,
				initialParameters ?? new Dictionary<string, object?>(), _services);
			_surfaces.Add(surface);
		}
		Publish();
		return surface;
	}

	public void SetViewportSize(Int2 size, float displayScale = 1.0f)
	{
		lock (_sync)
		{
			if (size.X <= 0 || size.Y <= 0) return;

			var scale = float.IsFinite(displayScale)
				? Math.Clamp(displayScale, MinDisplayScale, MaxDisplayScale)
				: 1.0f;

			if (_requestedViewportSize.X == size.X && _requestedViewportSize.Y == size.Y && _requestedScale.Equals(scale)) return;
			_requestedViewportSize = size; _requestedScale = scale; _resizePending = true;
		}
	}

	/// <summary>Pump once on the gameplay thread, even when interaction is disabled.</summary>
	public void BeginFrame(PointerInputContext context)
	{
		Dispatcher.Pump();
		_inputContext = context;
		lock (_sync)
		{
			if (_resizePending)
			{
				_viewportSize = _requestedViewportSize; _displayScale = _requestedScale; _resizePending = false;
				foreach (var surface in _surfaces) if (surface.Options.Kind == UiSurfaceKind.Screen) surface.ResizeAndRebuild();
			}
			GameplayUiSurface? selected = null;
			foreach (var surface in _surfaces)
			{
				surface.FlushComponentRender();
				if (surface.Options.Kind == UiSurfaceKind.Screen && (selected is null || surface.Options.Layer >= selected.Options.Layer)) selected = surface;
			}
			if (!ReferenceEquals(_inputSurface, selected)) { _inputSurface?.Pointer.Cancel(); _inputSurface = selected; }
		}
		_inputSurface?.Pointer.BeginFrame(context);
	}
	public bool Route(PointerInputEvent input) => _inputSurface?.Pointer.Route(input) ?? false;
	public void EndFrame()
	{
		Dispatcher.Pump();
		lock (_sync) foreach (var surface in _surfaces) surface.FlushComponentRender();
		_inputSurface?.Pointer.BeginFrame(_inputContext);
	}

	public bool TryConsumeLatest(out GameplayUiRenderFrame frame)
	{
		frame = GameplayUiRenderFrame.Empty;
		while (_pendingFrames.TryDequeue(out var candidate))
		{
			if (!ReferenceEquals(frame, GameplayUiRenderFrame.Empty)) frame.Release();
			frame = candidate;
		}
		return !ReferenceEquals(frame, GameplayUiRenderFrame.Empty);
	}

	internal void Publish()
	{
		using (FrameProfiler.Instance.Measure("Gameplay UI.Publish Frame"))
		{
			GameplayUiRenderFrame frame;
			lock (_sync)
			{
				GameplayUiSurface? screen = null;
				var textureCount = 0;
				for (var i = 0; i < _surfaces.Count; i++)
				{
					var surface = _surfaces[i];
					if (surface.Options.Kind == UiSurfaceKind.Screen &&
					    (screen is null || surface.Options.Layer >= screen.Options.Layer)) screen = surface;
					else if (surface.Options.Kind == UiSurfaceKind.Texture && surface.Texture is not null) textureCount++;
				}

				var textureSurfaces = textureCount == 0
					? Array.Empty<GameplayUiTextureSurfaceFrame>()
					: new GameplayUiTextureSurfaceFrame[textureCount];
				var textureIndex = 0;
				for (var i = 0; i < _surfaces.Count; i++)
				{
					var surface = _surfaces[i];
					if (surface.Options.Kind != UiSurfaceKind.Texture || surface.Texture is null) continue;
					textureSurfaces[textureIndex++] = new GameplayUiTextureSurfaceFrame
					{
						SurfaceId = surface.Id,
						Target = surface.Texture,
						Frame = surface.Frame.Retain(),
						Revision = surface.FrameRevision,
						ClearColor = surface.Options.ClearColor
					};
				}

				frame = new GameplayUiRenderFrame
				{
					Screen = screen?.Frame.Retain() ?? UiFrameData.Empty,
					TextureSurfaces = textureSurfaces
				};
			}
			_pendingFrames.Enqueue(frame);
			while (_pendingFrames.Count > 2 && _pendingFrames.TryDequeue(out var dropped)) dropped.Release();
		}
	}

	internal void Remove(GameplayUiSurface surface)
	{
		lock (_sync) _surfaces.Remove(surface);
		Publish();
	}

	private static string? LoadCss(System.Reflection.Assembly assembly, string? resourceName)
	{
		if (string.IsNullOrWhiteSpace(resourceName)) return null;
		var resolved = assembly.GetManifestResourceNames().FirstOrDefault(x =>
			string.Equals(x, resourceName, StringComparison.Ordinal) || x.EndsWith(resourceName, StringComparison.Ordinal));
		if (resolved is null) throw new InvalidOperationException($"Embedded UI stylesheet '{resourceName}' was not found in '{assembly.GetName().Name}'.");
		using var stream = assembly.GetManifestResourceStream(resolved)!;
		using var reader = new StreamReader(stream);
		return reader.ReadToEnd();
	}

	public void Dispose()
	{
		GameplayUiSurface[] surfaces;
		lock (_sync) surfaces = _surfaces.ToArray();
		for (var i = 0; i < surfaces.Length; i++) surfaces[i].Dispose();
		Fonts.Dispose();
		while (_pendingFrames.TryDequeue(out var frame)) frame.Release();
	}
}
