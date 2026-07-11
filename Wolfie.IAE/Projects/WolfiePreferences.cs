using System.Text.Json;

namespace Wolfie.IAE.Projects;

public sealed class WolfiePreferences
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
	private readonly string _preferencesFile;

	public WolfiePreferences() : this(Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
		"Wolfie",
		"WolfiePreferences.json"))
	{
	}

	public WolfiePreferences(string preferencesFile)
	{
		_preferencesFile = WolfiePath.NormalizeAbsolute(preferencesFile);
		Load();
	}

	public string? LastProjectPath { get; private set; }
	public string? BlenderPath { get; private set; }

	public void SetLastProjectPath(string projectPath)
	{
		LastProjectPath = WolfiePath.NormalizeAbsolute(projectPath);
		Save();
	}

	public void SetBlenderPath(string? blenderPath)
	{
		if (string.IsNullOrWhiteSpace(blenderPath)) BlenderPath = null;
		else
		{
			var normalized = WolfiePath.NormalizeAbsolute(blenderPath.Trim());
			if (!File.Exists(normalized) && !Directory.Exists(normalized))
				throw new ArgumentException("Select an existing Blender executable or application.", nameof(blenderPath));
			BlenderPath = normalized;
		}
		Save();
	}

	private void Load()
	{
		try
		{
			if (!File.Exists(_preferencesFile)) return;
			var data = JsonSerializer.Deserialize<PreferenceData>(File.ReadAllText(_preferencesFile), JsonOptions);
			LastProjectPath = string.IsNullOrWhiteSpace(data?.LastProjectPath)
				? null
				: WolfiePath.NormalizeAbsolute(data.LastProjectPath);
			BlenderPath = string.IsNullOrWhiteSpace(data?.BlenderPath)
				? null
				: WolfiePath.NormalizeAbsolute(data.BlenderPath);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
		{
			LastProjectPath = null;
			BlenderPath = null;
		}
	}

	private void Save()
	{
		var directory = Path.GetDirectoryName(_preferencesFile);
		if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
		var temporary = _preferencesFile + ".tmp";
		try
		{
			File.WriteAllText(temporary, JsonSerializer.Serialize(new PreferenceData
				{ LastProjectPath = LastProjectPath, BlenderPath = BlenderPath }, JsonOptions));
			File.Move(temporary, _preferencesFile, true);
		}
		finally { if (File.Exists(temporary)) File.Delete(temporary); }
	}

	private sealed class PreferenceData
	{
		public string? LastProjectPath { get; init; }
		public string? BlenderPath { get; init; }
	}
}
