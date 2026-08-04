using System.Numerics;
using WolfEngine.Mathematics;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// The values behind <c>cbFSR3Upscaler</c>, shared by every FSR3 pass.
/// </summary>
/// <remarks>
/// Mirrors <c>Fsr3UpscalerConstants</c> from FidelityFX SDK v1.1.2. The field order here
/// is the constant buffer's field order; see Shaders/ThirdParty/Fsr3/fsr3_constants.slang.
/// </remarks>
public readonly struct Fsr3ConstantValues
{
	public required Int2 RenderSize { get; init; }
	public required Int2 PreviousFrameRenderSize { get; init; }
	public required Int2 UpscaleSize { get; init; }
	public required Int2 PreviousFrameUpscaleSize { get; init; }
	public required Int2 MaxRenderSize { get; init; }
	public required Int2 MaxUpscaleSize { get; init; }

	/// <summary>See <see cref="Fsr3Constants.BuildDeviceToViewDepth"/>.</summary>
	public required Vector4 DeviceToViewDepth { get; init; }

	/// <summary>Jitter offset in pixels, matching the offset baked into the projection this frame.</summary>
	public required Vector2 Jitter { get; init; }
	public required Vector2 PreviousFrameJitter { get; init; }

	/// <summary>
	/// Converts the engine's motion vectors into the UV-space, current-to-previous vectors FSR3
	/// expects. The G-buffer writes render-resolution pixels, so this is 1/renderSize.
	/// </summary>
	public required Vector2 MotionVectorScale { get; init; }

	public required Vector2 DownscaleFactor { get; init; }

	/// <summary>
	/// Subtracted from every motion vector. Zero for this engine: the G-buffer builds velocity
	/// from unjittered clip positions, so no jitter needs cancelling.
	/// </summary>
	public required Vector2 MotionVectorJitterCancellation { get; init; }

	public required float TanHalfFov { get; init; }
	public required float JitterSequenceLength { get; init; }
	public required float DeltaTime { get; init; }
	public required float DeltaPreExposure { get; init; }

	/// <summary>Scales view-space depth into metres. 1.0 when world units are metres.</summary>
	public required float ViewSpaceToMetersFactor { get; init; }

	public required float FrameIndex { get; init; }
	public required float VelocityFactor { get; init; }
}

public static class Fsr3Constants
{
	/// <summary>
	/// Reproduces <c>ffxFsr3UpscalerGetJitterPhaseCount</c>: eight phases at native resolution,
	/// scaling with the square of the upscale ratio so that the same number of jittered samples
	/// lands per display pixel.
	/// </summary>
	public static int GetJitterPhaseCount(int renderWidth, int displayWidth)
	{
		if (renderWidth <= 0)
		{
			return TemporalJitter.DefaultPhaseCount;
		}

		const float basePhaseCount = 8.0f;
		var ratio = (float)displayWidth / renderWidth;
		return Math.Max((int)(basePhaseCount * MathF.Pow(ratio, 2.0f)), 1);
	}

	/// <summary>
	/// Reproduces the <c>deviceToViewDepth</c> derivation in ffx_fsr3upscaler.cpp, which inverts
	/// the projection's depth row so the shaders can recover view-space depth from a device depth
	/// with two constants.
	/// </summary>
	/// <param name="nearPlane">Near plane distance. Swapping near and far is harmless; <paramref name="inverted"/> decides the transform.</param>
	/// <param name="farPlane">Far plane distance.</param>
	/// <param name="verticalFovRadians">Vertical field of view, in radians.</param>
	/// <param name="aspect">Width over height of the render target.</param>
	/// <param name="inverted">True for reverse-Z. The engine currently uses non-inverted depth.</param>
	/// <param name="infinite">True for an infinite far plane.</param>
	public static Vector4 BuildDeviceToViewDepth(
		float nearPlane,
		float farPlane,
		float verticalFovRadians,
		float aspect,
		bool inverted = false,
		bool infinite = false)
	{
		var min = MathF.Min(nearPlane, farPlane);
		var max = MathF.Max(nearPlane, farPlane);
		if (inverted)
		{
			(min, max) = (max, min);
		}

		var q = max / (min - max);
		const float d = -1.0f;
		var epsilon = float.Epsilon;

		var matrixElemC = infinite
			? (inverted ? 0.0f + epsilon : -1.0f - epsilon)
			: q;
		var matrixElemE = infinite
			? (inverted ? max : -min - epsilon)
			: q * min;

		var cotHalfFovY = MathF.Cos(0.5f * verticalFovRadians) / MathF.Sin(0.5f * verticalFovRadians);
		var a = aspect > 0.0f ? cotHalfFovY / aspect : cotHalfFovY;
		var b = cotHalfFovY;

		return new Vector4(
			d * matrixElemC,
			matrixElemE,
			a != 0.0f ? 1.0f / a : 0.0f,
			b != 0.0f ? 1.0f / b : 0.0f);
	}

