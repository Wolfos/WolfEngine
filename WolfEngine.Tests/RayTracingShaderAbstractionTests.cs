namespace WolfEngine.Tests;

[TestFixture]
public class RayTracingShaderAbstractionTests
{
	[Test]
	public void EngineShaders_ConfinePortableRayQueriesToTheSharedAbstraction()
	{
		var shaderRoot = Path.GetFullPath(Path.Combine(
			TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "WolfEngine", "Shaders"));
		var abstractionPath = Path.GetFullPath(Path.Combine(shaderRoot, "Common", "raytracing_common.slang"));
		var offenders = Directory
			.EnumerateFiles(shaderRoot, "*.slang", SearchOption.AllDirectories)
			.Where(path => Path.GetFullPath(path).Equals(abstractionPath, StringComparison.Ordinal) == false)
			.Where(path => File.ReadAllText(path).Contains("RayQuery<", StringComparison.Ordinal))
			.Select(path => Path.GetRelativePath(shaderRoot, path))
			.ToArray();

		Assert.That(offenders, Is.Empty,
			"RayQuery on Metal lowers to the slower intersection_query compatibility path. " +
			"Use Common/raytracing_common.slang so Metal gets its native intersector fast path:\n" +
			string.Join("\n", offenders));
	}
}
