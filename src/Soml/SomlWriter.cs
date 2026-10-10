using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Soml;

/*
Writes values as SOML text, a port of `stringify.ts` in the JS reference implementation. The layout is canonical form: one member or item per line, one tab per level, no commas, and a top-level object without braces. With `canonical`, members are sorted by key, and the output is exact canonical form. Without it, members keep their order, as the spec's serializer that writes for people does.
*/
internal static class SomlWriter
{
	private static readonly SearchValues<char> EscapedStringCharacters = SearchValues.Create([.. Enumerable.Range(0, 0x20).Select(code => (char)code), '\\', '"', (char)0x7F, .. Enumerable.Range(0xD800, 0x800).Select(code => (char)code)]);
	private static readonly SearchValues<char> QuotedStringCharacters = SearchValues.Create([.. Enumerable.Range(0, 0x20).Select(code => (char)code), '\'', (char)0x7F]);

	/*
	The characters that keep a string from being written as it is in `'...'`: control characters, `'`, and surrogates.
	*/
	private static readonly SearchValues<char> SpecialStringCharacters = SearchValues.Create([.. Enumerable.Range(0, 0x20).Select(code => (char)code), '\'', (char)0x7F, .. Enumerable.Range(0xD800, 0x800).Select(code => (char)code)]);

	/*
	Writes a whole document, which ends with one line feed.
	*/
	public static string WriteDocument(SomlValue value, bool canonical)
	{
		var builder = new StringBuilder();
		WriteDocument(builder, value, canonical);
		return builder.ToString();
	}

	public static void WriteDocument(StringBuilder builder, SomlValue value, bool canonical)
	{
		var writer = new Writer(builder, canonical, isOneLine: false, isLenient: false, "");

		// An empty object keeps its braces, as `{}`, because an object without braces needs at least one entry.
		if (value.TryGetObject(out var result) && result.Count > 0)
		{
			// The members of the document's own object are one level deeper than it, as in any object, and they start at column 0.
			writer.WriteMembers(result, 2, 0);
		}
		else if (value.Kind is SomlValueKind.Array or SomlValueKind.Object)
		{
			writer.WriteValue(value, 1, 0);
			builder.Append('\n');
		}
		else
		{
			throw new SomlException($"The top-level value must be an object or an array, because a document is a collection, but it is {SomlValue.DescribeKind(value.Kind)}");
		}
	}

	/*
	Writes one value as it is written in a document, with no line feed at the end. A container's own depth is `depth`, and its lines after the first are indented by `indentation`. With `isOneLine`, a container is written on one line, as in `[1, {a: 2}]`. With `isLenient`, for showing a value, a string that SOML cannot represent is written with escapes rather than refused.
	*/
	public static void WriteValue(StringBuilder builder, SomlValue value, bool canonical, int depth = 1, ReadOnlySpan<char> indentation = default, bool isOneLine = false, bool isLenient = false)
	{
		new Writer(builder, canonical, isOneLine, isLenient, indentation).WriteValue(value, depth, 0);
	}

	/*
	Whether a key can be written without quotes: one or more ASCII letters, digits, `_`, or `-`, in any order.
	*/
	public static bool IsBareKey(string key) => key.Length > 0 && !key.AsSpan().ContainsAnyExcept(Parser.BareKeyCharacters);

	/*
	A key as it is written in a document: bare when it can be, and otherwise as a string.
	*/
	public static string FormatKey(string key) => IsBareKey(key) ? key : FormatString(key, "key");

	/*
	A string as it is written in a document: `'...'` when it needs no escapes, and otherwise `"..."`. The kind, “string” or “key”, is for the error message.
	*/
	public static string FormatString(string text, string kind = "string", bool isLenient = false)
	{
		if (IsLiteralString(text, kind, isLenient))
		{
			return string.Concat("'", text, "'");
		}

		var builder = new StringBuilder(text.Length + 2);
		AppendEscapedString(builder, text);
		return builder.ToString();
	}

	/*
	Appends a key as it is written in a document: bare when it can be, and otherwise as a string.
	*/
	public static void AppendKey(StringBuilder builder, string key, bool isLenient = false)
	{
		if (IsBareKey(key))
		{
			builder.Append(key);
		}
		else
		{
			AppendString(builder, key, "key", isLenient);
		}
	}

