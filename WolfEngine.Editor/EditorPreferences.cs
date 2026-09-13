using System.Text.Json;
using System.Text.Json.Serialization;
using ImGuiNET;
using WolfEngine.Rendering;

namespace WolfEngine.Editor;

public class EditorPreferences
{
	private static EditorPreferences? _instance;

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		IncludeFields = true,
		Converters = { new JsonStringEnumConverter() }
	};

	private const string PreferencesFileName = "EditorPreferences.json";

	public Dictionary<ImGuiCol, ColorRGBA> EditorColors { get; set; } = new();
	public float SceneViewportResolutionScale { get; set; } = 1.0f;
	public bool LimitFPS { get; set; } = false;
	public int MaxFPS { get; set; } = 0;
	public string? LastProjectPath { get; set; }
	public EditorWorkspacePreferences? WorkspaceSettings { get; set; }

	public EditorPreferences()
	{
		_instance = this;
	}

	public static void SetColor(ImGuiCol id, ColorRGBA color)
	{
		if (_instance == null)
		{
			_instance = new EditorPreferences();
		}

		_instance.EditorColors[id] = color;
	}

	public static float GetSceneViewportResolutionScale()
	{
		if (_instance == null)
		{
			_instance = new EditorPreferences();
		}

		return _instance.SceneViewportResolutionScale;
	}

	public static void SetSceneViewportResolutionScale(float scale)
	{
		if (_instance == null)
		{
			_instance = new EditorPreferences();
		}

		_instance.SceneViewportResolutionScale = Math.Clamp(scale, 0.5f, 1.0f);
	}

	public static bool GetLimitFPS()
	{
		if (_instance == null)
		{
			_instance = new EditorPreferences();
		}

		return _instance.LimitFPS;
	}

	public static void SetLimitFPS(bool val)
	{
		if (_instance == null)
		{
			_instance = new EditorPreferences();
		}

		_instance.LimitFPS = val;
	}

	public static int GetMaxFPS()
	{
		if (_instance == null)
		{
			_instance = new EditorPreferences();
		}

		return Math.Max(_instance.MaxFPS, 20);
	}

	public static void SetMaxFPS(int maxFPS)
	{
		if (_instance == null)
		{
			_instance = new EditorPreferences();
		}

		_instance.MaxFPS = maxFPS;
	}

	public static string? GetLastProjectPath()
	{
		if (_instance == null)
		{
			_instance = new EditorPreferences();
		}

		return _instance.LastProjectPath;
	}

	public static void SetLastProjectPath(string? projectPath)
	{
		if (_instance == null)
		{
			_instance = new EditorPreferences();
		}

		_instance.LastProjectPath = string.IsNullOrWhiteSpace(projectPath)
			? null
			: Path.GetFullPath(projectPath);
	}

	public static EditorWorkspacePreferences? GetWorkspaceSettings()
	{
		_instance ??= new EditorPreferences();
		return _instance.WorkspaceSettings;
	}

	public static void SetWorkspaceSettings(EditorWorkspacePreferences settings)
	{
		_instance ??= new EditorPreferences();
		_instance.WorkspaceSettings = settings;
	}

	public static void SetWorkspaceImGuiSettings(string settings)
	{
		_instance ??= new EditorPreferences();
		_instance.WorkspaceSettings ??= new EditorWorkspacePreferences();
		_instance.WorkspaceSettings.ImGuiSettings = settings;
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

		_instance.SceneViewportResolutionScale = Math.Clamp(_instance.SceneViewportResolutionScale, 0.5f, 1.0f);

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
		var temporaryPath = path + ".tmp";
		File.WriteAllText(temporaryPath, json);
		File.Move(temporaryPath, path, true);
	}

	private static string GetPreferencesPath()
	{
		var overridePath = Environment.GetEnvironmentVariable("WOLF_EDITOR_PREFERENCES_PATH");
		if (!string.IsNullOrWhiteSpace(overridePath)) return Path.GetFullPath(overridePath);
		var baseDir = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
			"WolfEngine");
		return Path.Combine(baseDir, PreferencesFileName);
	}
}

public sealed class EditorWorkspacePreferences
{
	public int Version { get; set; }
	public Guid ActiveWorkspaceId { get; set; }
	public List<EditorWorkspacePreference> Workspaces { get; set; } = new();
	public string ImGuiSettings { get; set; } = string.Empty;
}

public sealed class EditorWorkspacePreference
{
	public Guid Id { get; set; }
	public string Name { get; set; } = string.Empty;
	public List<string> OpenWindowIds { get; set; } = new();
}
