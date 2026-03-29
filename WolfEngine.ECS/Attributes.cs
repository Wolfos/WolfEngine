namespace WolfEngine.ECS;

[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public class ExcludeFromEditorAttribute: Attribute
{
	
}

[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public class EditorOnlyAttribute: Attribute
{
	
}
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public class ExcludeFromAddComponentAttribute: Attribute
{
	
}

[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Property | AttributeTargets.Field, Inherited = false)]
public sealed class NotSerializedAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = false)]
public sealed class HideFromEditorAttribute : Attribute
{
}

