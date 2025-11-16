namespace WolfEngine.ECS;

public readonly ref struct View<T1,T2>
	where T1:struct where T2:struct
{
	private readonly ComponentPool<T1> _a;
	private readonly ComponentPool<T2> _b;
	public View(ComponentPool<T1> a, ComponentPool<T2> b) { this._a=a; this._b=b; }

	public Enumerator GetEnumerator() => new(_a, _b);

	public ref struct Enumerator {
		// iterate dense array of the smaller pool; yield pairs when other Has(entity)
		// TODO: Implement
	}
}