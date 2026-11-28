using WolfEngine.Editor.UnityImport;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class UnityDetailConversionTests
{
	private const int Resolution = 16;

	[Test]
	public void Fit_RecoversDensityPaintedWhereALayerIs()
	{
		// Layer 0 on the left half, layer 1 on the right; four plants per cell on the left only.
		var alphamaps = CreateHalves();
		var details = CreateDetails((x, _) => x < Resolution / 2 ? (ushort)4 : (ushort)0);

		var fit = UnityDetailConversion.Fit(alphamaps, Resolution, 2, details, Resolution, 1.0f, Resolution * Resolution);

		Assert.Multiple(() =>
		{
			Assert.That(fit.Densities[0], Is.EqualTo(4.0f).Within(0.3f));
			Assert.That(fit.Densities[1], Is.EqualTo(0.0f).Within(0.3f));
			Assert.That(fit.RelativeError, Is.LessThan(0.2f));
			Assert.That(fit.FittedPlants, Is.EqualTo(fit.PaintedPlants).Within(fit.PaintedPlants * 0.05));
		});
	}

	[Test]
	public void Fit_NeverGivesALayerNegativeDensity()
	{
		// Painted everywhere except on layer 1, which least squares without the bound would make negative where the
		// layers overlap.
		var alphamaps = CreateHalves();
		var details = CreateDetails((x, _) => x < (Resolution / 2) + 2 ? (ushort)6 : (ushort)0);

		var fit = UnityDetailConversion.Fit(alphamaps, Resolution, 2, details, Resolution, 1.0f, Resolution * Resolution);

		Assert.That(fit.Densities, Has.All.GreaterThanOrEqualTo(0.0f));
	}

	[Test]
	public void Fit_ReportsPaintingTheLayersDoNotExplain()
	{
		// A checkerboard of patches larger than a fitting block, on a single layer: the layer's mean density is right, but
		// the patches are lost.
		var alphamaps = new byte[Resolution * Resolution];
		Array.Fill(alphamaps, (byte)255);
		var details = CreateDetails((x, z) => (((x / 4) + (z / 4)) % 2) == 0 ? (ushort)8 : (ushort)0);

		var fit = UnityDetailConversion.Fit(alphamaps, Resolution, 1, details, Resolution, 1.0f, Resolution * Resolution);

		Assert.Multiple(() =>
		{
			Assert.That(fit.Densities[0], Is.EqualTo(4.0f).Within(0.01f));
			Assert.That(fit.RelativeError, Is.GreaterThan(0.5f));
		});
	}

	private static byte[] CreateHalves()
	{
		var plane = Resolution * Resolution;
		var alphamaps = new byte[plane * 2];
		for (var z = 0; z < Resolution; z++)
		{
			for (var x = 0; x < Resolution; x++)
			{
				var left = x < Resolution / 2;
				alphamaps[(z * Resolution) + x] = left ? (byte)255 : (byte)0;
				alphamaps[plane + (z * Resolution) + x] = left ? (byte)0 : (byte)255;
			}
		}

		return alphamaps;
	}

	private static ushort[] CreateDetails(Func<int, int, ushort> value)
	{
		var details = new ushort[Resolution * Resolution];
		for (var z = 0; z < Resolution; z++)
		{
			for (var x = 0; x < Resolution; x++)
			{
				details[(z * Resolution) + x] = value(x, z);
			}
		}

		return details;
	}
}
