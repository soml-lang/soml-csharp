using System.Globalization;
using System.Text;

namespace Soml.Tests;

public sealed class ValueModelTests
{
	private static string Astral(int codePoint) => char.ConvertFromUtf32(codePoint);

	private static void InCulture(string name, Action action)
	{
		var culture = CultureInfo.CurrentCulture;
		var uiCulture = CultureInfo.CurrentUICulture;

		try
		{
			CultureInfo.CurrentCulture = new CultureInfo(name);
			CultureInfo.CurrentUICulture = new CultureInfo(name);
			action();
		}
		finally
		{
			CultureInfo.CurrentCulture = culture;
			CultureInfo.CurrentUICulture = uiCulture;
		}
	}

	// Equality and hash codes

	[Fact]
	public void EqualValuesHaveEqualHashCodes()
	{
		var small = new SomlObject { ["a"] = 1, ["b"] = new SomlObject { ["x"] = 1.5, ["y"] = new SomlArray { 1, "two" } } };
		var reordered = new SomlObject { ["b"] = new SomlObject { ["y"] = new SomlArray { 1, "two" }, ["x"] = 1.5 }, ["a"] = 1 };

		Assert.Equal((SomlValue)small, reordered);
		Assert.Equal(((SomlValue)small).GetHashCode(), ((SomlValue)reordered).GetHashCode());

		// One object large enough for the hash index, and one small enough not to have it.
		var large = new SomlObject();
		var growing = new SomlObject();

		for (var index = 0; index < 12; index++)
		{
			large[$"k{index}"] = index;
		}

		for (var index = 11; index >= 0; index--)
		{
			growing[$"k{index}"] = index;
		}

		Assert.Equal((SomlValue)large, growing);
		Assert.Equal(((SomlValue)large).GetHashCode(), ((SomlValue)growing).GetHashCode());

		// Removing members down to the linear search keeps equality.
		for (var index = 11; index >= 4; index--)
		{
			large.Remove($"k{index}");
		}

		var four = new SomlObject { ["k3"] = 3, ["k2"] = 2, ["k1"] = 1, ["k0"] = 0 };
		Assert.Equal((SomlValue)large, four);
		Assert.Equal(((SomlValue)large).GetHashCode(), ((SomlValue)four).GetHashCode());

		// An int and a float are different values, even when they are numerically equal.
		Assert.NotEqual((SomlValue)1, (SomlValue)1.0);
		Assert.NotEqual((SomlValue)0, (SomlValue)false);
		Assert.NotEqual((SomlValue)0, (SomlValue)SomlDuration.Zero);
		Assert.NotEqual((SomlValue)0, SomlValue.Null);
		Assert.NotEqual((SomlValue)"", SomlValue.Null);

		// The same instant written with another offset, and the same duration spelled another way.
		var instants = SomlValue.Parse("a: 2026-09-19T16:00:00+02:00\nb: 2026-09-19T14:00:00Z\nc: 90m\nd: 1h30m");
		Assert.Equal(instants["a"], instants["b"]);
		Assert.Equal(instants["a"].GetHashCode(), instants["b"].GetHashCode());
		Assert.Equal(instants["c"], instants["d"]);
		Assert.Equal(instants["c"].GetHashCode(), instants["d"].GetHashCode());
		Assert.Equal((SomlValue)SomlInstant.Parse("2026-09-19T14:00:00Z"), instants["a"]);
	}

	[Fact]
	public void HasOneZeroInEqualityHashesAndSets()
	{
		var parsed = SomlValue.Parse("a: -0.0\nb: 0.0\nc: -0e5\nd: -0.0e-10\ne: 0e0");
		SomlValue fromNegative = -0.0;
		SomlValue fromNegativeFloat = -0.0f;
		SomlValue[] zeros = [parsed["a"], parsed["b"], parsed["c"], parsed["d"], parsed["e"], fromNegative, fromNegativeFloat, 0.0];

		foreach (var zero in zeros)
		{
			Assert.Equal((SomlValue)0.0, zero);
			Assert.Equal(((SomlValue)0.0).GetHashCode(), zero.GetHashCode());
			Assert.Equal(0.0, zero.GetDouble());
			Assert.False(double.IsNegative(zero.GetDouble()));
		}

		Assert.Single(new HashSet<SomlValue>(zeros));
		Assert.Equal("a: 0.0\n", new SomlObject { ["a"] = -0.0 }.ToDocumentString());
	}

