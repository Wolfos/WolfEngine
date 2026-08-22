namespace WolfEngine.Rendering.Abstraction;

public interface IGpuSubmissionTimeline
{
	ulong LastSubmittedId { get; }
	ulong CompletedId { get; }

	void PumpCompleted();
}
