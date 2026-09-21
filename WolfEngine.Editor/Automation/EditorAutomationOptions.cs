using WolfEngine.Mathematics;

namespace WolfEngine.Editor.Automation;

public sealed class EditorAutomationOptions
{
	public const int DefaultWidth = 1280;
	public const int DefaultHeight = 720;
	public required string ProjectPath { get; init; }
	public required string ScenePath { get; init; }
	public required string CapturePath { get; init; }
	public required int Frames { get; init; }
	public int ProfileFrames { get; init; }
	public string? ProfileOutputPath { get; init; }
	public Int2 Resolution { get; init; } = new(DefaultWidth, DefaultHeight);

	/// <summary>
	/// When set, a preview scene is rendered through a second view alongside the scene, and that view is captured
	/// here after the scene capture. The preview view is a different size and aspect from the scene view, so the pair
	/// checks views stay independent: the scene capture must match a single-view run, and the preview must show only
	/// its own world.
	/// </summary>
	public string? PreviewCapturePath { get; init; }

	/// <summary>Size of the preview view, deliberately not the scene view's aspect.</summary>
	public static readonly Int2 PreviewResolution = new(640, 480);

	public static bool TryParse(string[] args, out EditorAutomationOptions? options, out string error)
	{
		options = null;
		error = string.Empty;
		if (args.Length == 0)
		{
			return true;
		}

		string? project = null;
		string? scene = null;
		string? capture = null;
		int? frames = null;
		var profileFrames = 0;
		string? profileOutput = null;
		string? previewCapture = null;
		var width = DefaultWidth;
		var height = DefaultHeight;
		for (var index = 0; index < args.Length; index++)
		{
			var argument = args[index];
			if (argument == "--quit") continue;
			if (argument is not ("--project" or "--scene" or "--frames" or "--capture" or "--width" or "--height" or "--profile-frames" or "--profile-output" or "--preview-capture"))
			{
				error = $"Unknown option '{argument}'.";
				return false;
			}
			if (++index >= args.Length)
			{
				error = $"Option '{argument}' requires a value.";
				return false;
			}

			var value = args[index];
			switch (argument)
			{
				case "--project": project = value; break;
				case "--scene": scene = value; break;
				case "--capture": capture = value; break;
				case "--frames" when int.TryParse(value, out var parsedFrames): frames = parsedFrames; break;
				case "--width" when int.TryParse(value, out var parsedWidth): width = parsedWidth; break;
				case "--height" when int.TryParse(value, out var parsedHeight): height = parsedHeight; break;
				case "--profile-frames" when int.TryParse(value, out var parsedProfileFrames): profileFrames = parsedProfileFrames; break;
				case "--profile-output": profileOutput = value; break;
				case "--preview-capture": previewCapture = value; break;
				default:
					error = $"Option '{argument}' requires a positive integer.";
					return false;
			}
		}

		if (string.IsNullOrWhiteSpace(scene) || string.IsNullOrWhiteSpace(capture) || frames is null || frames <= 0 || width <= 0 || height <= 0)
		{
			error = "Automation requires --scene, --frames, and --capture; frame count and dimensions must be positive.";
			return false;
		}
		if ((profileFrames > 0) != !string.IsNullOrWhiteSpace(profileOutput))
		{
			error = "--profile-frames and --profile-output must be provided together; profile frame count must be positive.";
			return false;
		}

		var projectPath = Path.GetFullPath(string.IsNullOrWhiteSpace(project) ? Directory.GetCurrentDirectory() : project);
		options = new EditorAutomationOptions
		{
			ProjectPath = projectPath,
			ScenePath = scene,
			CapturePath = capture,
			Frames = frames.Value,
			ProfileFrames = profileFrames,
			ProfileOutputPath = profileOutput,
			Resolution = new Int2(width, height),
			PreviewCapturePath = previewCapture
		};
		return true;
	}
}
