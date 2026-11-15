namespace WolfEngine.Rendering;

/// <summary>Reads the staleness inputs as they stand at the moment of the call.</summary>
public interface IDrawRecordStalenessInputSource
{
	DrawRecordStalenessInputs Read();
}
