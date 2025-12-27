using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImGuiNET;

namespace WolfEngine.Editor;

public class EditorPreferences
{
	private static EditorPreferences _instance;
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		IncludeFields = true,
		Converters = { new JsonStringEnumConverter() }
	};
	private const string PreferencesFileName = "EditorPreferences.json";
	
	public Dictionary<ImGuiCol, Vector4> EditorColors { get; set; } = new();

	public EditorPreferences()
	{
		_instance = this;
	}

	public static void SetColor(ImGuiCol id, Vector4 color)
	{
		if (_instance == null)
		{
			_instance = new EditorPreferences();
		}
		_instance.EditorColors[id] = color;
	}

	public static void Load()
	{
		var path = GetPreferencesPath();
		if (File.Exists(path))
		{
			try
			{
				var json = File.ReadAllText(path);
				_instance = JsonSerializer.Deserialize<EditorPreferences>(json, JsonOptions) ?? new EditorPreferences();
			}
			catch
			{
				_instance = new EditorPreferences();
			}
		}
		else
		{
			_instance = new EditorPreferences();
		}

		var style = ImGui.GetStyle();
		foreach (var (id, color) in _instance.EditorColors)
		{
			style.Colors[(int)id] = color;
		}
	}

	public static void Save()
	{
		if (_instance == null)
		{
			_instance = new EditorPreferences();
		}

		var path = GetPreferencesPath();
		var directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}

		var json = JsonSerializer.Serialize(_instance, JsonOptions);
		File.WriteAllText(path, json);
	}

	private static string GetPreferencesPath()
	{
		var baseDir = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
			"WolfEngine");
		return Path.Combine(baseDir, PreferencesFileName);
	}
}
