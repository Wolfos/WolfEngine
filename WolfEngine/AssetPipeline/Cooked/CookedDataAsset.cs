using WolfEngine.Serialization;

namespace WolfEngine.AssetPipeline;

/// <summary>A data asset cooked to binary: the asset's type key, its layout hash, then its value.</summary>
public static class CookedDataAsset
{
	public const int CurrentVersion = 1;

	private static readonly uint Magic = CookedFileHeader.Tag('W', 'D', 'A', 'T');

	public static byte[] Write(object value)
	{
		ArgumentNullException.ThrowIfNull(value);

		var type = SerializedTypeRegistry.GetDataAsset(value.GetType());
		var payload = new BinaryAssetWriter();
		type.WriteBoxed(payload, value);

		var writer = new BinaryAssetWriter(payload.Length + 64);
		CookedFileHeader.Write(writer, Magic, CurrentVersion);
		writer.WriteString(type.Key);
		writer.WriteUInt64(type.LayoutHash);
		writer.WriteBlock(payload.WrittenSpan);

		return writer.ToArray();
	}

	/// <summary>Reads a cooked data asset, failing when it is not a <paramref name="expectedType"/>.</summary>
	public static object Read(ReadOnlySpan<byte> data, Type expectedType)
	{
		ArgumentNullException.ThrowIfNull(expectedType);

		var reader = new BinaryAssetReader(data);
		CookedFileHeader.Read(ref reader, Magic, CurrentVersion, "data asset");
		var key = reader.ReadString() ?? throw new InvalidDataException("The cooked data asset has no type key.");
		var layoutHash = reader.ReadUInt64();
		var type = SerializedTypeRegistry.GetDataAsset(key);
		type.EnsureLayout(layoutHash);
		if (expectedType.IsAssignableFrom(type.Type) == false)
		{
			throw new InvalidDataException(
				$"The cooked data asset is a '{type.Type.FullName}', which is not a '{expectedType.FullName}'.");
		}

		var payload = new BinaryAssetReader(reader.ReadBlock());
		var value = type.ReadBoxed(ref payload);
		if (payload.IsAtEnd == false)
		{
			throw new InvalidDataException(
				$"The cooked '{type.Key}' data asset left {payload.Remaining} bytes unread; it does not match this build's serializer.");
		}

		return value;
	}
}
