using WolfEngine.Serialization;

namespace WolfEngine.AssetPipeline;

/// <summary>
/// The four-byte tag and version every cooked binary file starts with, so a file of the wrong kind, or one cooked
/// by an older build, fails with a message that says so rather than as garbled data.
/// </summary>
internal static class CookedFileHeader
{
	public static void Write(BinaryAssetWriter writer, uint magic, int version)
	{
		writer.WriteUInt32(magic);
		writer.WriteVarUInt32((uint)version);
	}

	public static void Read(ref BinaryAssetReader reader, uint magic, int version, string description)
	{
		if (reader.Remaining < sizeof(uint))
		{
			throw new InvalidDataException($"The cooked {description} is empty or truncated.");
		}

		var actualMagic = reader.ReadUInt32();
		if (actualMagic != magic)
		{
			throw new InvalidDataException(
				$"This is not a cooked {description} (tag {actualMagic:X8}, expected {magic:X8}). " +
				"Content cooked by an older build stored it as JSON; rebuild the game content.");
		}

		var actualVersion = (int)reader.ReadVarUInt32();
		if (actualVersion != version)
		{
			throw new InvalidDataException(
				$"The cooked {description} has format version {actualVersion}; this build reads version {version}. Rebuild the game content.");
		}
	}

	/// <summary>Packs four ASCII characters into the little-endian tag they read as at the start of a file.</summary>
	public static uint Tag(char a, char b, char c, char d)
	{
		return a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);
	}
}
