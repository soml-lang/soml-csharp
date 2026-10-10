using System.Text;

namespace Soml.Tests;

/*
Seeded random tests: round trips of random values, and random input that must either read or fail with a `SomlParseException`.
*/
public sealed class PropertyTests
{
	private const string Characters = "abcXYZ019 _-.:,'\"#/*\\[]{}\t\né😀";

	[Fact]
	public void FloatsRoundTripThroughCanonicalForm()
	{
		var random = new Random(1);

		for (var index = 0; index < 200_000; index++)
		{
			var value = BitConverter.Int64BitsToDouble(random.NextInt64(long.MinValue, long.MaxValue));

			if (!double.IsFinite(value))
			{
				continue;
			}

			var text = ((SomlValue)value).ToString();
			var readBack = SomlValue.Parse($"[{text}]")[0].GetDouble();

			Assert.True(readBack == value + 0.0, $"{value:R} was written as {text} and read back as {readBack:R}");
		}
	}

	[Fact]
	public void FloatsAreWrittenWithTheShortestDigits()
	{
		// 2^-958 and 2^-25, for which .NET's `R` format gives digits that do not read back.
		Assert.Equal("4.1045368012983762e-289", ((SomlValue)BitConverter.Int64BitsToDouble(0x0410000000000000)).ToString());
		Assert.Equal("2.9802322387695312e-8", ((SomlValue)BitConverter.Int64BitsToDouble(0x3e60000000000000)).ToString());
		Assert.Equal("5e-324", ((SomlValue)double.Epsilon).ToString());
		Assert.Equal("1.7976931348623157e308", ((SomlValue)double.MaxValue).ToString());
		Assert.Equal("100000000000000000000.0", ((SomlValue)1e20).ToString());
		Assert.Equal("1e21", ((SomlValue)1e21).ToString());
		Assert.Equal("0.000001", ((SomlValue)1e-6).ToString());
		Assert.Equal("1e-7", ((SomlValue)1e-7).ToString());
		Assert.Equal("1658206780088562.2", ((SomlValue)1658206780088562.25).ToString());
	}

	[Fact]
	public void EveryRepresentableHalfRoundTrips()
	{
		for (var bits = 0; bits <= ushort.MaxValue; bits++)
		{
			var value = BitConverter.UInt16BitsToHalf((ushort)bits);

			if (Half.IsNaN(value))
			{
				continue;
			}

			var text = SomlSerializer.Serialize(new[] { value });
			var readBack = SomlSerializer.Deserialize<Half[]>(text)[0];

			Assert.Equal(value, readBack);
			Assert.False(value == (Half)0 && Half.IsNegative(readBack));
		}
	}

	[Fact]
	public void IntsAndTimesRoundTrip()
	{
		var random = new Random(2);

		for (var index = 0; index < 20_000; index++)
		{
			var integer = random.NextInt64(long.MinValue, long.MaxValue);
			Assert.Equal(integer, SomlValue.Parse($"[{integer}]")[0].GetInt64());

			var duration = new SomlDuration(random.NextInt64(long.MinValue, long.MaxValue));
			Assert.Equal(duration, SomlDuration.Parse(duration.ToString()));

			var instant = new SomlInstant(random.NextInt64(SomlInstant.MinValue.UnixSeconds, SomlInstant.MaxValue.UnixSeconds + 1), random.Next(1_000_000_000));
			Assert.Equal(instant, SomlInstant.Parse(instant.ToString()));
		}

		Assert.Equal(long.MinValue, SomlValue.Parse("[-9223372036854775808]")[0].GetInt64());
		Assert.Equal(SomlDuration.MinValue, SomlDuration.Parse(SomlDuration.MinValue.ToString()));
	}

	[Fact]
	public void RandomValuesRoundTripThroughEveryWriter()
	{
		var random = new Random(3);

		for (var index = 0; index < 3000; index++)
		{
			SomlValue value = random.Next(2) == 0 ? RandomObject(random, 1) : RandomArray(random, 1);
			var canonical = SomlSerializer.Serialize(value, new SomlSerializerOptions { Canonical = true });
			var written = SomlSerializer.Serialize(value);

			Assert.Equal(value, SomlValue.Parse(canonical));
			Assert.True(Conformance.HaveSameOrder(value, SomlValue.Parse(written)), written);

			// Canonical form is formatted, and formatting keeps the value.
			Assert.Equal(canonical, SomlFormatter.Format(canonical));
			Assert.Equal(written, SomlFormatter.Format(written));
		}
	}

