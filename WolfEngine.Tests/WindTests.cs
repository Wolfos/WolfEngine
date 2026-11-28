using System.Text.Json;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class WindTests
{
	[Test]
	public void DisabledWindIsCalm()
	{
		var config = new WindConfig
		{
			Enabled = false
		};

		var state = WindFrameState.FromConfig(config, 12.0f);

		Assert.That(state, Is.EqualTo(WindFrameState.Calm));
		Assert.That(state.DirectionStrength.Z, Is.Zero);
	}

	[Test]
	public void DirectionIsMeasuredFromPlusZTowardsPlusX()
	{
		var config = new WindConfig
		{
			DirectionDegrees = 90.0f,
			Strength = 2.0f,
			GustScale = 50.0f
		};

		var state = WindFrameState.FromConfig(config, 3.0f);

		Assert.That(state.DirectionStrength.X, Is.EqualTo(1.0f).Within(1e-5f));
		Assert.That(state.DirectionStrength.Y, Is.EqualTo(0.0f).Within(1e-5f));
		Assert.That(state.DirectionStrength.Z, Is.EqualTo(2.0f));
		Assert.That(state.GustParams.Y, Is.EqualTo(1.0f / 50.0f).Within(1e-7f));
		Assert.That(state.GustParams.Z, Is.EqualTo(3.0f));
	}

	[Test]
	public void MaterialWindPacksModeAndHalfScales()
	{
		var material = new Material("test")
		{
			Wind = new MaterialWind
			{
				Mode = WindMode.Grass,
				StrengthScale = 0.5f,
				FlutterScale = 2.0f
			}
		};

		var scales = GpuMaterialWind.PackScales(material);

		Assert.That(GpuMaterialWind.PackMode(material), Is.EqualTo(2u));
		Assert.That((float)BitConverter.UInt16BitsToHalf((ushort)(scales & 0xFFFFu)), Is.EqualTo(0.5f));
		Assert.That((float)BitConverter.UInt16BitsToHalf((ushort)(scales >> 16)), Is.EqualTo(2.0f));
	}

	[Test]
	public void MaterialWithoutWindReadsAsNoneWithUnitScales()
	{
		var json = """{"Version": 2, "AssetType": "Material", "MaterialType": "Opaque"}""";

		var editorMaterial = JsonSerializer.Deserialize<MaterialAsset>(json, AssetJson.SerializerOptions)!;
		var contextMaterial = JsonSerializer.Deserialize(json, EngineJsonContext.Default.MaterialAsset)!;

		Assert.That(editorMaterial.Wind, Is.EqualTo(new MaterialWind()));
		Assert.That(contextMaterial.Wind, Is.EqualTo(new MaterialWind()));
		Assert.That(editorMaterial.Wind.StrengthScale, Is.EqualTo(1.0f));
	}

	[Test]
	public void MaterialWindRoundTripsThroughTheEditorAndTheContext()
	{
		var asset = new MaterialAsset
		{
			Wind = new MaterialWind
			{
				Mode = WindMode.Tree,
				StrengthScale = 0.75f,
				FlutterScale = 1.5f
			}
		};

		var json = JsonSerializer.Serialize(asset, AssetJson.SerializerOptions);
		var contextMaterial = JsonSerializer.Deserialize(json, EngineJsonContext.Default.MaterialAsset)!;

		Assert.That(json, Does.Contain("\"Tree\""));
		Assert.That(contextMaterial.Wind, Is.EqualTo(asset.Wind));
	}
}
