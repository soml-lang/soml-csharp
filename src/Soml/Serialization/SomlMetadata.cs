using System.ComponentModel;

namespace Soml.Serialization;

/// <summary>
/// Helpers for the code that the source generator writes. They are not meant to be called directly.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SomlMetadata
{
	/// <summary>
	/// Appends a member from trusted generated metadata whose keys have already been checked for uniqueness. This does not check for duplicate keys.
	/// </summary>
	public static void AddMember(SomlObject members, string key, SomlValue value) => members.Append(key, value);

	/// <summary>
	/// The key of a member: its <see cref="SomlNameAttribute"/>, or its name through the naming policy of the options.
	/// </summary>
	public static string Key(SomlSerializerOptions options, string name, string? explicitName)
	{
		ArgumentNullException.ThrowIfNull(options);
		return explicitName ?? options.ConvertName(name);
	}

	/// <summary>
	/// Checks that no two members of a type have the same key, as the reflection path does.
	/// </summary>
	/// <exception cref="SomlException">When two members have the same key.</exception>
	public static void CheckUniqueKeys(Type type, ReadOnlySpan<string> memberNames, ReadOnlySpan<string> keys)
	{
		if (keys.Length < 2)
		{
			return;
		}

		var seen = new Dictionary<string, string>(keys.Length, StringComparer.Ordinal);

		for (var index = 0; index < keys.Length; index++)
		{
			if (!seen.TryAdd(keys[index], memberNames[index]))
			{
				throw new SomlException(Message.DescribeText($"The members {seen[keys[index]]} and {memberNames[index]} of {SomlConverterState.DescribeType(type)} both have the key '{Message.Abbreviate(keys[index])}'. Use [SomlName] or [SomlIgnore] on one of them"));
			}
		}
	}

	/// <summary>
	/// Checks that no two constructor parameters that a type reads have the same key, as the reflection path does.
	/// </summary>
	/// <exception cref="SomlException">When two parameters have the same key.</exception>
	public static void CheckUniqueParameterKeys(Type type, ReadOnlySpan<string> keys)
	{
		if (keys.Length < 2)
		{
			return;
		}

		var seen = new HashSet<string>(keys.Length, StringComparer.Ordinal);

		foreach (var key in keys)
		{
			if (!seen.Add(key))
			{
				throw new SomlException(Message.DescribeText($"Two parameters of the constructor of {SomlConverterState.DescribeType(type)} have the key '{Message.Abbreviate(key)}'"));
			}
		}
	}

	/// <summary>
	/// The error of reading a type that has no constructor to read it with.
	/// </summary>
	public static SomlException NoConstructor(SomlConverterState state, Type type)
	{
		ArgumentNullException.ThrowIfNull(state);
		return state.Error($"Cannot create {SomlConverterState.DescribeType(type)}, because it has no public parameterless constructor and not exactly one other public constructor. Mark the constructor to read it with [SomlConstructor]");
	}
}
