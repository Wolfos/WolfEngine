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
	public float SceneViewportResolutionScale { get; set; } = 1.0f;

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

		style.WindowBorderSize = 0.0f;
		style.ChildBorderSize = 0.0f;
		style.PopupBorderSize = 0.0f;
		style.TabBorderSize = 0.0f;
		style.TabBarBorderSize = 0.0f;
		style.FrameBorderSize = 0.0f;
		style.DockingSeparatorSize = 0.0f;
		style.TabRounding = 6.0f;
		style.WindowMenuButtonPosition = ImGuiDir.None;
		style.Colors[(int)ImGuiCol.Border] = Vector4.Zero;
		style.Colors[(int)ImGuiCol.BorderShadow] = Vector4.Zero;
		style.Colors[(int)ImGuiCol.Separator] = Vector4.Zero;
		style.Colors[(int)ImGuiCol.SeparatorHovered] = Vector4.Zero;
		style.Colors[(int)ImGuiCol.SeparatorActive] = Vector4.Zero;
		style.Colors[(int)ImGuiCol.TabSelectedOverline] = Vector4.Zero;
		style.Colors[(int)ImGuiCol.TabDimmedSelectedOverline] = Vector4.Zero;
		style.Colors[(int)ImGuiCol.DockingEmptyBg] = style.Colors[(int)ImGuiCol.WindowBg];
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
