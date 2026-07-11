using System.Text.Json;

namespace Wolfie.IAE.Projects;

public sealed class WolfieProjectService
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	public static bool ValidateUnityProject(string path, out string error)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
			{
				error = "Select an existing Unity project directory.";
				return false;
			}

			var root = WolfiePath.NormalizeAbsolute(path);
			if (!Directory.Exists(Path.Combine(root, "Assets")) ||
			    !Directory.Exists(Path.Combine(root, "ProjectSettings")))
			{
				error = "This directory does not appear to be a Unity project. It must contain Assets and ProjectSettings folders.";
				return false;
			}

			error = string.Empty;
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			error = $"The Unity project path could not be inspected: {exception.Message}";
			return false;
		}
	}

	public WolfieProject Create(string unityPath, string parentLocation, string name, out string projectFile)
	{
		if (!ValidateUnityProject(unityPath, out var error)) throw new InvalidOperationException(error);
		if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Enter a Wolfie project name.");
		var unityRoot = WolfiePath.NormalizeAbsolute(unityPath);
		var parentRoot = WolfiePath.NormalizeAbsolute(parentLocation);
		if (!Directory.Exists(parentRoot))
			throw new InvalidOperationException("Select an existing parent folder for the Wolfie project.");
		var safeName = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
		if (string.IsNullOrWhiteSpace(safeName)) throw new InvalidOperationException("Enter a valid Wolfie project name.");
		var destinationRoot = Path.Combine(parentRoot, safeName);
		if (WolfiePath.IsWithin(destinationRoot, unityRoot) || WolfiePath.IsWithin(unityRoot, destinationRoot))
			throw new InvalidOperationException("The Wolfie and Unity projects must be separate, non-overlapping directories.");
		if (Directory.Exists(destinationRoot) && Directory.EnumerateFileSystemEntries(destinationRoot).Any())
			throw new InvalidOperationException($"The project folder already exists and is not empty: {destinationRoot}");

		Directory.CreateDirectory(destinationRoot);
		Directory.CreateDirectory(Path.Combine(destinationRoot, "Assets"));
		Directory.CreateDirectory(Path.Combine(destinationRoot, "Cache"));
		projectFile = Path.Combine(destinationRoot, safeName + ".wolfieproject");

		var project = new WolfieProject { Name = name.Trim(), UnityProjectPath = unityRoot };
		Save(project, projectFile);
		return project;
	}

	public WolfieProject Open(string projectFile)
	{
		var absoluteFile = WolfiePath.NormalizeAbsolute(projectFile);
		var project = JsonSerializer.Deserialize<WolfieProject>(File.ReadAllText(absoluteFile), JsonOptions)
		              ?? throw new InvalidDataException("The Wolfie project file is empty or invalid.");
		if (project.FormatVersion != WolfieProject.CurrentFormatVersion)
			throw new InvalidDataException($"Unsupported Wolfie project format version {project.FormatVersion}.");
		if (project.ProjectId == Guid.Empty || string.IsNullOrWhiteSpace(project.Name))
			throw new InvalidDataException("The Wolfie project file is missing its identifier or name.");
		if (!ValidateUnityProject(project.UnityProjectPath, out var error))
			throw new InvalidDataException($"The connected Unity project is unavailable. {error}");
		return project with { UnityProjectPath = WolfiePath.NormalizeAbsolute(project.UnityProjectPath) };
	}

	private static void Save(WolfieProject project, string projectFile) =>
		File.WriteAllText(projectFile, JsonSerializer.Serialize(project, JsonOptions));
}
