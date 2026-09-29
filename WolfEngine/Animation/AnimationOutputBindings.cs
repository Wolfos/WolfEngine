using WolfEngine.Rendering;
using WolfEngine.ECS;

namespace WolfEngine.Animation;

public delegate float AnimationPropertyRead<T>(in T component) where T : struct, IEntityComponent;
public delegate void AnimationPropertyWrite<T>(ref T component, float value) where T : struct, IEntityComponent;

/// <summary>Explicit scalar bindings; resolves once and never reflects over gameplay data per frame.</summary>
public static class AnimationPropertyBindings
{
    private static readonly Dictionary<string, Func<World, Entity, IAnimationPropertyBinding>> Factories = new(StringComparer.Ordinal);
    static AnimationPropertyBindings()
    {
        Register<Light>("Light.Intensity", (in Light light) => light.Intensity, (ref Light light, float value) => light.Intensity = value);
        Register<Light>("Light.Range", (in Light light) => light.Range, (ref Light light, float value) => light.Range = value);
    }
    public static void Register<T>(string property, AnimationPropertyRead<T> read, AnimationPropertyWrite<T> write) where T : struct, IEntityComponent
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        lock (Factories)
            if (!Factories.TryAdd(property, (world, entity) => world.HasComponent<T>(entity) ? new Binding<T>(world, entity, read, write) :
                throw new InvalidOperationException($"Animation target lacks '{typeof(T).Name}'.")))
                throw new InvalidOperationException($"Animation property '{property}' is already registered.");
    }
    internal static IAnimationPropertyBinding Resolve(string property, World world, Entity entity)
    {
        lock (Factories) return Factories.TryGetValue(property, out var factory) ? factory(world, entity) :
            throw new InvalidOperationException($"Animation property '{property}' is not registered.");
    }
    private sealed class Binding<T>(World world, Entity entity, AnimationPropertyRead<T> read, AnimationPropertyWrite<T> write) : IAnimationPropertyBinding where T : struct, IEntityComponent
    {
        public float Read() => read(in world.GetComponent<T>(entity));
        public void Write(float value) { if (world.HasComponent<T>(entity)) write(ref world.GetComponent<T>(entity), value); }
    }
}
internal interface IAnimationPropertyBinding { float Read(); void Write(float value); }
internal sealed class AnimationOutputBindings
{
    private readonly World _world;
    private readonly Entity[] _transforms;
    private readonly IAnimationPropertyBinding[] _properties;
    private AnimationOutputBindings(World world, Entity[] transforms, IAnimationPropertyBinding[] properties)
    { _world = world; _transforms = transforms; _properties = properties; }
    internal static AnimationOutputBindings Resolve(World world, Entity root, AnimationGraphInstance instance)
    {
        var program = instance.Program;
        var transforms = new Entity[program.TransformBindings.Length];
        for (var i = 0; i < transforms.Length; i++)
        {
            transforms[i] = ResolvePath(world, root, program.TransformBindings[i].Path);
            if (!world.HasComponent<LocalTransform>(transforms[i])) throw new InvalidOperationException("Animation transform target lacks a transform.");
            var local = world.GetComponent<LocalTransform>(transforms[i]);
            instance.SetTransformDefault(i, new(local.LocalPosition, local.LocalRotation, local.LocalScale));
        }
        var properties = new IAnimationPropertyBinding[program.PropertyBindings.Length];
        for (var i = 0; i < properties.Length; i++)
        {
            var binding = program.PropertyBindings[i];
            properties[i] = AnimationPropertyBindings.Resolve(binding.Property, world, ResolvePath(world, root, binding.Path));
            instance.SetPropertyDefault(i, properties[i].Read());
        }
        return new(world, transforms, properties);
    }
    internal void Apply(Pose pose)
    {
        for (var i = 0; i < _transforms.Length; i++)
        {
            var entity = _transforms[i]; if (!_world.HasComponent<LocalTransform>(entity)) continue;
            var value = pose.Transforms[i]; var local = _world.GetComponent<LocalTransform>(entity);
            if (local.LocalPosition != value.Position) _world.SetLocalPosition(entity, value.Position);
            if (local.LocalRotation != value.Rotation) _world.SetLocalRotation(entity, value.Rotation);
            if (local.LocalScale != value.Scale) _world.SetLocalScale(entity, value.Scale);
        }
        for (var i = 0; i < _properties.Length; i++) _properties[i].Write(pose.Values[i]);
    }
    private static Entity ResolvePath(World world, Entity root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (world.HasComponent<NameComponent>(current) && world.GetComponent<NameComponent>(current).Name == segment) continue;
            var match = default(Entity);
            var child = world.HasComponent<Children>(current) ? world.GetComponent<Children>(current).First : default;
            while (child.IsValid)
            {
                if (world.HasComponent<NameComponent>(child) && world.GetComponent<NameComponent>(child).Name == segment)
                {
                    if (match.IsValid) throw new InvalidOperationException($"Ambiguous animation path '{path}'.");
                    match = child;
                }
                child = world.HasComponent<Sibling>(child) ? world.GetComponent<Sibling>(child).Next : default;
            }
            if (!match.IsValid) throw new InvalidOperationException($"Animation target '{path}' is missing.");
            current = match;
        }
        return current;
    }
}
