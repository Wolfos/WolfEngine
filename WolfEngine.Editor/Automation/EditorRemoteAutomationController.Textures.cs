using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;
using AbstractionDepthStencilFormat = WolfEngine.Rendering.Abstraction.DepthStencilFormat;

namespace WolfEngine.Editor.Automation;

public sealed record TextureSampleResult(
	string AssetName,
	string Dimension,
	string Format,
	int Width,
	int Height,
	int MipCount,
	float MipLevel,
	string Filter,
	float[][] Samples);

/// <summary>
/// Texture probing. Loads an imported texture's runtime artifact for the running backend onto the GPU and samples
/// it in a compute shader through GetTexture or GetTextureCube, so a check sees what shaders see: the hardware's
/// decode of block compression, the cube face layout, and the bindless view for the texture's dimension.
/// </summary>
public sealed partial class EditorRemoteAutomationController
{
	private const string TextureSampleProbeEntryPoint = "SampleTextureCS";
	private const int MaxTextureSampleCount = 4096;

	/// <summary>
	/// Samples <paramref name="assetPath"/> at each coordinate: a direction (x, y, z) for a cube texture, or a UV
	/// (x, y) for a 2D one.
	/// </summary>
	public Task<TextureSampleResult> SampleTextureAsync(
		string assetPath,
		float[][] coordinates,
		float mipLevel,
		bool linearFilter,
		CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			ArgumentNullException.ThrowIfNull(coordinates);
			if (coordinates.Length == 0 || coordinates.Length > MaxTextureSampleCount)
			{
				throw new InvalidOperationException($"coordinates must hold between 1 and {MaxTextureSampleCount} entries.");
			}

			if (float.IsFinite(mipLevel) == false || mipLevel < 0.0f)
			{
				throw new InvalidOperationException("mip_level must be a non-negative number.");
			}

			var asset = ResolveTextureAsset(assetPath);
			var device = _renderer.GetGfxDevice()
				?? throw new InvalidOperationException("Texture sampling requires an initialized graphics device.");
			var texture = LoadRuntimeTexture(asset, device.BackendKind);
			var samples = SampleTextureOnGpu(device, texture, coordinates, mipLevel, linearFilter);

			return new TextureSampleResult(
				asset.Name,
				texture.Dimension.ToString(),
				texture.Format.ToString(),
				texture.Width,
				texture.Height,
				texture.MipCount,
				mipLevel,
				linearFilter ? "Bilinear" : "Point",
				samples);
		}, cancellationToken);

	private AssetDatabaseEntry ResolveTextureAsset(string assetPath)
	{
		if (string.IsNullOrWhiteSpace(assetPath))
		{
			throw new InvalidOperationException("asset_path is required.");
		}

		var fullProjectPath = Path.GetFullPath(_projectPath);
		var fullAssetPath = Path.GetFullPath(Path.IsPathRooted(assetPath)
			? assetPath
			: Path.Combine(fullProjectPath, assetPath));
		var relativePath = Normalize(Path.GetRelativePath(fullProjectPath, fullAssetPath));
		var asset = _projectService.CurrentAssetDatabase.Assets.FirstOrDefault(candidate =>
			candidate.Type == AssetType.Texture2D &&
			string.Equals(Normalize(candidate.RelativeSourcePath), relativePath, StringComparison.OrdinalIgnoreCase));

		return asset ?? throw new InvalidOperationException(
			$"Texture '{assetPath}' was not found in the open project's asset database.");
	}

	private Texture LoadRuntimeTexture(AssetDatabaseEntry asset, GraphicsBackendKind backendKind)
	{
		var target = backendKind == GraphicsBackendKind.Metal
			? "metal"
			: "d3d12";
		var artifact = asset.Artifacts.FirstOrDefault(candidate =>
			string.Equals(candidate.Kind, "RuntimeTexture", StringComparison.Ordinal) &&
			string.Equals(candidate.Target, target, StringComparison.OrdinalIgnoreCase))
			?? throw new InvalidOperationException($"Texture '{asset.Name}' has no {target} runtime artifact.");
		var absolutePath = _projectService.GetAbsoluteAssetPath(asset.Id, artifact.RelativePath);

		return TextureArtifactSerializer.Read(absolutePath, $"texture-sample-probe:{asset.Id:D}");
	}

	private float[][] SampleTextureOnGpu(
		IGfxDevice device,
		Texture texture,
		float[][] coordinates,
		float mipLevel,
		bool linearFilter)
	{
		_bindlessRegistry.EnsureInitialized(device);
		var compiled = _shaderProvider.GetComputeShaderWithReflection(
			EngineShaderPrograms.TextureSampleProbe,
			TextureSampleProbeEntryPoint,
			device.BackendKind);
		var pipeline = device.GetOrCreatePipeline(
			new PipelineKey(
				PassKind.Compute,
				vertexEntryPoint: null,
				pixelEntryPoint: null,
				computeEntryPoint: TextureSampleProbeEntryPoint,
				renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
				depthStencil: new AbstractionDepthStencilFormat(TextureFormat.Unknown),
				renderState: default,
				shaderVariant: EngineShaderPrograms.TextureSampleProbe.Value),
			new ShaderBytecodeSet(compute: compiled.Bytecode, computeThreadGroupSize: compiled.ThreadGroupSize));
		var parameters = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("TextureSampleProbeParams"));

		var resources = _renderer.CreateTextureResources(texture);
		var bufferBytes = (ulong)(coordinates.Length * Marshal.SizeOf<Vector4>());
		var coordinateBuffer = device.CreateBuffer(new BufferDescriptor(bufferBytes, BufferUsage.Structured, BufferFlags.AllowUnorderedAccess));
		var sampleBuffer = device.CreateBuffer(new BufferDescriptor(bufferBytes, BufferUsage.Structured, BufferFlags.AllowUnorderedAccess));
		var readbackBuffer = device.CreateBuffer(new BufferDescriptor(bufferBytes, BufferUsage.Staging));
		try
		{
			if (coordinateBuffer is not IWritableGpuBuffer writableCoordinates)
			{
				throw new InvalidOperationException("GPU coordinate buffer does not support CPU writes.");
			}

			writableCoordinates.Write<Vector4>(ToVectors(coordinates));
			var sampler = linearFilter
				? new SamplerDescriptor(FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp)
				: new SamplerDescriptor(FilterMode.Point, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp);

			var commandList = device.BeginCompute();
			try
			{
				commandList.BindPipeline(pipeline);
				parameters.Clear();
				parameters.SetUInt("textureHandle", _bindlessRegistry.GetTextureHandle(resources.Texture).Value);
				parameters.SetUInt("samplerHandle", _bindlessRegistry.GetSamplerHandle(in sampler).Value);
				parameters.SetUInt("sampleCount", (uint)coordinates.Length);
				parameters.SetUInt("sampleAsCube", texture.Dimension == TextureDimension.Cube ? 1u : 0u);
				parameters.SetFloat("mipLevel", mipLevel);
				commandList.SetComputeConstants(parameters.RegisterIndex, parameters.AsBytes());
				commandList.SetComputeBuffer(0, coordinateBuffer);
				commandList.SetComputeBuffer(1, sampleBuffer);
				var (groupsX, groupsY, groupsZ) = compiled.ThreadGroupSize.GetDispatchGroupCount((uint)coordinates.Length, 1u);
				commandList.Dispatch(groupsX, groupsY, groupsZ);
				commandList.CopyBuffer(sampleBuffer, 0, readbackBuffer, 0, bufferBytes);
			}
			finally
			{
				device.Submit(commandList);
				device.WaitForIdle();
			}

			if (readbackBuffer is not IReadableGpuBuffer readableSamples)
			{
				throw new InvalidOperationException("GPU readback buffer does not support CPU reads.");
			}

			var bytes = new byte[bufferBytes];
			readableSamples.Read(bytes);
			var values = MemoryMarshal.Cast<byte, Vector4>(bytes);
			var samples = new float[values.Length][];
			for (var i = 0; i < values.Length; i++)
			{
				samples[i] = [values[i].X, values[i].Y, values[i].Z, values[i].W];
			}

			return samples;
		}
		finally
		{
			DisposeIfDisposable(coordinateBuffer);
			DisposeIfDisposable(sampleBuffer);
			DisposeIfDisposable(readbackBuffer);
			DisposeIfDisposable(resources.Texture);
		}
	}

	private static Vector4[] ToVectors(float[][] coordinates)
	{
		var vectors = new Vector4[coordinates.Length];
		for (var i = 0; i < coordinates.Length; i++)
		{
			var coordinate = coordinates[i] ?? throw new InvalidOperationException($"coordinates[{i}] is null.");
			if (coordinate.Length < 2 || coordinate.Length > 4)
			{
				throw new InvalidOperationException($"coordinates[{i}] must have two to four components.");
			}

			vectors[i] = new Vector4(
				coordinate[0],
				coordinate[1],
				coordinate.Length > 2 ? coordinate[2] : 0.0f,
				coordinate.Length > 3 ? coordinate[3] : 0.0f);
		}

		return vectors;
	}

	private static void DisposeIfDisposable(object? resource)
	{
		if (resource is IDisposable disposable)
		{
			disposable.Dispose();
		}
	}
}
