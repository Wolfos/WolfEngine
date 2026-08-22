using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.UI;

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
	}

	private readonly IUiDrawRenderer _renderer;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly Dictionary<Texture, ITextureResources> _targets = new(ReferenceEqualityComparer.Instance);

	public GameplayUiGpuRenderer(IShaderProvider shaderProvider, BindlessResourceRegistry bindlessRegistry)
	{
		_bindlessRegistry = bindlessRegistry;
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
		if (_targets.Count == 0) return;
		var active = new HashSet<Texture>(ReferenceEqualityComparer.Instance);
		for (var i = 0; i < frame.TextureSurfaces.Length; i++) active.Add(frame.TextureSurfaces[i].Target);
		foreach (var pair in _targets.ToArray())
		{
			if (active.Contains(pair.Key)) continue;
			_targets.Remove(pair.Key);
			if (pair.Value.Texture is IDisposable disposable)
				device.Retire(disposable, $"Gameplay UI target '{pair.Key.Name}'");
		}
	}

	public void EnsureResources(IGfxDevice device, UiFrameData frame) => _renderer.EnsureResources(device, frame);

	public void Record(RenderGraphContext context, UiFrameData frame, IGfxTexture target, bool clearTarget,
		ColorRGBA? clearColor = null) =>
		_renderer.Record(context, frame, target, clearTarget, clearColor);

	public void InvalidateShaderPipeline() => _renderer.InvalidateShaderPipeline();
}
