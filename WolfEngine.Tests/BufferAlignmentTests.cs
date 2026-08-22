using WolfEngine.Rendering;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class BufferAlignmentTests
{
	[TestCase(0ul, 48ul, 0ul)]
	[TestCase(1ul, 48ul, 48ul)]
	[TestCase(48ul, 48ul, 48ul)]
	[TestCase(1535137ul, 48ul, 1535184ul)]
	[TestCase(13ul, 0ul, 13ul)]
	public void AlignUp_SupportsNonPowerOfTwoAlignment(
		ulong value,
		ulong alignment,
		ulong expected)
	{
		Assert.That(BufferAlignment.AlignUp(value, alignment), Is.EqualTo(expected));
	}

	[Test]
	public void AlignUp_DoesNotMoveAnAlreadyAlignedSecondMacarenaRange()
	{
		const ulong vertexStride = 48;
		const ulong betaSurfaceVertexCount = 15991;
		const ulong sourceAndInstanceEnd = betaSurfaceVertexCount * 2 * vertexStride;

		Assert.That(BufferAlignment.AlignUp(sourceAndInstanceEnd, vertexStride), Is.EqualTo(sourceAndInstanceEnd));
	}
}