	/*
	Appends a string as it is written in a document. With `isLenient`, for showing a value, a carriage return or a lone surrogate is written as an escape that SOML does not accept, rather than refused.
	*/
	public static void AppendString(StringBuilder builder, string text, string kind = "string", bool isLenient = false)
	{
		if (IsLiteralString(text, kind, isLenient))
		{
			builder.Append('\'').Append(text).Append('\'');
			return;
		}

		AppendEscapedString(builder, text);
	}

	/*
	Chooses the string's quotes and checks representability once, shared by direct formatting and document writing.
	*/
	private static bool IsLiteralString(string text, string kind, bool isLenient)
	{
		var span = text.AsSpan();
		var firstSpecial = span.IndexOfAny(SpecialStringCharacters);

		// Most strings have none of these characters, so one scan finds that they can be written as they are.
		if (firstSpecial == -1)
		{
			return true;
		}

		// A `'` also needs the `"..."` form, because a `'...'` string cannot hold one.
		var hasControl = !char.IsSurrogate(span[firstSpecial]) || span[firstSpecial..].ContainsAny(QuotedStringCharacters);
		var loneSurrogate = Parser.FindLoneSurrogate(span[firstSpecial..]);

		if (!isLenient)
		{
			if (loneSurrogate != -1)
			{
				throw new SomlException($"Cannot serialize a {kind} containing a lone surrogate, which is not a Unicode scalar value");
			}

			if (span[firstSpecial..].Contains('\r'))
			{
				throw new SomlException($"Cannot serialize a {kind} containing a carriage return (U+000D), which is not representable");
			}
		}

		// Only a surrogate pair, which a literal string holds as it is.
		if (!hasControl && loneSurrogate == -1)
		{
			return true;
		}

		return false;
	}

	private static void AppendEscapedString(StringBuilder builder, string text)
	{
		var span = text.AsSpan();

		builder.Append('"');
		var runStart = 0;

		for (var index = 0; index < span.Length; index++)
		{
			var next = span[index..].IndexOfAny(EscapedStringCharacters);

			if (next == -1)
			{
				break;
			}

			index += next;
			var character = span[index];

			if (char.IsSurrogate(character) && !Message.IsLoneSurrogate(span, index))
			{
				index++;
				continue;
			}

			builder.Append(span[runStart..index]);
			runStart = index + 1;

			switch (character)
			{
				case '\\':
				{
					builder.Append(@"\\");
					break;
				}
				case '"':
				{
					builder.Append("\\\"");
					break;
				}
				case '\n':
				{
					builder.Append(@"\n");
					break;
				}
				case '\t':
				{
					builder.Append(@"\t");
					break;
				}
				case < (char)0x20 or (char)0x7F:
				// A lone surrogate, which only a lenient writer gets this far with, as an escape.
				case >= (char)0xD800 and <= (char)0xDFFF:
				{
					// The digits are lowercase with no leading zeros, which is the only spelling that the parser accepts.
					builder.Append(CultureInfo.InvariantCulture, $"\\u{{{(int)character:x}}}");
					break;
				}
			}
		}

		builder.Append(span[runStart..]).Append('"');
	}

	/*
	A finite `float` or `Half` as the double of its own shortest digits, so 0.1f is 0.1 rather than the double it widens to, 0.10000000149011612. When those digits read back as a double that does not narrow to the same value, the double it widens to is used instead.
	*/
	public static double ToShortestDouble<T>(T value) where T : IBinaryFloatingPointIeee754<T>
	{
		Span<char> buffer = stackalloc char[32];
		value.TryFormat(buffer, out var length, "R", CultureInfo.InvariantCulture);
		var shortest = double.Parse(buffer[..length], CultureInfo.InvariantCulture);
		return T.CreateTruncating(shortest) == value ? shortest : double.CreateTruncating(value);
	}