	[Fact]
	public void CannotHoldNaN()
	{
		Assert.Throws<ArgumentException>(() => (SomlValue)double.NaN);
		Assert.Throws<ArgumentException>(() => (SomlValue)float.NaN);
		Assert.Throws<ArgumentException>(() => (SomlValue)BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8_0000_0000_0001)));
		Assert.Throws<SomlParseException>(() => SomlValue.Parse("a: nan"));

		// Infinities are values, and they work as keys of a set.
		var set = new HashSet<SomlValue> { double.PositiveInfinity, double.NegativeInfinity, float.PositiveInfinity, (SomlValue)double.MaxValue };
		Assert.Equal(3, set.Count);
		Assert.Contains((SomlValue)double.PositiveInfinity, set);

		var dictionary = new Dictionary<SomlValue, int> { [double.NegativeInfinity] = 1, [SomlValue.Parse("a: -infinity")["a"]] = 2 };
		Assert.Single(dictionary);
		Assert.Equal(2, dictionary[float.NegativeInfinity]);
	}

	// Accessors and conversions

	[Fact]
	public void ThrowsInvalidOperationExceptionForTheWrongKind()
	{
		SomlValue[] values = [SomlValue.Null, "text", 1, 1.5, true, DateOnly.MinValue, SomlInstant.UnixEpoch, SomlDuration.Zero, new SomlArray(), new SomlObject()];

		foreach (var value in values)
		{
			foreach (var kind in Enum.GetValues<SomlValueKind>())
			{
				Action read = kind switch
				{
					SomlValueKind.String => () => value.GetString(),
					SomlValueKind.Integer => () => value.GetInt64(),
					SomlValueKind.Float => () => value.GetDouble(),
					SomlValueKind.Boolean => () => value.GetBoolean(),
					SomlValueKind.Date => () => value.GetDate(),
					SomlValueKind.Instant => () => value.GetInstant(),
					SomlValueKind.Duration => () => value.GetDuration(),
					SomlValueKind.Array => () => value.GetArray(),
					SomlValueKind.Object => () => value.GetObject(),
					_ => () => { },
				};

				var isReadable = kind == value.Kind || (kind == SomlValueKind.Float && value.Kind == SomlValueKind.Integer) || kind == SomlValueKind.Null;

				if (isReadable)
				{
					read();
				}
				else
				{
					Assert.Throws<InvalidOperationException>(read);
				}
			}

			if (value.Kind != SomlValueKind.Object)
			{
				Assert.Throws<InvalidOperationException>(() => value["a"]);
				Assert.False(value.TryGetValue("a", out var member));
				Assert.True(member.IsNull);
			}

			if (value.Kind != SomlValueKind.Array)
			{
				Assert.Throws<InvalidOperationException>(() => value[0]);
			}
		}

		SomlValue document = new SomlObject { ["a"] = new SomlArray { 1 } };
		Assert.Throws<KeyNotFoundException>(() => document["b"]);
		Assert.Throws<ArgumentOutOfRangeException>(() => document["a"][1]);
		Assert.Throws<ArgumentOutOfRangeException>(() => document["a"][-1]);
		Assert.True(document.TryGetValue("a", out var array));
		Assert.Equal(1, array[0].GetInt64());
		Assert.False(document.TryGetValue("b", out _));

		// An int that a double cannot hold exactly does not read as a float.
		Assert.Throws<InvalidOperationException>(() => ((SomlValue)long.MaxValue).GetDouble());
		Assert.Equal(-9_223_372_036_854_775_808.0, ((SomlValue)long.MinValue).GetDouble());
		Assert.Equal(9_007_199_254_740_992.0, ((SomlValue)9_007_199_254_740_992L).GetDouble());
		Assert.False(((SomlValue)9_007_199_254_740_993L).TryGetDouble(out _));
	}

	[Fact]
	public void DefaultIsNullEverywhere()
	{
		SomlValue value = default;

		Assert.Equal(SomlValueKind.Null, value.Kind);
		Assert.True(value.IsNull);
		Assert.Equal(SomlValue.Null, value);
		Assert.Equal(0, value.GetHashCode());
		Assert.Equal("null", value.ToString());
		Assert.Null((string?)value);
		Assert.True(((SomlValue)(string?)null).IsNull);
		Assert.True(((SomlValue)(SomlArray?)null).IsNull);
		Assert.True(((SomlValue)(SomlObject?)null).IsNull);
		Assert.Throws<SomlException>(() => value.ToDocumentString());
		Assert.Equal("a: null\n", new SomlObject { ["a"] = value }.ToDocumentString());
	}

	[Fact]
	public void ConvertsEveryNumericTypeToTheRightKindAndValue()
	{
		Assert.Equal(byte.MaxValue, ((SomlValue)byte.MaxValue).GetInt64());
		Assert.Equal(sbyte.MinValue, ((SomlValue)sbyte.MinValue).GetInt64());
		Assert.Equal(short.MinValue, ((SomlValue)short.MinValue).GetInt64());
		Assert.Equal(ushort.MaxValue, ((SomlValue)ushort.MaxValue).GetInt64());
		Assert.Equal(int.MinValue, ((SomlValue)int.MinValue).GetInt64());
		Assert.Equal(uint.MaxValue, ((SomlValue)uint.MaxValue).GetInt64());
		Assert.Equal(long.MinValue, ((SomlValue)long.MinValue).GetInt64());
		Assert.Equal(long.MaxValue, ((SomlValue)(ulong)long.MaxValue).GetInt64());
		Assert.Throws<OverflowException>(() => (SomlValue)((ulong)long.MaxValue + 1));

		Assert.Equal(SomlValueKind.String, ((SomlValue)'x').Kind);
		Assert.Equal("x", ((SomlValue)'x').GetString());

		// A float keeps its shortest digits, and its extremes convert.
		Assert.Equal("3.4028235e38", ((SomlValue)float.MaxValue).ToString());
		Assert.Equal("-3.4028235e38", ((SomlValue)float.MinValue).ToString());
		Assert.Equal("1e-45", ((SomlValue)float.Epsilon).ToString());
		Assert.Equal("1.1754944e-38", ((SomlValue)1.17549435e-38f).ToString());
		Assert.Equal("infinity", ((SomlValue)float.PositiveInfinity).ToString());
		Assert.Equal("-infinity", ((SomlValue)float.NegativeInfinity).ToString());
		Assert.Equal("16777216.0", ((SomlValue)16_777_216f).ToString());
		Assert.Equal((float)0.3, (float)((SomlValue)0.3f).GetDouble());

		// Each float written reads back as the same float.
		var random = new Random(11);

		for (var index = 0; index < 20_000; index++)
		{
			var single = BitConverter.Int32BitsToSingle(random.Next() | (random.Next(2) << 31));

			if (float.IsNaN(single))
			{
				continue;
			}

			var text = new SomlObject { ["a"] = single }.ToDocumentString();
			var read = (float)SomlValue.Parse(text)["a"].GetDouble();
			Assert.True(read == single, $"{single:R} was written as {text}");
		}
	}

	[Fact]
	public void CastsBackToEveryNumericTypeItConvertsFrom()
	{
		Assert.Equal(int.MinValue, (int)(SomlValue)int.MinValue);
		Assert.Equal(uint.MaxValue, (uint)(SomlValue)uint.MaxValue);
		Assert.Equal((short)-5, (short)(SomlValue)(-5));
		Assert.Equal((ushort)5, (ushort)(SomlValue)(ushort)5);
		Assert.Equal((byte)255, (byte)(SomlValue)(byte)255);
		Assert.Equal((sbyte)-128, (sbyte)(SomlValue)(-128));
		Assert.Equal((ulong)long.MaxValue, (ulong)(SomlValue)long.MaxValue);
		Assert.Equal('c', (char)(SomlValue)'c');
		Assert.Equal(0.5f, (float)(SomlValue)0.5f);
		Assert.Equal(0.1f, (float)(SomlValue)0.1f);
		Assert.Equal(5m, (decimal)(SomlValue)5);
		Assert.Equal(0.1m, (decimal)(SomlValue)0.1);
		Assert.Throws<OverflowException>(() => (decimal)(SomlValue)double.PositiveInfinity);
		Assert.Throws<OverflowException>(() => (decimal)(SomlValue)1e300);

		// A nonzero float too small for a decimal is not rounded to zero, as the serializer reads it.
		Assert.Throws<OverflowException>(() => (decimal)(SomlValue)5e-324);

		// An int outside the range of the type throws rather than wrapping around, and a value of another kind throws as `GetInt64()` and `GetString()` do.
		Assert.Throws<OverflowException>(() => (int)(SomlValue)5_000_000_000L);
		Assert.Throws<OverflowException>(() => (byte)(SomlValue)300);
		Assert.Throws<OverflowException>(() => (ulong)(SomlValue)(-1));
		Assert.Throws<InvalidOperationException>(() => (int)(SomlValue)1.5);
		Assert.Throws<InvalidOperationException>(() => (char)(SomlValue)"ab");
		Assert.Throws<InvalidOperationException>(() => (float)(SomlValue)"x");
	}

	// Time types

	[Fact]
	public void ParsesInstantsToTheUnixTimeOfTheBcl()
	{
		var random = new Random(17);

		for (var index = 0; index < 20_000; index++)
		{
			var year = index < 100 ? (index % 2 == 0 ? 1 : 9999) : random.Next(1, 10_000);
			var month = random.Next(1, 13);
			var day = random.Next(1, DateTime.DaysInMonth(year, month) + 1);
			var hour = random.Next(24);
			var minute = random.Next(60);
			var second = random.Next(60);
			var offsetMinutes = random.Next(-(23 * 60) - 59, (23 * 60) + 60);
			var nanosecond = random.Next(1_000_000_000);
			var offset = offsetMinutes == 0 ? "Z" : string.Create(CultureInfo.InvariantCulture, $"{(offsetMinutes < 0 ? '-' : '+')}{Math.Abs(offsetMinutes) / 60:D2}:{Math.Abs(offsetMinutes) % 60:D2}");
			var text = string.Create(CultureInfo.InvariantCulture, $"{year:D4}-{month:D2}-{day:D2}T{hour:D2}:{minute:D2}:{second:D2}.{nanosecond:D9}{offset}");
			var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
			var unixSeconds = ((local.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond) - (offsetMinutes * 60L);

			if (unixSeconds < SomlInstant.MinValue.UnixSeconds || unixSeconds > SomlInstant.MaxValue.UnixSeconds)
			{
				Assert.False(SomlInstant.TryParse(text, out _), text);
			}
			else
			{
				Assert.Equal(new SomlInstant(unixSeconds, nanosecond), SomlInstant.Parse(text));
			}
		}
	}

	[Fact]
	public void ComparesInstantsConsistentlyWithEquality()
	{
		SomlInstant[] instants =
		[
			SomlInstant.MinValue,
			new(SomlInstant.MinValue.UnixSeconds, 1),
			new(-1, 0),
			new(-1, 999_999_999),
			SomlInstant.UnixEpoch,
			new(0, 1),
			new(1, 0),
			new(SomlInstant.MaxValue.UnixSeconds, 0),
			SomlInstant.MaxValue,
		];

		for (var left = 0; left < instants.Length; left++)
		{
			for (var right = 0; right < instants.Length; right++)
			{
				var a = instants[left];
				var b = instants[right];
				var expected = left.CompareTo(right);

				Assert.Equal(expected, Math.Sign(a.CompareTo(b)));
				Assert.Equal(expected, Math.Sign(a.CompareTo((object)b)));
				Assert.Equal(expected == 0, a.Equals(b));
				Assert.Equal(expected == 0, a == b);
				Assert.Equal(expected != 0, a != b);
				Assert.Equal(expected < 0, a < b);
				Assert.Equal(expected <= 0, a <= b);
				Assert.Equal(expected > 0, a > b);
				Assert.Equal(expected >= 0, a >= b);
				Assert.Equal(expected == 0, ((SomlValue)a).Equals((SomlValue)b));

				if (expected == 0)
				{
					Assert.Equal(a.GetHashCode(), b.GetHashCode());
				}
			}
		}

		Assert.Equal(1, SomlInstant.UnixEpoch.CompareTo(null));
		Assert.Throws<ArgumentException>(() => SomlInstant.UnixEpoch.CompareTo(SomlDuration.Zero));
		Assert.Equal(SomlInstant.UnixEpoch, default);
	}

	[Fact]
	public void ComparesDurationsConsistentlyWithEquality()
	{
		SomlDuration[] durations = [SomlDuration.MinValue, new(long.MinValue + 1), new(-1), SomlDuration.Zero, new(1), new(long.MaxValue - 1), SomlDuration.MaxValue];

		for (var left = 0; left < durations.Length; left++)
		{
			for (var right = 0; right < durations.Length; right++)
			{
				var a = durations[left];
				var b = durations[right];
				var expected = left.CompareTo(right);

				Assert.Equal(expected, Math.Sign(a.CompareTo(b)));
				Assert.Equal(expected, Math.Sign(a.CompareTo((object)b)));
				Assert.Equal(expected == 0, a.Equals(b));
				Assert.Equal(expected == 0, a == b);
				Assert.Equal(expected < 0, a < b);
				Assert.Equal(expected <= 0, a <= b);
				Assert.Equal(expected > 0, a > b);
				Assert.Equal(expected >= 0, a >= b);
			}
		}

		Assert.Equal(1, SomlDuration.Zero.CompareTo(null));
		Assert.Throws<ArgumentException>(() => SomlDuration.Zero.CompareTo(SomlInstant.UnixEpoch));
	}

	[Fact]
	public void FailsToFormatInstantsIntoEveryShortBuffer()
	{
		SomlInstant[] instants = [SomlInstant.MinValue, SomlInstant.MaxValue, new(-1, 500_000_000), new(0, 1), SomlInstant.UnixEpoch];

		foreach (var instant in instants)
		{
			var expected = instant.ToString();

			for (var length = 0; length < expected.Length; length++)
			{
				var buffer = new char[length];
				Assert.False(instant.TryFormat(buffer, out var written), $"{expected} into {length}");
				Assert.Equal(0, written);
			}

			var exact = new char[expected.Length];
			Assert.True(instant.TryFormat(exact, out var exactWritten));
			Assert.Equal(expected, new string(exact, 0, exactWritten));
			Assert.Equal(expected, $"{instant}");
			Assert.Equal(expected, instant.ToString("anything", CultureInfo.GetCultureInfo("sv-SE")));
		}
	}

	[Fact]
	public void FailsToFormatDurationsIntoEveryShortBuffer()
	{
		SomlDuration[] durations = [SomlDuration.MinValue, SomlDuration.MaxValue, SomlDuration.Zero, new(-1), new(1), SomlDuration.Parse("1h30m"), SomlDuration.Parse("-0.25s"), SomlDuration.Parse("1h0.5s")];

		foreach (var duration in durations)
		{
			var expected = duration.ToString();

			for (var length = 0; length < expected.Length; length++)
			{
				var buffer = new char[length];
				Assert.False(duration.TryFormat(buffer, out var written, default, null), $"{expected} into {length}");
				Assert.Equal(0, written);
			}

			var exact = new char[expected.Length];
			Assert.True(duration.TryFormat(exact, out var exactWritten, default, null));
			Assert.Equal(expected, new string(exact, 0, exactWritten));
			Assert.Equal(expected, $"{duration}");
		}
	}

	[Fact]
	public void RoundTripsInstantsAtTheEdges()
	{
		(SomlInstant Instant, string Text)[] cases =
		[
			(SomlInstant.MinValue, "0001-01-01T00:00:00Z"),
			(SomlInstant.MaxValue, "9999-12-31T23:59:59.999999999Z"),
			(new(-1, 1), "1969-12-31T23:59:59.000000001Z"),
			(new(-1, 999_999_999), "1969-12-31T23:59:59.999999999Z"),
			(new(-86_400, 0), "1969-12-31T00:00:00Z"),
			(new(-86_401, 0), "1969-12-30T23:59:59Z"),
			(new(951_782_400, 0), "2000-02-29T00:00:00Z"),
			(new(-2_208_988_800, 100), "1900-01-01T00:00:00.0000001Z"),
			(new(SomlInstant.MinValue.UnixSeconds, 1), "0001-01-01T00:00:00.000000001Z"),
		];

		foreach (var (instant, text) in cases)
		{
			Assert.Equal(text, instant.ToString());
			Assert.Equal(instant, SomlInstant.Parse(text));
			Assert.True(SomlInstant.TryParse(text, out var parsed));
			Assert.Equal(instant, parsed);
			Assert.Equal(instant, SomlInstant.Parse(text.AsSpan(), null));
			Assert.Equal(instant, SomlValue.Parse(new SomlObject { ["a"] = instant }.ToDocumentString())["a"].GetInstant());
		}

		// Random instants from year 1 to 9999, written and read back, against the date and time that the BCL gives them.
		var random = new Random(3);

		for (var index = 0; index < 20_000; index++)
		{
			var seconds = random.NextInt64(SomlInstant.MinValue.UnixSeconds, SomlInstant.MaxValue.UnixSeconds + 1);
			var nanosecond = random.Next(1_000_000_000);
			var instant = new SomlInstant(seconds, nanosecond);
			var text = instant.ToString();
			var bcl = DateTimeOffset.FromUnixTimeSeconds(seconds);

			Assert.StartsWith(bcl.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
			Assert.Equal(instant, SomlInstant.Parse(text));
		}
	}

	[Fact]
	public void ReadsInstantsWithOffsetsAtTheEdges()
	{
		Assert.Equal(SomlInstant.MinValue, SomlInstant.Parse("0001-01-01T01:00:00+01:00"));
		Assert.Throws<FormatException>(() => SomlInstant.Parse("0001-01-01T00:59:59+01:00"));
		Assert.Equal(SomlInstant.MaxValue, SomlInstant.Parse("9999-12-31T22:59:59.999999999-01:00"));
		Assert.Throws<FormatException>(() => SomlInstant.Parse("9999-12-31T23:00:00-01:00"));
		Assert.False(SomlInstant.TryParse("9999-12-31T23:00:00-01:00", out _));

		// The largest offsets move an instant across a day, and across the edges of the range.
		Assert.Equal(SomlInstant.MinValue, SomlInstant.Parse("0001-01-01T23:59:00+23:59"));
		Assert.Throws<FormatException>(() => SomlInstant.Parse("0001-01-01T23:58:59+23:59"));
		Assert.Equal("0001-01-01T23:59:00Z", SomlInstant.Parse("0001-01-01T00:00:00-23:59").ToString());
		Assert.Equal("9999-12-31T23:59:00Z", SomlInstant.Parse("9999-12-31T22:00:00-01:59").ToString());
		Assert.Equal("9999-12-31T00:01:00Z", SomlInstant.Parse("9999-12-31T23:59:00+23:58").ToString());
	}

	[Fact]
	public void RoundTripsDurationsAtTheEdges()
	{
		SomlDuration[] durations = [SomlDuration.MinValue, SomlDuration.MaxValue, new(long.MinValue + 1), new(-1), new(1), new(-999_999_999), new(3_600_000_000_000), new(-3_600_000_000_001)];

		foreach (var duration in durations)
		{
			var text = duration.ToString();

			Assert.Equal(duration, SomlDuration.Parse(text));
			Assert.True(SomlDuration.TryParse(text, out var parsed));
			Assert.Equal(duration, parsed);
			Assert.Equal(duration, SomlValue.Parse(new SomlObject { ["a"] = duration }.ToDocumentString())["a"].GetDuration());
		}

		Assert.Equal("-2562047h47m16.854775808s", SomlDuration.MinValue.ToString());
		Assert.Equal("2562047h47m16.854775807s", SomlDuration.MaxValue.ToString());
		Assert.Equal(SomlDuration.MinValue, SomlDuration.Parse("-9223372036854775808ns"));
		Assert.Equal(SomlDuration.MaxValue, SomlDuration.Parse("9223372036854775807ns"));
		Assert.Throws<FormatException>(() => SomlDuration.Parse("9223372036854775808ns"));
		Assert.Throws<FormatException>(() => SomlDuration.Parse("-9223372036854775809ns"));

		var random = new Random(5);

		for (var index = 0; index < 20_000; index++)
		{
			var duration = new SomlDuration(random.NextInt64(long.MinValue, long.MaxValue));
			Assert.Equal(duration, SomlDuration.Parse(duration.ToString()));
		}

	}

	[Fact]
	public void RoundTripsTheEdgesOfTheIntRange()
	{
		var ints = new SomlObject { ["min"] = long.MinValue, ["max"] = long.MaxValue };
		Assert.Equal((SomlValue)ints, SomlValue.Parse(ints.ToDocumentString()));
	}

	[Fact]
	public void ParsesOnlyAWholeValueOfTheRightKind()
	{
		string[] notDurations = [" 1s", "1s ", "1s\n", "\n1s", "1s # comment", "1s, 2s", "[1s]", "'1s'", "1", "1.5", "null", "2026-09-19T14:00:00Z", "", "1s\nb: 2", "a: 1s"];

		foreach (var text in notDurations)
		{
			Assert.False(SomlDuration.TryParse(text, out var result), text);
			Assert.Equal(default, result);
			Assert.Throws<FormatException>(() => SomlDuration.Parse(text));
		}

		string[] notInstants = [" 2026-09-19T14:00:00Z", "2026-09-19T14:00:00Z ", "2026-09-19T14:00:00Z\n", "1s", "1", "null", "'2026-09-19T14:00:00Z'", "2026-09-19", "", "[2026-09-19T14:00:00Z]"];

		foreach (var text in notInstants)
		{
			Assert.False(SomlInstant.TryParse(text, out var result), text);
			Assert.Equal(default, result);
			Assert.Throws<FormatException>(() => SomlInstant.Parse(text));
		}

		Assert.False(SomlDuration.TryParse(null, out _));
		Assert.False(SomlInstant.TryParse(null, out _));
		Assert.Throws<ArgumentNullException>(() => SomlDuration.Parse(null!));
		Assert.Throws<ArgumentNullException>(() => SomlInstant.Parse(null!));
	}

	[Fact]
	public void TryParseNeverThrowsAndParseThrowsOnlyFormatException()
	{
		const string alphabet = "0123456789000hmsunµ.._--+:TtZz [{'\"#\n,e";
		var random = new Random(19);
		var text = new StringBuilder();

		for (var index = 0; index < 30_000; index++)
		{
			text.Clear();

			if (random.Next(3) == 0)
			{
				text.Append("2026-09-19T14:00:00");
			}

			var length = random.Next(12);

			for (var character = 0; character < length; character++)
			{
				text.Append(alphabet[random.Next(alphabet.Length)]);
			}

			var candidate = text.ToString();
			var isDuration = SomlDuration.TryParse(candidate, out var duration);
			var isInstant = SomlInstant.TryParse(candidate, out var instant);

			if (isDuration)
			{
				Assert.Equal(duration, SomlDuration.Parse(duration.ToString()));
			}
			else
			{
				Assert.Throws<FormatException>(() => SomlDuration.Parse(candidate));
			}

			if (isInstant)
			{
				Assert.Equal(instant, SomlInstant.Parse(instant.ToString()));
			}
			else
			{
				Assert.Throws<FormatException>(() => SomlInstant.Parse(candidate));
			}
		}
	}

	[Fact]
	public void ConvertsTimeTypesAtTheEdgesOrThrows()
	{
		Assert.Equal(SomlInstant.MinValue, SomlInstant.FromDateTimeOffset(DateTimeOffset.MinValue));
		Assert.Equal(new SomlInstant(SomlInstant.MaxValue.UnixSeconds, 999_999_900), SomlInstant.FromDateTimeOffset(DateTimeOffset.MaxValue));
		Assert.Equal(DateTimeOffset.MinValue, SomlInstant.MinValue.ToDateTimeOffset());
		Assert.Equal(DateTimeOffset.MaxValue, new SomlInstant(SomlInstant.MaxValue.UnixSeconds, 999_999_900).ToDateTimeOffset());
		Assert.Equal(DateTime.MaxValue, new SomlInstant(SomlInstant.MaxValue.UnixSeconds, 999_999_900).ToDateTime());
		Assert.Equal(DateTimeKind.Utc, SomlInstant.MinValue.ToDateTime().Kind);
		Assert.Throws<InvalidOperationException>(() => SomlInstant.MaxValue.ToDateTimeOffset());
		Assert.Throws<InvalidOperationException>(() => new SomlInstant(-1, 1).ToDateTime());
		Assert.Throws<InvalidOperationException>(() => (DateTimeOffset)new SomlInstant(0, 50));

		// The offset is not part of the value, and an instant before 1970 with a fraction converts both ways.
		var withOffset = new DateTimeOffset(1969, 12, 31, 23, 59, 59, TimeSpan.FromHours(-5)).AddTicks(1);
		var instant = SomlInstant.FromDateTimeOffset(withOffset);
		Assert.Equal(withOffset, instant.ToDateTimeOffset());
		Assert.Equal(TimeSpan.Zero, instant.ToDateTimeOffset().Offset);
		Assert.Equal(instant, (SomlInstant)withOffset);
		Assert.Equal(instant, SomlInstant.FromDateTime(withOffset.UtcDateTime));
		Assert.Throws<ArgumentException>(() => SomlInstant.FromDateTime(new DateTime(2026, 1, 1)));

		Assert.Equal(new SomlDuration(long.MaxValue / 100 * 100), SomlDuration.FromTimeSpan(TimeSpan.FromTicks(long.MaxValue / 100)));
		Assert.Throws<OverflowException>(() => SomlDuration.FromTimeSpan(TimeSpan.FromTicks((long.MaxValue / 100) + 1)));
		Assert.Throws<OverflowException>(() => SomlDuration.FromTimeSpan(TimeSpan.MinValue));
		Assert.Throws<OverflowException>(() => (SomlDuration)TimeSpan.MaxValue);
		Assert.Equal(SomlDuration.FromTimeSpan(TimeSpan.FromTicks(long.MinValue / 100)), new SomlDuration(long.MinValue / 100 * 100));
		Assert.Equal(TimeSpan.FromTicks(long.MinValue / 100), new SomlDuration(long.MinValue / 100 * 100).ToTimeSpan());
		Assert.Throws<InvalidOperationException>(() => SomlDuration.MinValue.ToTimeSpan());
		Assert.Throws<InvalidOperationException>(() => (TimeSpan)SomlDuration.MaxValue);
		Assert.Throws<InvalidOperationException>(() => new SomlDuration(-1).ToTimeSpan());
		Assert.Equal(TimeSpan.FromTicks(-1), new SomlDuration(-100).ToTimeSpan());
	}

	[Fact]
	public void DoesTimeArithmeticAtTheEdges()
	{
		Assert.Equal(SomlInstant.MinValue, SomlInstant.MinValue + SomlDuration.Zero);
		Assert.Throws<OverflowException>(() => SomlInstant.MinValue - new SomlDuration(1));
		Assert.Throws<OverflowException>(() => SomlInstant.MinValue + new SomlDuration(-1));
		Assert.Equal(new SomlInstant(-1, 999_999_999), SomlInstant.UnixEpoch - new SomlDuration(1));
		Assert.Equal(new SomlInstant(-2, 0), new SomlInstant(-1, 1) + new SomlDuration(-1_000_000_001));
		Assert.Equal(SomlInstant.MinValue + SomlDuration.MaxValue, SomlInstant.MinValue - -SomlDuration.MaxValue);
		Assert.Equal(new SomlDuration(-1), SomlInstant.UnixEpoch - new SomlInstant(0, 1));
		Assert.Equal(new SomlDuration(-2_000_000_000), new SomlInstant(-1, 0) - new SomlInstant(1, 0));

		var farEnough = SomlInstant.UnixEpoch + SomlDuration.MaxValue;
		Assert.Equal(SomlDuration.MaxValue, farEnough - SomlInstant.UnixEpoch);
		Assert.Throws<OverflowException>(() => (farEnough + new SomlDuration(1)) - SomlInstant.UnixEpoch);
		var farBack = SomlInstant.UnixEpoch + SomlDuration.MinValue;
		Assert.Equal(SomlDuration.MinValue, farBack - SomlInstant.UnixEpoch);
		Assert.Throws<OverflowException>(() => (farBack - new SomlDuration(1)) - SomlInstant.UnixEpoch);

		Assert.Throws<OverflowException>(() => SomlDuration.MaxValue + new SomlDuration(1));
		Assert.Throws<OverflowException>(() => SomlDuration.MinValue - new SomlDuration(1));
		Assert.Throws<OverflowException>(() => -SomlDuration.MinValue);
		Assert.Equal(new SomlDuration(-long.MaxValue), -SomlDuration.MaxValue);
	}

	// SomlObject and SomlArray

	[Fact]
	public void FollowsTheDictionaryContract()
	{
		var members = new SomlObject { ["a"] = 1, ["b"] = 2 };
		IDictionary<string, SomlValue> dictionary = members;
		ICollection<KeyValuePair<string, SomlValue>> collection = members;

		Assert.Throws<ArgumentException>(() => members.Add("a", 3));
		Assert.Throws<ArgumentException>(() => collection.Add(new("a", 3)));
		Assert.Throws<ArgumentException>(() => new SomlObject([new("a", 1), new("a", 2)]));
		Assert.False(members.TryAdd("a", 3));
		Assert.Equal(1, members["a"].GetInt64());
		Assert.Throws<KeyNotFoundException>(() => members["c"]);
		Assert.Throws<ArgumentNullException>(() => members[null!]);
		Assert.Throws<ArgumentNullException>(() => members.ContainsKey(null!));
		Assert.False(collection.IsReadOnly);

		Assert.True(collection.Contains(new("a", 1)));
		Assert.False(collection.Contains(new("a", 2)));
		Assert.False(collection.Remove(new("a", 2)));
		Assert.Equal(2, members.Count);

		var target = new KeyValuePair<string, SomlValue>[4];
		collection.CopyTo(target, 1);
		Assert.Equal("a", target[1].Key);
		Assert.Equal("b", target[2].Key);
		Assert.Null(target[3].Key);
		Assert.Throws<ArgumentException>(() => collection.CopyTo(new KeyValuePair<string, SomlValue>[2], 1));
		Assert.Throws<ArgumentOutOfRangeException>(() => collection.CopyTo(target, -1));

		Assert.Equal(["a", "b"], dictionary.Keys);
		Assert.Equal([1, 2], dictionary.Values);
		Assert.True(dictionary.Keys.IsReadOnly);
		Assert.Throws<NotSupportedException>(() => dictionary.Keys.Add("c"));
		Assert.Throws<NotSupportedException>(() => dictionary.Values.Clear());

		var keys = members.Keys;
		members["c"] = 3;
		Assert.Equal(3, keys.Count);
		Assert.Equal("c", keys[2]);
		Assert.Throws<ArgumentOutOfRangeException>(() => keys[3]);
		Assert.Equal(2, members.Values.IndexOf(3));
		Assert.Contains(2, members.Values);
		Assert.Throws<ArgumentOutOfRangeException>(() => members.Keys.CopyTo(new string[5], -1));

		Assert.True(collection.Remove(new("b", 2)));
		Assert.Equal(["a", "c"], members.Keys);
		Assert.False(members.Remove("b"));
		Assert.Throws<ArgumentOutOfRangeException>(() => members.RemoveAt(2));
		Assert.Throws<ArgumentOutOfRangeException>(() => members.GetAt(-1));
		Assert.Throws<ArgumentOutOfRangeException>(() => members.Insert(3, "d", 4));
		Assert.Throws<ArgumentException>(() => members.Insert(0, "a", 4));
	}

	[Fact]
	public void InvalidatesEnumeratorsWhenTheMembersChange()
	{
		Action<SomlObject>[] changes =
		[
			members => members.Add("new", 1),
			members => members["new"] = 1,
			members => members.Remove("a"),
			members => members.RemoveAt(0),
			members => members.Insert(0, "new", 1),
			members => members.Clear(),
		];

		foreach (var change in changes)
		{
			foreach (var size in (int[])[2, 12])
			{
				var members = new SomlObject();

				for (var index = 0; index < size; index++)
				{
					members[index == 0 ? "a" : $"k{index}"] = index;
				}

				Assert.Throws<InvalidOperationException>(() =>
				{
					foreach (var _ in members)
					{
						change(members);
					}
				});

				var other = new SomlObject();

				for (var index = 0; index < size; index++)
				{
					other[index == 0 ? "a" : $"k{index}"] = index;
				}

				Assert.Throws<InvalidOperationException>(() =>
				{
					foreach (var _ in other.Keys)
					{
						change(other);
					}
				});
			}
		}
	}

	[Fact]
	public void KeepsItsIndexConsistentWithAListModel()
	{
		var random = new Random(11);
		string[] keys = ["", "a", "A", "é", "e\u0301", "😀", "😁", "\0", .. Enumerable.Range(0, 24).Select(index => $"k{index}")];

		for (var run = 0; run < 400; run++)
		{
			var members = new SomlObject();
			ICollection<KeyValuePair<string, SomlValue>> collection = members;
			var model = new List<KeyValuePair<string, SomlValue>>();

			for (var step = 0; step < 80; step++)
			{
				var key = keys[random.Next(keys.Length)];
				var modelIndex = model.FindIndex(member => member.Key == key);
				var operation = random.Next(100);

				if (operation < 30)
				{
					members[key] = step;

					if (modelIndex >= 0)
					{
						model[modelIndex] = new(key, step);
					}
					else
					{
						model.Add(new(key, step));
					}
				}
				else if (operation < 45)
				{
					var index = random.Next(model.Count + 1);

					if (modelIndex >= 0)
					{
						Assert.Throws<ArgumentException>(() => members.Insert(index, key, step));
					}
					else
					{
						members.Insert(index, key, step);
						model.Insert(index, new(key, step));
					}
				}
				else if (operation < 60)
				{
					Assert.Equal(modelIndex < 0, members.TryAdd(key, step));

					if (modelIndex < 0)
					{
						model.Add(new(key, step));
					}
				}
				else if (operation < 80)
				{
					Assert.Equal(modelIndex >= 0, members.Remove(key));

					if (modelIndex >= 0)
					{
						model.RemoveAt(modelIndex);
					}
				}
				else if (operation < 90)
				{
					if (model.Count > 0)
					{
						var index = random.Next(model.Count);
						members.RemoveAt(index);
						model.RemoveAt(index);
					}
				}
				else if (operation < 98)
				{
					// Equal numeric magnitudes of different SOML kinds must not remove a member.
					var value = modelIndex >= 0 ? model[modelIndex].Value : (SomlValue)step;
					var isMatchingValue = random.Next(2) == 0;
					var pair = new KeyValuePair<string, SomlValue>(key, isMatchingValue ? value : (SomlValue)(double)value.GetInt64());
					var expected = modelIndex >= 0 && isMatchingValue;
					Assert.Equal(expected, collection.Contains(pair));
					Assert.Equal(expected, collection.Remove(pair));

					if (expected)
					{
						model.RemoveAt(modelIndex);
					}
				}
				else
				{
					members.Clear();
					model.Clear();
				}

				Assert.Equal(model.Count, members.Count);
				Assert.Equal(model.Select(member => member.Key), members.Keys);
				Assert.Equal(model.Select(member => member.Value), members.Values);

				foreach (var candidateKey in keys)
				{
					var expected = model.FindIndex(member => member.Key == candidateKey);
					Assert.Equal(expected, members.IndexOf(candidateKey));
					Assert.Equal(expected >= 0, members.TryGetValue(candidateKey, out var value));

					if (expected >= 0)
					{
						Assert.Equal(model[expected].Value, value);
						Assert.Equal(model[expected].Value, members[candidateKey]);
					}
				}
			}
		}
	}

	[Fact]
	public void KeepsInsertionOrderAfterRemovingAndAddingAgain()
	{
		foreach (var size in (int[])[3, 9, 20])
		{
			var members = new SomlObject();

			for (var index = 0; index < size; index++)
			{
				members[$"k{index}"] = index;
			}

			members.Remove("k1");
			members["k1"] = 100;
			members.Remove("k0");
			members.Add("k0", 200);

			var expected = Enumerable.Range(2, size - 2).Select(index => $"k{index}").Concat(["k1", "k0"]);
			Assert.Equal(expected, members.Keys);
			Assert.Equal(100, members["k1"].GetInt64());
			Assert.Equal(200, members["k0"].GetInt64());
			Assert.Equal(expected, SomlValue.Parse(members.ToDocumentString()).GetObject().Keys);
		}
	}

	[Fact]
	public void FollowsTheListContract()
	{
		var array = new SomlArray { 1, "two" };
		IList<SomlValue> list = array;

		array.Insert(0, 0);
		array.Insert(3, 3.0);
		Assert.Equal((SomlValue[])[0, 1, "two", 3.0], array);
		Assert.Throws<ArgumentOutOfRangeException>(() => array.Insert(5, 1));
		Assert.Throws<ArgumentOutOfRangeException>(() => array.Insert(-1, 1));
		Assert.Throws<ArgumentOutOfRangeException>(() => array[4]);
		Assert.Throws<ArgumentOutOfRangeException>(() => array[-1]);
		Assert.Throws<ArgumentOutOfRangeException>(() => array[4] = 1);
		Assert.Throws<ArgumentOutOfRangeException>(() => array.RemoveAt(4));
		Assert.False(list.IsReadOnly);

		// Removal and search use value equality, so the float 3.0 is not the int 3.
		Assert.Equal(-1, array.IndexOf(3));
		Assert.Equal(3, array.IndexOf(3.0));
		Assert.False(array.Remove(3));
		Assert.True(array.Remove(3.0));
		Assert.Contains("two", array);

		var target = new SomlValue[5];
		array.CopyTo(target, 2);
		Assert.Equal((SomlValue[])[SomlValue.Null, SomlValue.Null, 0, 1, "two"], target);
		Assert.Throws<ArgumentException>(() => array.CopyTo(new SomlValue[3], 1));

		var nested = new SomlArray { new SomlObject { ["b"] = 1, ["a"] = 2 } };
		Assert.Contains(new SomlObject { ["a"] = 2, ["b"] = 1 }, nested);

		array.Clear();
		Assert.Empty(array);
		Assert.Equal("[]", array.ToString());
		Assert.Equal("[]\n", array.ToDocumentString());
	}

	// Writing

	[Fact]
	public void RoundTripsEveryCharacterInStringsAndKeys()
	{
		var failures = new List<string>();

		void Check(string text, int codePoint)
		{
			var shapes = new (string Name, SomlValue Value)[]
			{
				("value", new SomlObject { ["a"] = text }),
				("array item", new SomlArray { text }),
				("first key", new SomlObject { [text] = 1, ["b"] = 2 }),
				("second key", new SomlObject { ["b"] = 2, [text] = 1 }),
				("nested key", new SomlObject { ["b"] = new SomlObject { [text] = 1 } }),
				("key in an array", new SomlArray { new SomlObject { [text] = 1 } }),
			};

			foreach (var (name, value) in shapes)
			{
				try
				{
					var document = value.ToDocumentString();

					if (!SomlValue.Parse(document).Equals(value))
					{
						failures.Add($"U+{codePoint:X4} as {name}: read back differently from {document}");
					}
				}
				catch (SomlException exception)
				{
					failures.Add($"U+{codePoint:X4} as {name}: {exception.Message}");
				}
			}
		}

		for (var code = 0; code <= 0xFFFF; code++)
		{
			if (code is >= 0xD800 and <= 0xDFFF or 0x0D)
			{
				continue;
			}

			var character = ((char)code).ToString();
			Check(character, code);
			Check("a" + character + "b", code);
			Check(character + character, code);
		}

		foreach (var codePoint in (int[])[0x10000, 0x1F600, 0x1D11E, 0xE0001, 0xE007F, 0xF0000, 0x10FFFD, 0x10FFFF])
		{
			var text = Astral(codePoint);
			Check(text, codePoint);
			Check("a" + text + "\n" + text, codePoint);
			Check(text + "'", codePoint);
		}

		Assert.True(failures.Count == 0, string.Join("\n", failures.Take(40)) + $"\n({failures.Count} failures)");
	}

	[Fact]
	public void RoundTripsStringsWithEverySpecialCharacter()
	{
		string[] texts =
		[
			"",
			"'",
			"''",
			"'''",
			"\"",
			"\"\"\"",
			"\\",
			"a\\",
			"\\'",
			"'\\",
			"\\n",
			"\\u{41}",
			"\n",
			"\n\n",
			"a\n",
			"\na",
			"\t",
			" ",
			"  leading",
			"trailing  ",
			"#",
			"# not a comment",
			"a # b",
			"//",
			"{",
			"}",
			"[",
			"]",
			",",
			":",
			"a: b",
			"null",
			"true",
			"1",
			"-1",
			"1h30m",
			"2026-09-19T14:00:00Z",
			"infinity",
			"${a}",
			"\"'\\\n\t" + (char)0 + (char)0x7F + (char)0x1B,
			"'''\n'''",
			"\"\"\"\n\"\"\"",
		];

		foreach (var text in texts)
		{
			foreach (SomlValue value in (SomlValue[])[new SomlObject { ["a"] = text }, new SomlObject { [text] = text }, new SomlArray { text, new SomlArray { text } }])
			{
				var document = value.ToDocumentString();
				Assert.Equal(value, SomlValue.Parse(document));
			}
		}

		// A string that SOML cannot represent is refused by the document writer and shown by `ToString`.
		var lone = ((char)0xD83D).ToString();
		Assert.Throws<SomlException>(() => new SomlObject { ["a"] = lone }.ToDocumentString());
		Assert.Throws<SomlException>(() => new SomlObject { [lone] = 1 }.ToDocumentString());
		Assert.Throws<SomlException>(() => new SomlObject { ["a\rb"] = 1 }.ToDocumentString());
		Assert.Equal("\"\\u{d83d}\"", ((SomlValue)lone).ToString());
		Assert.Equal("{\n\t\"\\u{d}\": 1\n}", new SomlObject { ["\r"] = 1 }.ToString());
	}

	[Fact]
	public void FormatsMixedEscapesWithoutEscapingSurrogatePairs()
	{
		SomlValue value = "\0\u001f\u007f\r\n\t\\\"'\ud800😀\udfff";

		Assert.Equal("\"\\u{0}\\u{1f}\\u{7f}\\u{d}\\n\\t\\\\\\\"'\\u{d800}😀\\u{dfff}\"", value.ToString());
		Assert.Throws<SomlException>(() => new SomlArray { value }.ToDocumentString());
	}

	[Fact]
	public void RoundTripsKeysThatLookLikeValues()
	{
		string[] keys =
		[
			"1h30m", "123", "007", "0", "-5", "-", "--", "_", "-_-", "0x1F", "0b101", "0o17", "1e5", "1_000", "true", "false", "null",
			"infinity", "-infinity", "nan", "inf", "", " ", "2026-09-19", "2026-09-19T14:00:00Z", "1.5", "a.b", "a b", "#", "a#b", "ü", "A", "Z_9",
		];

		foreach (var key in keys)
		{
			SomlValue[] values =
			[
				new SomlObject { [key] = 1 },
				new SomlObject { [key] = new SomlObject { [key] = key } },
				new SomlObject { ["b"] = 1, [key] = 2 },
				new SomlArray { new SomlObject { [key] = 1 }, new SomlObject { ["z"] = 1, [key] = 2 } },
			];

			foreach (var value in values)
			{
				var document = value.ToDocumentString();
				Assert.Equal(value, SomlValue.Parse(document));
				Assert.Equal(value, SomlValue.Parse(value.ToDocumentString(canonical: true)));
			}
		}
	}

	[Fact]
	public void RoundTripsFloatsAtTheExtremes()
	{
		(double Value, string Text)[] cases =
		[
			(double.Epsilon, "5e-324"),
			(-double.Epsilon, "-5e-324"),
			(double.MaxValue, "1.7976931348623157e308"),
			(double.MinValue, "-1.7976931348623157e308"),
			(2.2250738585072014e-308, "2.2250738585072014e-308"),
			(2.225073858507201e-308, "2.225073858507201e-308"),
			(-0.0, "0.0"),
			(0.0, "0.0"),
			(double.PositiveInfinity, "infinity"),
			(double.NegativeInfinity, "-infinity"),
			(1e21, "1e21"),
			(1e20, "100000000000000000000.0"),
			(123456789012345680000.0, "123456789012345680000.0"),
			(1e-6, "0.000001"),
			(1e-7, "1e-7"),
			(1.5e-7, "1.5e-7"),
			(0.1, "0.1"),
			(5e-324 * 3, "1.5e-323"),
			(9007199254740993.0, "9007199254740992.0"),
		];

		foreach (var (value, text) in cases)
		{
			var members = new SomlObject { ["a"] = value };
			var document = members.ToDocumentString();

			Assert.Equal($"a: {text}\n", document);
			var read = SomlValue.Parse(document)["a"].GetDouble();
			Assert.Equal(BitConverter.DoubleToInt64Bits(value + 0.0), BitConverter.DoubleToInt64Bits(read));
		}

		// Powers of two whose lower neighbor is closer, which .NET's `R` format gets wrong, and random bit patterns read back exactly.
		for (var exponent = -1074; exponent <= 1023; exponent++)
		{
			var power = Math.Pow(2, exponent);
			var document = new SomlObject { ["a"] = power, ["b"] = -power }.ToDocumentString();
			var read = SomlValue.Parse(document);
			Assert.Equal(power, read["a"].GetDouble());
			Assert.Equal(-power, read["b"].GetDouble());
		}

		var random = new Random(7);

		for (var index = 0; index < 50_000; index++)
		{
			var value = BitConverter.Int64BitsToDouble(random.NextInt64(long.MinValue, long.MaxValue));

			if (double.IsNaN(value))
			{
				continue;
			}

			var document = new SomlObject { ["a"] = value }.ToDocumentString();
			Assert.Equal(value + 0.0, SomlValue.Parse(document)["a"].GetDouble());
		}
	}

	[Fact]
	public void SortsCanonicalKeysByUnicodeScalarValue()
	{
		string[] keys =
		[
			"",
			"a",
			"b",
			"ab",
			"aa",
			"A",
			"z",
			((char)0x7F).ToString(),
			((char)0xE9).ToString(),
			((char)0xD7FF).ToString(),
			((char)0xE000).ToString(),
			((char)0xF8FF).ToString(),
			((char)0xFB01).ToString(),
			((char)0xFFFD).ToString(),
			((char)0xFFFF).ToString(),
			Astral(0x10000),
			Astral(0x1F600),
			Astral(0x1F601),
			Astral(0x10FFFF),
			"a" + Astral(0x1F600),
			"a" + (char)0xFFFF,
			Astral(0x1F600) + "a",
			Astral(0x1F600) + (char)0xE000,
			Astral(0x1F600) + Astral(0x10000),
		];

		static int CompareScalars(string left, string right)
		{
			int[] leftScalars = [.. left.EnumerateRunes().Select(rune => rune.Value)];
			int[] rightScalars = [.. right.EnumerateRunes().Select(rune => rune.Value)];
			return leftScalars.AsSpan().SequenceCompareTo(rightScalars);
		}

		var expected = keys.Order(Comparer<string>.Create(CompareScalars)).ToList();
		var random = new Random(13);

		for (var run = 0; run < 20; run++)
		{
			var shuffled = keys.OrderBy(_ => random.Next()).ToArray();
			var members = new SomlObject();

			foreach (var key in shuffled)
			{
				members[key] = 1;
			}

			var document = members.ToDocumentString(canonical: true);
			var read = SomlValue.Parse(document).GetObject();
			Assert.Equal(expected, read.Keys);

			// Nested objects are sorted too, so the order of the outer members does not change the text.
			var nested = new SomlObject { ["outer"] = members, ["array"] = new SomlArray { members } };
			Assert.Equal(new SomlObject { ["array"] = new SomlArray { members }, ["outer"] = members }.ToDocumentString(canonical: true), nested.ToDocumentString(canonical: true));
		}
	}

	[Fact]
	public void WritesTheSameTextInEveryCulture()
	{
		var value = new SomlObject
		{
			["int"] = -5L,
			["min"] = long.MinValue,
			["float"] = -1.5e-10,
			["big"] = -1.5e300,
			["fraction"] = -0.5,
			["infinity"] = double.NegativeInfinity,
			["single"] = -0.1f,
			["instant"] = new SomlInstant(-1, 5),
			["duration"] = SomlDuration.Parse("-1h0.5s"),
			["minimum"] = SomlDuration.MinValue,
			["array"] = new SomlArray { -1, -2.5, "-" },
		};

		var expected = value.ToDocumentString();
		var expectedCanonical = value.ToDocumentString(canonical: true);
		var expectedShown = value.ToString();
		var parsedInvariant = SomlValue.Parse(expected);

		foreach (var name in (string[])["sv-SE", "nb-NO", "fi-FI", "fa-IR", "ar-SA", "th-TH", "he-IL", "de-CH", "hi-IN", "tr-TR"])
		{
			InCulture(name, () =>
			{
				Assert.Equal(expected, value.ToDocumentString());
				Assert.Equal(expectedCanonical, value.ToDocumentString(canonical: true));
				Assert.Equal(expectedShown, value.ToString());
				Assert.Equal(parsedInvariant, SomlValue.Parse(expected));
				Assert.Equal("1969-12-31T23:59:59.000000005Z", new SomlInstant(-1, 5).ToString());
				Assert.Equal("-1h0.5s", SomlDuration.Parse("-1h0.5s").ToString());
				Assert.Equal(SomlDuration.Parse("-1h0.5s"), SomlDuration.Parse("-3600.5s"));
				Assert.Equal(new SomlInstant(-1, 5), SomlInstant.Parse("1969-12-31T23:59:59.000000005Z"));
				Assert.Equal("The value 18446744073709551615 is outside the 64-bit range of a SOML int (-9223372036854775808 to 9223372036854775807)", Assert.Throws<OverflowException>(() => (SomlValue)ulong.MaxValue).Message);
				Assert.Equal("ı", SomlValue.Parse("I: 'ı'")["I"].GetString());
				Assert.Throws<KeyNotFoundException>(() => SomlValue.Parse("I: 1")["i"]);
			});
		}
	}

	[Fact]
	public void ShowsValuesWithToString()
	{
		Assert.Equal("'text'", ((SomlValue)"text").ToString());
		Assert.Equal("-5", ((SomlValue)(-5)).ToString());
		Assert.Equal("1.0", ((SomlValue)1.0).ToString());
		Assert.Equal("true", ((SomlValue)true).ToString());
		Assert.Equal("{}", new SomlObject().ToString());
		Assert.Equal("{}\n", new SomlObject().ToDocumentString());
		Assert.Equal("{\n\ta: 1\n\tb: [\n\t\t2\n\t]\n}", new SomlObject { ["a"] = 1, ["b"] = new SomlArray { 2 } }.ToString());
		Assert.Equal((SomlValue)new SomlObject(), SomlValue.Parse("{}"));
		Assert.Equal((SomlValue)new SomlObject { ["a"] = new SomlObject() }, SomlValue.Parse(new SomlObject { ["a"] = new SomlObject() }.ToDocumentString()));
		Assert.Equal((SomlValue)new SomlArray { new SomlArray() }, SomlValue.Parse(new SomlArray { new SomlArray() }.ToDocumentString()));
	}

	[Fact]
	public void RefusesToWriteAContainerThatHoldsItself()
	{
		// The depth limit stops the writer, so a cycle is an error rather than a stack overflow or a hang.
		var array = new SomlArray();
		array.Add(array);
		var members = new SomlObject();
		members["self"] = members;

		Assert.Contains("nested more than 100 levels", Assert.Throws<SomlException>(() => array.ToDocumentString()).Message);
		Assert.Contains("nested more than 100 levels", Assert.Throws<SomlException>(() => members.ToDocumentString(canonical: true)).Message);
		Assert.Throws<SomlException>(() => array.ToString());
	}

	[Fact]
	public void KeepsComposedAndDecomposedKeysApart()
	{
		// No Unicode normalization: U+00E9 and `e` with U+0301 are two keys, and canonical form sorts them by scalar value.
		var composed = ((char)0xE9).ToString();
		var decomposed = "e" + (char)0x301;
		var document = SomlValue.Parse($"'{composed}': 1\n'{decomposed}': 2\nf: 3\n");

		Assert.Equal(3, document.GetObject().Count);
		Assert.Equal(1, document[composed].GetInt64());
		Assert.Equal(2, document[decomposed].GetInt64());
		Assert.NotEqual((SomlValue)composed, (SomlValue)decomposed);
		Assert.Equal($"'{decomposed}': 2\nf: 3\n'{composed}': 1\n", document.ToDocumentString(canonical: true));
	}
}
