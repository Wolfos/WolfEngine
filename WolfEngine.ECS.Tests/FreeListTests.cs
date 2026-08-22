using NUnit.Framework;

namespace WolfEngine.ECS.Tests;

[TestFixture]
public class FreeListTests
{
    [Test]
    public void SetEnabled_UpdatesStoredEnabledState()
    {
        var freeList = new FreeList();
        var entity = freeList.Create();

        freeList.SetEnabled(entity, false);

        Assert.That(freeList.IsEnabled(entity), Is.False);
    }

    [Test]
    public void SetEnabled_IgnoresStaleEntityGeneration()
    {
        var freeList = new FreeList();
        var entity = freeList.Create();
        freeList.Destroy(entity);
        var replacement = freeList.Create();

        freeList.SetEnabled(entity, false);

        Assert.That(replacement.Index, Is.EqualTo(entity.Index));
        Assert.That(freeList.IsEnabled(replacement), Is.True);
    }
}
