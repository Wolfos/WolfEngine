using System.Buffers.Binary;
using WolfEngine.Editor.Tooling.Splash;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Splash;

namespace WolfEngine.Editor.Tests;

public sealed class SplashScreenBakeTests
{
	private static readonly Lazy<SplashScreenContent> Baked = new(() => SplashScreenBaker.Bake(FindSplashSourceDirectory()));

	[Test]
	public void LogoSvgReadsPathsInPaintOrderWithTransformsApplied()
	{
		var paths = SvgPathReader.Read(Path.Combine(FindSplashSourceDirectory(), SplashScreenBaker.LogoFileName), 0.25f);

		Assert.That(paths, Has.Count.EqualTo(5));
		Assert.That(paths[0].Fill.R * 255.0f, Is.EqualTo(52.0f).Within(0.01f), "The silhouette paints first.");
		Assert.That(paths[1].Fill.R * 255.0f, Is.EqualTo(220.0f).Within(0.01f));
		Assert.That(paths.All(path => path.Path.FillRule == VectorFillRule.EvenOdd), "The root style's fill rule is inherited.");

		// The two ears are one path mirrored by a nested transform, so they land either side of the centre line.
		var ear = paths[2].Path.Contours[0];
		var mirroredEar = paths[4].Path.Contours[0];
		var centreLine = paths[0].Path.Contours.SelectMany(contour => contour).Average(point => point.X);
		Assert.That(ear.Average(point => point.X), Is.GreaterThan(centreLine));
		Assert.That(mirroredEar.Average(point => point.X), Is.LessThan(centreLine));
		Assert.That(
			ear.Average(point => point.X) - centreLine,
			Is.EqualTo(centreLine - mirroredEar.Average(point => point.X)).Within(2.0f));
	}