	[Fact]
	public void RandomTextReadsOrFailsWithAPosition()
	{
		var random = new Random(4);
		var documents = Conformance.Cases("valid", ".soml").Select(row => Conformance.ReadText($"valid/{row.Data}.soml")).ToArray();

		for (var index = 0; index < 20_000; index++)
		{
			var text = random.Next(4) == 0 ? RandomText(random, random.Next(1, 40)) : Mutate(random, documents[random.Next(documents.Length)]);
			CheckReadsOrFails(text);
		}
	}

	[Fact]
	public void RandomBytesReadOrFailWithAPosition()
	{
		var random = new Random(5);

		for (var index = 0; index < 20_000; index++)
		{
			var bytes = new byte[random.Next(0, 24)];
			random.NextBytes(bytes);

			try
			{
				SomlValue.Parse(bytes);
			}
			catch (SomlParseException error)
			{
				Assert.InRange(error.Line, 1, bytes.Length + 1);
				Assert.InRange(error.Column, 1, bytes.Length + 1);
			}
		}
	}

	[Fact]
	public void RandomEditsGiveValidDocuments()
	{
		var random = new Random(6);
		var documents = Conformance.Cases("valid", ".soml").Select(row => Conformance.ReadText($"valid/{row.Data}.formatted.soml")).ToArray();

		for (var index = 0; index < 5000; index++)
		{
			var document = documents[random.Next(documents.Length)];
			var value = SomlValue.Parse(document);
			var path = RandomPath(random, value);

			if (path.Length == 0)
			{
				continue;
			}

			try
			{
				var edited = random.Next(3) == 0 ? SomlEditor.Remove(document, path) : SomlEditor.Set(document, path, RandomScalar(random));
				SomlValue.Parse(edited);
			}
			catch (SomlException exception) when (exception is not SomlParseException)
			{
				// A path that does not fit the document is an error.
			}
		}
	}

	[Fact]
	public void EditsChangeOnlyTheValueAtTheirPath()
	{
		var random = new Random(7);
		var documents = Conformance.Cases("valid", ".soml").Select(row => Conformance.ReadText($"valid/{row.Data}.soml")).ToArray();

		for (var index = 0; index < 10_000; index++)
		{
			var document = documents[random.Next(documents.Length)];
			var value = SomlValue.Parse(document);
			var path = RandomPath(random, value);

			if (path.Length == 0)
			{
				continue;
			}

			var isRemoval = random.Next(3) == 0;
			var newValue = random.Next(4) == 0 ? (SomlValue)RandomObject(random, 98) : RandomScalar(random);
			string Edit() => isRemoval ? SomlEditor.Remove(document, path) : SomlEditor.Set(document, path, newValue);
			var expected = Copy(value);
			bool isChanged;

			try
			{
				// Validate the operation independently, including an insertion past an array's end or beyond the nesting limit.
				isChanged = Apply(expected, path, isRemoval ? null : (SomlValue?)newValue);
				expected.ToDocumentString();
			}
			catch (Exception exception) when (exception is ArgumentOutOfRangeException or SomlException)
			{
				Assert.Throws<SomlException>(Edit);
				continue;
			}

			// A valid operation must succeed, rather than having an unexpected editor error skipped.
			var edited = Edit();

			if (!isChanged)
			{
				Assert.Equal(document, edited);
				continue;
			}

			Assert.True(expected.Equals(SomlValue.Parse(edited)), $"{document}\n--- {string.Join(", ", path)} {(isRemoval ? "removed" : newValue.ToString())}\n{edited}");
		}
	}

	private static SomlValue Copy(SomlValue value)
	{
		if (value.TryGetObject(out var members))
		{
			return new SomlObject(members.Select(member => KeyValuePair.Create(member.Key, Copy(member.Value))));
		}

		return value.TryGetArray(out var items) ? new SomlArray(items.Select(Copy)) : value;
	}

	/*
	Sets or removes the value at a path, creating missing objects, as the editor does. Returns whether anything changed.
	*/
	private static bool Apply(SomlValue root, SomlPathSegment[] path, SomlValue? value)
	{
		var current = root;

		for (var index = 0; index < path.Length - 1; index++)
		{
			var segment = path[index];

			if (current.TryGetObject(out var members))
			{
				if (!members.TryGetValue(segment.Key!, out var next))
				{
					if (value is null)
					{
						return false;
					}

					next = new SomlObject();
					members[segment.Key!] = next;
				}

				current = next;
			}
			else
			{
				var items = current.GetArray();

				if (segment.Index >= items.Count)
				{
					return false;
				}

				current = items[segment.Index];
			}
		}

		var last = path[^1];

		if (current.TryGetObject(out var container))
		{
			if (value is null)
			{
				return container.Remove(last.Key!);
			}

			container[last.Key!] = value.Value;
			return true;
		}

		var array = current.GetArray();

		if (value is null)
		{
			if (last.Index >= array.Count)
			{
				return false;
			}

			array.RemoveAt(last.Index);
			return true;
		}

		if (last.Index == array.Count)
		{
			array.Add(value.Value);
		}
		else
		{
			array[last.Index] = value.Value;
		}

		return true;
	}

