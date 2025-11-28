using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine.Importing;

public record ImportedScene(
	List<ImportedMaterial> Materials,
	List<ImportedTexture> Textures,
	List<ImportedMesh> Meshes
);

public record struct ImportedMaterial(
	Vector4 BaseColor
);

public record struct ImportedTexture(
	string Name
);

public record struct ImportedMesh(
	Transform Transform,
	Mesh Mesh,
	int MaterialIndex
);