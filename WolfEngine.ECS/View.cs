namespace WolfEngine.ECS;

public readonly ref struct View<T1>
	where T1:struct, IEntityComponent
{
	private readonly ComponentPool<T1> _a;
	public View(ComponentPool<T1> a) { _a = a; }

	public Enumerator GetEnumerator() => new(_a);

	public ref struct Enumerator {
		public readonly ref struct Entry
		{
			private readonly ComponentPool<T1> _a;
			public readonly Entity Entity;

			public Entry(Entity entity, ComponentPool<T1> a)
			{
				Entity = entity;
				_a = a;
			}

			public ref T1 First => ref _a.Get(Entity);
		}

		private readonly ComponentPool<T1> _pool1;
		private readonly ReadOnlySpan<Entity> _iterEntities;
		private int _index;

		public Enumerator(ComponentPool<T1> a)
		{
			_pool1 = a;
			_iterEntities = a.EntitiesSpan;
			_index = -1;
			Current = default;
		}

		public Entry Current { get; private set; }

		public bool MoveNext()
		{
			if (++_index >= _iterEntities.Length) return false;

			var entity = _iterEntities[_index];
			Current = new Entry(entity, _pool1);
			return true;
		}
	}
}

public readonly ref struct View<T1,T2>
	where T1:struct, IEntityComponent where T2:struct, IEntityComponent
{
	private readonly ComponentPool<T1> _a;
	private readonly ComponentPool<T2> _b;
	public View(ComponentPool<T1> a, ComponentPool<T2> b) { _a=a; _b=b; }

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
		private readonly ReadOnlySpan<Entity> _iterEntities;
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
				var entity = _iterEntities[_index];
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

public readonly ref struct View<T1,T2,T3>
	where T1:struct, IEntityComponent where T2:struct, IEntityComponent where T3:struct, IEntityComponent
{
	private readonly ComponentPool<T1> _a;
	private readonly ComponentPool<T2> _b;
	private readonly ComponentPool<T3> _c;
	public View(ComponentPool<T1> a, ComponentPool<T2> b, ComponentPool<T3> c) { _a=a; _b=b; _c=c; }

	public Enumerator GetEnumerator() => new(_a, _b, _c);

	public ref struct Enumerator {
		public readonly ref struct Entry
		{
			private readonly ComponentPool<T1> _a;
			private readonly ComponentPool<T2> _b;
			private readonly ComponentPool<T3> _c;
			public readonly Entity Entity;

			public Entry(Entity entity, ComponentPool<T1> a, ComponentPool<T2> b, ComponentPool<T3> c)
			{
				Entity = entity;
				_a = a;
				_b = b;
				_c = c;
			}

			public ref T1 First => ref _a.Get(Entity);
			public ref T2 Second => ref _b.Get(Entity);
			public ref T3 Third => ref _c.Get(Entity);
		}

		private readonly ComponentPool<T1> _pool1;
		private readonly ComponentPool<T2> _pool2;
		private readonly ComponentPool<T3> _pool3;
		private readonly ReadOnlySpan<Entity> _iterEntities;
		private readonly byte _iteratingPool;
		private int _index;

		public Enumerator(ComponentPool<T1> a, ComponentPool<T2> b, ComponentPool<T3> c)
		{
			_pool1 = a;
			_pool2 = b;
			_pool3 = c;

			if (a.Count <= b.Count && a.Count <= c.Count)
			{
				_iterEntities = a.EntitiesSpan;
				_iteratingPool = 1;
			}
			else if (b.Count <= c.Count)
			{
				_iterEntities = b.EntitiesSpan;
				_iteratingPool = 2;
			}
			else
			{
				_iterEntities = c.EntitiesSpan;
				_iteratingPool = 3;
			}

			_index = -1;
			Current = default;
		}

		public Entry Current { get; private set; }

		public bool MoveNext()
		{
			while (++_index < _iterEntities.Length)
			{
				var entity = _iterEntities[_index];
				switch (_iteratingPool)
				{
					case 1:
						if (_pool2.Has(entity) && _pool3.Has(entity))
						{
							Current = new Entry(entity, _pool1, _pool2, _pool3);
							return true;
						}
						break;
					case 2:
						if (_pool1.Has(entity) && _pool3.Has(entity))
						{
							Current = new Entry(entity, _pool1, _pool2, _pool3);
							return true;
						}
						break;
					default:
						if (_pool1.Has(entity) && _pool2.Has(entity))
						{
							Current = new Entry(entity, _pool1, _pool2, _pool3);
							return true;
						}
						break;
				}
			}

			return false;
		}
	}
}
