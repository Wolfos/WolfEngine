using WolfEngine.Editor.Projects;
using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class AlphaCoverageMipTests
{
	private const byte Cutoff = 200;

	[Test]
	public void PlainMips_LoseCoverageOfThinStrokes()
	{
		var mips = TextureMipGenerator.GenerateMipChain(CreateNoisyAlpha(), TextureFormat.Rgba8Unorm);

		Assert.That(TextureMipGenerator.GetAlphaCoverage(mips[2].Data, Cutoff, 1.0f), Is.LessThan(0.1f));
	}

	[Test]
	public void PreservedMips_KeepTheBaseCoverage()
	{
		var baseMip = CreateNoisyAlpha();
		var target = TextureMipGenerator.GetAlphaCoverage(baseMip.Data, Cutoff, 1.0f);

		var mips = TextureMipGenerator.GenerateMipChain(baseMip, TextureFormat.Rgba8Unorm, Cutoff / 255.0f);

		for (var level = 1; level < mips.Length - 2; level++)
		{
			Assert.That(
				TextureMipGenerator.GetAlphaCoverage(mips[level].Data, Cutoff, 1.0f),
				Is.EqualTo(target).Within(0.08f),
				$"mip {level}");
		}
	}

	/// <summary>
	/// Random alpha per texel, so about a fifth passes the cutoff at full size, and averaging pulls every texel towards
	/// half and below it.
	/// </summary>
	private static TextureMipData CreateNoisyAlpha()
	{
		const int size = 64;
		var data = new byte[size * size * 4];
		var random = new Random(7);
		for (var y = 0; y < size; y++)
		{
			for (var x = 0; x < size; x++)
			{
				var index = ((y * size) + x) * 4;
				data[index] = 40;
				data[index + 1] = 120;
				data[index + 2] = 30;
				data[index + 3] = (byte)random.Next(256);
			}
		}

		return new TextureMipData(size, size, data, 1);
	}
}
