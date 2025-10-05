using System.Numerics;

namespace WolfEngine;

public class Mesh
{
    public Mesh(IReadOnlyList<Vector4> vertices, IReadOnlyList<uint> indices)
    {
        Vertices = vertices?.ToArray() ?? throw new ArgumentNullException(nameof(vertices));
        if (Vertices.Length == 0)
        {
            throw new ArgumentException("Mesh must contain at least one vertex.", nameof(vertices));
        }

        Indices = indices?.ToArray() ?? throw new ArgumentNullException(nameof(indices));
        if (Indices.Length == 0)
        {
            throw new ArgumentException("Mesh must contain at least one index.", nameof(indices));
        }
    }

    public Vector4[] Vertices { get; }
    public uint[] Indices { get; }
}
