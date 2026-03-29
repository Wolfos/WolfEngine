using System.Numerics;

namespace WolfEngine.Mathematics;

public struct Box
{
	public Vector3 Center;
	public Vector3 Size;

	public Vector3 HalfSize => Size * 0.5f;
}