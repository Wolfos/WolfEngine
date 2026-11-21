using System.Numerics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Atmosphere;

/// <summary>
/// Writes a view's atmosphere into a shader's <c>AtmosphereConstants atmosphere</c> field (atmosphere_common.slang).
/// </summary>
internal static class AtmosphereConstants
{
	public static void Write(ShaderPropertyWriter writer, in AtmosphereFrame frame)
	{
		var model = frame.Model;
		writer.SetVector4("atmosphere.rayleighScattering", new Vector4(model.RayleighScattering, model.RayleighDensityExpScale));
		writer.SetVector4("atmosphere.mieScattering", new Vector4(model.MieScattering, model.MieDensityExpScale));
		writer.SetVector4("atmosphere.mieExtinction", new Vector4(model.MieExtinction, model.MiePhaseG));
		writer.SetVector4("atmosphere.mieAbsorption", new Vector4(model.MieAbsorption, model.BottomRadius));
		writer.SetVector4("atmosphere.absorptionExtinction", new Vector4(model.AbsorptionExtinction, model.TopRadius));
		writer.SetVector4("atmosphere.groundAlbedo", new Vector4(model.GroundAlbedo, 0.0f));
		writer.SetVector4(
			"atmosphere.absorptionDensity0",
			new Vector4(
				model.AbsorptionDensity0LayerWidth,
				model.AbsorptionDensity0ConstantTerm,
				model.AbsorptionDensity0LinearTerm,
				model.AbsorptionDensity1ConstantTerm));
		writer.SetVector4("atmosphere.absorptionDensity1", new Vector4(model.AbsorptionDensity1LinearTerm, 0.0f, 0.0f, 0.0f));
		writer.SetVector4(
			"atmosphere.view",
			new Vector4(model.BottomRadius + frame.CameraAltitudeKm, model.SunDiscHalfAngleRadians, 0.0f, 0.0f));
		writer.SetVector4("atmosphere.sunDirection", new Vector4(frame.SunDirectionInAtmosphere, 0.0f));
		writer.SetVector4("atmosphere.sunIlluminance", new Vector4(frame.SunIlluminance, 0.0f));
	}
}

/// <summary>A view's atmosphere for one frame: the medium, where the camera is in it, and the sun lighting it.</summary>
public readonly struct AtmosphereFrame
{
	public AtmosphereFrame(in AtmosphereModel model, float cameraWorldY, Vector3 sunForward, Vector3 sunIlluminance)
	{
		Model = model;
		CameraAltitudeKm = model.GetCameraAltitudeKm(cameraWorldY);

		var towardSun = sunForward == Vector3.Zero
			? Vector3.UnitY
			: Vector3.Normalize(-sunForward);
		SunDirectionWorld = towardSun;
		SunIlluminance = Vector3.Max(sunIlluminance, Vector3.Zero);
	}

	public AtmosphereModel Model { get; }
	public float CameraAltitudeKm { get; }

	/// <summary>Direction toward the sun, +Y up.</summary>
	public Vector3 SunDirectionWorld { get; }

	/// <summary>The sun light's color times intensity, before the atmosphere dims it.</summary>
	public Vector3 SunIlluminance { get; }

	/// <summary>Direction toward the sun in the atmosphere shaders' frame (+Z up); see ToAtmosphereDirection.</summary>
	public Vector3 SunDirectionInAtmosphere => new(SunDirectionWorld.X, SunDirectionWorld.Z, SunDirectionWorld.Y);
}

/// <summary>
/// What a pass that draws the sky or applies aerial perspective needs from the view's atmosphere. Disabled outside
/// the Procedural sky mode.
/// </summary>
public readonly struct AtmosphereBinding
{
	public bool Enabled { get; init; }
	public AtmosphereFrame Frame { get; init; }
	public DescriptorHandle TransmittanceLut { get; init; }
	public DescriptorHandle SkyViewLut { get; init; }
	public DescriptorHandle AerialPerspective { get; init; }

	internal static AtmosphereBinding Resolve(RenderGraphContext context, BindlessResourceRegistry bindlessRegistry, in AtmosphereViewHandles handles)
	{
		if (handles.Transmittance.IsValid == false)
		{
			return default;
		}

		return new AtmosphereBinding
		{
			Enabled = true,
			Frame = handles.Frame,
			TransmittanceLut = bindlessRegistry.GetTextureHandle(context.GetTexture(handles.Transmittance)),
			SkyViewLut = bindlessRegistry.GetTextureHandle(context.GetTexture(handles.SkyView)),
			AerialPerspective = bindlessRegistry.GetTextureHandle(context.GetTexture(handles.AerialPerspective))
		};
	}
}
