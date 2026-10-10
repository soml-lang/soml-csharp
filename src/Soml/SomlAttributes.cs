namespace Soml;

/// <summary>
/// Sets the key for a property, field, or constructor parameter, or the string for an enum member, overriding the naming policy.
/// </summary>
/// <example>
/// <code>
/// public sealed class Server
/// {
/// 	[SomlName("listen-on")]
/// 	public int Port { get; set; }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class SomlNameAttribute(string name) : Attribute
{
	/// <summary>
	/// The key, or the string of an enum member.
	/// </summary>
	public string Name { get; } = name;
}

/// <summary>
/// Leaves a property, field, or enum member out of reading and writing.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class SomlIgnoreAttribute : Attribute;

/// <summary>
/// Marks the constructor that <see cref="SomlSerializer"/> creates a type with.
/// </summary>
/// <remarks>
/// Without it, the public parameterless constructor is used, or the only public constructor when there is no parameterless one, such as the primary constructor of a record. Use it to choose another public constructor.
/// When inherited members share a CLR name, give the parameter a different name and use <see cref="SomlNameAttribute"/> to select its key.
/// </remarks>
[AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false)]
public sealed class SomlConstructorAttribute : Attribute;
