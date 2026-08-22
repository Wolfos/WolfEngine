using WolfEngine.AssetPipeline;

namespace WolfEngine.Importing;

public interface IThreeDFileImporter
{
    ImportedScene Import(string filename, ModelImportSettings settings);
}
