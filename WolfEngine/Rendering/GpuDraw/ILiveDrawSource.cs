using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering;

/// <summary>One draw that is live in the shared draw tables, as draw records need it.</summary>
public readonly record struct LiveDraw(uint DrawIndex, GpuDrawExecutionKey ExecutionKey, Mesh Mesh);

/// <summary>
/// The draws that are live in the shared draw tables this frame, across every view's draw database. Draw records
/// read it to re-encode a set in full, and to tell whether a structural change still applies.
/// </summary>
public interface ILiveDrawSource
{
	/// <summary>False once the draw has been removed, or its slot reused by another draw.</summary>
	bool IsCurrent(in GpuDrawHandle drawHandle);

	/// <summary>Fills <paramref name="destination"/> with every live draw that records can encode, each resident.</summary>
	void CollectResidentDraws(List<LiveDraw> destination);

	/// <summary>Makes a mesh named by a structural change resident before it is encoded.</summary>
	void EnsureResident(Mesh mesh);
}
