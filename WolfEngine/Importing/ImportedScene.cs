using WolfEngine.ECS;

namespace WolfEngine.Importing;

public record ImportedScene(
	List<ImportedMaterial> Materials,
	List<ImportedTexture> Textures,
	List<ImportedMesh> Meshes
);

public record struct ImportedMaterial(
	IReadOnlyCollection<ImportedMaterialProperty> Properties
);

public record struct ImportedMaterialProperty(
	string Name
);

public record struct ImportedTexture(
	string Name
);

public record struct ImportedMesh(
	Transform Transform,
	Mesh Mesh,
	int MaterialIndex
);