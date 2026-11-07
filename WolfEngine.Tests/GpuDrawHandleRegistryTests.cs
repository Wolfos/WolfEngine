using WolfEngine.ECS;
using WolfEngine.Rendering;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class GpuDrawHandleRegistryTests
{
	[Test]
	public void TryAcquire_ReportsAFullPoolInsteadOfThrowing()
	{
		var pool = new GpuDrawHandlePool(2);

		Assert.Multiple(() =>
		{
			Assert.That(pool.TryAcquire(out var first), Is.True);
			Assert.That(pool.TryAcquire(out var second), Is.True);
			Assert.That(first, Is.Not.EqualTo(second));
			Assert.That(pool.TryAcquire(out var none), Is.False);
			Assert.That(none.IsValid, Is.False);
			Assert.Throws<InvalidOperationException>(() => pool.Acquire());
		});
	}

	[Test]
	public void Handles_PackA20BitIndexAndA12BitGeneration()
	{
		var handle = GpuDrawHandle.Create(GpuDrawHandle.MaxIndex, 0xFFF);

		Assert.Multiple(() =>
		{
			Assert.That(GpuDrawHandle.MaxIndex, Is.EqualTo((1 << 20) - 1));
			Assert.That(handle.Index, Is.EqualTo(GpuDrawHandle.MaxIndex));
			Assert.That(handle.Generation, Is.EqualTo(0xFFF));
			Assert.That(handle.Value, Is.EqualTo(0xFFFFFFFFu));
			Assert.Throws<ArgumentOutOfRangeException>(() => GpuDrawHandle.Create(GpuDrawHandle.MaxIndex + 1, 1));
			Assert.Throws<ArgumentOutOfRangeException>(() => GpuDrawHandle.Create(1, 0x1000));
		});
	}

	[Test]
	public void Generations_WrapWithinTwelveBits_AndSkipZero()
	{
		Assert.Multiple(() =>
		{
			Assert.That(GpuDrawHandle.NextGeneration(1), Is.EqualTo(2));
			Assert.That(GpuDrawHandle.NextGeneration(0xFFF), Is.EqualTo(1));
		});
	}

	[Test]
	public void ReleasingASlot_InvalidatesItsHandle_AndTheSlotIsReusedBeforeTheTableGrows()
	{
		var pool = new GpuDrawHandlePool(8);
		var first = pool.Acquire();
		pool.Acquire();
		Assert.That(pool.SlotCount, Is.EqualTo(3), "slot zero and the two handed out");

		pool.Release(first);
		var reused = pool.Acquire();

		Assert.Multiple(() =>
		{
			Assert.That(pool.IsCurrent(first), Is.False);
			Assert.That(reused.Index, Is.EqualTo(first.Index));
			Assert.That(reused.Generation, Is.Not.EqualTo(first.Generation));
			Assert.That(pool.SlotCount, Is.EqualTo(3), "a reused slot does not grow the table");
		});
	}

	[Test]
	public void AcquireInstance_GoesPastTheOldSixteenBitLimit()
	{
		var registry = new GpuDrawHandleRegistry();
		const int count = 70000;
		var last = GpuDrawHandle.Invalid;
		for (var i = 0; i < count; i++)
		{
			last = registry.AcquireInstance(Key(i));
		}

		Assert.Multiple(() =>
		{
			Assert.That(last.IsValid, Is.True);
			Assert.That(last.Index, Is.EqualTo(count));
			Assert.That(registry.GetActiveInstanceUpperBound(), Is.EqualTo((uint)count + 1));
		});
	}

	[TestCase(0, 1, 16, 64, 16)]
	[TestCase(16, 16, 16, 64, 16)]
	[TestCase(16, 17, 16, 64, 32)]
	[TestCase(16, 50, 16, 64, 64)]
	[TestCase(16, 1000, 16, 64, 64)]
	public void GrowCapacity_DoublesUpToTheMaximum(int current, int required, int initial, int maximum, int expected)
	{
		Assert.That(GpuDrawResources.GrowCapacity(current, required, initial, maximum), Is.EqualTo(expected));
	}

	[Test]
	public void AcquireInstance_SharesOneSlotPerKey_UntilEveryHolderReleasesIt()
	{
		var registry = new GpuDrawHandleRegistry();
		var first = registry.AcquireInstance(Key(1));
		var second = registry.AcquireInstance(Key(1));
		var other = registry.AcquireInstance(Key(2));

		Assert.That(second, Is.EqualTo(first));
		Assert.That(other, Is.Not.EqualTo(first));

		registry.ReleaseInstance(Key(2));
		Assert.That(registry.GetActiveInstanceUpperBound(), Is.EqualTo((uint)first.Index + 1));

		registry.ReleaseInstance(Key(1));
		Assert.That(registry.GetActiveInstanceUpperBound(), Is.EqualTo((uint)first.Index + 1), "one holder remains");

		registry.ReleaseInstance(Key(1));
		Assert.That(registry.GetActiveInstanceUpperBound(), Is.EqualTo(1u));
	}

	private static GpuDrawDatabase.DrawRecordKey Key(int entityIndex) => new(0, new Entity(entityIndex, 1), 0);
}
