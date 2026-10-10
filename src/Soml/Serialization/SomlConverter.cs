using System.Globalization;
using System.Runtime.InteropServices;

namespace Soml.Serialization;

/// <summary>
/// Reads and writes the values of one type, for <see cref="SomlSerializer"/> with a <see cref="SomlTypeInfo{T}"/>, as the source generator makes them.
/// </summary>
/// <typeparam name="T">The type.</typeparam>
public abstract class SomlConverter<T> : IExpected
{
	/// <summary>
	/// The kind of value that the converter reads, for error messages, such as <c>an int</c> or <c>an object</c>.
	/// </summary>
	public abstract string Expected { get; }

	/// <summary>
	/// Reads a value that is not null.
	/// </summary>
	/// <exception cref="SomlException">When the value does not fit the type.</exception>
	public abstract T Read(SomlValue value, SomlConverterState state);

	/// <summary>
	/// Writes a value that is not null.
	/// </summary>
	/// <exception cref="SomlException">When the value cannot be written.</exception>
	public abstract SomlValue Write(T value, SomlConverterState state);

	/*
	The member names of an enum, which a dictionary key of the enum is one of, or `null` for another type.
	*/
	internal virtual IEnumKeys<T>? EnumKeys => null;
}

/*
The kind of value that a converter reads, such as “an int”, without its type, for the reflection path, which has the type only at run time.
*/
internal interface IExpected
{
	string Expected { get; }
}

/// <summary>
/// The state of one call of <see cref="SomlSerializer"/>: its options, and the path to the value that it is reading or writing, for error messages.
/// </summary>
public class SomlConverterState
{
	private const int MaximumDiagnosticLength = 1000;

	private List<(string? Key, int Index)>? _path;

	private List<(string? Key, int Index)> Path => _path ??= [];

	internal static bool CanBeNull<T>() => Nullability<T>.Value;

	private static class Nullability<T>
	{
		public static readonly bool Value = default(T) is null;
	}

	internal SomlConverterState(SomlSerializerOptions options)
	{
		Options = options;
	}

	/// <summary>
	/// The options of the call.
	/// </summary>
	public SomlSerializerOptions Options { get; }

	/// <summary>
	/// Enters the member with a key, for the path of an error.
	/// </summary>
	public void Push(string key)
	{
		ArgumentNullException.ThrowIfNull(key);
		Path.Add((key, 0));
	}

