namespace WolfEngine.Editor;

/// <summary>
/// Decides which editor frames draw the 3D views, so the scene can be drawn less often than the editor UI.
/// </summary>
/// <remarks>
/// A frame either is due or is not; the editor publishes a scene snapshot only on a due frame and presents the UI
/// over the last image otherwise. Frames arrive at the display's rate, so a frame landing a little before the exact
/// due time still counts: without that tolerance, vsync jitter would turn 30 FPS on a 60 Hz display into 20.
/// <para>
/// Motion history advances once per published snapshot, so camera and object motion vectors span the gap between
/// drawn frames and temporal anti-aliasing stays correct. Skinned meshes are the exception: their previous pose
/// advances with every animation update, so at a low limit in Play mode their motion vectors cover only the last
/// editor frame, and animated characters can ghost slightly.
/// </para>
/// </remarks>
public sealed class ViewportFrameLimiter
{
	// The fraction of an interval a frame may arrive early and still draw.
	private const double EarlyTolerance = 0.25;

	private TimeSpan _nextFrameTime;
	private bool _hasNextFrameTime;

	/// <summary>
	/// Whether the frame at <paramref name="now"/> draws the scene, given a limit of
	/// <paramref name="maxFramesPerSecond"/>. When it does, the next one becomes due an interval later.
	/// </summary>
	public bool TryBeginFrame(TimeSpan now, int maxFramesPerSecond)
	{
		if (maxFramesPerSecond <= 0)
		{
			Reset();
			return true;
		}

		var interval = TimeSpan.FromSeconds(1.0 / maxFramesPerSecond);
		if (_hasNextFrameTime == false)
		{
			_nextFrameTime = now + interval;
			_hasNextFrameTime = true;
			return true;
		}

		if (now < _nextFrameTime - interval * EarlyTolerance)
		{
			return false;
		}

		// Keep the cadence across small delays, but restart it after a stall rather than catching up with a burst.
		if (now - _nextFrameTime > interval)
		{
			_nextFrameTime = now;
		}

		_nextFrameTime += interval;
		return true;
	}

	/// <summary>Makes the next frame due immediately, for a frame drawn outside the limit.</summary>
	public void Reset()
	{
		_hasNextFrameTime = false;
	}
}
