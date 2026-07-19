using System;
using WolfEngine.Platform;

namespace WolfEngine.Utility;

public sealed class FileDialogOptions
{
	public string? Title { get; init; }
	public string? InitialDirectory { get; init; }
	public string? DefaultFileName { get; init; }
	public string[]? AllowedExtensions { get; init; }
}

public interface IFileDialogService
{
	string? OpenFile(FileDialogOptions? options = null);
	string? SaveFile(FileDialogOptions? options = null);
	string? OpenFolder(FileDialogOptions? options = null);
}

public sealed class FileDialogService : IFileDialogService
{
	private readonly IMainThreadDispatcher _mainThreadDispatcher;

	public FileDialogService(IMainThreadDispatcher mainThreadDispatcher)
	{
		_mainThreadDispatcher = mainThreadDispatcher ?? throw new ArgumentNullException(nameof(mainThreadDispatcher));
	}

	public string? OpenFile(FileDialogOptions? options = null)
	{
		var resolved = options ?? new FileDialogOptions();
		return _mainThreadDispatcher.Invoke(() => OpenFileOnMainThread(resolved));
	}

	public string? OpenFolder(FileDialogOptions? options = null)
	{
		var resolved = options ?? new FileDialogOptions();
		return _mainThreadDispatcher.Invoke(() => OpenFolderOnMainThread(resolved));
	}

	public string? SaveFile(FileDialogOptions? options = null)
	{
		var resolved = options ?? new FileDialogOptions();
		return _mainThreadDispatcher.Invoke(() => SaveFileOnMainThread(resolved));
	}

	private static string? OpenFileOnMainThread(FileDialogOptions options)
	{
		if (OperatingSystem.IsMacOS())
		{
			return MacOSFileDialog.OpenFile(options);
		}

		if (OperatingSystem.IsWindows())
		{
			return WindowsHelpers.OpenFile(options);
		}

		return null;
	}

	private static string? OpenFolderOnMainThread(FileDialogOptions options)
	{
		if (OperatingSystem.IsMacOS())
		{
			return MacOSFileDialog.OpenFolder(options);
		}

		if (OperatingSystem.IsWindows())
		{
			return WindowsHelpers.OpenFolder(options);
		}

		return null;
	}

	private static string? SaveFileOnMainThread(FileDialogOptions options)
	{
		if (OperatingSystem.IsMacOS())
		{
			return MacOSFileDialog.SaveFile(options);
		}

		if (OperatingSystem.IsWindows())
		{
			return WindowsHelpers.SaveFile(options);
		}

		return null;
	}
}
