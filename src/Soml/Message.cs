using System.Globalization;
using System.Text;

namespace Soml;

/*
The wording of error messages: how they show characters, keys, and text from the document. The wording follows the JS reference implementation word for word, so every implementation reports a document the same way.
*/
internal static class Message
{
	// Note: Not using IndexOfAnyExceptInRange because it allocated in Tier0 on .NET 10 ARM64, and SearchValues already specializes this contiguous range.
	internal static readonly System.Buffers.SearchValues<char> PrintableAsciiCharacters = System.Buffers.SearchValues.Create(" !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~");

	/*
	Why NaN cannot be a value, for every error about one.
	*/
	public const string NaN = "NaN is not a SOML value. Use null for a missing value";

	/*
	The range of a SOML int, for the errors about an int outside it.
	*/
	public const string IntRange = "(-9223372036854775808 to 9223372036854775807)";

	/*
	Names for the characters that are invisible or easy to mistake, so an error can say what it found.
	*/
	private static string? CharacterName(int codePoint) => codePoint switch
	{
		0x061C => "ARABIC LETTER MARK",
		0x85 => "NEXT LINE",
		0xA0 => "NO-BREAK SPACE",
		0xAD => "SOFT HYPHEN",
		0x1680 => "OGHAM SPACE MARK",
		0x2000 => "EN QUAD",
		0x2001 => "EM QUAD",
		0x2002 => "EN SPACE",
		0x2003 => "EM SPACE",
		0x2004 => "THREE-PER-EM SPACE",
		0x2005 => "FOUR-PER-EM SPACE",
		0x2006 => "SIX-PER-EM SPACE",
		0x2007 => "FIGURE SPACE",
		0x2008 => "PUNCTUATION SPACE",
		0x2009 => "THIN SPACE",
		0x200A => "HAIR SPACE",
		0x200B => "ZERO WIDTH SPACE",
		0x200C => "ZERO WIDTH NON-JOINER",
		0x200D => "ZERO WIDTH JOINER",
		0x200E => "LEFT-TO-RIGHT MARK",
		0x200F => "RIGHT-TO-LEFT MARK",
		0x2028 => "LINE SEPARATOR",
		0x2029 => "PARAGRAPH SEPARATOR",
		0x202A => "LEFT-TO-RIGHT EMBEDDING",
		0x202B => "RIGHT-TO-LEFT EMBEDDING",
		0x202C => "POP DIRECTIONAL FORMATTING",
		0x202D => "LEFT-TO-RIGHT OVERRIDE",
		0x202E => "RIGHT-TO-LEFT OVERRIDE",
		0x202F => "NARROW NO-BREAK SPACE",
		0x205F => "MEDIUM MATHEMATICAL SPACE",
		0x2060 => "WORD JOINER",
		0x2066 => "LEFT-TO-RIGHT ISOLATE",
		0x2067 => "RIGHT-TO-LEFT ISOLATE",
		0x2068 => "FIRST STRONG ISOLATE",
		0x2069 => "POP DIRECTIONAL ISOLATE",
		0x3000 => "IDEOGRAPHIC SPACE",
		0xFEFF => "ZERO WIDTH NO-BREAK SPACE",
		_ => null,
	};

	/// <summary>
	/// Shortens text quoted in an error message, so a huge token does not make a huge message. The length is counted in UTF-16 code units, as in the JS reference implementation, and the cut never splits a surrogate pair.
	/// </summary>
	public static string Abbreviate(string text, int maximumLength = 40) => text.Length <= maximumLength ? text : Abbreviate(text.AsSpan(), maximumLength);

	public static string Abbreviate(ReadOnlySpan<char> text, int maximumLength = 40)
	{
		if (text.Length <= maximumLength)
		{
			return text.ToString();
		}

		var end = char.IsHighSurrogate(text[maximumLength - 1]) ? maximumLength - 1 : maximumLength;
		return string.Concat(text[..end], "…");
	}

	/// <summary>
	/// A code point as <c>U+00A0</c>, with at least four uppercase hexadecimal digits.
	/// </summary>
	public static string FormatCodePoint(int codePoint) => $"U+{codePoint:X4}";

	/// <summary>
	/// Describes a character for an error message, such as <c>“$”</c> or <c>U+00A0 (NO-BREAK SPACE; only space, tab, and line feed are whitespace)</c>. A lone surrogate is passed as its code unit.
	/// </summary>
	public static string DescribeCharacter(int codePoint)
	{
		if (CharacterName(codePoint) is { } name)
		{
			var note = UnicodeTable.IsWhiteSpace(codePoint) ? "; only space, tab, and line feed are whitespace" : "";
			return $"{FormatCodePoint(codePoint)} ({name}{note})";
		}

		// Control, format, private-use, unassigned, surrogate, separator, and default-ignorable characters are invisible or ambiguous, so they are shown by code point.
		if (UnicodeTable.IsInvisible(codePoint))
		{
			return FormatCodePoint(codePoint);
		}

		// Note: Not using char.ConvertFromUtf32 plus string.Concat because it allocates an intermediate scalar string instead of writing directly into the quoted result.
		return string.Create(codePoint > 0xFFFF ? 4 : 3, codePoint, static (characters, value) =>
		{
			characters[0] = '“';
			new Rune(value).EncodeToUtf16(characters[1..]);
			characters[^1] = '”';
		});
	}

