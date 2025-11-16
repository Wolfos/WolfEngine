namespace WolfEngine.ECS;

public readonly ref struct View<T1,T2>
	where T1:struct where T2:struct
{
	private readonly ComponentPool<T1> _a;
	private readonly ComponentPool<T2> _b;
	public View(ComponentPool<T1> a, ComponentPool<T2> b) { this._a=a; this._b=b; }

	public Enumerator GetEnumerator() => new(_a, _b);

	public ref struct Enumerator {
		public readonly ref struct Entry
		{
			private readonly ComponentPool<T1> _a;
			private readonly ComponentPool<T2> _b;
			public readonly Entity Entity;

			public Entry(Entity entity, ComponentPool<T1> a, ComponentPool<T2> b)
			{
				Entity = entity;
				_a = a;
				_b = b;
			}

			public ref T1 First => ref _a.Get(Entity);
			public ref T2 Second => ref _b.Get(Entity);
		}

		private readonly ComponentPool<T1> _pool1;
		private readonly ComponentPool<T2> _pool2;
		private readonly ReadOnlySpan<int> _iterEntities;
		private readonly bool _iteratingPool1;
		private int _index;

		public Enumerator(ComponentPool<T1> a, ComponentPool<T2> b)
		{
			_pool1 = a;
			_pool2 = b;

			if (a.Count <= b.Count)
			{
				_iterEntities = a.EntitiesSpan;
				_iteratingPool1 = true;
			}
			else
			{
				_iterEntities = b.EntitiesSpan;
				_iteratingPool1 = false;
			}

			_index = -1;
			Current = default;
		}

		public Entry Current { get; private set; }

		public bool MoveNext()
		{
			while (++_index < _iterEntities.Length)
			{
				var entity = new Entity(_iterEntities[_index], 0);
				if (_iteratingPool1)
				{
					if (_pool2.Has(entity))
					{
						Current = new Entry(entity, _pool1, _pool2);
						return true;
					}
				}
				else
				{
					if (_pool1.Has(entity))
					{
						Current = new Entry(entity, _pool1, _pool2);
						return true;
					}
				}
			}

			return false;
		}
	}
}
