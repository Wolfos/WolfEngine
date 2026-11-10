using System.Text.Json;
using System.Text.Json.Serialization;
using ImGuiNET;
using WolfEngine.Editor.UI;
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
	// Limit how often the 3D views are drawn, not the editor as a whole. The names predate that and stay so that
	// saved preferences keep loading.
	public bool LimitFPS { get; set; } = false;
	public int MaxFPS { get; set; } = 0;
	public string? LastProjectPath { get; set; }
	// The asset each document kind last had open, per project path. Assets are kept by GUID so that moving or
	// renaming them while the editor is closed does not lose them.
	public Dictionary<string, Dictionary<EditorDocumentKind, Guid>> LastDocumentAssetIds { get; set; } = new();

	// Read only to migrate preferences saved before every document kind was remembered.
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public Dictionary<string, Guid>? LastSceneAssetIds { get; set; }
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

	public static Guid? GetLastDocumentAssetId(string projectPath, EditorDocumentKind kind)
	{
		_instance ??= new EditorPreferences();
		var key = GetProjectKey(projectPath);
		if (_instance.LastDocumentAssetIds.TryGetValue(key, out var assetIds) == false) return null;

		return assetIds.TryGetValue(kind, out var assetId) && assetId != Guid.Empty
			? assetId
			: null;
	}

	public static void SetLastDocumentAssetId(string projectPath, EditorDocumentKind kind, Guid assetId)
	{
		_instance ??= new EditorPreferences();
		var key = GetProjectKey(projectPath);
		if (assetId == Guid.Empty)
		{
			if (_instance.LastDocumentAssetIds.TryGetValue(key, out var assetIds))
			{
				assetIds.Remove(kind);
				if (assetIds.Count == 0)
				{
					_instance.LastDocumentAssetIds.Remove(key);
				}
			}

			return;
		}

		if (_instance.LastDocumentAssetIds.TryGetValue(key, out var existing) == false)
		{
			existing = new Dictionary<EditorDocumentKind, Guid>();
			_instance.LastDocumentAssetIds[key] = existing;
		}

		existing[kind] = assetId;
	}

	private static string GetProjectKey(string projectPath)
	{
		return Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath));
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
		MigrateLastSceneAssetIds(_instance);

		var style = ImGui.GetStyle();
		foreach (var (id, color) in _instance.EditorColors)
		{
			style.Colors[(int)id] = color;
		}
	}

	internal static void MigrateLastSceneAssetIds(EditorPreferences preferences)
	{
		if (preferences.LastSceneAssetIds is null) return;

		foreach (var (projectPath, assetId) in preferences.LastSceneAssetIds)
		{
			if (assetId == Guid.Empty) continue;

			if (preferences.LastDocumentAssetIds.TryGetValue(projectPath, out var assetIds) == false)
			{
				assetIds = new Dictionary<EditorDocumentKind, Guid>();
				preferences.LastDocumentAssetIds[projectPath] = assetIds;
			}

			assetIds.TryAdd(EditorDocumentKind.Scene, assetId);
		}

		preferences.LastSceneAssetIds = null;
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
	public bool AnimationWorkspaceSeeded { get; set; }
	public bool AnimationGraphWorkspaceRenamed { get; set; }
	public bool AnimateWorkspaceSeeded { get; set; }
	public bool ModelWorkspaceSeeded { get; set; }
	public int Version { get; set; }
	public Guid ActiveWorkspaceId { get; set; }
	public List<EditorWorkspacePreference> Workspaces { get; set; } = new();
	public string ImGuiSettings { get; set; } = string.Empty;
}

public sealed class EditorWorkspacePreference
{
	public Guid Id { get; set; }
	public string Name { get; set; } = string.Empty;

	/// <summary>Null for workspaces saved before workspaces owned a document.</summary>
	public EditorDocumentKind? OwnedDocument { get; set; }

	public List<string> OpenWindowIds { get; set; } = new();
}