	/// <summary>
	/// Enters the item at an index, for the path of an error.
	/// </summary>
	public void Push(int index)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(index);
		Path.Add((null, index));
	}

	/// <summary>
	/// Leaves the member or item that the last <see cref="Push(string)"/> or <see cref="Push(int)"/> entered.
	/// </summary>
	public void Pop() => _path!.RemoveAt(_path.Count - 1);

	/// <summary>
	/// An error about the value at the current path. Every character of the message that a terminal acts on, such as a control character from a key, is shown as an escape, such as <c>\u{9b}</c>.
	/// </summary>
	/// <remarks>Diagnostic text is limited to 1000 UTF-16 code units before terminal escaping. When an error wraps another exception, its full original message remains available through <see cref="Exception.InnerException"/>.</remarks>
	public SomlException Error(string message) => Error(message, isAboutKey: false);

	/*
	The message can hold text from the document, such as a key, so every character that a terminal acts on is shown as an escape, as `Message.DescribeText` does.
	*/
	internal SomlException Error(string message, bool isAboutKey = false, Exception? innerException = null)
	{
		return new SomlException(Message.DescribeText(Message.Abbreviate(message, MaximumDiagnosticLength)), CollectionsMarshal.AsSpan(_path), isAboutKey, innerException);
	}

	internal static string ExceptionMessage(Exception exception) => Message.Abbreviate(exception.Message, MaximumDiagnosticLength);

	/// <summary>
	/// The error for a value that is not what <paramref name="expected"/> says, such as “Expected an int for int, but found the string 'eighty'”.
	/// </summary>
	public SomlException Mismatch(SomlValue value, string expected, Type type, string hint = "") => Error($"Expected {expected} for {DescribeType(type)}, but found {DescribeValue(value)}{hint}");

	/// <summary>
	/// Reads a value with a converter. Null is only allowed when <paramref name="isNullable"/> is true, and it is always allowed for <see cref="SomlValue"/>, which holds null itself.
	/// </summary>
	/// <exception cref="SomlException">When the value does not fit the type, or is nested more than 100 levels deep.</exception>
	public T Read<T>(SomlValue value, SomlConverter<T> converter, bool isNullable)
	{
		ArgumentNullException.ThrowIfNull(converter);

		if (value.IsNull)
		{
			if (typeof(T) == typeof(SomlValue))
			{
				return default!;
			}

			return isNullable ? default! : throw Mismatch(value, converter.Expected, typeof(T));
		}

		CheckDepth("deserialize", value.Kind is SomlValueKind.Object or SomlValueKind.Array);
		return converter.Read(value, this);
	}

	/// <summary>
	/// Writes a value with a converter, or null for a null value.
	/// </summary>
	/// <exception cref="SomlException">When the value cannot be written.</exception>
	public SomlValue Write<T>(T value, SomlConverter<T> converter)
	{
		ArgumentNullException.ThrowIfNull(converter);

		if (CanBeNull<T>() && value is null)
		{
			return SomlValue.Null;
		}

		CheckDepth("serialize");
		return converter.Write(value, this);
	}

	/// <summary>
	/// The members of an object, or the error that the value is not one.
	/// </summary>
	/// <exception cref="SomlException">When the value is not an object.</exception>
	public SomlObject ReadObject(SomlValue value, Type type) => value.TryGetObject(out var members) ? members : throw Mismatch(value, "an object", type);

	/// <summary>
	/// Checks that every key of an object is one that the type reads, when the options reject unknown keys. Suggestions for likely misspellings are best-effort for keys up to 100 characters.
	/// </summary>
	/// <exception cref="SomlException">When the options reject unknown keys and the object has one.</exception>
	public void CheckUnknownKeys(SomlObject members, IReadOnlyCollection<string> knownKeys, Type type)
	{
		ArgumentNullException.ThrowIfNull(members);
		ArgumentNullException.ThrowIfNull(knownKeys);

		if (!Options.RejectUnknownKeys)
		{
			return;
		}

		foreach (var (key, _) in members.AsSpan())
		{
			if (knownKeys.Contains(key))
			{
				continue;
			}

			Push(key);
			var suggestion = Suggest(key, knownKeys);
			throw Error($"Unknown key {Message.DescribeKey(key)}, which {DescribeType(type)} does not read{(suggestion is null ? "" : $". Did you mean {Message.DescribeKey(suggestion)}?")}", isAboutKey: true);
		}
	}

	/// <summary>
	/// The error for a key that a type requires and the document leaves out.
	/// </summary>
	public SomlException MissingKey(string key, Type type) => Error($"Missing the key {Message.DescribeKey(key)}, which {DescribeType(type)} requires");

	/// <summary>
	/// The error for a constructor that threw while reading a type.
	/// </summary>
	public SomlException ConstructorFailed(Type type, Exception exception)
	{
		ArgumentNullException.ThrowIfNull(exception);
		return Error($"The constructor of {DescribeType(type)} threw: {ExceptionMessage(exception)}", innerException: exception);
	}

	/// <summary>
	/// The error for a setter that threw while reading a member.
	/// </summary>
	public SomlException SetterFailed(string memberName, Type type, Exception exception)
	{
		ArgumentNullException.ThrowIfNull(exception);
		return Error($"Setting {memberName} of {DescribeType(type)} threw: {ExceptionMessage(exception)}", innerException: exception);
	}

	/// <summary>
	/// The error for a getter that threw while writing a member.
	/// </summary>
	public SomlException GetterFailed(string memberName, Type type, Exception exception)
	{
		ArgumentNullException.ThrowIfNull(exception);
		return Error($"Getting {memberName} of {DescribeType(type)} threw: {ExceptionMessage(exception)}", innerException: exception);
	}

	/*
	The error for a collection that the collection's own code could not create from its items.
	*/
	internal SomlException CreateFailed(Type type, Exception exception) => Error($"Cannot create {DescribeType(type)}: {ExceptionMessage(exception)}", innerException: exception);

	/*
	The error for a value whose own code cannot write it, such as a collection that cannot be enumerated or a type whose formatter throws.
	*/
	internal SomlException WriteFailed(Type type, Exception exception) => Error($"Cannot write {DescribeType(type)}: {ExceptionMessage(exception)}", innerException: exception);

	/*
	The key that a misspelled key was most likely meant as, within an edit distance of a third of its length. Suggestions are best-effort for keys up to 100 characters, to bound the work of comparing them.
	*/
	private static string? Suggest(string key, IEnumerable<string> keys)
	{
		if (key.Length > 100)
		{
			return null;
		}

		var maximum = Math.Max(1, key.Length / 3);
		string? best = null;
		var bestDistance = maximum + 1;

		foreach (var candidate in keys)
		{
			// The difference in length is a lower bound on edit distance.
			if (Math.Abs(key.Length - candidate.Length) >= bestDistance)
			{
				continue;
			}

			var distance = EditDistance(key, candidate, bestDistance);

			if (distance < bestDistance)
			{
				best = candidate;
				bestDistance = distance;

				// The key is unknown, so a distance of one is already the closest possible match.
				if (distance <= 1)
				{
					return best;
				}
			}
		}

		return best;
	}

	private static int EditDistance(string left, string right, int limit)
	{
		var leftText = left.AsSpan();
		var rightText = right.AsSpan();
		var prefixLength = leftText.CommonPrefixLength(rightText);
		leftText = leftText[prefixLength..];
		rightText = rightText[prefixLength..];

		while (!leftText.IsEmpty && !rightText.IsEmpty && leftText[^1] == rightText[^1])
		{
			leftText = leftText[..^1];
			rightText = rightText[..^1];
		}

		if (leftText.IsEmpty || rightText.IsEmpty)
		{
			return leftText.Length + rightText.Length;
		}

		// Suggest bounds keys to 100 characters and candidate lengths to that plus the maximum distance.
		Span<int> previous = stackalloc int[rightText.Length + 1];
		Span<int> current = stackalloc int[rightText.Length + 1];

		for (var column = 0; column <= rightText.Length; column++)
		{
			previous[column] = column;
		}

		for (var row = 1; row <= leftText.Length; row++)
		{
			current[0] = row;
			var minimum = row;

			for (var column = 1; column <= rightText.Length; column++)
			{
				var cost = leftText[row - 1] == rightText[column - 1] ? 0 : 1;
				current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1), previous[column - 1] + cost);
				minimum = Math.Min(minimum, current[column]);
			}

			if (minimum >= limit)
			{
				return limit;
			}

			var temporary = previous;
			previous = current;
			current = temporary;
		}

		return previous[rightText.Length];
	}

	/*
	A container that holds itself would be written, or a `SomlValue` that holds itself read, forever. The limit is the writer's and the parser's. When reading, a container counts itself as well as its ancestors, even when it is empty.
	*/
	internal void CheckDepth(string action, bool isContainer = false)
	{
		if ((_path?.Count ?? 0) + (isContainer ? 1 : 0) > Parser.MaxDepth)
		{
			throw Error($"Cannot {action} a value nested more than {Parser.MaxDepth} levels deep. Check for a reference cycle");
		}
	}

	/*
	A type as an error message names it, as C# writes it: `int`, `string`, `List<string>`, or `int?`. It only reads names and generic arguments, so it is safe with trimming.
	*/
	internal static string DescribeType(Type type)
	{
		if (type == typeof(nint))
		{
			return "nint";
		}

		if (type == typeof(nuint))
		{
			return "nuint";
		}

		if (Nullable.GetUnderlyingType(type) is { } underlyingType)
		{
			return $"{DescribeType(underlyingType)}?";
		}

		var keyword = Type.GetTypeCode(type) switch
		{
			TypeCode.Boolean => "bool",
			TypeCode.Char => "char",
			TypeCode.SByte => "sbyte",
			TypeCode.Byte => "byte",
			TypeCode.Int16 => "short",
			TypeCode.UInt16 => "ushort",
			TypeCode.Int32 => "int",
			TypeCode.UInt32 => "uint",
			TypeCode.Int64 => "long",
			TypeCode.UInt64 => "ulong",
			TypeCode.Single => "float",
			TypeCode.Double => "double",
			TypeCode.Decimal => "decimal",
			TypeCode.String => "string",
			_ => null,
		};

		if (keyword is not null && !type.IsEnum)
		{
			return keyword;
		}

		if (type == typeof(object))
		{
			return "object";
		}

		if (type.IsArray)
		{
			return $"{DescribeType(type.GetElementType()!)}[{new string(',', type.GetArrayRank() - 1)}]";
		}

		if (!type.IsGenericType)
		{
			return type.Name;
		}

		var name = type.Name;
		var backtick = name.IndexOf('`');
		return $"{(backtick == -1 ? name : name[..backtick])}<{string.Join(", ", type.GetGenericArguments().Select(DescribeType))}>";
	}

	/*
	A value as an error message describes it, such as “the string 'eighty'” or “an object”.
	*/
	internal static string DescribeValue(SomlValue value) => value.Kind switch
	{
		SomlValueKind.Null => "null",
		SomlValueKind.String => $"the string {Message.DescribeString(value.GetString())}",
		SomlValueKind.Integer => $"the int {value.GetInt64().ToString(CultureInfo.InvariantCulture)}",
		SomlValueKind.Float => $"the float {SomlWriter.FormatFloat(value.GetDouble())}",
		SomlValueKind.Boolean => $"the bool {(value.GetBoolean() ? "true" : "false")}",
		SomlValueKind.Date => $"the date {value}",
		SomlValueKind.Instant => $"the instant {value.GetInstant()}",
		SomlValueKind.Duration => $"the duration {value.GetDuration()}",
		SomlValueKind.Array => "an array",
		_ => "an object",
	};
}
