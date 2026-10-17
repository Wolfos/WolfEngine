using System.Numerics;

namespace WolfEngine.Rendering;

public readonly record struct FogVolumePacket(FogVolume Volume, Matrix4x4 Transform);
