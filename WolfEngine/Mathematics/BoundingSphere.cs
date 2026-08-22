using System.Numerics;

namespace WolfEngine.Mathematics;

public readonly struct BoundingSphere
{
    public BoundingSphere(Vector3 center, float radius)
    {
        Center = center;
        Radius = radius;
    }

    public Vector3 Center { get; }

    public float Radius { get; }
}
