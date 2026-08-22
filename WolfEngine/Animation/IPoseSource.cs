namespace WolfEngine.Animation;

/// <summary>
/// Anything that can produce a pose for a skeleton. <see cref="SingleClipPoseSource"/> is the only
/// implementation today; the node-based animator graph will be another, and the components that
/// consume poses are written against this interface so that swap costs nothing.
/// </summary>
public interface IPoseSource
{
	Skeleton Skeleton { get; }

	/// <summary>
	/// Advances by <paramref name="deltaTime"/> and writes the resulting pose. Passing zero
	/// re-evaluates the current time without advancing, which is what editor scrubbing does.
	/// </summary>
	void Evaluate(float deltaTime, Pose destination);
}
