using System.Numerics;
using WolfEngine.Rendering;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class DebugPrimitiveMeshFactoryTests
{
	[Test]
	public void GetMesh_ReusesCachedMeshPerPrimitiveType()
	{
		var factory = new DebugPrimitiveMeshFactory();

		var first = factory.GetMesh(DebugPrimitiveType.Box);
		var second = factory.GetMesh(DebugPrimitiveType.Box);
		var sphere = factory.GetMesh(DebugPrimitiveType.Sphere);

		Assert.That(first, Is.SameAs(second));
		Assert.That(first, Is.Not.SameAs(sphere));
	}

	[TestCase(DebugPrimitiveType.Box)]
	[TestCase(DebugPrimitiveType.Sphere)]
	[TestCase(DebugPrimitiveType.Quad)]
	public void GetMesh_GeneratesValidMeshData(DebugPrimitiveType primitiveType)
	{
		var factory = new DebugPrimitiveMeshFactory();

		var mesh = factory.GetMesh(primitiveType);

		Assert.That(mesh.Vertices, Is.Not.Empty);
		Assert.That(mesh.Indices, Is.Not.Empty);
		Assert.That(mesh.BoundingSphere.Radius, Is.GreaterThan(0.0f));
	}

	[Test]
	public void SphereTrianglesFaceTheirOutwardVertexNormals()
	{
		var mesh = new DebugPrimitiveMeshFactory().GetMesh(DebugPrimitiveType.Sphere);
		var checkedTriangles = 0;
		for (var i = 0; i < mesh.Indices.Length; i += 3)
		{
			var a = (int)mesh.Indices[i];
			var b = (int)mesh.Indices[i + 1];
			var c = (int)mesh.Indices[i + 2];
			var edgeA = new Vector3(mesh.Vertices[b].X - mesh.Vertices[a].X,
				mesh.Vertices[b].Y - mesh.Vertices[a].Y, mesh.Vertices[b].Z - mesh.Vertices[a].Z);
			var edgeB = new Vector3(mesh.Vertices[c].X - mesh.Vertices[a].X,
				mesh.Vertices[c].Y - mesh.Vertices[a].Y, mesh.Vertices[c].Z - mesh.Vertices[a].Z);
			var faceNormal = Vector3.Cross(edgeA, edgeB);
			if (faceNormal.LengthSquared() < 1e-10f) continue;
			Assert.That(Vector3.Dot(faceNormal, mesh.Normals[a]), Is.GreaterThan(0.0f));
			checkedTriangles++;
		}
		Assert.That(checkedTriangles, Is.GreaterThan(0));
	}
}