	/// <summary>
	/// Writes <c>cbFSR3Upscaler</c>. Every FSR3 pass binds the same block, so they all route
	/// through here rather than each writing the fields it happens to read.
	/// </summary>
	internal static void Write(ShaderPropertyWriter writer, in Fsr3ConstantValues values)
	{
		ArgumentNullException.ThrowIfNull(writer);

		writer.Clear();
		writer.SetInt("iRenderSizeX", Math.Max(values.RenderSize.X, 1));
		writer.SetInt("iRenderSizeY", Math.Max(values.RenderSize.Y, 1));
		writer.SetInt("iPreviousFrameRenderSizeX", Math.Max(values.PreviousFrameRenderSize.X, 1));
		writer.SetInt("iPreviousFrameRenderSizeY", Math.Max(values.PreviousFrameRenderSize.Y, 1));

		writer.SetInt("iUpscaleSizeX", Math.Max(values.UpscaleSize.X, 1));
		writer.SetInt("iUpscaleSizeY", Math.Max(values.UpscaleSize.Y, 1));
		writer.SetInt("iPreviousFrameUpscaleSizeX", Math.Max(values.PreviousFrameUpscaleSize.X, 1));
		writer.SetInt("iPreviousFrameUpscaleSizeY", Math.Max(values.PreviousFrameUpscaleSize.Y, 1));

		writer.SetInt("iMaxRenderSizeX", Math.Max(values.MaxRenderSize.X, 1));
		writer.SetInt("iMaxRenderSizeY", Math.Max(values.MaxRenderSize.Y, 1));
		writer.SetInt("iMaxUpscaleSizeX", Math.Max(values.MaxUpscaleSize.X, 1));
		writer.SetInt("iMaxUpscaleSizeY", Math.Max(values.MaxUpscaleSize.Y, 1));

		writer.SetVector4("fDeviceToViewDepth", values.DeviceToViewDepth);

		writer.SetVector2("fJitter", values.Jitter);
		writer.SetVector2("fPreviousFrameJitter", values.PreviousFrameJitter);

		writer.SetVector2("fMotionVectorScale", values.MotionVectorScale);
		writer.SetVector2("fDownscaleFactor", values.DownscaleFactor);

		writer.SetVector2("fMotionVectorJitterCancellation", values.MotionVectorJitterCancellation);
		writer.SetFloat("fTanHalfFOV", values.TanHalfFov);
		writer.SetFloat("fJitterSequenceLength", values.JitterSequenceLength);

		writer.SetFloat("fDeltaTime", values.DeltaTime);
		writer.SetFloat("fDeltaPreExposure", values.DeltaPreExposure);
		writer.SetFloat("fViewSpaceToMetersFactor", values.ViewSpaceToMetersFactor);
		writer.SetFloat("fFrameIndex", values.FrameIndex);

		writer.SetFloat("fVelocityFactor", values.VelocityFactor);
	}

	/// <summary>
	/// Builds the constant values for a frame, filling in the parts that follow from the two
	/// resolutions so callers only supply what actually varies.
	/// </summary>
	public static Fsr3ConstantValues Build(
		Int2 renderSize,
		Int2 upscaleSize,
		Int2 previousFrameRenderSize,
		Int2 previousFrameUpscaleSize,
		Vector4 deviceToViewDepth,
		Vector2 jitter,
		Vector2 previousFrameJitter,
		float verticalFovRadians,
		float deltaTimeSeconds,
		float frameIndex,
		float viewSpaceToMetersFactor = 1.0f,
		float deltaPreExposure = 1.0f,
		float velocityFactor = 1.0f)
	{
		var width = Math.Max(renderSize.X, 1);
		var height = Math.Max(renderSize.Y, 1);

		return new Fsr3ConstantValues
		{
			RenderSize = renderSize,
			PreviousFrameRenderSize = previousFrameRenderSize,
			UpscaleSize = upscaleSize,
			PreviousFrameUpscaleSize = previousFrameUpscaleSize,
			MaxRenderSize = renderSize,
			MaxUpscaleSize = upscaleSize,
			DeviceToViewDepth = deviceToViewDepth,
			Jitter = jitter,
			PreviousFrameJitter = previousFrameJitter,

			// The G-buffer stores velocity in render-resolution pixels, so the UV conversion is
			// the whole of the scale. A negative sign would go here if the engine's vectors
			// pointed previous-to-current; they already point current-to-previous.
			MotionVectorScale = new Vector2(1.0f / width, 1.0f / height),

			DownscaleFactor = new Vector2(
				(float)width / Math.Max(upscaleSize.X, 1),
				(float)height / Math.Max(upscaleSize.Y, 1)),
			MotionVectorJitterCancellation = Vector2.Zero,
			TanHalfFov = MathF.Tan(0.5f * verticalFovRadians),
			JitterSequenceLength = GetJitterPhaseCount(width, Math.Max(upscaleSize.X, 1)),
			DeltaTime = deltaTimeSeconds,
			DeltaPreExposure = deltaPreExposure,
			ViewSpaceToMetersFactor = viewSpaceToMetersFactor,
			FrameIndex = frameIndex,
			VelocityFactor = velocityFactor
		};
	}
}
