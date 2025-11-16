namespace WolfEngine.ECS;

public class FreeList
{
	struct Slot
	{
		public int NextFree;
		public int Generation;
		public bool Alive;
	}

	private Slot[] _slots = new Slot[1024];
	private int _freeHead = -1;
	private int _count;

	public Entity Create()
	{
		int index;

		if (_freeHead != -1)
		{
			// Reuse a slot
			index = _freeHead;
			_freeHead = _slots[index].NextFree;
		}
		else
		{
			// Expand if needed
			if (_count >= _slots.Length)
				Array.Resize(ref _slots, _slots.Length * 2);

			index = _count++;
		}

		ref var slot = ref _slots[index];
		slot.Alive = true;
		slot.Generation++;

		return new Entity(index, slot.Generation);
	}

	public void Destroy(Entity entity)
	{
		ref var slot = ref _slots[entity.Index];

		// Ignore stale destroy calls
		if (!slot.Alive || slot.Generation != entity.Generation)
			return;

		slot.Alive = false;
		slot.NextFree = _freeHead;
		_freeHead = entity.Index;
	}

	public bool IsAlive(Entity entity)
	{
		return entity.Index < _count &&
			_slots[entity.Index].Alive &&
			_slots[entity.Index].Generation == entity.Generation;
	}
}