	/*
	The decimal that a float's shortest digits spell, so 0.1 is 0.1m rather than the binary value of the double. It is `false` for an infinity, for a float outside the range of decimal, and for a nonzero float too small for one, which would parse as zero.
	*/
	public static bool TryConvertToDecimal(double value, out decimal result)
	{
		// Note: Not using decimal.CreateChecked because it rounds to 15 significant digits instead of preserving the float's shortest spelling.
		Span<char> buffer = stackalloc char[32];

		if (!double.IsFinite(value) || !decimal.TryParse(buffer[..FormatFloat(value, buffer)], NumberStyles.Float, CultureInfo.InvariantCulture, out result) || (result == 0 && value != 0))
		{
			result = 0;
			return false;
		}

		return true;
	}

	/*
	A float as canonical form writes it: the shortest decimal that reads back as the same binary64 value, laid out as ECMAScript's `Number::toString` does, with `.0` added when there is neither a fraction nor an exponent, and with no `+` in an exponent.
	*/
	public static string FormatFloat(double value)
	{
		// Negative zero is equal to zero too, so both are written `0.0`, as the spec requires.
		if (FloatLiteral(value) is { } literal)
		{
			return literal;
		}

		Span<char> buffer = stackalloc char[32];
		return new string(buffer[..FormatFloat(value, buffer)]);
	}

	public static void AppendFloat(StringBuilder builder, double value)
	{
		Span<char> buffer = stackalloc char[32];
		builder.Append(buffer[..FormatFloat(value, buffer)]);
	}

	/*
	Writes the canonical float spelling to a fixed-size span. A binary64 value needs at most 25 characters, including its sign, decimal point, and exponent.
	*/
	private static int FormatFloat(double value, Span<char> destination)
	{
		if (FloatLiteral(value) is { } literal)
		{
			literal.CopyTo(destination);
			return literal.Length;
		}

		// The value is 0.d₁d₂… × 10^n, as the spec's steps state it, where s is the k digits without leading and trailing zeros.
		Span<char> buffer = stackalloc char[32];
		var (k, n) = ShortestDigits(Math.Abs(value), buffer);
		var s = buffer[..k];
		var length = 0;

		if (value < 0)
		{
			destination[length++] = '-';
		}

		if (k <= n && n <= 21)
		{
			s.CopyTo(destination[length..]);
			length += k;
			destination.Slice(length, n - k).Fill('0');
			length += n - k;
			".0".CopyTo(destination[length..]);
			length += 2;
		}
		else if (0 < n && n < k)
		{
			s[..n].CopyTo(destination[length..]);
			length += n;
			destination[length++] = '.';
			s[n..].CopyTo(destination[length..]);
			length += k - n;
		}
		else if (-6 < n && n <= 0)
		{
			"0.".CopyTo(destination[length..]);
			length += 2;
			destination.Slice(length, -n).Fill('0');
			length -= n;
			s.CopyTo(destination[length..]);
			length += k;
		}
		else
		{
			destination[length++] = s[0];

			if (k > 1)
			{
				destination[length++] = '.';
				s[1..].CopyTo(destination[length..]);
				length += k - 1;
			}

			destination[length++] = 'e';
			(n - 1).TryFormat(destination[length..], out var written, default, CultureInfo.InvariantCulture);
			length += written;
		}

		return length;
	}

	/*
	The values whose canonical spelling needs no digit conversion. Negative zero is equal to zero too, so both are written `0.0`, as the spec requires.
	*/
	private static string? FloatLiteral(double value) => value switch
	{
		double.PositiveInfinity => "infinity",
		double.NegativeInfinity => "-infinity",
		0 => "0.0",
		_ => null,
	};

	/*
	Appends an integer in the invariant culture.

	Note: Not using StringBuilder.Append(long) because it uses the current culture, whose minus sign is U+2212 in Swedish, Norwegian, and Finnish, which SOML does not accept.
	*/
	private static void AppendInteger(StringBuilder builder, long value)
	{
		// Note: Not using invariant interpolated StringBuilder.Append because actual writer probes added per-value allocations with default tiering on .NET 10 ARM64, despite a faster isolated probe.
		// 20 characters hold the longest long, `-9223372036854775808`.
		Span<char> buffer = stackalloc char[20];
		value.TryFormat(buffer, out var length, default, CultureInfo.InvariantCulture);
		builder.Append(buffer[..length]);
	}

