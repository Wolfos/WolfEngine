using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImGuiNET;
using WolfEngine.Rendering;

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

	public Dictionary<ImGuiCol, ColorRGBA> EditorColors { get; set; } = new();
	public float SceneViewportResolutionScale { get; set; } = 1.0f;
	public string? LastProjectPath { get; set; }

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
