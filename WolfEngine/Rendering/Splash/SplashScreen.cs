using System.Diagnostics;

namespace WolfEngine.Rendering.Splash;

/// <summary>What the render thread draws of the splash screen this frame.</summary>
/// <param name="Exclusive">
/// The splash owns the window: the renderer draws it alone, without waiting for a game frame. False while it fades
/// out over the game.
/// </param>
internal readonly record struct SplashScreenFrame(
	SplashScreenContent? Content,
	int ContentRevision,
	bool Exclusive,
	SplashScreenPose Pose)
{
	public bool IsVisible => Content is not null;
}

/// <summary>
/// Shows the engine splash screen while a game loads, then fades it out over the game's first frames.
/// </summary>
/// <remarks>
/// The host calls <see cref="Show"/> before rendering starts and loads its scene while the render thread draws the
/// splash on its own. The splash's clock starts at its first drawn frame, so a slow window does not eat into the
/// animation. Once <see cref="IsIntroComplete"/> and the scene is loaded, the host calls <see cref="Reveal"/> and
/// starts publishing game frames; the render thread then draws them with the splash fading out on top, and drops
/// the splash when the fade ends.
/// <para>Thread-safe: the host thread drives it while the render thread reads it once per frame.</para>
/// </remarks>
public sealed class SplashScreen
{
	private readonly object _sync = new();
	private SplashScreenContent? _content;
	private int _contentRevision;
	private long _startTimestamp;
	private long _revealTimestamp;
	private bool _revealRequested;
	private float? _frozenTime;

	/// <summary>Whether a splash is on screen or about to be.</summary>
	public bool IsShowing
	{
		get
		{
			lock (_sync)
			{
				return _content is not null;
			}
		}
	}

	/// <summary>
	/// Whether the game may be revealed: the intro has played for <see cref="SplashScreenTimeline.IntroDuration"/>.
	/// True when no splash is showing.
	/// </summary>
	public bool IsIntroComplete
	{
		get
		{
			lock (_sync)
			{
				return IsIntroCompleteLocked(Stopwatch.GetTimestamp());
			}
		}
	}

	/// <summary>Starts the splash from its first frame. It owns the window until <see cref="Reveal"/>.</summary>
	public void Show(SplashScreenContent content)
	{
		ArgumentNullException.ThrowIfNull(content);

		lock (_sync)
		{
			Reset(content);
		}
	}

	/// <summary>
	/// Shows the splash held at <paramref name="time"/> seconds into its animation until <see cref="Hide"/>, for
	/// inspecting a single moment of it.
	/// </summary>
	public void ShowFrozen(SplashScreenContent content, float time)
	{
		ArgumentNullException.ThrowIfNull(content);
		if (float.IsFinite(time) == false || time < 0.0f)
		{
			throw new ArgumentOutOfRangeException(nameof(time), "The splash time must be a non-negative number of seconds.");
		}

		lock (_sync)
		{
			Reset(content);
			_frozenTime = time;
		}
	}

	/// <summary>Removes the splash at once, without fading it out.</summary>
	public void Hide()
	{
		lock (_sync)
		{
			_content = null;
			_frozenTime = null;
			_contentRevision++;
			Monitor.PulseAll(_sync);
		}
	}

	/// <summary>
	/// Blocks until <see cref="IsIntroComplete"/> or <paramref name="timeout"/> passes. Returns whether the intro is
	/// complete.
	/// </summary>
	public bool WaitForIntro(TimeSpan timeout)
	{
		var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
		lock (_sync)
		{
			while (true)
			{
				var now = Stopwatch.GetTimestamp();
				if (IsIntroCompleteLocked(now))
				{
					return true;
				}

				if (now >= deadline)
				{
					return false;
				}

				// Before the first frame there is no start time to count from; the render thread pulses when it starts.
				var wait = TimeSpan.FromSeconds((double)(deadline - now) / Stopwatch.Frequency);
				if (_startTimestamp != 0)
				{
					var introEnd = _startTimestamp + (long)(SplashScreenTimeline.IntroDuration * Stopwatch.Frequency);
					var untilIntroEnd = TimeSpan.FromSeconds((double)(introEnd - now) / Stopwatch.Frequency);
					wait = untilIntroEnd < wait ? untilIntroEnd : wait;
				}

				Monitor.Wait(_sync, wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1));
			}
		}
	}

	/// <summary>
	/// The game's frames follow: the renderer stops drawing the splash alone and fades it out over them, starting
	/// with the first game frame it draws.
	/// </summary>
	public void Reveal()
	{
		lock (_sync)
		{
			if (_content is not null && _frozenTime is null)
			{
				_revealRequested = true;
			}
		}
	}

	/// <summary>Render thread, once per frame: what to draw at <paramref name="timestamp"/>.</summary>
	internal SplashScreenFrame BeginFrame(long timestamp)
	{
		lock (_sync)
		{
			if (_content is null)
			{
				return new SplashScreenFrame(null, _contentRevision, false, default);
			}

			if (_frozenTime is { } frozen)
			{
				return new SplashScreenFrame(_content, _contentRevision, true, SplashScreenTimeline.Evaluate(frozen));
			}

			if (_startTimestamp == 0)
			{
				_startTimestamp = timestamp;
				Monitor.PulseAll(_sync);
			}

			var time = Seconds(timestamp - _startTimestamp);
			if (_revealRequested == false)
			{
				return new SplashScreenFrame(_content, _contentRevision, true, SplashScreenTimeline.Evaluate(time));
			}

			var revealTime = _revealTimestamp == 0 ? 0.0f : Seconds(timestamp - _revealTimestamp);
			if (revealTime >= SplashScreenTimeline.RevealDuration)
			{
				_content = null;
				_contentRevision++;
				return new SplashScreenFrame(null, _contentRevision, false, default);
			}

			return new SplashScreenFrame(
				_content,
				_contentRevision,
				false,
				SplashScreenTimeline.Evaluate(time, revealTime));
		}
	}

	/// <summary>Render thread: the splash was drawn over a game frame. The fade-out counts from the first one.</summary>
	internal void MarkDrawnOverGame(long timestamp)
	{
		lock (_sync)
		{
			if (_revealRequested && _revealTimestamp == 0)
			{
				_revealTimestamp = timestamp;
			}
		}
	}

	private void Reset(SplashScreenContent content)
	{
		_content = content;
		_contentRevision++;
		_startTimestamp = 0;
		_revealTimestamp = 0;
		_revealRequested = false;
		_frozenTime = null;
		Monitor.PulseAll(_sync);
	}

	private bool IsIntroCompleteLocked(long now)
	{
		if (_content is null)
		{
			return true;
		}

		return _startTimestamp != 0 &&
		       Seconds(now - _startTimestamp) >= SplashScreenTimeline.IntroDuration;
	}

	private static float Seconds(long ticks) => (float)((double)ticks / Stopwatch.Frequency);
}
