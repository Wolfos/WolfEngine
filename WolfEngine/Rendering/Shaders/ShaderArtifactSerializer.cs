#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Shaders;

public static class ShaderArtifactSerializer
{
	private static readonly byte[] Magic = "WOLFSHDR"u8.ToArray();

	public static void Write(Stream destination, CompiledShaderArtifact artifact)
	{
		ArgumentNullException.ThrowIfNull(destination);
		ArgumentNullException.ThrowIfNull(artifact);
		using var writer = new BinaryWriter(destination, System.Text.Encoding.UTF8, leaveOpen: true);
		writer.Write(Magic);
		writer.Write(CompiledShaderArtifact.CurrentFormatVersion);
		var json = JsonSerializer.SerializeToUtf8Bytes(ToDto(artifact));
		writer.Write(json.Length);
		writer.Write(json);
	}

	public static CompiledShaderArtifact Read(Stream source)
	{
		ArgumentNullException.ThrowIfNull(source);
		using var reader = new BinaryReader(source, System.Text.Encoding.UTF8, leaveOpen: true);
		if (reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) == false)
			throw new InvalidDataException("The file is not a WolfEngine shader artifact.");
		var version = reader.ReadInt32();
		if (version != CompiledShaderArtifact.CurrentFormatVersion)
			throw new InvalidDataException($"Unsupported shader artifact version {version}.");
		var jsonLength = reader.ReadInt32();
		if (jsonLength <= 0 || jsonLength > 128 * 1024 * 1024)
			throw new InvalidDataException("Shader artifact payload length is invalid.");
		var dto = JsonSerializer.Deserialize<ArtifactDto>(reader.ReadBytes(jsonLength))
		          ?? throw new InvalidDataException("Shader artifact payload is invalid.");
		return FromDto(dto);
	}

	private static ArtifactDto ToDto(CompiledShaderArtifact artifact) => new()
	{
		ProgramId = artifact.Request.ProgramId.Value,
		Kind = artifact.Request.Kind,
		BackendKind = artifact.Request.BackendKind,
		VertexEntryPoint = artifact.Request.VertexEntryPoint,
		PixelEntryPoint = artifact.Request.PixelEntryPoint,
		ComputeEntryPoint = artifact.Request.ComputeEntryPoint,
		Defines = artifact.Request.Defines,
		ContentKey = artifact.ContentKey,
		Vertex = artifact.Bytecode.Vertex?.ToArray(),
		Pixel = artifact.Bytecode.Pixel?.ToArray(),
		Compute = artifact.Bytecode.Compute?.ToArray(),
		ThreadGroupX = artifact.ThreadGroupSize?.X,
		ThreadGroupY = artifact.ThreadGroupSize?.Y,
		ThreadGroupZ = artifact.ThreadGroupSize?.Z,
		ConstantBuffers = artifact.ReflectionLayout.ConstantBuffersByName.Values
			.OrderBy(buffer => buffer.Name, StringComparer.Ordinal)
			.Select(buffer => new ConstantBufferDto
			{
				Name = buffer.Name,
				RegisterIndex = buffer.RegisterIndex,
				SizeInBytes = buffer.SizeInBytes,
				Fields = buffer.Fields.Values.OrderBy(field => field.Path, StringComparer.Ordinal)
					.Select(field => new FieldDto
					{
						Path = field.Path, Offset = field.Offset, ByteSize = field.ByteSize, ValueKind = field.ValueKind
					}).ToArray()
			}).ToArray(),
		Resources = artifact.ReflectionLayout.ResourcesByName.Values.OrderBy(resource => resource.Name, StringComparer.Ordinal)
			.Select(resource => new ResourceDto { Name = resource.Name, RegisterIndex = resource.RegisterIndex }).ToArray()
	};

	private static CompiledShaderArtifact FromDto(ArtifactDto dto)
	{
		var programId = new ShaderProgramId(dto.ProgramId);
		var defines = string.IsNullOrEmpty(dto.Defines) ? [] : dto.Defines.Split(';');
		var request = dto.Kind == ShaderRequestKind.Compute
			? ShaderRequest.Compute(programId, dto.ComputeEntryPoint!, dto.BackendKind, defines)
			: ShaderRequest.Graphics(programId, dto.VertexEntryPoint!, dto.PixelEntryPoint!, dto.BackendKind, defines);
		var buffers = (dto.ConstantBuffers ?? []).Select(buffer => new ShaderConstantBufferLayout(
			buffer.Name, buffer.RegisterIndex, buffer.SizeInBytes,
			(buffer.Fields ?? []).ToDictionary(field => field.Path,
				field => new ShaderConstantFieldLayout(field.Path, field.Offset, field.ByteSize, field.ValueKind),
				StringComparer.Ordinal)));
		var resources = (dto.Resources ?? []).Select(resource => new ShaderResourceBindingLayout(resource.Name, resource.RegisterIndex));
		var reflection = new ShaderReflectionLayout(buffers, resources);
		ComputeThreadGroupSize? threadGroup = dto.ThreadGroupX.HasValue
			? new ComputeThreadGroupSize(dto.ThreadGroupX.Value, dto.ThreadGroupY!.Value, dto.ThreadGroupZ!.Value)
			: null;
		var bytecode = dto.Kind == ShaderRequestKind.Compute
			? new ShaderBytecodeSet(
				compute: new ReadOnlyMemory<byte>(dto.Compute ?? throw new InvalidDataException("Compute bytecode is missing.")),
				computeThreadGroupSize: threadGroup)
			: new ShaderBytecodeSet(
				new ReadOnlyMemory<byte>(dto.Vertex ?? throw new InvalidDataException("Vertex bytecode is missing.")),
				new ReadOnlyMemory<byte>(dto.Pixel ?? throw new InvalidDataException("Pixel bytecode is missing.")));
		return new CompiledShaderArtifact(request, dto.ContentKey, bytecode, reflection, threadGroup);
	}

	private sealed class ArtifactDto
	{
		public string ProgramId { get; set; } = string.Empty;
		public ShaderRequestKind Kind { get; set; }
		public GraphicsBackendKind BackendKind { get; set; }
		public string? VertexEntryPoint { get; set; }
		public string? PixelEntryPoint { get; set; }
		public string? ComputeEntryPoint { get; set; }
		public string Defines { get; set; } = string.Empty;
		public string ContentKey { get; set; } = string.Empty;
		public byte[]? Vertex { get; set; }
		public byte[]? Pixel { get; set; }
		public byte[]? Compute { get; set; }
		public uint? ThreadGroupX { get; set; }
		public uint? ThreadGroupY { get; set; }
		public uint? ThreadGroupZ { get; set; }
		public ConstantBufferDto[]? ConstantBuffers { get; set; }
		public ResourceDto[]? Resources { get; set; }
	}

	private sealed class ConstantBufferDto
	{
		public string Name { get; set; } = string.Empty;
		public uint RegisterIndex { get; set; }
		public int SizeInBytes { get; set; }
		public FieldDto[]? Fields { get; set; }
	}

	private sealed class FieldDto
	{
		public string Path { get; set; } = string.Empty;
		public int Offset { get; set; }
		public int ByteSize { get; set; }
		public ShaderConstantFieldValueKind ValueKind { get; set; }
	}

	private sealed class ResourceDto
	{
		public string Name { get; set; } = string.Empty;
		public uint RegisterIndex { get; set; }
	}
}
