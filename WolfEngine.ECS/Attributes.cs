namespace WolfEngine.ECS;

[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public class ExcludeFromEditorAttribute: Attribute
{
	
}

// Doesn't actually do anything yet, but asset cooker will strip these from final build
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public class EditorOnly: Attribute
{
	
}
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public class ExcludeFromAddComponentAttribute: Attribute
{
	
}