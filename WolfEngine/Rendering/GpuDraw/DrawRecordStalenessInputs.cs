namespace WolfEngine.Rendering;

/// <summary>
/// The generations of everything whose replacement makes draw records stale, read at one moment. A record set
/// remembers the inputs it was encoded against; any difference from the current inputs means it must be
/// re-encoded before it is executed.
/// </summary>
public readonly record struct DrawRecordStalenessInputs(
	ulong SharedDrawBufferGeneration,
	ulong PackedGeometryGeneration,
	ulong BindlessTableGeneration);
