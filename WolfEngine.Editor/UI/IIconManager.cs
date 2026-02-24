namespace WolfEngine.Editor.UI;

public interface IIconManager
{
	nint Get(string name);
	bool TryGet(string name, out nint textureId);
}