	private static void CheckReadsOrFails(string text)
	{
		SomlValue value;

		try
		{
			value = SomlValue.Parse(text);
		}
		catch (SomlParseException error)
		{
			var lineCount = text.Count(character => character == '\n') + 1;
			Assert.InRange(error.Line, 1, lineCount);
			Assert.InRange(error.Offset, 0, text.Length);
			return;
		}

		// A valid input must format successfully, and its output must remain valid.
		var formatted = SomlFormatter.Format(text);
		Assert.Equal(formatted, SomlFormatter.Format(formatted));
		Assert.True(Conformance.HaveSameOrder(value, SomlValue.Parse(formatted)));
	}

	private static string Mutate(Random random, string text)
	{
		var builder = new StringBuilder(text);

		for (var operation = random.Next(1, 4); operation > 0; operation--)
		{
			var position = random.Next(builder.Length + 1);

			if (random.Next(2) == 0 && position < builder.Length)
			{
				builder.Remove(position, Math.Min(random.Next(1, 4), builder.Length - position));
			}
			else
			{
				builder.Insert(position, RandomText(random, random.Next(1, 3)));
			}
		}

		return builder.ToString();
	}

	private static string RandomText(Random random, int length)
	{
		var builder = new StringBuilder();

		while (builder.Length < length)
		{
			var index = random.Next(Characters.Length);

			// Keep the surrogate pair of the emoji together.
			if (char.IsSurrogate(Characters[index]))
			{
				builder.Append("😀");
			}
			else
			{
				builder.Append(Characters[index]);
			}
		}

		return builder.ToString();
	}

	private static SomlPathSegment[] RandomPath(Random random, SomlValue value)
	{
		var path = new List<SomlPathSegment>();
		var current = value;

		while (true)
		{
			if (current.TryGetArray(out var array))
			{
				var index = array.Count > 0 && random.Next(5) > 0 ? random.Next(array.Count) : array.Count + random.Next(2);
				path.Add(index);
				current = index < array.Count ? array[index] : SomlValue.Null;
			}
			else if (current.TryGetObject(out var members))
			{
				var key = members.Count > 0 && random.Next(5) > 0 ? members.GetAt(random.Next(members.Count)).Key : "new";
				path.Add(key);
				current = members.TryGetValue(key, out var member) ? member : SomlValue.Null;
			}
			else
			{
				return [.. path];
			}

			if (random.Next(3) == 0)
			{
				return [.. path];
			}
		}
	}

	private static SomlValue RandomScalar(Random random) => random.Next(10) switch
	{
		0 => SomlValue.Null,
		1 => random.Next(2) == 0,
		2 => random.NextInt64(long.MinValue, long.MaxValue),
		3 => BitConverter.Int64BitsToDouble(random.NextInt64(long.MinValue, long.MaxValue)) is var number && double.IsFinite(number) ? number : double.NegativeInfinity,
		4 => new SomlDuration(random.NextInt64(long.MinValue, long.MaxValue)),
		5 => new SomlInstant(random.NextInt64(SomlInstant.MinValue.UnixSeconds, SomlInstant.MaxValue.UnixSeconds + 1), random.Next(1_000_000_000)),
		6 => RandomString(random),
		7 => "",
		8 => DateOnly.FromDayNumber(random.Next(DateOnly.MaxValue.DayNumber + 1)),
		_ => random.Next(-10, 10),
	};

	private static string RandomString(Random random)
	{
		var builder = new StringBuilder();

		for (var index = random.Next(0, 8); index > 0; index--)
		{
			// Any scalar value except CR and the surrogates, which are not representable.
			var codePoint = random.Next(3) switch
			{
				0 => random.Next(0, 0x80),
				1 => random.Next(0x80, 0xD800),
				_ => random.Next(0xE000, 0x110000),
			};

			if (codePoint != '\r')
			{
				builder.Append(char.ConvertFromUtf32(codePoint));
			}
		}

		return builder.ToString();
	}

