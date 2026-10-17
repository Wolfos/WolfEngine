using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

public enum MeshIndexStream
{
    Main,
    ShadowOpaque,
    ShadowAlphaTest
}

/// <summary>
/// Uploads precomputed shadow indexing, sharing the original vertex stream and preserving every triangle.
/// Runtime-created meshes fall back to original indices; skinned instances reuse their source's ranges.
/// </summary>
internal sealed class ShadowIndexBuffers
{
    private readonly uint[] _main;
    internal uint[] Opaque { get; }
    internal uint[] AlphaTest { get; }
    internal ulong MainOffsetBytes { get; }
    internal ulong OpaqueOffsetBytes { get; }
    internal ulong AlphaTestOffsetBytes { get; }
    internal ulong EndOffsetBytes { get; }

    internal ShadowIndexBuffers(Mesh mesh, ulong mainOffsetBytes)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        _main = mesh.Indices;
        Opaque = mesh.ShadowOpaqueIndices;
        AlphaTest = mesh.ShadowAlphaTestIndices;

        MainOffsetBytes = mainOffsetBytes;
        var length = checked((ulong)_main.Length * sizeof(uint));
        var end = checked(mainOffsetBytes + length);
        OpaqueOffsetBytes = ReferenceEquals(Opaque, _main) ? mainOffsetBytes : end;
        if (!ReferenceEquals(Opaque, _main)) end = checked(end + length);
        AlphaTestOffsetBytes = ReferenceEquals(AlphaTest, _main) ? mainOffsetBytes
            : ReferenceEquals(AlphaTest, Opaque) ? OpaqueOffsetBytes : end;
        if (!ReferenceEquals(AlphaTest, _main) && !ReferenceEquals(AlphaTest, Opaque)) end = checked(end + length);
        EndOffsetBytes = end;
    }

    internal void Upload(IWritableGpuBuffer buffer)
    {
        buffer.Write<uint>(_main, MainOffsetBytes / sizeof(uint));
        if (!ReferenceEquals(Opaque, _main)) buffer.Write<uint>(Opaque, OpaqueOffsetBytes / sizeof(uint));
        if (!ReferenceEquals(AlphaTest, _main) && !ReferenceEquals(AlphaTest, Opaque))
            buffer.Write<uint>(AlphaTest, AlphaTestOffsetBytes / sizeof(uint));
    }

    internal void AssignOffsets(Mesh mesh)
    {
        mesh.PackedIndexOffsetBytes = MainOffsetBytes;
        mesh.PackedShadowOpaqueIndexOffsetBytes = OpaqueOffsetBytes;
        mesh.PackedShadowAlphaTestIndexOffsetBytes = AlphaTestOffsetBytes;
    }

}
