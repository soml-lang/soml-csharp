using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Soml.Tests;

/*
The language-neutral conformance suite, a synced copy of `conformance` in the `soml` repository. Never edit the copy by hand; run `../soml/sync-conformance.sh tests/Soml.Tests/conformance` instead.
*/
internal static class Conformance
{
	public static string Directory { get; } = FindDirectory();

	private static string FindDirectory()
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			var candidate = Path.Combine(directory.FullName, "tests", "Soml.Tests", "conformance");

			if (System.IO.Directory.Exists(candidate))
			{
				return candidate;
			}
		}

		throw new InvalidOperationException("Could not find the conformance suite");
	}

	/*
	The names of the cases in a folder, such as `int/binary-max`, without the extensions.
	*/
	public static TheoryData<string> Cases(string folder, string extension)
	{
		var root = Path.Combine(Directory, folder);
		var data = new TheoryData<string>();

		foreach (var path in System.IO.Directory.EnumerateFiles(root, $"*{extension}", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
		{
			var name = Path.GetRelativePath(root, path).Replace('\\', '/');

			// Only the input of a valid case, not its canonical or formatted form.
			if (name.EndsWith(".canonical.soml", StringComparison.Ordinal) || name.EndsWith(".formatted.soml", StringComparison.Ordinal))
			{
				continue;
			}

			data.Add(name[..^extension.Length]);
		}

		return data;
	}

	public static byte[] ReadBytes(string relativePath) => File.ReadAllBytes(Path.Combine(Directory, relativePath));

	/*
	The suite's text files are UTF-8. Reading them as bytes keeps a BOM or a CR that a case may hold.
	*/
	public static string ReadText(string relativePath) => Encoding.UTF8.GetString(ReadBytes(relativePath));

	/*
	A value in the tagged JSON of the suite, as in toml-test: a scalar is `{"type": "int", "value": "3"}`, and objects and arrays are plain JSON.
	*/
	public static SomlValue FromTagged(JsonNode? node)
	{
		switch (node)
		{
			case JsonArray array:
			{
				var result = new SomlArray();

				foreach (var item in array)
				{
					result.Add(FromTagged(item));
				}

				return result;
			}
			case JsonObject jsonObject when IsTaggedScalar(jsonObject):
			{
				var type = jsonObject["type"]!.GetValue<string>();
				var value = jsonObject["value"]!.GetValue<string>();

				return type switch
				{
					"string" => value,
					"int" => long.Parse(value, CultureInfo.InvariantCulture),
					"float" => value switch
					{
						"infinity" => double.PositiveInfinity,
						"-infinity" => double.NegativeInfinity,
						_ => double.Parse(value, CultureInfo.InvariantCulture),
					},
					"bool" => value == "true",
					"null" => SomlValue.Null,
					"date" => DateOnly.ParseExact(value, "O", CultureInfo.InvariantCulture),
					"instant" => SomlInstant.Parse(value),
					"duration" => new SomlDuration(long.Parse(value, CultureInfo.InvariantCulture)),
					_ => throw new InvalidOperationException($"Unknown type {type}"),
				};
			}
			case JsonObject jsonObject:
			{
				var result = new SomlObject();

				foreach (var (key, value) in jsonObject)
				{
					result.Add(key, FromTagged(value));
				}

				return result;
			}
			default:
			{
				throw new InvalidOperationException($"Unexpected tagged JSON {node?.ToJsonString()}");
			}
		}
	}

	private static bool IsTaggedScalar(JsonObject jsonObject) => jsonObject.Count == 2 && jsonObject["type"] is JsonValue type && type.GetValueKind() == JsonValueKind.String && jsonObject["value"] is JsonValue value && value.GetValueKind() == JsonValueKind.String;

	/*
	Whether two values are equal and every object has its members in the same order.
	*/
	public static bool HaveSameOrder(SomlValue left, SomlValue right)
	{
		if (left.TryGetObject(out var leftObject) && right.TryGetObject(out var rightObject))
		{
			return leftObject.Keys.SequenceEqual(rightObject.Keys) && leftObject.Keys.All(key => HaveSameOrder(leftObject[key], rightObject[key]));
		}

		if (left.TryGetArray(out var leftArray) && right.TryGetArray(out var rightArray))
		{
			return leftArray.Count == rightArray.Count && leftArray.Zip(rightArray).All(pair => HaveSameOrder(pair.First, pair.Second));
		}

		return left.Equals(right);
	}

	public static JsonObject InvalidReasons { get; } = JsonNode.Parse(ReadText("invalid-reasons.json"))!.AsObject();
}
