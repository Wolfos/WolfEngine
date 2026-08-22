using System.Numerics;

namespace WolfEngine.Rendering;

public readonly struct DecalProjectorPacket
{
	public DecalProjectorPacket(DecalProjector projector, Matrix4x4 transform)
	{
		Projector = projector;
		Transform = transform;
	}

	public DecalProjector Projector { get; }

	public Matrix4x4 Transform { get; }
}
