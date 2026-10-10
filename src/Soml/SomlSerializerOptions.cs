using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Soml;

/// <summary>
/// Options for <see cref="SomlSerializer"/>.
/// </summary>
/// <remarks>
/// Reuse an instance to reuse its cached type converters.
/// </remarks>
public sealed class SomlSerializerOptions
{
	private readonly ConcurrentDictionary<Type, Converter> _converters = new();

	/// <summary>
	/// The default options: kebab-case keys, members in the order of the type, unknown keys ignored, and null members written.
	/// </summary>
	public static SomlSerializerOptions Default { get; } = new();

	/// <summary>
	/// Whether to sort every object's keys for canonical output suitable for hashing, signing, or comparing. By default, members keep their type or collection order; all other canonical rules still apply.
	/// </summary>
	public bool Canonical { get; init; }

	/// <summary>
	/// Converts property, field, constructor parameter, and enum member names. The default is kebab-case: <c>AllowedOrigins</c> becomes <c>allowed-origins</c>. Set to <c>null</c> to preserve names. <see cref="SomlNameAttribute"/> overrides this policy. Dictionary string keys are unchanged; enum keys follow the enum naming policy.
	/// </summary>
	public JsonNamingPolicy? PropertyNamingPolicy { get; init; } = JsonNamingPolicy.KebabCaseLower;

	/// <summary>
	/// Whether unknown keys are errors, helping catch misspellings. By default, they are ignored.
	/// </summary>
	public bool RejectUnknownKeys { get; init; }

	/// <summary>
	/// Whether to omit null properties and fields when writing. By default, they are written as <c>null</c>, which differs from a missing member.
	/// </summary>
	public bool OmitNullMembers { get; init; }

	// Note: Not using GetOrAdd without TryGetValue because it gave mixed hot/cold timings and no consistent speedup on .NET 10 ARM64.
	[RequiresUnreferencedCode(SomlSerializer.ReflectionMessage)]
	[RequiresDynamicCode(SomlSerializer.ReflectionMessage)]
	internal Converter GetConverter(Type type) => _converters.TryGetValue(type, out var converter) ? converter : _converters.GetOrAdd(type, static (type, options) => Converter.Create(type, options), this);

	internal string ConvertName(string name) => PropertyNamingPolicy?.ConvertName(name) ?? name;
}