	[Test]
	public void SvgReaderRejectsEmbeddedImages()
	{
		var path = Path.Combine(Path.GetTempPath(), "WolfSplashImage-" + Guid.NewGuid().ToString("N") + ".svg");
		File.WriteAllText(
			path,
			"""
			<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 10 10">
				<use xlink:href="#image" x="0" y="0"/>
				<defs><image id="image" width="10" height="10" xlink:href="data:image/png;base64,AA=="/></defs>
			</svg>
			""");
		try
		{
			Assert.That(
				() => SvgPathReader.Read(path, 0.25f),
				Throws.InstanceOf<InvalidDataException>().With.Message.Contains("embedded image"));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Test]
	public void SvgPathDataParsesRelativeCommandsAndPackedNumbers()
	{
		var path = Path.Combine(Path.GetTempPath(), "WolfSplashPath-" + Guid.NewGuid().ToString("N") + ".svg");
		// A 10x10 square written with relative commands and numbers packed without separators.
		File.WriteAllText(
			path,
			"""
			<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 20">
				<path transform="translate(5,0)" fill="#ff0000" d="m0 0h10v10h-10z"/>
				<path fill="rgb(0,0,255)" d="M0,0L.5.5-1e1 3z"/>
			</svg>
			""");
		try
		{
			var paths = SvgPathReader.Read(path, 0.25f);

			Assert.That(paths[0].Path.Contours[0], Is.EqualTo(new[]
			{
				new System.Numerics.Vector2(5, 0),
				new System.Numerics.Vector2(15, 0),
				new System.Numerics.Vector2(15, 10),
				new System.Numerics.Vector2(5, 10)
			}));
			Assert.That(paths[0].Fill.R, Is.EqualTo(1.0f));
			Assert.That(paths[1].Path.Contours[0], Is.EqualTo(new[]
			{
				new System.Numerics.Vector2(0, 0),
				new System.Numerics.Vector2(0.5f, 0.5f),
				new System.Numerics.Vector2(-10, 3)
			}));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Test]
	public void BakedLogoIsFilledInsideAndEmptyAtItsCorners()
	{
		var logo = Baked.Value.Logo;

		Assert.That(logo.Format, Is.EqualTo(TextureFormat.Rgba16Float));
		Assert.That(Sample(logo, 4, 0, 0), Is.GreaterThan(0.0f), "Corners lie outside the silhouette.");
		Assert.That(Sample(logo, 4, logo.Width / 2, logo.Height / 2, 0), Is.LessThan(0.0f), "The centre lies inside the silhouette.");
		// The exact centre is the dark V between the brows; the muzzle below it is highlight.
		Assert.That(Sample(logo, 4, logo.Width / 2, logo.Height * 3 / 5, 1), Is.LessThan(0.0f), "The face highlight covers the muzzle.");

		// The nose detail sits on the centre line, low in the face.
		var noseFound = Enumerable.Range(logo.Height / 2, logo.Height / 3).Any(y => Sample(logo, 4, logo.Width / 2, y, 2) < 0.0f);
		Assert.That(noseFound, "The nose is a detail on the centre line.");
	}

	[Test]
	public void TraceStartsAtTheTopAndBothHalvesMeetAtTheChin()
	{
		var logo = Baked.Value.Logo;
		var centreX = logo.Width / 2;

		// Walk down the centre line from the top and bottom until the silhouette edge.
		var top = Enumerable.Range(0, logo.Height).First(y => Sample(logo, 4, centreX, y, 0) <= 0.0f);
		var bottom = Enumerable.Range(0, logo.Height).Reverse().First(y => Sample(logo, 4, centreX, y, 0) <= 0.0f);
		Assert.That(Sample(logo, 4, centreX, top, 3), Is.LessThan(0.05f), "The trace starts between the ears.");
		Assert.That(Sample(logo, 4, centreX, bottom, 3), Is.GreaterThan(0.95f), "The two halves meet at the chin.");

		// The logo is symmetric, so mirrored texels on the outline are equally far along their halves.
		var asymmetric = 0;
		var onOutline = 0;
		for (var y = 0; y < logo.Height; y += 3)
		{
			for (var x = 0; x < logo.Width / 2; x += 3)
			{
				if (MathF.Abs(Sample(logo, 4, x, y, 0)) > 2.0f)
				{
					continue;
				}

				onOutline++;
				var mirrored = Sample(logo, 4, logo.Width - 1 - x, y, 3);
				if (MathF.Abs(Sample(logo, 4, x, y, 3) - mirrored) > 0.02f)
				{
					asymmetric++;
				}
			}
		}

		Assert.That(onOutline, Is.GreaterThan(100));
		Assert.That(asymmetric, Is.LessThan(onOutline / 50), "Both halves of the trace advance together.");
	}

	[Test]
	public void BakedTextSitsBelowTheLogoWithBothLinesCentred()
	{
		var content = Baked.Value;
		var text = content.Text;

		Assert.That(text.Format, Is.EqualTo(TextureFormat.Rg16Float));
		Assert.That(text.Center.Y - text.Size.Y * 0.5f, Is.GreaterThan(content.Logo.Center.Y), "The text block sits below the logo.");
		Assert.That(text.Center.X, Is.EqualTo(0.0f).Within(1.0f), "The text block is centred.");

		// "Powered by" is the upper line and the wordmark the lower; each channel has ink only in its own band.
		var poweredByRows = InkRows(text, 0);
		var wordmarkRows = InkRows(text, 1);
		Assert.That(poweredByRows.Max(), Is.LessThan(wordmarkRows.Min()));

		// The wordmark is wider than "Powered by" at the larger size.
		Assert.That(InkColumns(text, 1).Count(), Is.GreaterThan(InkColumns(text, 0).Count()));
	}

	[Test]
	public void SplashScreenContentRoundTrips()
	{
		var content = Baked.Value;

		var read = SplashScreenSerializer.Read(SplashScreenSerializer.Write(content));

		Assert.That(read.Logo.Texels, Is.EqualTo(content.Logo.Texels));
		Assert.That(read.Text.Texels, Is.EqualTo(content.Text.Texels));
		Assert.That(read.Logo.Center, Is.EqualTo(content.Logo.Center));
		Assert.That(read.Text.Size, Is.EqualTo(content.Text.Size));
		Assert.That(read.HighlightColor.G, Is.EqualTo(content.HighlightColor.G));
	}

	[Test]
	public void SplashScreenSerializerRejectsTruncatedData()
	{
		var data = SplashScreenSerializer.Write(Baked.Value);

		Assert.That(
			() => SplashScreenSerializer.Read(data.AsSpan(0, data.Length - 1)),
			Throws.InstanceOf<InvalidDataException>());
	}

	private static IEnumerable<int> InkRows(SplashScreenLayer layer, int channel) =>
		Enumerable.Range(0, layer.Height)
			.Where(y => Enumerable.Range(0, layer.Width).Any(x => Sample(layer, 2, x, y, channel) < 0.0f));

	private static IEnumerable<int> InkColumns(SplashScreenLayer layer, int channel) =>
		Enumerable.Range(0, layer.Width)
			.Where(x => Enumerable.Range(0, layer.Height).Any(y => Sample(layer, 2, x, y, channel) < 0.0f));

	private static float Sample(SplashScreenLayer layer, int channels, int x, int y, int channel = 0)
	{
		var offset = ((y * layer.Width + x) * channels + channel) * 2;

		return (float)BinaryPrimitives.ReadHalfLittleEndian(layer.Texels.AsSpan(offset, 2));
	}

	private static string FindSplashSourceDirectory()
	{
		var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
		while (directory is not null)
		{
			var candidate = Path.Combine(directory.FullName, "WolfEngine", "BuiltInContent", "Splash");
			if (File.Exists(Path.Combine(candidate, SplashScreenBaker.LogoFileName)))
			{
				return candidate;
			}

			directory = directory.Parent;
		}

		throw new DirectoryNotFoundException("The engine splash sources were not found above the test directory.");
	}
}
