using System;
using WolfEngine.Platform;

namespace WolfEngine.Utility;

public sealed class FileDialogOptions
{
	public string? Title { get; init; }
	public string? InitialDirectory { get; init; }
	public string[]? AllowedExtensions { get; init; }
}

public interface IFileDialogService
{
	string? OpenFile(FileDialogOptions? options = null);
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
}
