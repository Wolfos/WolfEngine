#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;

namespace WolfEngine.Rendering;

public sealed class DebugPrimitiveMeshFactory
{
	private readonly Dictionary<DebugPrimitiveType, Mesh> _meshes = new();

	public Mesh GetMesh(DebugPrimitiveType primitiveType)
	{
		var resolvedType = Enum.IsDefined(primitiveType)
			? primitiveType
			: DebugPrimitiveType.Box;
		if (_meshes.TryGetValue(resolvedType, out var mesh))
		{
			return mesh;
		}

		mesh = resolvedType switch
		{
			DebugPrimitiveType.Box => CreateBox(),
			DebugPrimitiveType.Sphere => CreateSphere(),
			DebugPrimitiveType.Quad => CreateQuad(),
			_ => CreateBox()
		};
		_meshes[resolvedType] = mesh;
		return mesh;
	}

	private static Mesh CreateBox()
	{
		var vertices = new[]
		{
			new Vector4(-0.5f, -0.5f, 0.5f, 1.0f),
			new Vector4(0.5f, -0.5f, 0.5f, 1.0f),
			new Vector4(0.5f, 0.5f, 0.5f, 1.0f),
			new Vector4(-0.5f, 0.5f, 0.5f, 1.0f),
			new Vector4(0.5f, -0.5f, -0.5f, 1.0f),
			new Vector4(-0.5f, -0.5f, -0.5f, 1.0f),
			new Vector4(-0.5f, 0.5f, -0.5f, 1.0f),
			new Vector4(0.5f, 0.5f, -0.5f, 1.0f),
			new Vector4(-0.5f, -0.5f, -0.5f, 1.0f),
			new Vector4(-0.5f, -0.5f, 0.5f, 1.0f),
			new Vector4(-0.5f, 0.5f, 0.5f, 1.0f),
			new Vector4(-0.5f, 0.5f, -0.5f, 1.0f),
			new Vector4(0.5f, -0.5f, 0.5f, 1.0f),
			new Vector4(0.5f, -0.5f, -0.5f, 1.0f),
			new Vector4(0.5f, 0.5f, -0.5f, 1.0f),
			new Vector4(0.5f, 0.5f, 0.5f, 1.0f),
			new Vector4(-0.5f, 0.5f, 0.5f, 1.0f),
			new Vector4(0.5f, 0.5f, 0.5f, 1.0f),
			new Vector4(0.5f, 0.5f, -0.5f, 1.0f),
			new Vector4(-0.5f, 0.5f, -0.5f, 1.0f),
			new Vector4(-0.5f, -0.5f, -0.5f, 1.0f),
			new Vector4(0.5f, -0.5f, -0.5f, 1.0f),
			new Vector4(0.5f, -0.5f, 0.5f, 1.0f),
			new Vector4(-0.5f, -0.5f, 0.5f, 1.0f)
		};
		var uvs = new[]
		{
			new Vector2(0.0f, 1.0f), new Vector2(1.0f, 1.0f), new Vector2(1.0f, 0.0f), new Vector2(0.0f, 0.0f),
			new Vector2(0.0f, 1.0f), new Vector2(1.0f, 1.0f), new Vector2(1.0f, 0.0f), new Vector2(0.0f, 0.0f),
			new Vector2(0.0f, 1.0f), new Vector2(1.0f, 1.0f), new Vector2(1.0f, 0.0f), new Vector2(0.0f, 0.0f),
			new Vector2(0.0f, 1.0f), new Vector2(1.0f, 1.0f), new Vector2(1.0f, 0.0f), new Vector2(0.0f, 0.0f),
			new Vector2(0.0f, 1.0f), new Vector2(1.0f, 1.0f), new Vector2(1.0f, 0.0f), new Vector2(0.0f, 0.0f),
			new Vector2(0.0f, 1.0f), new Vector2(1.0f, 1.0f), new Vector2(1.0f, 0.0f), new Vector2(0.0f, 0.0f)
		};
		var normals = new[]
		{
			Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ,
			-Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ,
			-Vector3.UnitX, -Vector3.UnitX, -Vector3.UnitX, -Vector3.UnitX,
			Vector3.UnitX, Vector3.UnitX, Vector3.UnitX, Vector3.UnitX,
			Vector3.UnitY, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY,
			-Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY
		};
		var tangents = new[]
		{
			new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1),
			new Vector4(-1, 0, 0, 1), new Vector4(-1, 0, 0, 1), new Vector4(-1, 0, 0, 1), new Vector4(-1, 0, 0, 1),
			new Vector4(0, 0, 1, 1), new Vector4(0, 0, 1, 1), new Vector4(0, 0, 1, 1), new Vector4(0, 0, 1, 1),
			new Vector4(0, 0, -1, 1), new Vector4(0, 0, -1, 1), new Vector4(0, 0, -1, 1), new Vector4(0, 0, -1, 1),
			new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1),
			new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1)
		};
		var indices = new uint[]
		{
			0, 1, 2, 0, 2, 3,
			4, 5, 6, 4, 6, 7,
			8, 9, 10, 8, 10, 11,
			12, 13, 14, 12, 14, 15,
			16, 17, 18, 16, 18, 19,
			20, 21, 22, 20, 22, 23
		};
		return new Mesh(vertices, indices, normals, uvs, tangents);
	}

	private static Mesh CreateQuad()
	{
		var vertices = new[]
		{
			new Vector4(-0.5f, -0.5f, 0.0f, 1.0f),
			new Vector4(0.5f, -0.5f, 0.0f, 1.0f),
			new Vector4(0.5f, 0.5f, 0.0f, 1.0f),
			new Vector4(-0.5f, 0.5f, 0.0f, 1.0f)
		};
		var normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ };
		var uvs = new[]
		{
			new Vector2(0.0f, 1.0f),
			new Vector2(1.0f, 1.0f),
			new Vector2(1.0f, 0.0f),
			new Vector2(0.0f, 0.0f)
		};
		var tangents = new[]
		{
			new Vector4(1, 0, 0, 1),
			new Vector4(1, 0, 0, 1),
			new Vector4(1, 0, 0, 1),
			new Vector4(1, 0, 0, 1)
		};
		var indices = new uint[] { 0, 1, 2, 0, 2, 3 };
		return new Mesh(vertices, indices, normals, uvs, tangents);
	}

	private static Mesh CreateSphere()
	{
		const int longitudeSegments = 16;
		const int latitudeSegments = 12;
		var vertices = new List<Vector4>((longitudeSegments + 1) * (latitudeSegments + 1));
		var normals = new List<Vector3>(vertices.Capacity);
		var uvs = new List<Vector2>(vertices.Capacity);
		var tangents = new List<Vector4>(vertices.Capacity);
		var indices = new List<uint>(longitudeSegments * latitudeSegments * 6);

		for (var lat = 0; lat <= latitudeSegments; lat++)
		{
			var v = lat / (float)latitudeSegments;
			var phi = v * MathF.PI;
			var y = MathF.Cos(phi);
			var radius = MathF.Sin(phi);

			for (var lon = 0; lon <= longitudeSegments; lon++)
			{
				var u = lon / (float)longitudeSegments;
				var theta = u * MathF.Tau;
				var x = MathF.Cos(theta) * radius;
				var z = MathF.Sin(theta) * radius;
				var normal = Vector3.Normalize(new Vector3(x, y, z));
				vertices.Add(new Vector4(normal * 0.5f, 1.0f));
				normals.Add(normal);
				uvs.Add(new Vector2(u, 1.0f - v));
				var tangentDir = new Vector3(-MathF.Sin(theta), 0.0f, MathF.Cos(theta));
				if (tangentDir.LengthSquared() <= 1e-5f)
				{
					tangentDir = Vector3.UnitX;
				}

				tangents.Add(new Vector4(Vector3.Normalize(tangentDir), 1.0f));
			}
		}

		for (var lat = 0; lat < latitudeSegments; lat++)
		{
			for (var lon = 0; lon < longitudeSegments; lon++)
			{
				var current = lat * (longitudeSegments + 1) + lon;
				var next = current + longitudeSegments + 1;
				indices.Add((uint)current);
				indices.Add((uint)next);
				indices.Add((uint)(current + 1));
				indices.Add((uint)(current + 1));
				indices.Add((uint)next);
				indices.Add((uint)(next + 1));
			}
		}

		return new Mesh(vertices, indices, normals, uvs, tangents);
	}
}
