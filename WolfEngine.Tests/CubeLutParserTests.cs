using System.Globalization;
using System.Numerics;
using System.Text;
using WolfEngine.AssetPipeline;
using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine.Tests;

[TestFixture]
public class CubeLutParserTests
{
	[Test]
	public void Parse_ReadsHeaderAndKeepsRedFastestOrder()
	{
		const string text = """
			# Comment lines and blank lines are ignored.
			TITLE "Test LUT"
			LUT_3D_SIZE 2
			DOMAIN_MIN 0 0 0
			DOMAIN_MAX 1 1 2

			0 0 0
			1 0 0
			0 1 0
			1 1 0
			0 0 1
			1 0 1
			0 1 1
			1 1 1
			""";

		var lut = CubeLutParser.Parse(new StringReader(text), "test.cube");

		Assert.That(lut.Title, Is.EqualTo("Test LUT"));
		Assert.That(lut.Size, Is.EqualTo(2));
		Assert.That(lut.DomainMin, Is.EqualTo(Vector3.Zero));
		Assert.That(lut.DomainMax, Is.EqualTo(new Vector3(1, 1, 2)));
		// Red varies fastest, so lattice point (r, g, b) is at r + g * N + b * N * N.
		Assert.That(lut.Values[1], Is.EqualTo(new Vector3(1, 0, 0)));
		Assert.That(lut.Values[2], Is.EqualTo(new Vector3(0, 1, 0)));
		Assert.That(lut.Values[4], Is.EqualTo(new Vector3(0, 0, 1)));
	}

	[Test]
	public void Parse_AcceptsResolveInputRange()
	{
		var text = "LUT_3D_INPUT_RANGE -0.5 1.5\n" + IdentityLut(2);

		var lut = CubeLutParser.Parse(new StringReader(text), "range.cube");

		Assert.That(lut.DomainMin, Is.EqualTo(new Vector3(-0.5f)));
		Assert.That(lut.DomainMax, Is.EqualTo(new Vector3(1.5f)));
	}

	[Test]
	public void Parse_WrongEntryCount_Throws()
	{
		const string text = """
			LUT_3D_SIZE 2
			0 0 0
			1 0 0
			""";

		var exception = Assert.Throws<InvalidDataException>(() => CubeLutParser.Parse(new StringReader(text), "short.cube"));
		Assert.That(exception!.Message, Does.Contain("expected 8 entries"));
	}

	[Test]
	public void Parse_OneDimensionalLut_IsRejected()
	{
		const string text = """
			LUT_1D_SIZE 2
			0 0 0
			1 1 1
			""";

		var exception = Assert.Throws<InvalidDataException>(() => CubeLutParser.Parse(new StringReader(text), "1d.cube"));
		Assert.That(exception!.Message, Does.Contain("1D"));
	}

	[Test]
	public void Parse_DataBeforeSize_Throws()
	{
		const string text = """
			0 0 0
			LUT_3D_SIZE 2
			""";

		Assert.Throws<InvalidDataException>(() => CubeLutParser.Parse(new StringReader(text), "order.cube"));
	}

	[Test]
	public void Parse_MalformedDataLine_ReportsLine()
	{
		var text = IdentityLut(2).Replace("1 1 1", "1 1", StringComparison.Ordinal);

		var exception = Assert.Throws<InvalidDataException>(() => CubeLutParser.Parse(new StringReader(text), "bad.cube"));
		Assert.That(exception!.Message, Does.Contain("bad.cube line"));
	}

	[Test]
	public void Parse_InvertedDomain_Throws()
	{
		var text = "DOMAIN_MIN 1 1 1\nDOMAIN_MAX 0 0 0\n" + IdentityLut(2);

		Assert.Throws<InvalidDataException>(() => CubeLutParser.Parse(new StringReader(text), "domain.cube"));
	}

