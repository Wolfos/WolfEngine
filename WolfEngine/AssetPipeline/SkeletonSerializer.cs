using WolfEngine.Animation;

namespace WolfEngine.AssetPipeline;

public static class SkeletonSerializer
{
	private static ReadOnlySpan<byte> Magic => "WESK"u8;
	public const int CurrentVersion = 1;

	public static void Write(string path, ImportedSkeletonAssetFile skeleton)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(skeleton);

		AssetBinaryPrimitives.WriteFileAtomically(path, writer =>
		{
			writer.Write(Magic);
			writer.Write(CurrentVersion);
			writer.Write(skeleton.Name ?? string.Empty);
			AssetBinaryPrimitives.WriteStringArray(writer, skeleton.BoneNames);
			AssetBinaryPrimitives.WriteInt32Array(writer, skeleton.ParentIndices);
			WriteBoneTransformArray(writer, skeleton.BindPoseLocal);
			AssetBinaryPrimitives.WriteMatrix4x4Array(writer, skeleton.InverseBindMatrices);
		});
	}

	public static ImportedSkeletonAssetFile Read(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		if (File.Exists(path) == false)
		{
			throw new FileNotFoundException($"Imported skeleton artifact '{path}' was not found.", path);
		}

		using var stream = File.OpenRead(path);
		return Read(stream);
	}

	public static ImportedSkeletonAssetFile Read(Stream stream)
	{
		ArgumentNullException.ThrowIfNull(stream);
		using var reader = new BinaryReader(stream);
		var magic = reader.ReadBytes(Magic.Length);
		if (magic.AsSpan().SequenceEqual(Magic) == false)
		{
			throw new InvalidOperationException("Imported skeleton artifact has an invalid header.");
		}

		var version = reader.ReadInt32();
		if (version != CurrentVersion)
		{
			throw new InvalidOperationException(
				$"Unsupported imported skeleton artifact version {version}. Expected {CurrentVersion}.");
		}

		return new ImportedSkeletonAssetFile
		{
			Version = version,
			Name = reader.ReadString(),
			BoneNames = AssetBinaryPrimitives.ReadStringArray(reader),
			ParentIndices = AssetBinaryPrimitives.ReadInt32Array(reader),
			BindPoseLocal = ReadBoneTransformArray(reader),
			InverseBindMatrices = AssetBinaryPrimitives.ReadMatrix4x4Array(reader)
		};
	}

	internal static void WriteBoneTransformArray(BinaryWriter writer, BoneTransform[] values)
	{
		values ??= [];
		writer.Write(values.Length);
		for (var i = 0; i < values.Length; i++)
		{
			var value = values[i];
			writer.Write(value.Position.X);
			writer.Write(value.Position.Y);
			writer.Write(value.Position.Z);
			writer.Write(value.Rotation.X);
			writer.Write(value.Rotation.Y);
			writer.Write(value.Rotation.Z);
			writer.Write(value.Rotation.W);
			writer.Write(value.Scale.X);
			writer.Write(value.Scale.Y);
			writer.Write(value.Scale.Z);
		}
	}

	internal static BoneTransform[] ReadBoneTransformArray(BinaryReader reader)
	{
		var count = reader.ReadInt32();
		var values = new BoneTransform[count];
		for (var i = 0; i < count; i++)
		{
			values[i] = new BoneTransform(
				new System.Numerics.Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
				new System.Numerics.Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
				new System.Numerics.Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
		}

		return values;
	}
}