	/*
	The shortest digits that read back as the value, closest to it, as the spec's step 2 states: the digits go into `digits`, and the result is their count k and the exponent n, so the value is 0.d₁d₂… × 10^n.

	.NET's `R` format gives these digits in its own layout, such as `1E+21` or `1.5E-07`, so they are read from it.

	Note: Not using direct double.TryFormat output with `R` because some powers of two, whose lower neighbor is closer than their upper one, get digits that do not read back as the same value, such as `4.104536801298376E-289` for 2^-958.

	Then the closest digits of each length are tried, from that length up, along with the digits on the other side of the value, which can read back when the closest do not.
	*/
	private static (int Count, int Exponent) ShortestDigits(double magnitude, Span<char> digits)
	{
		Span<char> text = stackalloc char[32];
		// The invariant culture is used because `ReadDigits` expects `.` as the decimal point, and another culture can write a `,` or a U+2212 minus in the exponent.
		magnitude.TryFormat(text, out var length, "R", CultureInfo.InvariantCulture);
		var shortest = ReadDigits(text[..length], digits);

		if (ParseDigits(digits[..shortest.Count], shortest.Exponent) == magnitude)
		{
			return shortest;
		}

		Span<char> other = stackalloc char[18];

		for (var precision = shortest.Count; precision <= 17; precision++)
		{
			magnitude.TryFormat(text, out length, precision switch
			{
				1 => "E0",
				2 => "E1",
				3 => "E2",
				4 => "E3",
				5 => "E4",
				6 => "E5",
				7 => "E6",
				8 => "E7",
				9 => "E8",
				10 => "E9",
				11 => "E10",
				12 => "E11",
				13 => "E12",
				14 => "E13",
				15 => "E14",
				16 => "E15",
				_ => "E16",
			}, CultureInfo.InvariantCulture);
			var closest = ReadDigits(text[..length], digits);
			var closestValue = ParseDigits(digits[..closest.Count], closest.Exponent);

			if (closestValue == magnitude)
			{
				return closest;
			}

			// The digits on the other side of the value, one unit away in the last digit.
			digits[..closest.Count].CopyTo(other);
			other[closest.Count..precision].Fill('0');
			var otherExponent = closest.Exponent;

			if (closestValue < magnitude)
			{
				var index = precision - 1;

				while (index >= 0 && other[index] == '9')
				{
					other[index--] = '0';
				}

				if (index < 0)
				{
					other[0] = '1';
					otherExponent++;
				}
				else
				{
					other[index]++;
				}
			}
			else
			{
				var index = precision - 1;

				while (other[index] == '0')
				{
					other[index--] = '9';
				}

				other[index]--;

				if (other[0] == '0')
				{
					other[1..precision].CopyTo(other);
					other[precision - 1] = '9';
					otherExponent--;
				}
			}

			if (ParseDigits(other[..precision], otherExponent) == magnitude)
			{
				var trimmed = other[..precision].TrimEnd('0');
				trimmed.CopyTo(digits);
				return (trimmed.Length, otherExponent);
			}
		}

		// Seventeen digits always read back, so this is not reached.
		throw new InvalidOperationException($"No digits read back as {magnitude:R}");
	}

