using System.Numerics;
using Facebook.Yoga;
using Microsoft.Extensions.DependencyInjection;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.UI.Tests;

[NonParallelizable]
public sealed class ImageTests
{
	private sealed class Registry : IAssetInstanceRegistry
	{
		public Dictionary<Guid, object> Instances { get; } = [];

		public object? GetInstance(Guid assetId, Type expectedType)
		{
			return Instances.GetValueOrDefault(assetId);
		}

		public void RefreshProject(string projectRootPath, AssetDatabase database)
		{
		}

		public void InvalidateAssets(IEnumerable<Guid> assetIds)
		{
		}

		public void ClearCachedInstances()
		{
		}

		public void Clear()
		{
		}
	}

	private Registry _registry = null!;

	[SetUp]
	public void SetUp()
	{
		_registry = new Registry();
		AssetDatabase.SetInstanceRegistry(_registry);
	}

	[TearDown]
	public void TearDown()
	{
		AssetDatabase.ClearInstanceRegistry();
	}

	[Test]
	public void AssetReferenceTextRoundTripsAndAnUnsetReferenceIsEmpty()
	{
		var id = Guid.NewGuid();
		var reference = new AssetRef<Texture> { NodeId = id };

		Assert.Multiple(() =>
		{
			Assert.That(reference.ToString(), Is.EqualTo("asset:" + id.ToString("D")));
			Assert.That(AssetReferenceText.TryParse(reference.ToString(), out var parsed), Is.True);
			Assert.That(parsed, Is.EqualTo(id));
			Assert.That(default(AssetRef<Texture>).ToString(), Is.Empty);
			Assert.That(AssetReferenceText.TryParse("/Assets/UI/Potion.png", out _), Is.False);
			Assert.That(AssetReferenceText.TryParse("asset:" + Guid.Empty.ToString("D"), out _), Is.False);
		});
	}

	[Test]
	public void ImageTakesTheTexturesSizeAndDerivesAMissingSideFromItsAspectRatio()
	{
		var root = new UiNode { Name = "root" };
		var natural = Image(CreateTexture(64, 32));
		var widthOnly = Image(CreateTexture(64, 32), "width-only");
		var heightOnly = Image(CreateTexture(64, 32), "height-only");
		var both = Image(CreateTexture(64, 32), "both");
		var empty = new UiNode { Name = "img" };
		var row = new UiNode { Name = "div" };
		row.Attributes["class"] = "row";
		row.Children.Add(natural);
		row.Children.Add(widthOnly);
		row.Children.Add(heightOnly);
		row.Children.Add(both);
		row.Children.Add(empty);
		root.Children.Add(row);
		var sheet = CssStyleSheet.Parse("""
			.row { flex-direction: row; align-items: flex-start; }
			.width-only { width: 32px; }
			.height-only { height: 48px; }
			.both { width: 10px; height: 40px; }
			""");

		sheet.Apply(root, 800, 600);
		using var layout = new YogaLayoutEngine();
		layout.Layout(root, 800, 600);

		Assert.Multiple(() =>
		{
			Assert.That(new Vector2(natural.Width, natural.Height), Is.EqualTo(new Vector2(64, 32)));
			Assert.That(new Vector2(widthOnly.Width, widthOnly.Height), Is.EqualTo(new Vector2(32, 16)));
			Assert.That(new Vector2(heightOnly.Width, heightOnly.Height), Is.EqualTo(new Vector2(96, 48)));
			Assert.That(new Vector2(both.Width, both.Height), Is.EqualTo(new Vector2(10, 40)));
			Assert.That(new Vector2(empty.Width, empty.Height), Is.EqualTo(Vector2.Zero));
		});
	}

	[Test]
	public void AnAtMostLimitCapsOnlyTheDerivedSide()
	{
		Assert.Multiple(() =>
		{
			Assert.That(
				UiImageLayout.Measure(64, 32, 40, MeasureMode.Exactly, 10, MeasureMode.AtMost),
				Is.EqualTo(new Vector2(40, 10)));
			Assert.That(
				UiImageLayout.Measure(64, 32, 50, MeasureMode.AtMost, 32, MeasureMode.Exactly),
				Is.EqualTo(new Vector2(50, 32)));
			Assert.That(
				UiImageLayout.Measure(64, 32, 10, MeasureMode.AtMost, 10, MeasureMode.AtMost),
				Is.EqualTo(new Vector2(64, 32)));
		});
	}

	[Test]
	public void RazorImageBoundToAnAssetReferenceDrawsThatTexture()
	{
		var texture = CreateTexture(64, 32, isSrgb: true);
		var icon = Register(texture);
		using var services = new ServiceCollection().BuildServiceProvider();
		using var host = new GameplayUiHost(services);
		using var surface = host.Create<ImageHud>(
			new UiSurfaceOptions(),
			initialParameters: new Dictionary<string, object?>
			{
				[nameof(ImageHud.Icon)] = icon,
				[nameof(ImageHud.IconStyle)] = "width: 32px;"
			});

		Assert.That(host.TryConsumeLatest(out var frame), Is.True);

		try
		{
			var images = ImageCommands(frame.Screen);

			Assert.Multiple(() =>
			{
				Assert.That(images, Has.Length.EqualTo(1));
				Assert.That(images[0].Image, Is.SameAs(texture));
				Assert.That(images[0].ElemCount, Is.EqualTo(6));
				Assert.That(images[0].ResolveSampleMode(rendererSamplesTexture: false), Is.EqualTo(UiSampleModes.SrgbColor));
				Assert.That(images[0].IsWaitingForImage, Is.True);
				Assert.That(frame.Screen.HasPendingImages(), Is.True);
				Assert.That(ImageQuadSize(frame.Screen, images[0]), Is.EqualTo(new Vector2(32, 16)));
			});
		}
		finally
		{
			frame.Release();
		}
	}

