using System.Collections.Concurrent;

namespace Wolfie.IAE.UI;

public sealed class IconManager : IIconManager
{
	private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
		{ ".png", ".jpg", ".jpeg", ".bmp", ".tga" };
	private readonly IImageLoader _imageLoader;
	private readonly ConcurrentDictionary<string, IconEntry> _icons = new(StringComparer.OrdinalIgnoreCase);
	private readonly string[] _iconNames;
	private volatile bool _hasPendingIcons;

	private sealed class IconEntry { public required string Path { get; init; } public nint TextureId; }

	public IconManager(IImageLoader imageLoader)
	{
		_imageLoader = imageLoader;
		ScanIcons();
		_iconNames = _icons.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).ToArray();
		_hasPendingIcons = !_icons.IsEmpty;
		TryLoadAll();
	}

	public nint Get(string name) => TryGet(name, out var id) ? id :
		throw new KeyNotFoundException($"Icon '{name}' was not found or could not be loaded.");

	public bool TryGet(string name, out nint textureId)
	{
		textureId = 0;
		if (string.IsNullOrWhiteSpace(name)) return false;
		if (_hasPendingIcons) TryLoadAll();
		if (!_icons.TryGetValue(name, out var icon)) return false;
		if (icon.TextureId != 0) { textureId = icon.TextureId; return true; }
		if (!_imageLoader.TryGetImGuiTextureId(icon.Path, out var loaded)) return false;
		icon.TextureId = textureId = loaded;
		return true;
	}

	public IReadOnlyList<string> GetNames() => _iconNames;

	private void ScanIcons()
	{
		var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons");
		if (!Directory.Exists(directory)) return;
		foreach (var file in Directory.EnumerateFiles(directory))
			if (SupportedExtensions.Contains(Path.GetExtension(file)))
				_icons[Path.GetFileNameWithoutExtension(file)] = new IconEntry { Path = file };
	}

	private void TryLoadAll()
	{
		var pending = false;
		foreach (var icon in _icons.Values)
		{
			if (icon.TextureId != 0) continue;
			if (_imageLoader.TryGetImGuiTextureId(icon.Path, out var id)) icon.TextureId = id;
			else pending = true;
		}
		_hasPendingIcons = pending;
	}
}
