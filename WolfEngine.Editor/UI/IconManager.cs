using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WolfEngine.Editor.UI;

public sealed class IconManager : IIconManager
{
	private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".png",
		".jpg",
		".jpeg",
		".bmp",
		".tga"
	};

	private readonly IImageLoader _imageLoader;
	private readonly ConcurrentDictionary<string, IconEntry> _icons = new(StringComparer.OrdinalIgnoreCase);
	private readonly string[] _iconNames;
	private volatile bool _hasPendingIcons;

	private sealed class IconEntry
	{
		public required string Path { get; init; }
		public nint TextureId;
	}

	public IconManager(IImageLoader imageLoader)
	{
		_imageLoader = imageLoader ?? throw new ArgumentNullException(nameof(imageLoader));
		ScanIcons();
		_iconNames = _icons.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).ToArray();
		_hasPendingIcons = _icons.IsEmpty == false;
		TryLoadAll();
	}

	public nint Get(string name)
	{
		if (TryGet(name, out var textureId))
		{
			return textureId;
		}

		throw new KeyNotFoundException($"Icon '{name}' was not found or could not be loaded.");
	}

	public bool TryGet(string name, out nint textureId)
	{
		textureId = 0;
		if (string.IsNullOrWhiteSpace(name))
		{
			return false;
		}

		if (_hasPendingIcons)
		{
			TryLoadAll();
		}

		if (_icons.TryGetValue(name, out var icon) == false)
		{
			return false;
		}

		if (icon.TextureId != 0)
		{
			textureId = icon.TextureId;
			return true;
		}

		if (_imageLoader.TryGetImGuiTextureId(icon.Path, out var loadedTextureId))
		{
			icon.TextureId = loadedTextureId;
			textureId = loadedTextureId;
			return true;
		}

		return false;
	}

	public IReadOnlyList<string> GetNames()
	{
		return _iconNames;
	}

	private void ScanIcons()
	{
		var iconsDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons");
		if (Directory.Exists(iconsDirectory) == false)
		{
			return;
		}

		foreach (var file in Directory.EnumerateFiles(iconsDirectory))
		{
			var extension = Path.GetExtension(file);
			if (SupportedExtensions.Contains(extension) == false)
			{
				continue;
			}

			var iconName = Path.GetFileNameWithoutExtension(file);
			if (string.IsNullOrWhiteSpace(iconName))
			{
				continue;
			}

			_icons[iconName] = new IconEntry
			{
				Path = file
			};
		}
	}

	private void TryLoadAll()
	{
		var hasPendingIcons = false;
		foreach (var entry in _icons.Values)
		{
			if (entry.TextureId != 0)
			{
				continue;
			}

			if (_imageLoader.TryGetImGuiTextureId(entry.Path, out var textureId))
			{
				entry.TextureId = textureId;
				continue;
			}

			hasPendingIcons = true;
		}

		_hasPendingIcons = hasPendingIcons;
	}
}
