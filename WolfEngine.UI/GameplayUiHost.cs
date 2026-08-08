using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Components;
using WolfEngine.Mathematics;
using WolfEngine.Profiling;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.UI;

internal sealed class GameplayUiSurface : IGameplayUiSurface
{
	private readonly GameplayUiHost _host;
	private readonly RazorTreeRenderer _renderer;
	private readonly int _rootComponentId;
	private readonly CssStyleSheet _styleSheet;
	private readonly IUiLayoutEngine _layout;
	private readonly UiFrameBuilder _frames;
	private readonly string _profilerName;
	private IReadOnlyDictionary<string, object?> _parameters;
	private bool _disposed;
	private long _revision;
	private UiNode? _root;
	private int _layoutWidth;
	private int _layoutHeight;

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
		_parameters = initialParameters;
		_renderer = new RazorTreeRenderer(services);
		_rootComponentId = _renderer.AttachRoot(componentType);
		_styleSheet = CssStyleSheet.Parse(css ?? string.Empty);
		_layout = new YogaLayoutEngine();
		_frames = new UiFrameBuilder();
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
	internal bool IsDirty { get; private set; } = true;

	public void SetParameters(IReadOnlyDictionary<string, object?> parameters)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		_parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
		Rebuild();
	}

	public void Invalidate()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		Rebuild();
	}

	internal void ResizeAndRebuild()
	{
		if (!_disposed && Options.Kind == UiSurfaceKind.Screen) Rebuild();
	}

	internal void MarkPublishedClean() => IsDirty = false;

	private void Rebuild()
	{
		using (FrameProfiler.Instance.Measure(_profilerName))
		{
			var width = Options.Kind == UiSurfaceKind.Screen ? Math.Max(1, _host.ViewportSize.X) : Math.Max(1, Options.Width);
			var height = Options.Kind == UiSurfaceKind.Screen ? Math.Max(1, _host.ViewportSize.Y) : Math.Max(1, Options.Height);
			var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
			var timer = Stopwatch.StartNew();
			using (FrameProfiler.Instance.Measure("Gameplay UI.Razor Render"))
			{
				_renderer.Render(_rootComponentId, _parameters);
			}

			UiNode updatedRoot;
			using (FrameProfiler.Instance.Measure("Gameplay UI.Build Tree"))
			{
				updatedRoot = _renderer.BuildTree(_rootComponentId);
			}

			using (FrameProfiler.Instance.Measure("Gameplay UI.Apply CSS"))
			{
				_styleSheet.Apply(updatedRoot, width, height);
			}

			UiTreeChanges changes;
			using (FrameProfiler.Instance.Measure("Gameplay UI.Reconcile"))
			{
				changes = _root is null
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
			else
			{
				_renderer.RecycleTree(updatedRoot);
			}

			var fullLayoutRequired = topologyChanged || _layoutWidth != width || _layoutHeight != height ||
			                         changes.LayoutChanged;
			var layoutRan = fullLayoutRequired || changes.IntrinsicSizeChanged;
			if (layoutRan)
			{
				using (FrameProfiler.Instance.Measure("Gameplay UI.Yoga Layout"))
				{
					_layout.Layout(_root!, width, height, fullLayoutRequired);
				}
				_layoutWidth = width;
				_layoutHeight = height;
			}
			var geometryChanged = layoutRan || changes.VisualChanged;
			if (geometryChanged)
			{
				using (FrameProfiler.Instance.Measure("Gameplay UI.Build Geometry"))
				{
					var previousFrame = Frame;
					Frame = _frames.Build(_root!, width, height);
					previousFrame.Release();
				}
				IsDirty = true;
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

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
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

public sealed class GameplayUiHost : IGameplayUiHost, IGameplayUiFrameProvider, IDisposable
{
	private readonly IServiceProvider _services;
	private readonly object _sync = new();
	private readonly List<GameplayUiSurface> _surfaces = [];
	private readonly ConcurrentQueue<GameplayUiRenderFrame> _pendingFrames = new();
	private long _nextSurfaceId;
	private Int2 _viewportSize = new(1280, 720);

	public GameplayUiHost(IServiceProvider services) => _services = services;

	internal Int2 ViewportSize
	{
		get { lock (_sync) return _viewportSize; }
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

	public void SetViewportSize(Int2 size)
	{
		GameplayUiSurface[] screens;
		lock (_sync)
		{
			if (size.X <= 0 || size.Y <= 0 || (_viewportSize.X == size.X && _viewportSize.Y == size.Y)) return;
			_viewportSize = size;
			screens = _surfaces.Where(x => x.Options.Kind == UiSurfaceKind.Screen).ToArray();
		}
		for (var i = 0; i < screens.Length; i++) screens[i].ResizeAndRebuild();
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
						IsDirty = surface.IsDirty,
						ClearColor = surface.Options.ClearColor
					};
				}

				frame = new GameplayUiRenderFrame
				{
					Screen = screen?.Frame.Retain() ?? UiFrameData.Empty,
					TextureSurfaces = textureSurfaces
				};
				for (var i = 0; i < _surfaces.Count; i++) _surfaces[i].MarkPublishedClean();
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
		while (_pendingFrames.TryDequeue(out var frame)) frame.Release();
	}
}
