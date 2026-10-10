using System.Globalization;
using System.Text.RegularExpressions;

namespace Soml;

/*
The messages for values that are not valid, ported from the JS reference implementation. The regular expressions only ever see a bounded part of the input. They use `[0-9]` rather than `\d`, which matches other digits in .NET, and `\z` rather than `$`, which matches before a final line feed in .NET.

Note: Not using RegexOptions.NonBacktracking because it falls back to runtime Regex initialization instead of specialized generated matchers, and these diagnostic inputs are already bounded.
*/
internal ref partial struct Parser
{
	private const string InstantFormat = "An instant is written as 2026-09-19T14:00:00Z, with an optional fraction of up to nine digits and an offset of Z or ±HH:MM";

	private static readonly System.Buffers.SearchValues<char> HexadecimalDigitCharacters = System.Buffers.SearchValues.Create("0123456789abcdefABCDEF_");
	private static readonly System.Buffers.SearchValues<char> OctalDigitCharacters = System.Buffers.SearchValues.Create("01234567_");
	private static readonly System.Buffers.SearchValues<char> BinaryDigitCharacters = System.Buffers.SearchValues.Create("01_");
	private static readonly System.Buffers.SearchValues<char> HexadecimalEscapeCharacters = System.Buffers.SearchValues.Create("0123456789ABCDEFabcdef");
	private static readonly System.Buffers.SearchValues<char> LowercaseHexadecimalEscapeCharacters = System.Buffers.SearchValues.Create("0123456789abcdef");
	private static readonly System.Buffers.SearchValues<char> OctalCharacters = System.Buffers.SearchValues.Create("01234567");
	private static readonly System.Buffers.SearchValues<char> NumberCharacters = System.Buffers.SearchValues.Create("0123456789-._e");

	private readonly record struct InstantParts(int Year, int Month, int Day, int Hour, int Minute, int Second, int FractionLength, int Nanosecond, bool HasOffset, bool IsNegativeOffset, int OffsetHour, int OffsetMinute)
	{
		/*
		Checks the ranges in diagnostic order and converts the matched parts to UTC. Shared by document and span time parsing.
		*/
		public string? Validate(out SomlInstant value)
		{
			value = default;
			var reason = FractionLength > 9 ? "a fractional second has at most nine digits" : DateProblem(Year, Month, Day);

			if (reason is not null)
			{
				return reason;
			}

			reason = this switch
			{
				{ Hour: > 23 } => "the hour must be 00 to 23",
				{ Minute: > 59 } => "the minute must be 00 to 59",
				{ Second: > 59 } => "the second must be 00 to 59, and a leap second is not representable",
				{ HasOffset: true, OffsetHour: > 23 } => "the offset hour must be 00 to 23",
				{ HasOffset: true, OffsetMinute: > 59 } => "the offset minute must be 00 to 59",
				{ HasOffset: true, IsNegativeOffset: true, OffsetHour: 0, OffsetMinute: 0 } => "-00:00 means “offset unknown” in RFC 3339, which is not representable; use Z or +00:00",
				_ => null,
			};

			if (reason is not null)
			{
				return reason;
			}

			var offsetMinutes = ((OffsetHour * 60) + OffsetMinute) * (IsNegativeOffset ? -1 : 1);
			var instant = SomlInstant.FromComponents(Year, Month, Day, Hour, Minute, Second, Nanosecond, offsetMinutes);

			if (instant is null)
			{
				return "in UTC it falls outside the years 0001 to 9999";
			}

			value = instant.Value;
			return null;
		}
	}

	/*
	`^\d{4}-\d{2}-\d{2}`, which starts a date or an instant.
	*/
	private static bool HasDatePrefix(ReadOnlySpan<char> text) => text.Length >= 10 && IsDigits(text, 0, 4) && text[4] == '-' && IsDigits(text, 5, 2) && text[7] == '-' && IsDigits(text, 8, 2);

	private static bool IsDigits(ReadOnlySpan<char> text, int start, int length)
	{
		if (start + length > text.Length)
		{
			return false;
		}

		// Note: Not using ContainsAnyExceptInRange because it allocates in Tier0 on .NET 10 ARM64.
		foreach (var digit in text.Slice(start, length))
		{
			if (!IsDigit(digit))
			{
				return false;
			}
		}

		return true;
	}

	private static int ReadDigits(ReadOnlySpan<char> text, int start, int length)
	{
		// Note: Not using int.Parse because these two/four ASCII digits are already validated and it repeated work and was slower on .NET 10 ARM64.
		var value = 0;

		foreach (var digit in text.Slice(start, length))
		{
			value = (value * 10) + digit - '0';
		}

		return value;
	}

	/*
	Matches `\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})` against the whole token, after checking the date prefix, without checking the ranges.
	*/
	private static bool MatchInstant(ReadOnlySpan<char> text, out InstantParts parts)
	{
		parts = default;

		if (text.Length < 20 || text[10] != 'T' || !IsDigits(text, 11, 2) || text[13] != ':' || !IsDigits(text, 14, 2) || text[16] != ':' || !IsDigits(text, 17, 2))
		{
			return false;
		}

		var index = 19;
		var fractionLength = 0;
		var nanosecond = 0;

		if (text[index] == '.')
		{
			index++;

			while (index < text.Length && IsDigit(text[index]))
			{
				// Only the first nine digits are kept, so the int cannot overflow. A longer fraction is an error, which `ParseInstant` reports from `FractionLength`.
				if (fractionLength < 9)
				{
					nanosecond = (nanosecond * 10) + (text[index] - '0');
				}

				fractionLength++;
				index++;
			}

			if (fractionLength == 0)
			{
				return false;
			}

			for (var digit = fractionLength; digit < 9; digit++)
			{
				nanosecond *= 10;
			}
		}

		var hasOffset = false;
		var isNegativeOffset = false;
		var offsetHour = 0;
		var offsetMinute = 0;

		if (index == text.Length - 1 && text[index] == 'Z')
		{
			// UTC.
		}
		else if (index == text.Length - 6 && text[index] is '+' or '-' && IsDigits(text, index + 1, 2) && text[index + 3] == ':' && IsDigits(text, index + 4, 2))
		{
			hasOffset = true;
			isNegativeOffset = text[index] == '-';
			offsetHour = ReadDigits(text, index + 1, 2);
			offsetMinute = ReadDigits(text, index + 4, 2);
		}
		else
		{
			return false;
		}

		parts = new InstantParts(
			ReadDigits(text, 0, 4),
			ReadDigits(text, 5, 2),
			ReadDigits(text, 8, 2),
			ReadDigits(text, 11, 2),
			ReadDigits(text, 14, 2),
			ReadDigits(text, 17, 2),
			fractionLength,
			nanosecond,
			hasOffset,
			isNegativeOffset,
			offsetHour,
			offsetMinute);

		return true;
	}

	/*
	The name of a TOML table header on its line, as in `[server]` or `[[servers]]`, or `null`. The name is a valid key, so that the suggestion is valid.
	*/
	private static string? MatchTableHeader(ReadOnlySpan<char> line)
	{
		line = line.Trim(" \t");

		if (line.Length < 3 || line[0] != '[' || line[^1] != ']')
		{
			return null;
		}

		line = line[1..^1];

		if (line.StartsWith('['))
		{
			line = line[1..];
		}

		if (line.EndsWith(']'))
		{
			line = line[..^1];
		}

		return !line.IsEmpty && (IsLetter(line[0]) || line[0] == '_') && !line.ContainsAnyExcept(BareKeyCharacters) ? line.ToString() : null;
	}

	/*
	A comma used as a thousands separator, as in `[1,000]`, makes the group after it a separate item, which fails only when it has a leading zero. Writing that item in octal, as the leading zero message suggests, would parse to the wrong items.
	*/
	private string? DescribeThousandsSeparator(ReadOnlySpan<char> text, int start)
	{
		if (!(text.Length == 3 && text[0] == '0' && IsDigit(text[1]) && IsDigit(text[2])) || Code(start - 1) != ',')
		{
			return null;
		}

		static int GetGroupStart(string source, int groupEnd)
		{
			var groupStart = groupEnd;

			while (IsDigit(CodeAt(source, groupStart - 1)))
			{
				groupStart--;
			}

			return groupStart;
		}

		var groupEnd = start - 1;
		var numberStart = GetGroupStart(_source, groupEnd);

		// Earlier groups of three digits, as in `12,345,000`.
		while (groupEnd - numberStart == 3 && Code(numberStart - 1) == ',' && IsDigit(Code(numberStart - 2)))
		{
			groupEnd = numberStart - 1;
			numberStart = GetGroupStart(_source, groupEnd);
		}

		var before = Code(numberStart - 1);

		// The first group has one to three digits, which are not the end of a float, of a number with underscores, or of a number with a radix prefix.
		if (numberStart == groupEnd || before == '.' || before == '_' || numberStart < groupEnd - 3 || IsLetter(before))
		{
			return null;
		}

		// The sign belongs to the number, as in `-1,000`.
		if (before == '-')
		{
			numberStart--;
		}

		var groupsStart = start + text.Length;
		var groupsEnd = groupsStart;

		// `^(?:,\d{3}(?!\d))*`, within the diagnosed length.
		while (groupsEnd + 4 <= Math.Min(_source.Length, groupsStart + MaxDiagnosedLength) && Code(groupsEnd) == ',' && IsDigit(Code(groupsEnd + 1)) && IsDigit(Code(groupsEnd + 2)) && IsDigit(Code(groupsEnd + 3)) && !IsDigit(Code(groupsEnd + 4)))
		{
			groupsEnd += 4;
		}

		var number = Message.Abbreviate(_source.AsSpan(numberStart, groupsEnd - numberStart), MaxDiagnosedLength + 1);
		var reason = $"Leading zeros are not allowed in a decimal number. A comma separates items, so {Message.Abbreviate(number)} is not one number";
		// Both spellings are the same int, so they are suggested only when it is in range.
		var withUnderscores = number.Replace(',', '_');
		return IsValidValue(withUnderscores) ? $"{reason}. Write it as {Message.Abbreviate(withUnderscores)} or {Message.Abbreviate(number.Replace(",", "", StringComparison.Ordinal))}" : reason;
	}

	private static string DescribeBadDurationUnit(string unit, string text)
	{
		if (unit.Length == 0)
		{
			return "every number needs a unit: h, m, s, ms, us, or ns";
		}

		if (unit is "d" or "day" or "days")
		{
			return "there is no day unit, because a day is not a fixed length. Write 24h for a fixed 24 hours";
		}

		if (unit is "w" or "week" or "weeks")
		{
			return "there is no week unit, because a day is not a fixed length. Write 168h for a fixed 168 hours";
		}

		var lowercase = unit.ToLowerInvariant();

		if (lowercase is "y" or "yr" or "yrs" or "year" or "years")
		{
			return "there is no year unit, because a year is not a fixed length. Write 8760h for a fixed 365 days";
		}

		if (unit == "M")
		{
			// A number with `M` alone is more often a size, as in `memory: 512M`, and `512m` would read as minutes.
			var sizeHint = text.Length <= MaxDiagnosedLength && SizeRegex().IsMatch(text) ? $". A size, such as 512M, is a string: '{Message.Abbreviate(text)}'" : "";
			return $"there is no month unit, because a month is not a fixed length{sizeHint}";
		}

		return DurationUnitSize(lowercase) != 0 ? $"the units are lowercase: {lowercase}" : $"“{Message.Abbreviate(unit, 10)}” is not a unit. The units are h, m, s, ms, us, and ns, and a string must be quoted";
	}

	[GeneratedRegex(@"^[0-9][0-9_]*M\z")]
	private static partial Regex SizeRegex();

	[GeneratedRegex(@"^(?:y|yrs?|years?)(?:\z|[0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex YearUnitPrefixRegex();

	/*
	`text` is the unquoted string that `word` begins, which the suggestion quotes.
	*/
	private static string DescribeUnknownWord(string word, string text)
	{
		// A keyword hint is only for the word on its own. In `Yes please` or `Nan Goldin`, following it would leave text behind or change the value.
		// Note: Not using repeated OrdinalIgnoreCase tests because avoiding lowercasing made this cold diagnostic switch much more verbose.
		var lowercase = text == word ? word.ToLowerInvariant() : "";

		return lowercase switch
		{
			"true" or "false" or "yes" or "no" or "on" or "off" => $"“{word}” is not a value. Booleans are written true and false, in lowercase",
			"null" or "nil" or "none" or "undefined" => $"“{word}” is not a value. Null is written null, in lowercase",
			"nan" => "NaN is not representable. Use null for a missing value",
			"inf" or "infinity" => $"“{word}” is not a value. Infinity is written infinity, in lowercase",
			_ => $"Unexpected “{Message.Abbreviate(word)}”. A string value must be quoted{QuotingExample(text)}",
		};
	}

	private static string DescribeBadUnicodeEscape(ReadOnlySpan<char> rest)
	{
		if (rest.StartsWith('u') && rest.Length >= 5 && !rest.Slice(1, 4).ContainsAnyExcept(HexadecimalEscapeCharacters))
		{
			return DescribeFourDigitEscape(rest);
		}

		if (UppercaseEscapeRegex().IsMatch(rest))
		{
			return "A Unicode escape uses lowercase hexadecimal digits";
		}

		if (rest.StartsWith("u{}", StringComparison.Ordinal))
		{
			return "A Unicode escape needs one to six hexadecimal digits";
		}

		return rest.StartsWith("u{", StringComparison.Ordinal) && rest.Length >= 9 && !rest.Slice(2, 7).ContainsAnyExcept(LowercaseHexadecimalEscapeCharacters) ? "A Unicode escape has at most six hexadecimal digits" : "A Unicode escape is written \\u{…} with one to six lowercase hexadecimal digits";
	}

	// Note: Not using span searches for uppercase escapes because they added prefix handling without consistently improving public diagnostics on .NET 10 ARM64.
	[GeneratedRegex("^u\\{[0-9A-Fa-f]*[A-F]")]
	private static partial Regex UppercaseEscapeRegex();

	/*
	The JSON form `\uXXXX`, from its `u`, with the escape to write instead. JSON writes a character above U+FFFF as two of them, a surrogate pair, which is one `\u{…}` escape here. Here, a lone surrogate and a carriage return have no escape.
	*/
	private static string DescribeFourDigitEscape(ReadOnlySpan<char> rest)
	{
		var code = int.Parse(rest.Slice(1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
		var low = rest.Length >= 11 && rest[5] == '\\' && rest[6] == 'u' && !rest[7..11].ContainsAnyExcept(HexadecimalEscapeCharacters) ? rest[7..11] : default;
		var lowCode = low.IsEmpty ? -1 : int.Parse(low, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

		if (code is >= 0xD800 and <= 0xDBFF && lowCode is >= 0xDC00 and <= 0xDFFF)
		{
			// Note: Not using char.ConvertToUtf32 because it repeats the established range checks and was slower for validated pairs in .NET 10 ARM64 probes.
			var codePoint = 0x10000 + ((code - 0xD800) * 0x400) + (lowCode - 0xDC00);
			return $"The four-digit \\{rest[..5]}\\u{low} form is not an escape. Write \\u{{{codePoint:x}}}";
		}

		var form = $"The four-digit \\{rest[..5]} form is not an escape";

		if (code is >= 0xD800 and <= 0xDFFF)
		{
			return $"{form}, and a lone surrogate is not a Unicode scalar value. Write the character it is half of as one \\u{{…}} escape";
		}

		return code == 0x0D ? $"{form}, and a carriage return (U+000D) cannot be represented" : $"{form}. Write \\u{{{code:x}}}";
	}

	/*
	`unquotedText` is the unquoted string that `fullText` begins, for the suggestion to quote it.
	*/
	private static string DescribeBadNumber(ReadOnlySpan<char> fullText, string unquotedText)
	{
		if (fullText.Length > MaxDiagnosedLength)
		{
			return $"Invalid number “{Message.Abbreviate(fullText)}”";
		}

		var text = fullText;

		if (text.Contains('+'))
		{
			return "A “+” sign is not allowed in a number, including in an exponent";
		}

		var unsigned = text.StartsWith('-') ? text[1..] : text;

		if (unsigned is ['0', 'B' or 'O' or 'X', ..])
		{
			return "A number prefix is lowercase: 0x, 0o, or 0b";
		}

		// The whole number, not the part cut at the diagnosed length, because a cut can end before the digit or the underscore that the message is about.
		var isNegativeRadix = fullText.StartsWith("-0", StringComparison.Ordinal) && fullText.Length > 2 && fullText[2] is 'b' or 'o' or 'x';

		if (isNegativeRadix || (fullText.StartsWith('0') && fullText.Length > 1 && fullText[1] is 'b' or 'o' or 'x'))
		{
			var radix = fullText[isNegativeRadix ? 2 : 1];
			var digits = fullText[(isNegativeRadix ? 3 : 2)..];
			var name = radix switch
			{
				'x' => "hexadecimal",
				'o' => "octal",
				_ => "binary",
			};

			if (isNegativeRadix)
			{
				return $"{(radix == 'o' ? "An" : "A")} {name} integer cannot have a sign, because it states a bit pattern rather than a quantity";
			}

			if (digits.Length == 0)
			{
				return $"Expected {name} digits after “0{radix}”";
			}

			if (radix == 'x' && digits.IndexOfAnyInRange('a', 'f') != -1 && !digits.ContainsAnyExcept(HexadecimalDigitCharacters))
			{
				// The uppercase spelling is only suggested when it is valid, so a misplaced underscore is reported first, and a value outside the 64-bit range gets no example.
				if (!HexDigitsRegex().IsMatch(digits))
				{
					return "An underscore in a number must be between two digits";
				}

				var uppercase = $"0x{digits.ToString().ToUpperInvariant()}";
				return IsValidValue(uppercase) ? $"Hexadecimal digits are uppercase: {Message.Abbreviate(uppercase)}" : "Hexadecimal digits are uppercase";
			}

			// A lowercase hexadecimal digit is a digit in the wrong case, so the one named is a character that is no digit in either case.
			var validCharacters = radix switch
			{
				'x' => HexadecimalDigitCharacters,
				'o' => OctalDigitCharacters,
				_ => BinaryDigitCharacters,
			};

			var invalid = digits.IndexOfAnyExcept(validCharacters);
			return invalid == -1 ? "An underscore in a number must be between two digits" : $"Invalid {name} digit “{digits[invalid]}”";
		}

		// Only when the “E” is where an exponent marker goes, with digits, a `-`, or nothing after it. In `5EUR` or `10EB`, it is part of a word, so lowercasing it would not help.
		if (UppercaseExponentRegex().IsMatch(text))
		{
			return "An exponent marker is a lowercase “e”";
		}

		if (TrailingDotRegex().IsMatch(text))
		{
			return "A decimal point must be followed by a digit";
		}

		if (text.StartsWith("-.", StringComparison.Ordinal))
		{
			return "A number cannot begin with “.”; write a digit before it, as in -0.5";
		}

		if (ColonNumberRegex().IsMatch(text))
		{
			return TimeOfDayRegex().IsMatch(text) ? "A time of day is a string, so it must be quoted" : $"Invalid number “{Message.Abbreviate(text)}”. A value that contains “:” must be quoted, as in '{Message.Abbreviate(text)}'";
		}

		// A value with a `:` that starts with a zero, such as the MAC address `00:1A:2B`, is not a number with a leading zero, because removing the zero would not make it valid.
		if (unsigned.Length >= 2 && unsigned[0] == '0' && (IsDigit(unsigned[1]) || unsigned[1] == '_') && !text.Contains(':'))
		{
			// Removing the zero gives a valid number with a different meaning, so the message says what the zero usually meant. A `'...'` string cannot hold a `'`, so then there is no example.
			var identifier = $"an identifier, such as a ZIP code, as a string{(unquotedText.Contains('\'', StringComparison.Ordinal) ? "" : $": '{Message.Abbreviate(unquotedText)}'")}";

			// The octal suggestion is only for the number on its own, not for one that more text follows, as in `0412 345 678`, and only when it is in range. It is decided on the whole number, because a cut can end before a digit that is not octal or that changes the value.
			var digits = fullText.TrimStart('0');
			var octal = $"0o{(digits.IsEmpty ? "0".AsSpan() : digits)}";

			if (fullText.SequenceEqual(unquotedText) && fullText.Length > 1 && fullText[0] == '0' && !fullText[1..].ContainsAnyExcept(OctalCharacters) && IsValidValue(octal))
			{
				return $"Leading zeros are not allowed in a decimal number. Write an octal number, such as a file mode, as {Message.Abbreviate(octal)}, and {identifier}";
			}

			return text.Length > 1 && text[0] == '0' && IsDigits(text, 1, text.Length - 1) ? $"Leading zeros are not allowed in a decimal number. Write {identifier}" : "Leading zeros are not allowed in a decimal number";
		}

		// Only in a number: an underscore next to a letter other than the exponent marker is part of a word, such as `4k_video`.
		if (MisplacedUnderscoreRegex().IsMatch(text) && !text.ContainsAnyExcept(NumberCharacters))
		{
			return "An underscore in a number must be between two digits";
		}

		if (ExponentLeadingZeroRegex().IsMatch(text))
		{
			return "Leading zeros are not allowed in an exponent";
		}

		if (NegativeZeroExponentRegex().IsMatch(text))
		{
			return "“e-0” is not allowed, because an exponent of zero has one spelling: e0";
		}

		// Only a number ends with its exponent marker. A word that ends with an “e”, such as `1byte` or `-verbose`, has no exponent.
		if (MissingExponentRegex().IsMatch(text))
		{
			return "Expected digits after the exponent marker “e”";
		}

		if (text.Count('.') > 1)
		{
			return $"Invalid number “{Message.Abbreviate(text)}”. A value with several dots, such as a version number, must be quoted";
		}

		if (text.StartsWith('-') && (text.Length == 1 || !IsDigit(text[1])))
		{
			// The whole word, as for one without the “-”, so that a word such as `-nano` or `-info` is not taken for NaN or infinity.
			if (text.Equals("-nan", StringComparison.OrdinalIgnoreCase))
			{
				return "NaN is not representable. Use null for a missing value";
			}

			return text.Equals("-inf", StringComparison.OrdinalIgnoreCase) || text.Equals("-infinity", StringComparison.OrdinalIgnoreCase) ? $"“{Message.Abbreviate(text)}” is not a value. Negative infinity is written -infinity" : "Expected a digit or “infinity” after “-”";
		}

		return text.IndexOfAnyInRange('A', 'Z') != -1 || text.IndexOfAnyInRange('a', 'z') != -1 ? $"Invalid number “{Message.Abbreviate(text)}”. A string value must be quoted{QuotingExample(unquotedText)}" : $"Invalid number “{Message.Abbreviate(text)}”";
	}

	[GeneratedRegex(@"^[0-9a-fA-F]+(?:_[0-9a-fA-F]+)*\z")]
	private static partial Regex HexDigitsRegex();

	[GeneratedRegex(@"^-?[0-9][0-9_]*(?:\.[0-9_]+)?E-?[0-9_]*\z")]
	private static partial Regex UppercaseExponentRegex();

	[GeneratedRegex(@"^-?[0-9_]+\.(?:\z|[^0-9_])")]
	private static partial Regex TrailingDotRegex();

	[GeneratedRegex(@"^[0-9]+(?::[0-9]+)+\z")]
	private static partial Regex ColonNumberRegex();

	[GeneratedRegex(@"^[0-9]{1,2}:[0-9]{2}(?::[0-9]{2}(?:\.[0-9]+)?)?\z")]
	private static partial Regex TimeOfDayRegex();

	[GeneratedRegex(@"_(?:\z|[^0-9])|(?:^|[^0-9])_")]
	private static partial Regex MisplacedUnderscoreRegex();

	[GeneratedRegex(@"^-?[0-9][0-9_]*(?:\.[0-9_]+)?e-?0_?[0-9]")]
	private static partial Regex ExponentLeadingZeroRegex();

	[GeneratedRegex(@"^-?[0-9][0-9_]*(?:\.[0-9_]+)?e-0\z")]
	private static partial Regex NegativeZeroExponentRegex();

	[GeneratedRegex(@"^-?[0-9][0-9_]*(?:\.[0-9_]+)?e-?\z")]
	private static partial Regex MissingExponentRegex();

	/*
	A date alone is a calendar value, not an instant. The first invalid proleptic Gregorian component, shared by native dates and the calendar part of an instant.
	*/
	private static string? DateProblem(int year, int month, int day)
	{
		if (year is < 1 or > 9999)
		{
			return "the year must be 0001 to 9999";
		}
		if (month is < 1 or > 12)
		{
			return "the month must be 01 to 12";
		}
		var lastDay = DaysInMonth(year, month);
		return day < 1 || day > lastDay ? $"the day must be 01 to {lastDay} in that month" : null;
	}

	/*
	`time` is the token after a space that follows `text`, or an empty string. `isWholeValue` is whether nothing that may be part of the instant follows `text`. `offset` is an offset after a space that follows `text`, or `null`.
	*/
	private static string DescribeBadInstant(ReadOnlySpan<char> text, string time, bool isWholeValue, string? offset)
	{
		if (text.Length > MaxDiagnosedLength)
		{
			return $"Invalid instant “{Message.Abbreviate(text)}”. {InstantFormat}";
		}

		if (text.Length == 10)
		{
			return DescribeSpaceSeparatedInstant(text, time, isWholeValue);
		}

		if (text[10] == 't')
		{
			return "The date and time separator in an instant is an uppercase “T”";
		}

		if (text.EndsWith('z'))
		{
			return "The UTC offset in an instant is an uppercase “Z”";
		}

		if (text.Length >= 5 && text[^5] is '+' or '-' && IsDigits(text, text.Length - 4, 4))
		{
			return "An instant's offset is written with a colon, as in +07:00";
		}

		if (!LocalDateTimeRegex().IsMatch(text))
		{
			return $"Invalid instant “{Message.Abbreviate(text)}”. {InstantFormat}";
		}

		if (offset is not null)
		{
			// The instant has the offset it was meant in, so it is not a local time to write as a string.
			const string Reason = "An instant's offset follows its time directly, without a space";
			return IsValidValue($"{text}{offset}") ? $"{Reason}, as in {Message.Abbreviate($"{text}{offset}")}" : Reason;
		}

		return isWholeValue ? $"An instant needs an offset: Z or ±HH:MM. A date and time without an offset is a local time, which is not an instant, so write it as a string, as in '{Message.Abbreviate(text)}', or add the offset it was meant in" : "An instant needs an offset: Z or ±HH:MM";
	}

	[GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?\z")]
	private static partial Regex LocalDateTimeRegex();

	/*
	A date and a time with a space between them, where an instant has a "T". Following the example must not turn a local time into UTC silently, so a time without an offset is described as what it is. An instant is only shown when it is valid, so a date that does not exist, a time out of range, or an instant outside the years 0001 to 9999 in UTC gets the general format instead.
	*/
	private static string DescribeSpaceSeparatedInstant(ReadOnlySpan<char> date, string time, bool isWholeValue)
	{
		const string Separator = "The date and time separator in an instant is an uppercase “T”, not a space";
		var instant = $"{date}T{time}";

		if (instant.Length > MaxDiagnosedLength)
		{
			return $"{Separator}. {InstantFormat}";
		}

		if (IsValidValue(instant))
		{
			return $"{Separator}, as in {Message.Abbreviate(instant)}";
		}

		return isWholeValue && LocalDateTimeRegex().IsMatch(instant) && IsValidValue($"{instant}Z") ? $"{Separator}, and an instant needs the offset it was meant in, as in {Message.Abbreviate(instant)}Z for UTC. A date and time without an offset is a local time, which is not an instant, so write it as a string, as in '{Message.Abbreviate($"{date} {time}")}'" : $"{Separator}. {InstantFormat}";
	}
}
