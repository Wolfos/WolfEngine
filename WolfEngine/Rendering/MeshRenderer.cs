using WolfEngine.ECS;

namespace WolfEngine;

public struct MeshRenderer: IEntityComponent
{
	public Material Material { get; set; }
	public Mesh Mesh { get; set; }
}