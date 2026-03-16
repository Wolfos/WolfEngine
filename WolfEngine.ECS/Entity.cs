namespace WolfEngine.ECS;

public readonly struct Entity : IEquatable<Entity>
{
	public readonly int Index;
	public readonly int Generation;
	public bool IsValid => Generation != 0;

	public Entity(int index, int generation)
	{
		Index = index;
		Generation = generation;
	}
	

	public bool Equals(Entity other)
	{
		return Index == other.Index && Generation == other.Generation;
	}

	public override bool Equals(object? obj)
	{
		return obj is Entity other && Equals(other);
	}

	public override int GetHashCode()
	{
		return HashCode.Combine(Index, Generation);
	}

    public static bool operator ==(Entity left, Entity right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Entity left, Entity right)
    {
        return (left == right) == false;
    }
}