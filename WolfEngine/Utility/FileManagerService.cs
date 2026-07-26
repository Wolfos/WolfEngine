using System;
using System.Diagnostics;
using System.IO;

namespace WolfEngine.Utility;

/// <summary>
/// Opens paths in the host operating system's file manager.
/// </summary>
public interface IFileManagerService
{
	/// <summary>
	/// Display name of the host file manager, for use in menu labels.
	/// </summary>
	string FileManagerName { get; }

	/// <summary>
	/// Opens the folder itself so its contents are listed.
	/// </summary>
	void OpenFolder(string absoluteFolderPath);

	/// <summary>
	/// Opens the parent folder of a file or folder with that entry selected. Platforms without a
	/// reveal mechanism fall back to opening the parent folder.
	/// </summary>
	void RevealPath(string absolutePath);
}

public sealed class FileManagerService : IFileManagerService
{
	public string FileManagerName =>
		OperatingSystem.IsWindows() ? "Explorer" :
		OperatingSystem.IsMacOS() ? "Finder" :
		"File Manager";

	public void OpenFolder(string absoluteFolderPath)
	{
		var folderPath = NormalizePath(absoluteFolderPath, nameof(absoluteFolderPath));
		if (Directory.Exists(folderPath) == false)
		{
			throw new DirectoryNotFoundException($"Folder '{folderPath}' does not exist.");
		}

		if (OperatingSystem.IsWindows())
		{
			// explorer.exe parses its own command line, so the path is passed pre-quoted.
			StartWithArguments("explorer.exe", $"\"{folderPath}\"");
			return;
		}

		StartWithArgumentList(OperatingSystem.IsMacOS() ? "open" : "xdg-open", folderPath);
	}

	public void RevealPath(string absolutePath)
	{
		var path = NormalizePath(absolutePath, nameof(absolutePath));
		if (File.Exists(path) == false && Directory.Exists(path) == false)
		{
			throw new FileNotFoundException($"Path '{path}' does not exist.", path);
		}

		if (OperatingSystem.IsWindows())
		{
			StartWithArguments("explorer.exe", $"/select,\"{path}\"");
			return;
		}

		if (OperatingSystem.IsMacOS())
		{
			StartWithArgumentList("open", "-R", path);
			return;
		}

		// xdg-open has no reveal equivalent, so show the containing folder instead.
		var parentPath = Path.GetDirectoryName(path);
		StartWithArgumentList("xdg-open", string.IsNullOrEmpty(parentPath) ? path : parentPath);
	}

	private static string NormalizePath(string path, string parameterName)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new ArgumentException("Path cannot be null or empty.", parameterName);
		}

		// A trailing separator inside a quoted Windows argument escapes the closing quote.
		return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
	}

	private static void StartWithArguments(string fileName, string arguments)
	{
		Start(new ProcessStartInfo(fileName)
		{
			Arguments = arguments,
			UseShellExecute = false
		});
	}

	private static void StartWithArgumentList(string fileName, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo(fileName) { UseShellExecute = false };
		for (var i = 0; i < arguments.Length; i++)
		{
			startInfo.ArgumentList.Add(arguments[i]);
		}

		Start(startInfo);
	}

	private static void Start(ProcessStartInfo startInfo)
	{
		using var process = Process.Start(startInfo);
		if (process is null)
		{
			throw new InvalidOperationException($"'{startInfo.FileName}' could not be started.");
		}
	}
}
