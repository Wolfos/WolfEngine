#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Shaders;

public enum ShaderRequestKind
{
	Compute,
	Graphics
}

public readonly record struct ShaderRequest
{
	private ShaderRequest(ShaderProgramId programId, ShaderRequestKind kind, GraphicsBackendKind backendKind,
		string? vertexEntryPoint, string? pixelEntryPoint, string? computeEntryPoint, string defines)
	{
		ProgramId = programId;
		Kind = kind;
		BackendKind = backendKind;
		VertexEntryPoint = vertexEntryPoint;
		PixelEntryPoint = pixelEntryPoint;
		ComputeEntryPoint = computeEntryPoint;
		Defines = defines;
	}

	public ShaderProgramId ProgramId { get; }
	public ShaderRequestKind Kind { get; }
	public GraphicsBackendKind BackendKind { get; }
	public string? VertexEntryPoint { get; }
	public string? PixelEntryPoint { get; }
	public string? ComputeEntryPoint { get; }
	public string Defines { get; }

	public static ShaderRequest Compute(ShaderProgramId id, string entryPoint, GraphicsBackendKind backend, params string[] defines)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);
		return new ShaderRequest(id, ShaderRequestKind.Compute, backend, null, null, entryPoint, NormalizeDefines(defines));
	}

	public static ShaderRequest Graphics(ShaderProgramId id, string vertexEntryPoint, string pixelEntryPoint,
		GraphicsBackendKind backend, params string[] defines)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(vertexEntryPoint);
		ArgumentException.ThrowIfNullOrWhiteSpace(pixelEntryPoint);
		return new ShaderRequest(id, ShaderRequestKind.Graphics, backend, vertexEntryPoint, pixelEntryPoint, null,
			NormalizeDefines(defines));
	}

	public string[] GetDefines() => string.IsNullOrEmpty(Defines) ? [] : Defines.Split(';', StringSplitOptions.RemoveEmptyEntries);

	private static string NormalizeDefines(string[]? defines)
	{
		if (defines is not { Length: > 0 }) return string.Empty;
		var normalized = defines.Where(value => string.IsNullOrWhiteSpace(value) == false)
			.Select(value => value.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
		return string.Join(';', normalized);
	}
}

public sealed class CompiledShaderArtifact
{
	public const int CurrentFormatVersion = 1;

	public CompiledShaderArtifact(ShaderRequest request, string contentKey, ShaderBytecodeSet bytecode,
		ShaderReflectionLayout reflectionLayout, ComputeThreadGroupSize? threadGroupSize = null)
	{
		Request = request;
		ContentKey = string.IsNullOrWhiteSpace(contentKey) ? throw new ArgumentException("Content key is required.", nameof(contentKey)) : contentKey;
		Bytecode = bytecode;
		ReflectionLayout = reflectionLayout ?? throw new ArgumentNullException(nameof(reflectionLayout));
		ThreadGroupSize = threadGroupSize;
	}

	public int FormatVersion => CurrentFormatVersion;
	public ShaderRequest Request { get; }
	public string ContentKey { get; }
	public ShaderBytecodeSet Bytecode { get; }
	public ShaderReflectionLayout ReflectionLayout { get; }
	public ComputeThreadGroupSize? ThreadGroupSize { get; }
}

public sealed record ShaderReloadFailure(ShaderRequest Request, string Error);

public sealed record ShaderReloadResult(int AppliedArtifactCount, IReadOnlyList<ShaderReloadFailure> Failures)
{
	public bool Succeeded => Failures.Count == 0;
}
