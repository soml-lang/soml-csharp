using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Soml.Extensions.Configuration;

/// <summary>
/// A SOML file as a source of configuration.
/// </summary>
public sealed class SomlConfigurationSource : FileConfigurationSource
{
	/// <summary>
	/// Builds the provider for this source.
	/// </summary>
	public override IConfigurationProvider Build(IConfigurationBuilder builder)
	{
		EnsureDefaults(builder);
		return new SomlConfigurationProvider(this);
	}
}

/// <summary>
/// Loads configuration from a SOML file.
/// </summary>
/// <param name="source">The source of the file.</param>
public sealed class SomlConfigurationProvider(SomlConfigurationSource source) : FileConfigurationProvider(source)
{
	/// <summary>
	/// Loads the configuration from a stream of the file's UTF-8 bytes.
	/// </summary>
	/// <exception cref="FormatException">When the file is not a valid SOML document, or flattened keys collide case-insensitively.</exception>
	public override void Load(Stream stream) => Data = SomlConfigurationParser.Parse(stream);
}

/// <summary>
/// A stream of a SOML document as a source of configuration.
/// </summary>
public sealed class SomlStreamConfigurationSource : StreamConfigurationSource
{
	/// <summary>
	/// Builds the provider for this source.
	/// </summary>
	public override IConfigurationProvider Build(IConfigurationBuilder builder) => new SomlStreamConfigurationProvider(this);
}

/// <summary>
/// Loads configuration from a stream of a SOML document.
/// </summary>
/// <param name="source">The source of the stream.</param>
public sealed class SomlStreamConfigurationProvider(SomlStreamConfigurationSource source) : StreamConfigurationProvider(source)
{
	/// <summary>
	/// Loads the configuration from a stream of UTF-8 bytes.
	/// </summary>
	/// <exception cref="FormatException">When the stream is not a valid SOML document, or flattened keys collide case-insensitively.</exception>
	public override void Load(Stream stream) => Data = SomlConfigurationParser.Parse(stream);
}

/*
Turns a document into the flat keys and string values of configuration, as the JSON provider does for JSON: nested keys are joined with `:`, array items are keyed by their index, an empty object is a key with a null value, and an empty array is a key with an empty string.
*/
internal static class SomlConfigurationParser
{
	public static Dictionary<string, string?> Parse(Stream stream)
	{
		SomlValue document;

		try
		{
			document = SomlValue.Parse(stream);
		}
		catch (SomlParseException exception)
		{
			throw new FormatException($"Could not parse the SOML document: {exception.Reason} at line {exception.Line}, column {exception.Column}", exception);
		}

		// Configuration keys are case-insensitive, as in every provider.
		var data = new Dictionary<string, string?>(document.Kind == SomlValueKind.Object ? document.GetObject().Count : document.GetArray().Count, StringComparer.OrdinalIgnoreCase);
		// Note: Not using ConfigurationPath.Combine because it adds a params array per nested join.
		// Reusing one builder also avoids strings for intermediate container paths.
		Visit(document, new StringBuilder(), 0, [], data);
		return data;
	}

	private static void Visit(SomlValue value, StringBuilder path, int depth, ReadOnlySpan<char> segment, Dictionary<string, string?> data)
	{
		var prefixLength = path.Length;

		if (depth > 1)
		{
			path.Append(ConfigurationPath.KeyDelimiter);
		}

		path.Append(segment);

		switch (value.Kind)
		{
			case SomlValueKind.Object:
			{
				var members = value.GetObject();

				for (var index = 0; index < members.Count; index++)
				{
					var (key, member) = members.GetAt(index);
					Visit(member, path, depth + 1, key, data);
				}

				if (members.Count == 0 && depth > 0)
				{
					Add(data, path.ToString(), null);
				}

				break;
			}
			case SomlValueKind.Array:
			{
				var items = value.GetArray();
				Span<char> indexText = stackalloc char[10];

				for (var index = 0; index < items.Count; index++)
				{
					index.TryFormat(indexText, out var length, provider: CultureInfo.InvariantCulture);
					Visit(items[index], path, depth + 1, indexText[..length], data);
				}

				if (items.Count == 0 && depth > 0)
				{
					Add(data, path.ToString(), "");
				}

				break;
			}
			default:
			{
				Add(data, path.ToString(), ToConfigurationString(value));
				break;
			}
		}

		path.Length = prefixLength;
	}

	private static void Add(Dictionary<string, string?> data, string key, string? value)
	{
		// Two keys that differ only in case are one key in configuration, so a document that has both is ambiguous.
		if (!data.TryAdd(key, value))
		{
			// Bound the source before encoding it, since a configuration key may come from a large, untrusted document.
			const int maximumLength = 200;
			var length = Math.Min(key.Length, maximumLength);

			if (length < key.Length && char.IsHighSurrogate(key[length - 1]))
			{
				length--;
			}

			throw new FormatException($"A duplicate key '{JsonEncodedText.Encode(key.AsSpan(0, length))}{(length < key.Length ? "…" : "")}' was found. Configuration keys are case-insensitive");
		}
	}

	/*
	A value as the configuration binder reads it. A duration is written as `TimeSpan` writes itself, as in `01:30:00`, because the binder cannot read `1h30m`, unless it is finer than a `TimeSpan` holds.
	*/
	private static string? ToConfigurationString(SomlValue value) => value.Kind switch
	{
		SomlValueKind.Null => null,
		SomlValueKind.String => value.GetString(),
		// As `bool.ToString()` writes it, which the JSON provider gives too.
		// Note: Not using bool.ToString because provider comparisons on .NET 10 ARM64 had the same allocation cost but did not establish non-regression.
		SomlValueKind.Boolean => value.GetBoolean() ? "True" : "False",
		SomlValueKind.Duration => DurationText(value.GetDuration()),
		_ => value.ToString(),
	};

	// Note: Not using parameterless TimeSpan.ToString because it reaches the same invariant formatter and showed no consistent provider gain in the comparisons.
	private static string DurationText(SomlDuration duration) => duration.Nanoseconds % 100 == 0 ? duration.ToTimeSpan().ToString("c", CultureInfo.InvariantCulture) : duration.ToString();
}