	/*
	Reads the digits of a number that .NET wrote, such as `123.45` or `1.5E-07`, into `digits`, without leading and trailing zeros. Returns their count k and the exponent n, so the value is 0.d₁d₂… × 10^n.

	Note: Not using span copies and TrimEnd in ReadDigits because they improved an isolated probe but not complete writer throughput on .NET 10 ARM64.
	*/
	private static (int Count, int Exponent) ReadDigits(ReadOnlySpan<char> text, Span<char> digits)
	{
		var exponentIndex = text.IndexOf('E');
		var exponent = exponentIndex == -1 ? 0 : int.Parse(text[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
		var mantissa = exponentIndex == -1 ? text : text[..exponentIndex];
		var pointIndex = mantissa.IndexOf('.');
		var n = (pointIndex == -1 ? mantissa.Length : pointIndex) + exponent;
		var count = 0;

		foreach (var character in mantissa)
		{
			if (character == '.')
			{
				continue;
			}

			if (count == 0 && character == '0')
			{
				n--;
				continue;
			}

			digits[count++] = character;
		}

		return (digits[..count].TrimEnd('0').Length, n);
	}

	private static double ParseDigits(ReadOnlySpan<char> digits, int exponent)
	{
		Span<char> text = stackalloc char[34];
		text[0] = '0';
		text[1] = '.';
		digits.CopyTo(text[2..]);
		var length = digits.Length + 2;
		text[length++] = 'E';
		exponent.TryFormat(text[length..], out var written, default, CultureInfo.InvariantCulture);
		return double.Parse(text[..(length + written)], NumberStyles.Float, CultureInfo.InvariantCulture);
	}

	/*
	Canonical key order: by Unicode scalar value. Ordinal comparison of UTF-16 code units puts U+E000 to U+FFFF after every character above U+FFFF, so a surrogate is moved past them when two differing code units are both from U+D800 up.

	Note: Not using EnumerateRunes for key comparison because it decoded common prefixes instead of skipping them and was much slower in these .NET 10 ARM64 comparisons.
	*/
	public static int CompareKeys(string left, string right)
	{
		var length = Math.Min(left.Length, right.Length);
		var mismatch = left.AsSpan(0, length).CommonPrefixLength(right.AsSpan(0, length));

		if (mismatch == length)
		{
			return left.Length - right.Length;
		}

		int leftCode = left[mismatch];
		int rightCode = right[mismatch];

		if (leftCode >= 0xD800 && rightCode >= 0xD800)
		{
			leftCode = ToCodePointOrder(leftCode);
			rightCode = ToCodePointOrder(rightCode);
		}

		return leftCode - rightCode;
	}

	private static int ToCodePointOrder(int code)
	{
		if (code >= 0xE000)
		{
			return code - 0x800;
		}

		return code + 0x2000;
	}

	/*
	The writer of one document or value. It keeps the path to the value it is writing, for error messages, as keys and indexes that are only turned into a path when there is an error.
	*/
	private ref struct Writer(StringBuilder builder, bool canonical, bool isOneLine, bool isLenient, ReadOnlySpan<char> baseIndentation)
	{
		private readonly ReadOnlySpan<char> _baseIndentation = baseIndentation;
		private readonly List<(string? Key, int Index)> _path = [];

		private SomlException Error(string message)
		{
			return new SomlException(message, CollectionsMarshal.AsSpan(_path));
		}

		/*
		A value deeper than the limit cannot be read back, so it is refused. This also stops a container that holds itself.
		*/
		private void CheckDepth(int depth)
		{
			if (depth > Parser.MaxDepth)
			{
				throw Error($"Cannot serialize a value nested more than {Parser.MaxDepth} levels deep");
			}
		}

		/*
		Starts a line at `level` tabs past the base indentation, or separates items on one line.
		*/
		private void StartItem(int index, int level)
		{
			if (isOneLine)
			{
				if (index > 0)
				{
					builder.Append(", ");
				}

				return;
			}

			builder.Append(_baseIndentation).Append('\t', level);
		}

		/*
		Writes a value. A container's own depth is `depth`, and its closing bracket is indented by `level` tabs past the base indentation.
		*/
		public void WriteValue(SomlValue value, int depth, int level)
		{
			switch (value.Kind)
			{
				case SomlValueKind.Null:
				{
					builder.Append("null");
					break;
				}
				case SomlValueKind.String:
				{
					try
					{
						AppendString(builder, value.GetString(), isLenient: isLenient);
					}
					catch (SomlException exception)
					{
						throw Error(exception.Message);
					}

					break;
				}
				case SomlValueKind.Integer:
				{
					AppendInteger(builder, value.GetInt64());
					break;
				}
				case SomlValueKind.Float:
				{
					AppendFloat(builder, value.GetDouble());
					break;
				}
				case SomlValueKind.Boolean:
				{
					builder.Append(value.GetBoolean() ? "true" : "false");
					break;
				}
				case SomlValueKind.Date:
				{
					Span<char> buffer = stackalloc char[10];
					value.GetDate().TryFormat(buffer, out var length, "O", CultureInfo.InvariantCulture);
					builder.Append(buffer[..length]);
					break;
				}
				case SomlValueKind.Instant:
				{
					Span<char> buffer = stackalloc char[32];
					value.GetInstant().TryFormat(buffer, out var length);
					builder.Append(buffer[..length]);
					break;
				}
				case SomlValueKind.Duration:
				{
					Span<char> buffer = stackalloc char[40];
					value.GetDuration().TryFormat(buffer, out var length);
					builder.Append(buffer[..length]);
					break;
				}
				case SomlValueKind.Array:
				{
					WriteArray(value.GetArray(), depth, level);
					break;
				}
				case SomlValueKind.Object:
				{
					WriteObject(value.GetObject(), depth, level);
					break;
				}
			}
		}

		private void WriteArray(SomlArray array, int depth, int level)
		{
			CheckDepth(depth);

			if (array.Count == 0)
			{
				builder.Append("[]");
				return;
			}

			builder.Append(isOneLine ? "[" : "[\n");
			var items = array.AsSpan();

			for (var index = 0; index < items.Length; index++)
			{
				_path.Add((null, index));
				StartItem(index, level + 1);
				WriteValue(items[index], depth + 1, level + 1);

				if (!isOneLine)
				{
					builder.Append('\n');
				}

				_path.RemoveAt(_path.Count - 1);
			}

			CloseContainer(']', level);
		}

		private void WriteObject(SomlObject result, int depth, int level)
		{
			CheckDepth(depth);

			if (result.Count == 0)
			{
				builder.Append("{}");
				return;
			}

			builder.Append(isOneLine ? "{" : "{\n");
			WriteMembers(result, depth + 1, level + 1);
			CloseContainer('}', level);
		}

		private void CloseContainer(char closing, int level)
		{
			if (!isOneLine)
			{
				builder.Append(_baseIndentation).Append('\t', level);
			}

			builder.Append(closing);
		}

		/*
		Writes the members of an object, whose values have the depth `depth`, at `level` tabs past the base indentation.
		*/
		public void WriteMembers(SomlObject result, int depth, int level)
		{
			var members = result.AsSpan();
			KeyValuePair<string, SomlValue>[]? rented = null;

			try
			{
				if (canonical)
				{
					// Note: Not using OrderBy for canonical members because it added materialization and sort-index allocations even for already-sorted input.
					for (var index = 1; index < members.Length; index++)
					{
						if (CompareKeys(members[index - 1].Key, members[index].Key) > 0)
						{
							rented = ArrayPool<KeyValuePair<string, SomlValue>>.Shared.Rent(members.Length);
							var sorted = rented.AsSpan(0, members.Length);
							members.CopyTo(sorted);
							sorted.Sort(MemberComparison);
							members = sorted;
							break;
						}
					}
				}

				for (var index = 0; index < members.Length; index++)
				{
					var (key, value) = members[index];
					_path.Add((key, index));
					StartItem(index, level);

					try
					{
						AppendKey(builder, key, isLenient);
					}
					catch (SomlException exception)
					{
						throw Error(exception.Message);
					}

					builder.Append(": ");
					WriteValue(value, depth, level);

					if (!isOneLine)
					{
						builder.Append('\n');
					}

					_path.RemoveAt(_path.Count - 1);
				}
			}
			finally
			{
				if (rented is not null)
				{
					ArrayPool<KeyValuePair<string, SomlValue>>.Shared.Return(rented, clearArray: true);
				}
			}
		}
	}

	private static readonly Comparison<KeyValuePair<string, SomlValue>> MemberComparison = (left, right) => CompareKeys(left.Key, right.Key);

	/*
	A path as an error message shows it, such as `servers[0].port` or `labels.'the name'`, or `null` for the top level.
	*/
	public static string? FormatPath(IReadOnlyList<object> path)
	{
		if (path.Count == 0)
		{
			return null;
		}

		var builder = new StringBuilder();

		for (var position = 0; position < path.Count && builder.Length <= 200; position++)
		{
			var segment = path[position];

			if (segment is int index)
			{
				builder.Append(CultureInfo.InvariantCulture, $"[{index}]");
			}
			else
			{
				var key = (string)segment;

				if (builder.Length > 0)
				{
					builder.Append('.');
				}

				if (IsBareKey(key))
				{
					builder.Append(key.AsSpan(0, Math.Min(key.Length, 201 - builder.Length)));
				}
				else
				{
					builder.Append('\'').Append(Message.DescribeText(Message.Abbreviate(key))).Append('\'');
				}
			}
		}

		// A path from a cycle is a hundred segments long, so it is cut short.
		return Message.Abbreviate(builder.ToString(), 200);
	}
}
