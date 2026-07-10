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
}
