namespace WolfEngine.Rendering.Splash;

/// <summary>How far each part of the splash screen has animated at one moment.</summary>
/// <param name="TraceProgress">How much of the outline is drawn, from 0 to 1.</param>
/// <param name="StrokeOpacity">The traced outline and its glow.</param>
/// <param name="FillOpacity">The logo's filled shapes.</param>
/// <param name="PoweredByOpacity">The "Powered by" line.</param>
/// <param name="WordmarkOpacity">The "WOLFENGINE" line.</param>
/// <param name="Opacity">The whole splash, background included, over whatever is behind it.</param>
public readonly record struct SplashScreenPose(
	float TraceProgress,
	float StrokeOpacity,
	float FillOpacity,
	float PoweredByOpacity,
	float WordmarkOpacity,
	float Opacity);

/// <summary>
/// The splash screen's choreography: trace the silhouette, fill the logo in as the stroke fades, fade the two text
/// lines in, hold, then fade the whole splash out over the game.
/// </summary>
public static class SplashScreenTimeline
{
	public const float TraceStart = 0.3f;
	public const float TraceDuration = 1.6f;
	public const float FillStart = 1.6f;
	public const float FillDuration = 0.8f;
	public const float StrokeFadeStart = 2.0f;
	public const float StrokeFadeDuration = 0.8f;
	public const float PoweredByStart = 2.2f;
	public const float WordmarkStart = 2.45f;
	public const float TextFadeDuration = 0.6f;

	/// <summary>The time from the first splash frame until the game may be revealed.</summary>
	public const float IntroDuration = 4.0f;

	/// <summary>How long the splash takes to fade out over the game's first frames.</summary>
	public const float RevealDuration = 0.6f;

	/// <param name="time">Seconds since the splash's first frame.</param>
	/// <param name="revealTime">Seconds since the splash began fading out over the game, or null before then.</param>
	public static SplashScreenPose Evaluate(float time, float? revealTime = null)
	{
		var trace = EaseInOut(Progress(time, TraceStart, TraceDuration));
		var fill = EaseInOut(Progress(time, FillStart, FillDuration));
		var strokeFade = EaseInOut(Progress(time, StrokeFadeStart, StrokeFadeDuration));
		var poweredBy = EaseInOut(Progress(time, PoweredByStart, TextFadeDuration));
		var wordmark = EaseInOut(Progress(time, WordmarkStart, TextFadeDuration));
		var opacity = revealTime is { } reveal
			? 1.0f - EaseInOut(Progress(reveal, 0.0f, RevealDuration))
			: 1.0f;

		return new SplashScreenPose(
			trace,
			(trace > 0.0f ? 1.0f : 0.0f) * (1.0f - strokeFade),
			fill,
			poweredBy,
			wordmark,
			opacity);
	}

	private static float Progress(float time, float start, float duration) =>
		Math.Clamp((time - start) / duration, 0.0f, 1.0f);

	private static float EaseInOut(float t) =>
		t < 0.5f
			? 4.0f * t * t * t
			: 1.0f - MathF.Pow(-2.0f * t + 2.0f, 3.0f) * 0.5f;
}