	private static SomlObject RandomObject(Random random, int depth)
	{
		var result = new SomlObject();

		for (var index = random.Next(0, 5); index > 0; index--)
		{
			result[RandomString(random)] = RandomValue(random, depth + 1);
		}

		return result;
	}

	private static SomlArray RandomArray(Random random, int depth)
	{
		var result = new SomlArray();

		for (var index = random.Next(0, 5); index > 0; index--)
		{
			result.Add(RandomValue(random, depth + 1));
		}

		return result;
	}

	private static SomlValue RandomValue(Random random, int depth) => depth < 5 && random.Next(4) == 0 ? (random.Next(2) == 0 ? RandomObject(random, depth) : RandomArray(random, depth)) : RandomScalar(random);
}

/*
Reading, formatting, and editing take linear time: an input 8 times as large takes less than 24 times as much CPU time, where quadratic time would take about 64 times as much. Each shape is timed several times, and the fastest run counts, so scheduling delays on a busy machine do not affect the result.
*/
[Collection(nameof(ComplexityTests))]
public sealed class ComplexityTests
{
	public static TheoryData<string> Shapes => [.. ShapeGenerators.Keys];

	private static readonly Dictionary<string, Func<int, string>> ShapeGenerators = new()
	{
		["many members"] = size => string.Concat(Enumerable.Range(0, size).Select(index => $"key{index}: {index}\n")),
		["long literal string"] = size => $"a: '{new string('x', size * 10)}'",
		["long escaped string"] = size => $"a: \"{string.Concat(Enumerable.Repeat("x\\n", size * 3))}\"",
		["long block string"] = size => $"a:\n\t'''\n{string.Concat(Enumerable.Repeat("\tline\n\n", size))}\t'''",
		["long comment"] = size => $"# {new string('x', size * 10)}\na: 1",
		["many block comments on one line"] = size => $"a: [1 {string.Concat(Enumerable.Repeat("/* x */ ", size))}]",
		["many items on one line"] = size => $"a: [{string.Join(", ", Enumerable.Range(0, size))}]",
		["long number"] = size => $"a: 1.{new string('0', size * 10)}1",
		["long invalid number"] = size => $"a: 1{new string('0', size * 10)}x",
		["long key without colon"] = size => new string('k', size * 10),
		["long unterminated string"] = size => $"a: '{new string('x', size * 10)}",
		["many small objects"] = size => $"a: [\n{string.Concat(Enumerable.Repeat("\t{b: 1, c: [2]}\n", size))}]",
		["key with many dots"] = size => $"{string.Join('.', Enumerable.Repeat("a", size))}: 1",
		["long duration fraction"] = size => $"a: 1.{new string('0', size * 10)}s",
	};

	[Theory]
	[MemberData(nameof(Shapes))]
	public void TakesLinearTime(string shape)
	{
		var generate = ShapeGenerators[shape];
		// Both sizes put copied strings and output buffers on the large-object heap, so collection cost does not distort their ratios.
		var small = generate(16_000);
		var large = generate(128_000);

		void Run(string text)
		{
			try
			{
				SomlValue.Parse(text);
				SomlFormatter.Format(text);
				SomlEditor.Set(text, ["zz"], 1);
				SomlEditor.Remove(text, ["a"]);
			}
			catch (SomlParseException)
			{
				// An invalid shape is timed for its error.
			}
		}

		// A collection first, so that garbage from earlier tests is not collected while this one is timed.
		GC.Collect();
		GC.WaitForPendingFinalizers();
		Run(small);
		Run(large);
		var ratio = Fastest(() => Run(large)) / Fastest(() => Run(small));
		Assert.True(ratio < 24, $"{shape}: 8 times the input took {ratio:F1} times as long");
	}

	private static double Fastest(Action action)
	{
		var fastest = double.MaxValue;
		var repetitions = 1;

		for (var run = 0; run < 5; run++)
		{
			while (true)
			{
				var start = Environment.CpuUsage.TotalTime;

				for (var repetition = 0; repetition < repetitions; repetition++)
				{
					action();
				}

				var elapsed = (Environment.CpuUsage.TotalTime - start).TotalMilliseconds;

				// CPU clocks can have coarse precision, so measure a batch of at least 50 milliseconds and normalize by its size.
				if (elapsed < 50)
				{
					repetitions *= 2;
					continue;
				}

				fastest = Math.Min(fastest, elapsed / repetitions);
				break;
			}
		}

		return fastest;
	}
}

/*
The timing tests run alone, so that other tests running at the same time do not disturb the ratios.
*/
[CollectionDefinition(nameof(ComplexityTests), DisableParallelization = true)]
public sealed class ComplexityTestsCollection;