	[Test]
	public void SwappingToAnImageOfTheSameSizeRepaintsWithoutLayout()
	{
		var first = Register(CreateTexture(64, 32));
		var sameSize = Register(CreateTexture(64, 32));
		var otherSize = Register(CreateTexture(16, 16));
		using var services = new ServiceCollection().BuildServiceProvider();
		using var host = new GameplayUiHost(services);
		var surface = (GameplayUiSurface)host.Create<ImageHud>(
			new UiSurfaceOptions(),
			initialParameters: new Dictionary<string, object?> { [nameof(ImageHud.Icon)] = first });

		try
		{
			surface.SetParameters(new Dictionary<string, object?> { [nameof(ImageHud.Icon)] = sameSize });
			var sameSizePerformance = surface.Performance;
			var sameSizeImage = ImageCommands(surface.Frame).Single().Image;

			surface.SetParameters(new Dictionary<string, object?> { [nameof(ImageHud.Icon)] = otherSize });
			var otherSizePerformance = surface.Performance;
			var otherSizeQuad = ImageQuadSize(surface.Frame, ImageCommands(surface.Frame).Single());

			Assert.Multiple(() =>
			{
				Assert.That(sameSizeImage, Is.SameAs(sameSize.Asset));
				Assert.That(sameSizePerformance.LayoutRan, Is.False);
				Assert.That(otherSizePerformance.LayoutRan, Is.True);
				Assert.That(otherSizeQuad, Is.EqualTo(new Vector2(16, 16)));
			});
		}
		finally
		{
			surface.Dispose();
		}
	}

	[Test]
	public void APathSourceDrawsNoImage()
	{
		using var services = new ServiceCollection().BuildServiceProvider();
		using var host = new GameplayUiHost(services);
		using var surface = host.Create<ImageHud>(
			new UiSurfaceOptions(),
			initialParameters: new Dictionary<string, object?> { [nameof(ImageHud.Path)] = "/Assets/UI/Potion.png" });

		Assert.That(host.TryConsumeLatest(out var frame), Is.True);

		try
		{
			Assert.That(ImageCommands(frame.Screen), Is.Empty);
		}
		finally
		{
			frame.Release();
		}
	}

	[Test]
	public void ImagesOfOneTextureShareABatchThatSolidShapesNeverJoin()
	{
		var icon = CreateTexture(8, 8);
		var other = CreateTexture(8, 8);
		using var geometry = new UiGeometryBuilder();
		geometry.AddFilledRect(new Vector2(0, 0), new Vector2(10, 10), 0xffffffff);
		geometry.AddImage(new Vector2(0, 0), new Vector2(8, 8), Vector2.Zero, Vector2.One, 0xffffffff, icon);
		geometry.AddImage(new Vector2(10, 0), new Vector2(18, 8), Vector2.Zero, Vector2.One, 0xffffffff, icon);
		geometry.AddFilledRect(new Vector2(20, 0), new Vector2(30, 10), 0xffffffff);
		geometry.AddImage(new Vector2(30, 0), new Vector2(38, 8), Vector2.Zero, Vector2.One, 0xffffffff, other);

		var frame = geometry.BuildFrame(64, 64, new UiTextureAtlas { Width = 1, Height = 1, PixelsRgba = [255, 255, 255, 255] });

		try
		{
			var commands = frame.Commands.AsSpan(0, frame.CommandCount).ToArray();

			Assert.Multiple(() =>
			{
				Assert.That(commands.Select(command => command.Image), Is.EqualTo(new[] { null, icon, null, other }));
				Assert.That(commands[1].ElemCount, Is.EqualTo(12));
				Assert.That(commands[0].Solid, Is.True);
				Assert.That(commands[1].Solid, Is.False);
				Assert.That(commands[1].ResolveSampleMode(rendererSamplesTexture: false), Is.EqualTo(UiSampleModes.Color));
			});
		}
		finally
		{
			frame.Release();
		}
	}

	private AssetRef<Texture> Register(Texture texture)
	{
		var id = Guid.NewGuid();
		_registry.Instances[id] = texture;

		return new AssetRef<Texture> { NodeId = id };
	}

	private static UiNode Image(Texture texture, string? className = null)
	{
		var node = new UiNode
		{
			Name = "img",
			Image = texture
		};

		if (className is not null)
		{
			node.Attributes["class"] = className;
		}

		return node;
	}

	private static Texture CreateTexture(int width, int height, bool isSrgb = false)
	{
		return new Texture(
			"UI test image",
			width,
			height,
			isSrgb,
			TextureFormat.Rgba8Unorm,
			[new TextureMipData(width, height, new byte[width * height * 4])]);
	}

	private static UiDrawCommand[] ImageCommands(UiFrameData frame)
	{
		return frame.Commands
			.Take(frame.CommandCount)
			.Where(command => command.Image is not null)
			.ToArray();
	}

	private static Vector2 ImageQuadSize(UiFrameData frame, UiDrawCommand command)
	{
		var first = frame.Indices[command.IdxOffset];
		var opposite = frame.Indices[command.IdxOffset + 2];

		return frame.Vertices[opposite].Position - frame.Vertices[first].Position;
	}
}
