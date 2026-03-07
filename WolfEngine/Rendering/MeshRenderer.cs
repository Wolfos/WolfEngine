using WolfEngine.ECS;

namespace WolfEngine;

public struct MeshRenderer: IEntityComponent
{
	public Material Material;
	public Mesh Mesh;

	public bool IsValid => Material != null && Mesh != null;
}