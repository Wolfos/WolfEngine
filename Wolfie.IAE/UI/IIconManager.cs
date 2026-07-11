namespace Wolfie.IAE.UI;

public interface IIconManager
{
	nint Get(string name);
	bool TryGet(string name, out nint textureId);
	IReadOnlyList<string> GetNames();
}