	[Test]
	public void Artifact_RoundTripsHeaderAndHalfFloatTexels()
	{
		var lut = CubeLutParser.Parse(new StringReader("TITLE \"Identity\"\n" + IdentityLut(3)), "identity.cube");
		var artifact = ColorLookupTableArtifactSerializer.CreateArtifact(lut);

		using var stream = new MemoryStream();
		ColorLookupTableArtifactSerializer.Write(stream, artifact);
		stream.Position = 0;
		var read = ColorLookupTableArtifactSerializer.Read(stream);

		Assert.That(read.Title, Is.EqualTo("Identity"));
		Assert.That(read.Size, Is.EqualTo(3));
		Assert.That(read.DomainMin, Is.EqualTo(Vector3.Zero));
		Assert.That(read.DomainMax, Is.EqualTo(Vector3.One));
		Assert.That(read.Rgba16FloatTexels, Is.EqualTo(artifact.Rgba16FloatTexels));
		Assert.That(read.Rgba16FloatTexels.Length, Is.EqualTo(3 * 3 * 3 * ColorLookupTableArtifactSerializer.BytesPerTexel));

		// Texel (2, 1, 0) of an identity LUT is (1, 0.5, 0) with opaque alpha.
		var offset = (2 + 1 * 3) * ColorLookupTableArtifactSerializer.BytesPerTexel;
		Assert.That(BitConverter.ToHalf(read.Rgba16FloatTexels, offset), Is.EqualTo((Half)1.0f));
		Assert.That(BitConverter.ToHalf(read.Rgba16FloatTexels, offset + 2), Is.EqualTo((Half)0.5f));
		Assert.That(BitConverter.ToHalf(read.Rgba16FloatTexels, offset + 4), Is.EqualTo((Half)0.0f));
		Assert.That(BitConverter.ToHalf(read.Rgba16FloatTexels, offset + 6), Is.EqualTo(Half.One));
	}

	[Test]
	public void TextureDescriptor_VolumeWithWritableUsage_IsRejected()
	{
		Assert.Throws<ArgumentException>(() => _ = new TextureDescriptor(
			4, 4, TextureFormat.Rgba16Float, TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			dimension: TextureDimension.Texture3D, depth: 4));
		Assert.Throws<ArgumentOutOfRangeException>(() => _ = new TextureDescriptor(
			4, 4, TextureFormat.Rgba16Float, TextureUsage.ShaderResource, depth: 4));
	}

	[Test]
	public void ColorLookupTable_RequiresMatchingVolume()
	{
		var texels = new byte[2 * 2 * 2 * ColorLookupTableArtifactSerializer.BytesPerTexel];
		var volume = Texture.Create3D("lut", 2, 2, 2, TextureFormat.Rgba16Float, [new TextureMipData(2, 2, texels, 2)]);
		var flat = new Texture("flat", 2, 2, false, TextureFormat.Rgba16Float, [new TextureMipData(2, 2, new byte[32])]);

		Assert.DoesNotThrow(() => _ = new ColorLookupTable("lut", 2, Vector3.Zero, Vector3.One, volume));
		Assert.Throws<ArgumentException>(() => _ = new ColorLookupTable("flat", 2, Vector3.Zero, Vector3.One, flat));
	}

	internal static string IdentityLut(int size)
	{
		var builder = new StringBuilder();
		builder.Append("LUT_3D_SIZE ").Append(size).Append('\n');
		for (var b = 0; b < size; b++)
		for (var g = 0; g < size; g++)
		for (var r = 0; r < size; r++)
		{
			builder.Append(string.Create(CultureInfo.InvariantCulture,
				$"{FormatLattice(r, size)} {FormatLattice(g, size)} {FormatLattice(b, size)}\n"));
		}

		return builder.ToString();
	}

	private static string FormatLattice(int index, int size) =>
		(index / (float)(size - 1)).ToString("0.######", CultureInfo.InvariantCulture);
}
