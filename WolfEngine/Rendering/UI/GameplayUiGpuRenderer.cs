using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.UI;

internal sealed class GameplayUiTextureRevision
{
	private long? _rendered;
	public bool NeedsRedraw(long revision) => _rendered != revision;
	public void MarkRendered(long revision) => _rendered = revision;
}

/// <summary>
/// Owns the native gameplay UI renderer, its white texture, and upload buffers.
/// </summary>
public sealed class GameplayUiGpuRenderer
{
	private sealed class RenderTargetResources : ITextureResources
	{
		public required IGfxTexture Texture { get; init; }
		public required DescriptorHandle RegisteredShaderResourceView { get; init; }
		public DescriptorHandle ShaderResourceView => RegisteredShaderResourceView;
		public GameplayUiTextureRevision Revision { get; } = new();
	}

	private readonly IUiDrawRenderer _renderer;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly Dictionary<Texture, RenderTargetResources> _targets = new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<Texture, ITextureResources> _atlases = new(ReferenceEqualityComparer.Instance);
	private readonly IRenderer _resourceFactory;
	private readonly HashSet<Texture> _active = new(ReferenceEqualityComparer.Instance);
	private readonly List<Texture> _unused = [];

	public GameplayUiGpuRenderer(IShaderProvider shaderProvider, BindlessResourceRegistry bindlessRegistry, IRenderer resourceFactory)
	{
		_bindlessRegistry = bindlessRegistry;
		_resourceFactory = resourceFactory;
		_renderer = OperatingSystem.IsMacOS()
			? new MetalUiRenderer(shaderProvider, bindlessRegistry, sampleTexture: false)
			: new D3D12UiRenderer(shaderProvider, sampleTexture: false);
	}

	public IGfxTexture EnsureTarget(IGfxDevice device, Texture target)
	{
		if (_targets.TryGetValue(target, out var existing))
		{
			return existing.Texture;
		}

		var texture = device.CreateTexture(new TextureDescriptor(
			target.Width,
			target.Height,
			target.Format,
			TextureUsage.RenderTarget | TextureUsage.ShaderResource,
			default,
			mipLevels: 1,
			isSrgb: target.IsSrgb));
		_bindlessRegistry.EnsureInitialized(device);
		var resources = new RenderTargetResources
		{
			Texture = texture,
			RegisteredShaderResourceView = _bindlessRegistry.RegisterTexture(texture)
		};
		_targets.Add(target, resources);
		target.MarkGpuResourcesCreated(resources);
		return texture;
	}

	public void PruneTargets(IGfxDevice device, GameplayUiRenderFrame frame)
	{
		_active.Clear();
		CollectAtlases(frame.Screen);
		foreach (var surface in frame.TextureSurfaces) CollectAtlases(surface.Frame);
		_unused.Clear();
		foreach (var atlas in _atlases.Keys) if (!_active.Contains(atlas)) _unused.Add(atlas);
		foreach (var atlas in _unused)
		{
			var resources = _atlases[atlas];
			_atlases.Remove(atlas);
			atlas.DetachGpuResources();
			device.Retire(() =>
			{
				(resources.Texture as IDisposable)?.Dispose();
				if (!ReferenceEquals(resources.Texture, resources)) (resources as IDisposable)?.Dispose();
			}, "Gameplay UI font atlas");
		}
		if (_targets.Count == 0) return;
		_active.Clear();
		for (var i = 0; i < frame.TextureSurfaces.Length; i++) _active.Add(frame.TextureSurfaces[i].Target);
		_unused.Clear();
		foreach (var target in _targets.Keys) if (!_active.Contains(target)) _unused.Add(target);
		foreach (var target in _unused)
		{
			var resources = _targets[target];
			_targets.Remove(target);
			target.DetachGpuResources();
			if (resources.Texture is IDisposable disposable)
				device.Retire(disposable, $"Gameplay UI target '{target.Name}'");
		}
	}

	internal bool NeedsRedraw(GameplayUiTextureSurfaceFrame surface) =>
		_targets[surface.Target].Revision.NeedsRedraw(surface.Revision);

	internal void MarkRendered(GameplayUiTextureSurfaceFrame surface) =>
		_targets[surface.Target].Revision.MarkRendered(surface.Revision);

	private void CollectAtlases(UiFrameData data)
	{
		for (var i = 0; i < data.CommandCount; i++) if (data.Commands[i].Atlas is { } atlas) _active.Add(atlas);
	}

	public void EnsureResources(IGfxDevice device, UiFrameData frame)
	{
		for (var i = 0; i < frame.CommandCount; i++)
		{
			if (frame.Commands[i].Atlas is not { } atlas || _atlases.ContainsKey(atlas)) continue;
			var resources = _resourceFactory.CreateTextureResources(atlas);
			atlas.MarkGpuResourcesCreated(resources);
			_atlases.Add(atlas, resources);
		}
		_renderer.EnsureResources(device, frame);
	}

	public void Record(RenderGraphContext context, UiFrameData frame, IGfxTexture target, bool clearTarget,
		ColorRGBA? clearColor = null) =>
		_renderer.Record(context, frame, target, clearTarget, clearColor);

	public void InvalidateShaderPipeline() => _renderer.InvalidateShaderPipeline();
}
