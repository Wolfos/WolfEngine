using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Shaders;

/// <summary>
/// The renderer's view of compiled engine shaders: the shader provider together with the shader revision the
/// renderer has applied. Compute kernels, graphics shaders and shader variant sets compare the revision they were
/// built at with <see cref="AppliedRevision"/> and rebuild themselves when it has moved, so nothing has to tell
/// them about a shader reload.
/// </summary>
/// <remarks>
/// The applied revision moves only when the render graph applies a reload between frames, after the GPU is idle and
/// the device's pipeline cache has been cleared. Comparing against the provider's own revision instead would let a
/// pass rebuild a pipeline mid-frame, before the cache clear disposes it.
/// </remarks>
public sealed class ShaderSource
{
	private long _appliedRevision;

	public ShaderSource(IShaderProvider provider)
	{
		Provider = provider ?? throw new ArgumentNullException(nameof(provider));
		_appliedRevision = provider.Revision;
	}

	/// <summary>The shader revision everything built from this source must match to be current.</summary>
	public long AppliedRevision => Interlocked.Read(ref _appliedRevision);

	internal IShaderProvider Provider { get; }

	/// <summary>
	/// Makes <paramref name="revision"/> the applied revision. The render graph calls this between frames, once the
	/// GPU is idle and the old pipelines are gone.
	/// </summary>
	internal void ApplyRevision(long revision)
	{
		Interlocked.Exchange(ref _appliedRevision, revision);
	}

	internal CompiledComputeShaderWithReflection GetComputeShader(
		ShaderProgramId program,
		string entryPoint,
		GraphicsBackendKind backendKind,
		IReadOnlyList<string> defines)
	{
		return Provider.GetComputeShaderWithReflection(
			program,
			entryPoint,
			backendKind,
			ToArray(defines));
	}

	internal CompiledGraphicsShaderWithReflection GetGraphicsShader(
		ShaderProgramId program,
		string vertexEntryPoint,
		string pixelEntryPoint,
		GraphicsBackendKind backendKind,
		IReadOnlyList<string> defines)
	{
		return Provider.GetGraphicsShaderWithReflection(
			program,
			vertexEntryPoint,
			pixelEntryPoint,
			backendKind,
			ToArray(defines));
	}

	/// <summary>
	/// The defines a shader is compiled with, normalised the way a <see cref="ShaderRequest"/> normalises them: empty
	/// entries dropped, trimmed, de-duplicated and sorted.
	/// </summary>
	internal static string[] NormalizeDefines(IEnumerable<string>? defines)
	{
		if (defines is null) return [];

		return defines
			.Where(define => string.IsNullOrWhiteSpace(define) == false)
			.Select(define => define.Trim())
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.ToArray();
	}

	private static string[] ToArray(IReadOnlyList<string> defines)
	{
		return defines as string[] ?? defines.ToArray();
	}
}
