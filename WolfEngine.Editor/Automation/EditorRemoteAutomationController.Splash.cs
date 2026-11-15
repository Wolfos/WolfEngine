using WolfEngine.Editor.Tooling.Splash;
using WolfEngine.Rendering.Splash;

namespace WolfEngine.Editor.Automation;

public sealed record SplashScreenPreviewResult(
	bool Showing,
	float? TimeSeconds,
	long EditorFrameSequence,
	long RenderFrameSequence);

/// <summary>
/// Splash screen automation. Runtime builds show the splash while they load; in the editor it can only be held at
/// one moment of its animation, which covers the window until it is hidden.
/// </summary>
public sealed partial class EditorRemoteAutomationController
{
	private SplashScreenContent? _splashScreenContent;

	/// <summary>
	/// Bakes the splash from the engine's sources on first use, then holds it at <paramref name="timeSeconds"/> into
	/// its animation, or hides it when that is null.
	/// </summary>
	public Task<SplashScreenPreviewResult> PreviewSplashScreenAsync(float? timeSeconds, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (timeSeconds is not { } time)
			{
				_splashScreen.Hide();
				return CreateSplashScreenPreviewResult(null);
			}

			if (float.IsFinite(time) == false || time < 0.0f)
			{
				throw new InvalidOperationException("time_seconds must be a non-negative number of seconds.");
			}

			_splashScreenContent ??= SplashScreenBaker.Bake(
				SplashScreenBaker.GetSourceDirectory(_engineShaderOptions.EngineContentRoot));
			_splashScreen.ShowFrozen(_splashScreenContent, time);
			return CreateSplashScreenPreviewResult(time);
		}, cancellationToken);

	private SplashScreenPreviewResult CreateSplashScreenPreviewResult(float? time) =>
		new(
			_splashScreen.IsShowing,
			time,
			_editorFrameCoordinator.CompletedSequence,
			_renderFrameCoordinator.CompletedSequence);
}
