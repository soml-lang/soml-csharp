namespace Soml.Serialization;

/// <summary>
/// The base of a context that the source generator fills in with the code to read and write types, so that <see cref="SomlSerializer"/> needs no reflection, as with trimming and Native AOT.
/// </summary>
/// <example>
/// <code>
/// [SomlSerializable(typeof(Config))]
/// public partial class AppSomlContext : SomlSerializerContext;
///
/// var config = SomlSerializer.Deserialize(text, AppSomlContext.Default.Config);
/// </code>
/// </example>
public abstract class SomlSerializerContext
{
	/// <summary>
	/// Creates a context with options, or the default options.
	/// </summary>
	protected SomlSerializerContext(SomlSerializerOptions? options)
	{
		Options = options ?? SomlSerializerOptions.Default;
	}

	/// <summary>
	/// The options of the context, which every <see cref="SomlTypeInfo{T}"/> of it uses. The naming policy decides the keys when the context makes its converters.
	/// </summary>
	public SomlSerializerOptions Options { get; }
}

/// <summary>
/// How to read and write values of a type, for <see cref="SomlSerializer"/>, as a <see cref="SomlSerializerContext"/> gives it.
/// </summary>
/// <typeparam name="T">The type.</typeparam>
/// <param name="context">The context it belongs to.</param>
/// <param name="createConverter">Makes the converter of the type, on its first use, as the reflection path does, so that a document that is not valid is reported before a type that cannot be read.</param>
public sealed class SomlTypeInfo<T>(SomlSerializerContext context, Func<SomlConverter<T>> createConverter)
{
	private readonly Func<SomlConverter<T>> _createConverter = createConverter ?? throw new ArgumentNullException(nameof(createConverter));

	/// <summary>
	/// The context it belongs to.
	/// </summary>
	public SomlSerializerContext Context { get; } = context ?? throw new ArgumentNullException(nameof(context));

	/// <summary>
	/// The options of the context.
	/// </summary>
	public SomlSerializerOptions Options => Context.Options;

	/// <summary>
	/// The converter of the type.
	/// </summary>
	/// <exception cref="SomlException">When the type cannot be read or written, such as when two of its members have the same key.</exception>
	public SomlConverter<T> Converter
	{
		get => field ??= _createConverter();
	}
}

/// <summary>
/// Makes the source generator write the code to read and write a type, and the types it holds, into a <see cref="SomlSerializerContext"/>. The context gets a property of the type's name, which gives its <see cref="SomlTypeInfo{T}"/>.
/// </summary>
/// <param name="type">The type.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class SomlSerializableAttribute(Type type) : Attribute
{
	/// <summary>
	/// The type.
	/// </summary>
	public Type Type { get; } = type;

	/// <summary>
	/// The name of the context's property for the type, when the type's own name is not right, as when two types have the same name.
	/// </summary>
	public string? TypeInfoPropertyName { get; set; }
}
