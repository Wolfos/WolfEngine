namespace WolfEngine.Importing;

public interface IThreeDFileImporter
{
    ImportedScene Import(string filename);
}