	/// <summary>
	/// The code point at <c>index</c>, or the code unit when it is a lone surrogate.
	/// </summary>
	public static int CodePointAt(string text, int index)
	{
		// Note: Not using Rune.TryGetRuneAt because it still needs a lone-surrogate fallback here and was slower on .NET 10 ARM64.
		var code = text[index];

		if (char.IsHighSurrogate(code) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
		{
			return char.ConvertToUtf32(code, text[index + 1]);
		}

		return code;
	}

	/// <summary>
	/// Whether the code unit at <c>index</c> is a surrogate that is not half of a pair.
	/// </summary>
	public static bool IsLoneSurrogate(ReadOnlySpan<char> text, int index)
	{
		var code = text[index];
		return char.IsSurrogate(code) && !(char.IsHighSurrogate(code) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) && !(char.IsLowSurrogate(code) && index > 0 && char.IsHighSurrogate(text[index - 1]));
	}

	/// <summary>
	/// Text as an error message shows it, with every character that a terminal acts on as an escape, such as <c>\u{9b}</c>, so that a value from user input, such as a key, cannot send commands to a terminal.
	/// </summary>
	public static string DescribeText(string text)
	{
		StringBuilder? builder = null;

		for (var index = 0; index < text.Length; index++)
		{
			var next = text.AsSpan(index).IndexOfAnyExcept(PrintableAsciiCharacters);

			if (next == -1)
			{
				builder?.Append(text.AsSpan(index));
				break;
			}

			builder?.Append(text.AsSpan(index, next));
			index += next;
			var codePoint = CodePointAt(text, index);
			var length = codePoint > 0xFFFF ? 2 : 1;

			if (UnicodeTable.IsTerminalCharacter(codePoint))
			{
				builder ??= new StringBuilder(text, 0, index, text.Length + 8);
				builder.Append(CultureInfo.InvariantCulture, $"\\u{{{codePoint:x}}}");
			}
			else
			{
				builder?.Append(text, index, length);
			}

			index += length - 1;
		}

		return builder?.ToString() ?? text;
	}

	/// <summary>
	/// A string value as an error message shows it: quoted as SOML writes it, cut short when it is long, and with every character that a terminal acts on as an escape.
	/// </summary>
	public static string DescribeString(string text) => DescribeText(SomlWriter.FormatString(Abbreviate(text), isLenient: true));

	/// <summary>
	/// A key as an error message shows it: bare when it can be, and otherwise as a JSON string, and cut short when it is long.
	/// </summary>
	public static string DescribeKey(string key) => SomlWriter.IsBareKey(key) ? Abbreviate(key) : ToJsonString(key);

	/*
	`JSON.stringify()` of a string, which the JS reference implementation uses to quote a key in a message: `"` and `\` are escaped, as are the control characters, with `\b`, `\f`, `\n`, `\r`, and `\t` for those that have a short form, and a lone surrogate.

	Note: Not using System.Text.Json or JavaScriptEncoder because they do not reproduce this exact escaping, including the spelling of lone surrogates, even with relaxed escaping.
	*/
	private static string ToJsonString(ReadOnlySpan<char> text)
	{
		var isTruncated = text.Length > 40;

		if (isTruncated)
		{
			text = text[..(char.IsHighSurrogate(text[39]) ? 39 : 40)];
		}

		// Forty code units each need at most six escape characters, plus quotes and an ellipsis.
		Span<char> buffer = stackalloc char[(40 * 6) + 3];
		var length = 0;
		buffer[length++] = '"';

		for (var index = 0; index < text.Length; index++)
		{
			var code = text[index];

			var escape = code switch
			{
				'"' => "\\\"",
				'\\' => "\\\\",
				'\b' => "\\b",
				'\f' => "\\f",
				'\n' => "\\n",
				'\r' => "\\r",
				'\t' => "\\t",
				_ => null,
			};

			if (escape is not null)
			{
				escape.CopyTo(buffer[length..]);
				length += escape.Length;
			}
			else if (code < 0x20 || IsLoneSurrogate(text, index))
			{
				// The digits are lowercase and four long, as `JSON.stringify()` writes them, such as `\u001b`.
				buffer[length++] = '\\';
				buffer[length++] = 'u';
				((int)code).TryFormat(buffer[length..], out var written, "x4", CultureInfo.InvariantCulture);
				length += written;
			}
			else
			{
				buffer[length++] = code;
			}
		}

		if (isTruncated)
		{
			buffer[length++] = '…';
		}

		buffer[length++] = '"';
		return new string(buffer[..length]);
	}
}
