using System.Buffers;
using System.Globalization;
using System.Text;

namespace Soml;

/*
A recursive-descent reader over the UTF-16 text of a document, a port of `parse.ts` in the JS reference implementation. It follows that implementation function by function, including its diagnostics, so that every error has the same reason and the same position. Offsets are UTF-16 indexes, as in JS.

A failure inside the parser is a `ParseFailure`, which costs no code frame, because some diagnostics parse speculatively and catch it. The entry points turn it into a `SomlParseException`.
*/
internal ref partial struct Parser
{
	/*
	The deepest nesting that reading and writing accept. A recursive-descent parser would otherwise run out of stack.
	*/
	public const int MaxDepth = 100;

	/*
	The longest token that the diagnostics look at. A longer token is rejected with a general message, or described from its first part.
	*/
	private const int MaxDiagnosedLength = 1000;

	private const int End = -1;

	private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	private const string KeyQuotingHint = ". A key that contains characters other than letters, digits, “_”, and “-” must be quoted";

	/*
	Every C0 control character except tab and line feed, and DEL. They are errors anywhere in a document.
	*/
	private static readonly SearchValues<char> ControlCharacters = SearchValues.Create("\0\x01\x02\x03\x04\x05\x06\x07\x08\x0B\x0C\x0D\x0E\x0F\x10\x11\x12\x13\x14\x15\x16\x17\x18\x19\x1A\x1B\x1C\x1D\x1E\x1F\x7F");

	internal static readonly SearchValues<char> BareKeyCharacters = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-");
	private static readonly SearchValues<char> SurrogateCharacters = SearchValues.Create(string.Create(0x800, 0, static (characters, _) =>
	{
		for (var index = 0; index < characters.Length; index++)
		{
			characters[index] = (char)(0xD800 + index);
		}
	}));
	private static readonly SearchValues<char> NumberTokenCharacters = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-.:+");

	// Note: Not using the three-value IndexOfAny overload because removing this cache did not preserve all public parsing timings in .NET 10 ARM64 tests.
	private static readonly SearchValues<char> EscapedStringSpecial = SearchValues.Create("\n\"\\");

	private readonly string _source;
	private int _index;

	/*
	Where the document's collection starts, after the comments and whitespace before it.
	*/
	private int _documentStart;

	/*
	Whether the parser only checks the document, for the formatter and the editor, which build a syntax tree of their own. It checks everything as it does when it reads values, so the errors are the same, but it does not build objects, arrays, or string values. The keys of an object go into a set, for the check for duplicates.
	*/
	private readonly bool _isValidating;

	/*
	The keys of the object being read at each depth, for the check for duplicates, reused for every object at the depth: in validation, which builds no objects, and for an object with more than `SomlObject.MaxLinearCount` members, so that the object does not grow its index of keys while it is read.
	*/
	private List<HashSet<string>?>? _keySets;

	private Parser(string source, bool isValidating = false)
	{
		_source = source;
		_isValidating = isValidating;
	}

	/*
	Reads a whole document. The characters are checked first.
	*/
	public static SomlValue ParseDocument(string source)
	{
		try
		{
			CheckCharacters(source);
			return new Parser(source).ParseDocument();
		}
		catch (ParseFailure failure)
		{
			throw new SomlParseException(failure.Reason, source, failure.Offset);
		}
	}

	/*
	The value of the `"..."` string at `start` in a document that is already checked, without checking the characters again.
	*/
	public static string DecodeEscapedString(string source, int start) => new Parser(source) { _index = start }.ParseEscapedString();

	/*
	Checks a whole document, as `ParseDocument` does, without building its value.
	*/
	public static void Validate(string source)
	{
		try
		{
			CheckCharacters(source);
			new Parser(source, isValidating: true).ParseDocument();
		}
		catch (ParseFailure failure)
		{
			throw new SomlParseException(failure.Reason, source, failure.Offset);
		}
	}

	/*
	The first value in a document that has already been validated, skipping its leading whitespace and comments without constructing a syntax tree.
	*/
	internal static int FindDocumentStart(string source)
	{
		var parser = new Parser(source);
		parser.SkipTrivia();
		return parser._index;
	}

	/*
	Reads one value that fills the text, such as `1h30m`, and requires it to be of a kind. For `Parse()` of the time types, which throw a `FormatException`.
	*/
	public static SomlValue ParseScalar(string text, SomlValueKind kind)
	{
		try
		{
			return ParseScalarOrFail(text, kind);
		}
		catch (ParseFailure failure)
		{
			var exception = new SomlParseException(failure.Reason, text, failure.Offset);
			throw new FormatException(exception.Reason, exception);
		}
	}

	/*
	Reads a time token directly from a span. Valid tokens are ASCII and fill the span, so no source string or document parser is needed. String Parse falls back to the full parser for its diagnostic wording; span Parse reports a bounded format error.
	*/
	public static bool TryParseTime(ReadOnlySpan<char> text, SomlValueKind kind, out SomlValue value)
	{
		value = default;

		if (kind == SomlValueKind.Date)
		{
			if (text.Length == 10 && HasDatePrefix(text))
			{
				var year = ReadDigits(text, 0, 4);
				var month = ReadDigits(text, 5, 2);
				var day = ReadDigits(text, 8, 2);
				if (DateProblem(year, month, day) is null)
				{
					value = new DateOnly(year, month, day);
					return true;
				}
			}
			return false;
		}

		if (kind == SomlValueKind.Instant)
		{
			if (text.Length <= MaxDiagnosedLength && HasDatePrefix(text) && MatchInstant(text, out var parts) && parts.Validate(out var instant) is null)
			{
				value = instant;
				return true;
			}

			return false;
		}

		if (text.IsEmpty || !IsDurationLike(text))
		{
			return false;
		}

		try
		{
			value = ParseDuration(text, 0);
			return true;
		}
		catch (ParseFailure)
		{
			return false;
		}
	}

	private static SomlValue ParseScalarOrFail(string text, SomlValueKind kind)
	{
		if (kind is SomlValueKind.Date or SomlValueKind.Instant or SomlValueKind.Duration && TryParseTime(text, kind, out var time))
		{
			return time;
		}

		CheckCharacters(text);
		var parser = new Parser(text);
		var value = parser.ParseValue(1);

		if (parser._index != text.Length)
		{
			throw Failure($"Unexpected {parser.DescribeHere()} after a value", parser._index);
		}

		if (value.Kind != kind)
		{
			throw Failure($"Expected {SomlValue.DescribeKind(kind)}, but found {SomlValue.DescribeKind(value.Kind)}", 0);
		}

		return value;
	}

	/*
	Decodes UTF-8 strictly, with the position of the first invalid sequence. A byte order mark is kept, so that it is rejected rather than silently dropped.
	*/
	public static string Decode(ReadOnlySpan<byte> bytes)
	{
		if (bytes.IsEmpty)
		{
			return "";
		}

		try
		{
			// UTF-8 never has fewer bytes than UTF-16 has code units, but decoding directly into the final string avoids that document-sized character buffer altogether.
			// Note: Not using Utf8.ToUtf16 because its invalid-prefix progress would require another document-sized UTF-16 buffer and copy here.
			return StrictUtf8.GetString(bytes);
		}
		catch (DecoderFallbackException failure)
		{
			var valid = StrictUtf8.GetString(bytes[..failure.Index]);

			// A sequence that the end of the input cuts short is valid as far as it goes.
			var rest = bytes[failure.Index..];
			var isTruncated = Rune.DecodeFromUtf8(rest, out _, out _) == OperationStatus.NeedMoreData;
			var reason = isTruncated ? "Incomplete UTF-8 sequence at the end of the input" : $"Invalid UTF-8 byte 0x{rest[0]:X2}";
			throw new SomlParseException(reason, valid, valid.Length);
		}
	}

	/*
	Reads the rest of a stream and decodes it as `Decode` does. A seekable stream, such as a file, gets a bounded initial allocation based on its remaining size.
	*/
	public static string Decode(Stream stream)
	{
		if (TryDecodeMemoryStream(stream, out var text))
		{
			return text;
		}

		using var buffer = CreateBuffer(stream);
		stream.CopyTo(buffer);
		return Decode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
	}

	public static async ValueTask<string> DecodeAsync(Stream stream, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (TryDecodeMemoryStream(stream, out var text))
		{
			return text;
		}

		using var buffer = CreateBuffer(stream);
		await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
		return Decode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
	}

	private static MemoryStream CreateBuffer(Stream stream) => new(stream.CanSeek ? (int)Math.Clamp(stream.Length - stream.Position, 0, 64 * 1024) : 0);

	/*
	An exposed MemoryStream already holds the remaining bytes contiguously. Consume that slice without copying it, keeping virtual read behavior for subclasses.
	*/
	private static bool TryDecodeMemoryStream(Stream stream, out string text)
	{
		if (stream.GetType() != typeof(MemoryStream) || !((MemoryStream)stream).TryGetBuffer(out var bytes))
		{
			text = "";
			return false;
		}

		var memory = (MemoryStream)stream;
		var start = (int)Math.Min(memory.Position, memory.Length);
		memory.Position += bytes.Count - start;
		text = Decode(bytes.AsSpan(start));
		return true;
	}

	/*
	Characters that are errors wherever they appear, so they are checked once, up front.
	*/
	private static void CheckCharacters(string source)
	{
		if (source.Length > 0 && source[0] == (char)0xFEFF)
		{
			throw Failure("A byte order mark (BOM) is not allowed", 0);
		}

		var control = source.AsSpan().IndexOfAny(ControlCharacters);

		if (control != -1)
		{
			var code = (int)source[control];

			if (code == '\r')
			{
				throw Failure("A carriage return (U+000D) is not allowed anywhere. Use LF line endings", control);
			}

			throw Failure($"A raw control character ({Message.FormatCodePoint(code)}) is not allowed anywhere, including in strings and comments. In a string, write it as the escape \\u{{{code:x}}} inside \"...\"", control);
		}

		var loneSurrogate = FindLoneSurrogate(source);

		if (loneSurrogate != -1)
		{
			throw Failure($"A lone surrogate ({Message.DescribeCharacter(source[loneSurrogate])}) is not a Unicode scalar value", loneSurrogate);
		}
	}

	/*
	The index of the first surrogate that is not half of a pair, or -1.
	*/
	internal static int FindLoneSurrogate(ReadOnlySpan<char> text)
	{
		// Note: Not using Utf16.IndexOfInvalidSubsequence because it is unavailable on .NET 10.
		var index = text.IndexOfAny(SurrogateCharacters);

		while (index != -1)
		{
			if (!char.IsHighSurrogate(text[index]) || index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
			{
				return index;
			}

			index += 2;
			var next = text[index..].IndexOfAny(SurrogateCharacters);
			index = next == -1 ? -1 : index + next;
		}

		return -1;
	}

	private static ParseFailure Failure(string reason, int offset) => new(reason, offset);

	private ParseFailure Failure(string reason) => new(reason, _index);

	/*
	The unsigned comparison also turns a negative offset, such as `start - 1` for a token at the start of the text, into `End`.
	*/
	private int Code(int offset) => (uint)offset < (uint)_source.Length ? _source[offset] : End;

	private int Code() => Code(_index);

	private bool IsAtEnd => _index >= _source.Length;

	private SomlValue ParseDocument()
	{
		SkipTrivia();
		_documentStart = _index;

		if (IsAtEnd)
		{
			throw Failure("A document must contain an object or an array, but this one is empty");
		}

		var code = Code();

		SomlValue value = code switch
		{
			'{' => ParseObject(1),
			'[' => ParseArray(1),
			_ => ParseBareObject(),
		};

		SkipTrivia();

		if (!IsAtEnd)
		{
			throw Failure($"Unexpected {DescribeHere()} after the end of the document");
		}

		return value;
	}

	/*
	`bare-object = entry ( entry-sep entry )*`, where a separator is one or more line breaks.
	*/
	private SomlValue ParseBareObject()
	{
		var result = StartObject(1);
		var start = _index;

		try
		{
			ParseEntry(result, 1);
		}
		catch (ParseFailure)
		{
			DiagnoseBareValue(start);
			throw;
		}

		var hasLineBreak = SkipTrivia();

		while (!IsAtEnd)
		{
			if (Code() == ',')
			{
				throw Failure("Top-level entries are separated by line breaks, not commas. Use braces for a one-line object");
			}

			if (!hasLineBreak)
			{
				throw Failure($"Expected a line break before the next entry, but found {DescribeHere()}{SlashCommentHint()}");
			}

			ParseEntry(result, 1);
			hasLineBreak = SkipTrivia();
		}

		return result is null ? SomlValue.Null : result;
	}

	/*
	A new object to read members into, or, in validation, none, and the cleared set of keys at the depth.
	*/
	private SomlObject? StartObject(int depth)
	{
		if (!_isValidating)
		{
			return new SomlObject();
		}

		KeySet(depth).Clear();
		return null;
	}

	private HashSet<string> KeySet(int depth)
	{
		_keySets ??= [];

		while (_keySets.Count <= depth)
		{
			_keySets.Add(null);
		}

		return _keySets[depth] ??= new HashSet<string>(StringComparer.Ordinal);
	}

	/*
	Adds a member, unless the object already has its key. A small object is searched one key at a time, as `SomlObject` searches it, and a larger one through the set of keys at its depth, which is filled when the object passes `SomlObject.MaxLinearCount` members.
	*/
	private bool TryAddMember(SomlObject? result, int depth, string key, SomlValue value)
	{
		if (result is null)
		{
			return KeySet(depth).Add(key);
		}

		if (result.Count < SomlObject.MaxLinearCount)
		{
			return result.TryAdd(key, value);
		}

		var keys = KeySet(depth);

		if (result.Count == SomlObject.MaxLinearCount)
		{
			keys.Clear();

			foreach (var member in result.AsSpan())
			{
				keys.Add(member.Key);
			}
		}

		if (!keys.Add(key))
		{
			return false;
		}

		result.Append(key, value);
		return true;
	}

	/*
	A whole document that is one scalar, such as `5` or an instant, fails as an entry. That failure is reported as what it is.
	*/
	private void DiagnoseBareValue(int start)
	{
		_index = start;
		var isBareValue = false;

		try
		{
			ParseValue(1);
			SkipTrivia();
			isBareValue = IsAtEnd;
		}
		catch (ParseFailure)
		{
			// Not a bare value either, so the original error stands.
		}

		if (isBareValue)
		{
			throw Failure("A bare value is not a document. A document is an object or an array, so write it as `key: value` or `[value]`", start);
		}
	}

	/*
	Skips spaces, tabs, line breaks, and comments, and reports whether a line break was crossed. A line break inside a block comment does not count.
	*/
	private bool SkipTrivia()
	{
		var source = _source;
		var hasCrossedLineBreak = false;

		while (true)
		{
			if ((uint)_index >= (uint)source.Length)
			{
				return hasCrossedLineBreak;
			}

			var code = source[_index];

			if (code is ' ' or '\t' or '\n')
			{
				var end = SkipWhitespace(source, _index);
				hasCrossedLineBreak |= source.AsSpan(_index, end - _index).Contains('\n');
				_index = end;
			}
			else if (!SkipComment())
			{
				return hasCrossedLineBreak;
			}
		}
	}

	/*
	Skips the comment that starts here, or returns `false` when there is none.
	*/
	private bool SkipComment()
	{
		var code = Code();

		if (code == '#')
		{
			_index = FindLineEnd(_source, _index);
			return true;
		}

		if (code == '/' && Code(_index + 1) == '*')
		{
			SkipBlockComment();
			return true;
		}

		return false;
	}

	private void SkipBlockComment()
	{
		// Note: Not using a combined SearchValues<string> marker scan because it misclassifies valid overlaps such as /*/*/ and changes unterminated/nested error precedence.
		var start = _index;
		var end = _source.IndexOf("*/", start + 2, StringComparison.Ordinal);

		if (end == -1)
		{
			throw Failure("Unterminated block comment", start);
		}

		var nested = _source.AsSpan(start + 2, end - start - 2).IndexOf("/*");

		// The body may not contain `/*`. An opening that overlaps the closing `*/`, as in `/*/`, is not inside the body.
		if (nested != -1)
		{
			throw Failure("Block comments cannot be nested, and their body may not contain “/*”", start + 2 + nested);
		}

		_index = end + 2;
	}

	/*
	`entry = key ":" ws value`.
	*/
	private void ParseEntry(SomlObject? result, int depth)
	{
		var keyStart = _index;
		var key = ParseKey();
		var code = Code();

		if (code == '.')
		{
			throw FailDotInKey(keyStart);
		}

		if (code != ':')
		{
			throw FailMissingColon(keyStart);
		}

		var colon = _index;
		_index++;
		SkipTrivia();
		SomlValue value = default;
		ParseFailure? failure = null;

		try
		{
			value = ParseValue(depth + 1);
		}
		catch (ParseFailure caught)
		{
			failure = caught;
		}

		// The failure is diagnosed and thrown again after the `catch`, not inside it, because a `catch` runs on top of the frames of the throw. Inside it, every level of a deep document would add a whole exception dispatch to the stack, which overflows a small one.
		if (failure is not null)
		{
			DiagnoseBadValue(failure, key, keyStart, colon);
			throw failure;
		}

		if (!TryAddMember(result, depth, key, value))
		{
			throw Failure($"Duplicate key {Message.DescribeKey(key)}", keyStart);
		}
	}

	/*
	Reports a value that failed to parse as what the entry was meant to be, when that is clear.
	*/
	private void DiagnoseBadValue(ParseFailure failure, string key, int keyStart, int colon)
	{
		// A key that contains a `:`, as in `12:30: 'lunch'`, ends at the first `:`, and the rest is read as the value.
		if (IsBareKeyCharacter(Code(colon + 1)))
		{
			DiagnoseKeyWithColon(keyStart);
		}

		_index = colon + 1;
		var isValueOnNextLine = SkipTrivia();
		var valueStart = _index;
		var commentHint = DescribeCommentAsValue(colon);

		if (isValueOnNextLine)
		{
			DiagnoseMissingValue(valueStart, key, keyStart, commentHint);
		}

		// A value that was left out at the end of the document or of an object.
		var code = Code(valueStart);

		if (commentHint.Length > 0 && failure.Offset == valueStart && (code == '}' || code == End))
		{
			throw Failure($"{failure.Reason}{commentHint}", valueStart);
		}
	}

	/*
	A bare key followed directly by `:` and more of a key, up to a `:` that ends the key, as in `a:b: 1`.
	*/
	private void DiagnoseKeyWithColon(int keyStart)
	{
		for (var index = keyStart; index < keyStart + MaxDiagnosedLength; index++)
		{
			var code = Code(index);

			if (code == ':')
			{
				var next = Code(index + 1);

				if (next is '\n' or ' ' or '\t' or End)
				{
					throw Failure($"A key that contains “:” must be quoted, as in '{Message.Abbreviate(_source.AsSpan(keyStart, index - keyStart))}'", keyStart);
				}
			}
			else if (!IsBareKeyCharacter(code))
			{
				return;
			}
		}
	}

	/*
	The hint for a `#` directly after a `:`, as in `color: #FFF`, which starts a comment rather than a value.
	*/
	private string DescribeCommentAsValue(int colon)
	{
		var hash = SkipSpaces(_source, colon + 1);

		if (Code(hash) != '#')
		{
			return "";
		}

		// A `#` followed by a space, or by another `#`, starts an ordinary comment. A separator or a closing bracket after the value is not part of it.
		var limit = Math.Min(_source.Length, hash + MaxDiagnosedLength);
		var end = hash + 1;

		if (end >= limit || Code(end) is '\t' or '\n' or ' ' or '#' or ',' or ']' or '}')
		{
			return "";
		}

		while (end < limit && Code(end) is not ('\t' or '\n' or ' ' or ',' or ']' or '}'))
		{
			end++;
		}

		return $". “#” starts a comment, so a value that starts with “#” must be quoted{QuotingExample(_source[hash..end])}";
	}

	/*
	An entry whose value was left out, as in `a:` followed by `'b': 1` on the next line, reads the next key as the value. That failure is reported as what it is.
	*/
	private void DiagnoseMissingValue(int start, string parent, int keyStart, string commentHint)
	{
		// A YAML block sequence.
		if (Code(start) == '-' && Code(start + 1) is ' ' or '\t')
		{
			throw Failure("Expected a value, but found a “-” list. An array is written in brackets, as in [80, 443]", start);
		}

		_index = start;
		string? key;

		try
		{
			key = ParseKey();
		}
		catch (ParseFailure)
		{
			// Not a key either, so the original error stands.
			key = null;
		}

		if (key is null || Code() != ':')
		{
			return;
		}

		// Whitespace or the end follows the `:` of a bare key. A digit follows the `:` in an instant such as `2026-09-19T25:00:00Z`, whose own error is more precise. A quoted key cannot be part of a value, so anything may follow its `:`.
		var next = Code(_index + 1);
		var firstCode = Code(start);

		if (firstCode is not ('\'' or '"') && next is not ('\n' or ' ' or '\t' or End))
		{
			return;
		}

		var hint = commentHint.Length == 0 ? DescribeIndentedKey(parent, key, keyStart, start) : commentHint;
		throw Failure($"Expected a value, but found the key {Message.DescribeKey(key)}{hint}", start);
	}

	/*
	The hint for a key at `start` that is indented under the entry whose value is missing, as YAML nests an object.
	*/
	private string DescribeIndentedKey(string parent, string key, int keyStart, int start)
	{
		var keyIndentation = GetIndentation(_source, keyStart);
		var indentation = GetIndentation(_source, start);

		if (keyIndentation is null || indentation is null || indentation <= keyIndentation)
		{
			return "";
		}

		// The keys are written as in a document, so that the suggestion is valid.
		return $". Indentation does not nest objects, so write {FormatHintKey(parent)}: {{{FormatHintKey(key)}: …}}";

		static string FormatHintKey(string value) => Message.Abbreviate(SomlWriter.FormatKey(Message.Abbreviate(value, 200)), 200);
	}

	private ParseFailure FailMissingColon(int keyStart)
	{
		var source = _source;
		var next = SkipSpaces(source, _index);
		var nextCode = Code(next);

		if (nextCode == ':' && next > _index)
		{
			return Failure("Whitespace is not allowed between a key and its “:”");
		}

		// A block comment followed by the ":" was meant to come before it.
		if (nextCode == '/' && Code(next + 1) == '*')
		{
			var commentEnd = source.IndexOf("*/", next + 2, StringComparison.Ordinal);

			if (commentEnd != -1 && Code(SkipSpaces(source, commentEnd + 2)) == ':')
			{
				return Failure("A comment is not allowed between a key and its “:”", next);
			}
		}

		var colon = next < source.Length ? source.IndexOf(':', next) : -1;

		// A key with a space in it, such as `the name: 1`. The hint is only given for plain words, because quoting a quoted key would change what it means, and for a `:` that whitespace or the end follows, because quoting the words before the `:` in `server localhost:8080` would give a valid document with another meaning. So `the name:1` gets no hint.
		if (colon != -1 && next > _index && IsBareKeyCharacter(nextCode) && colon < FindLineEnd(source, next) && Code(colon + 1) is ' ' or '\t' or '\n' or End)
		{
			// Only spaces and tabs are trimmed, because other characters are not whitespace in SOML, such as U+00A0.
			var key = source.AsSpan(keyStart, SkipSpacesBack(source, colon) - keyStart);

			if (IsWordsWithSpaces(key))
			{
				return Failure($"A bare key cannot contain spaces. Quote it, as in '{Message.Abbreviate(key)}'", keyStart);
			}
		}

		if (IsAtEnd || Code() == '\n')
		{
			return Failure("Expected “:” after the key");
		}

		// A character directly after a bare key is most likely meant to be part of it.
		var previousCode = Code(_index - 1);
		var hint = previousCode is not ('\'' or '"') && next == _index && IsQuotableKeyCharacter(Message.CodePointAt(source, next)) ? KeyQuotingHint : "";
		_index = next;
		return Failure($"Expected “:” after the key, but found {DescribeHere()}{hint}");
	}

	private string ParseKey()
	{
		var source = _source;
		var start = _index;
		var code = Code(start);

		if (code is '\'' or '"')
		{
			if (Code(start + 1) == code && Code(start + 2) == code)
			{
				throw Failure("A block string cannot be a key");
			}

			// A key is a string, so it may hold anything a string can, including a line feed written as an escape.
			return code == '\'' ? ParseLiteralString() : ParseEscapedString();
		}

		var end = FindBareKeyEnd(_source, start);

		if (end == start)
		{
			if (code == '[')
			{
				DiagnoseTableHeader();
			}

			throw Failure(IsAtEnd ? "Expected a key" : $"Expected a key, but found {DescribeHere()}{DescribeKeyQuotingHint()}{SlashCommentHint()}");
		}

		_index = end;
		return source[start..end];
	}

	/*
	The hint for a key that starts with a character a bare key cannot hold, such as the `$` in `$schema`, or nothing for a character with another meaning, such as `}`.
	*/
	private string DescribeKeyQuotingHint()
	{
		if (!IsQuotableKeyCharacter(Message.CodePointAt(_source, _index)))
		{
			return "";
		}

		// A longer key gets the hint without the example.
		var limit = Math.Min(_source.Length, _index + MaxDiagnosedLength);
		var end = _index;

		while (end < limit && IsQuotableKeyCharacter(Message.CodePointAt(_source, end)))
		{
			end += char.IsHighSurrogate(_source[end]) && end + 1 < _source.Length && char.IsLowSurrogate(_source[end + 1]) ? 2 : 1;
		}

		// The run must end at a `:` within the diagnosed length, as the reference's `^Q+(?=:)` on a cut slice requires.
		return end < limit && end <= _source.Length && Code(end) == ':' ? $"{KeyQuotingHint}, as in '{Message.Abbreviate(_source.AsSpan(_index, end - _index))}'" : KeyQuotingHint;
	}

	/*
	A `.` after a key, as in `example.com: 1`. A key is never a path, so the `.` is meant either as part of the key or as nesting.
	*/
	private ParseFailure FailDotInKey(int keyStart)
	{
		var end = _index;

		while (end < keyStart + MaxDiagnosedLength && (IsBareKeyCharacter(Code(end)) || Code(end) == '.'))
		{
			end++;
		}

		var key = _source[keyStart..end];
		var words = key.Split('.');

		// The suggestions are only given for a bare key whose dots each have a word on both sides, so that both are valid.
		if (Code(end) != ':' || !IsBareKeyCharacter(Code(keyStart)) || words.Contains(""))
		{
			return Failure("A key cannot contain “.” unless it is quoted. Quote the whole key, or use braces to nest, as in a: {b: …}");
		}

		var nested = $"{string.Join(": {", words)}: …{new string('}', words.Length - 1)}";
		return Failure($"A bare key cannot contain “.”. Quote it, as in '{Message.Abbreviate(key)}', or use braces to nest, as in {Message.Abbreviate(nested)}");
	}

	private SomlValue ParseObject(int depth)
	{
		var result = StartObject(depth);
		ParseItems(depth, '}', result, null);
		return result is null ? SomlValue.Null : result;
	}

	private SomlValue ParseArray(int depth)
	{
		var result = _isValidating ? null : new SomlArray();
		ParseItems(depth, ']', null, result);
		return result is null ? SomlValue.Null : result;
	}

	/*
	`{` or `[` at depth `depth`, then items separated by a comma, a line break, or both, with an optional trailing comma, then the `closing` bracket. A comma must be on the line of the item before it. The results are null in validation.
	*/
	private void ParseItems(int depth, char closing, SomlObject? objectResult, SomlArray? arrayResult)
	{
		if (depth > MaxDepth)
		{
			throw Failure($"The document is nested more than {MaxDepth} levels deep");
		}

		var start = _index;
		_index++;
		SkipTrivia();

		while (Code() != closing)
		{
			var hasLineBreak = false;
			var itemEnd = _index;

			if (!IsAtEnd)
			{
				if (closing == '}')
				{
					ParseEntry(objectResult, depth);
				}
				else
				{
					var item = ParseValue(depth + 1);
					arrayResult?.Add(item);
				}

				itemEnd = _index;
				hasLineBreak = SkipTrivia();
			}

			if (IsAtEnd)
			{
				throw Failure($"Unterminated {(closing == '}' ? "object" : "array")}: expected “{closing}”", start);
			}

			if (Code() == ',')
			{
				if (hasLineBreak)
				{
					throw Failure("A comma must be on the same line as the item before it. The line break already separates the items, so remove the comma");
				}

				_index++;
				SkipTrivia();
			}
			else if (!hasLineBreak && Code() != closing)
			{
				var item = closing == '}' ? "an object member" : "an array item";
				// Without a line break that separates, a line break in the gap is inside a block comment.
				var hint = _source.AsSpan(itemEnd, _index - itemEnd).Contains('\n') ? ". A line break inside a block comment does not separate items" : SlashCommentHint();
				throw Failure($"Expected “,”, a line break, or “{closing}” after {item}, but found {DescribeHere()}{hint}");
			}
		}

		_index++;
	}

	private SomlValue ParseValue(int depth)
	{
		var code = Code();

		if (code == '{')
		{
			return ParseObject(depth);
		}

		if (code == '[')
		{
			return ParseArray(depth);
		}

		var start = _index;
		SomlValue value;
		// An int or a float written with digits.
		var isNumber = false;

		if (code is '\'' or '"')
		{
			var text = ParseString(isBuilding: !_isValidating);
			value = _isValidating ? SomlValue.Null : SomlValue.FromString(text);
		}
		else if (code == 't' && IsKeyword("true"))
		{
			value = true;
		}
		else if (code == 'f' && IsKeyword("false"))
		{
			value = false;
		}
		else if (code == 'n' && IsKeyword("null"))
		{
			value = SomlValue.Null;
		}
		else if (code == 'i' && IsKeyword("infinity"))
		{
			value = SomlValue.FromFloat(double.PositiveInfinity);
		}
		else if (code == '-' && IsKeyword("-infinity"))
		{
			value = SomlValue.FromFloat(double.NegativeInfinity);
		}
		else if (code == '-' || IsDigit(code))
		{
			value = ParseNumberOrTemporal(code);
			isNumber = value.Kind is SomlValueKind.Integer or SomlValueKind.Float;
		}
		else
		{
			throw FailUnexpectedValue();
		}

		if (!IsValueEnd(Code()))
		{
			throw FailAfterValue(start, isNumber);
		}

		if (isNumber && Code() is ' ' or '\t')
		{
			DiagnoseUnitAfterSpace(start);
		}

		return value;
	}

	/*
	A character that cannot follow the value that starts at `start`.
	*/
	private ParseFailure FailAfterValue(int start, bool isNumber)
	{
		var code = Code();

		// Go writes microseconds as `µs`, with the micro sign or the Greek letter mu. The duration is only suggested when it is valid, which a number with an exponent or a radix prefix, for example, is not.
		if (isNumber && code is 0xB5 or 0x3BC && Code(_index + 1) == 's')
		{
			var number = Message.Abbreviate(_source.AsSpan(start, _index - start), MaxDiagnosedLength + 1);
			return Failure($"The unit for microseconds is written us{(IsValidValue($"{number}us") ? $", as in {Message.Abbreviate(number)}us" : "")}");
		}

		// A `''` inside a '...' string, as SQL and YAML escape a quote, ends the string.
		if (code == '\'' && Code(start) == '\'')
		{
			return Failure("There is no '' escape in a '...' string. Write a string that contains ' as \"...\", as in \"it's\"");
		}

		return Failure($"Unexpected {DescribeHere()} after a value");
	}

	/*
	A number followed by a space and a word, such as `512 MiB` or `10 seconds`, which is an error anyway. A word followed by more than spaces, a separator, or a comment is left to the general errors, because it may be a key, as in `a: 1 b: 2`, or a sentence.
	*/
	private void DiagnoseUnitAfterSpace(int start)
	{
		var unitStart = SkipSpaces(_source, _index);
		var unitEnd = unitStart;

		while (IsLetter(Code(unitEnd)))
		{
			unitEnd++;
		}

		var unit = _source.AsSpan(unitStart, unitEnd - unitStart);

		// A keyword after a number is a missing comma.
		if (unit.Length == 0 || !IsValueEndAt(_source, SkipSpaces(_source, unitEnd)) || unit is "true" or "false" or "null" or "infinity")
		{
			return;
		}

		var number = _source.AsSpan(start, _index - start);
		// The duration is only suggested when it is valid, which a number with an exponent or a radix prefix, a fraction of a nanosecond, a negative zero, or a value outside the 64-bit range is not. The string is always offered, because a unit such as `m` may mean meters rather than minutes.
		var duration = number.Length <= MaxDiagnosedLength && DurationUnitSize(unit) != 0 && IsValidValue($"{number}{unit}") ? $"{Message.Abbreviate(number)}{unit}" : "10s";
		// One character beyond the quoted diagnostic's limit preserves whether it needs an ellipsis without copying the full token.
		var example = string.Concat(number[..Math.Min(number.Length, 41)], " ", unit[..Math.Min(unit.Length, 41)]);
		throw Failure($"A unit cannot follow a number after a space. Write a duration without the space, as in {duration}, and anything else, such as a size, as a string, as in '{Message.Abbreviate(example)}'", unitStart);
	}

	private bool IsKeyword(string word)
	{
		if (!_source.AsSpan(_index).StartsWith(word))
		{
			return false;
		}

		// A longer word, such as `nullable`, is not the keyword.
		if (IsBareKeyCharacter(Code(_index + word.Length)))
		{
			return false;
		}

		_index += word.Length;
		return true;
	}

	private ParseFailure FailUnexpectedValue()
	{
		if (IsAtEnd)
		{
			return Failure("Expected a value, but reached the end of the document");
		}

		var code = Code();

		if (code == '+')
		{
			return Failure("A “+” sign is not allowed. A number without a sign is positive");
		}

		// A `.` that no digit follows begins a string, such as `.env` or `./foo`, rather than a number.
		if (code == '.')
		{
			return Failure(IsDigit(Code(_index + 1)) ? "A number cannot begin with “.”; write a digit before it, as in 0.5" : DescribeUnknownWord(".", UnquotedText()));
		}

		var wordEnd = FindBareKeyEnd(_source, _index);

		if (wordEnd > _index)
		{
			var word = Message.Abbreviate(_source.AsSpan(_index, wordEnd - _index), MaxDiagnosedLength + 1);

			// A key where a value should be, as when the value of an entry is left out and the next line has a key, or as in `[a: 1]`, is reported as a key. A word after the `:` of a member and a space, as in `msg: Error: file not found` or `url: https://example.com`, is that member's value instead, an unquoted string. Without the space, as in `a:b: 1`, the first `:` was most likely meant as part of the key.
			var spacesStart = SkipSpacesBack(_source, _index);
			var isMemberValue = spacesStart < _index && Code(spacesStart - 1) == ':';

			if (!isMemberValue && Code(wordEnd) == ':')
			{
				return Failure($"Expected a value, but found the key {Message.Abbreviate(word)}");
			}

			DiagnoseTableHeader(isValue: true);
			return Failure(DescribeUnknownWord(word, UnquotedText()));
		}

		return Failure($"Expected a value, but found {DescribeHere()}{(IsBlockScalarIndicator() ? ". Write a multiline string as a block string, between ''' lines" : SlashCommentHint())}");
	}

	/*
	The text from `start` that was most likely meant as one unquoted string, such as `John Smith`: up to the end of the line, a comma, a closing bracket, or a comment after a space or a tab, without the spaces and tabs at its end.
	*/
	private string UnquotedText() => UnquotedText(_index);

	private string UnquotedText(int start)
	{
		var limit = Math.Min(_source.Length, start + MaxDiagnosedLength);
		var end = start;

		while (end < limit && !IsUnquotedTextEnd(end))
		{
			end++;
		}

		return _source[start..Math.Max(start, SkipSpacesBack(_source, end))];
	}

	private bool IsUnquotedTextEnd(int index)
	{
		var code = Code(index);
		var isComment = code == '#' || (code == '/' && Code(index + 1) == '*');
		return code is '\n' or ',' or ']' or '}' || (isComment && Code(index - 1) is ' ' or '\t');
	}

	/*
	A TOML table header, as in `[server]` or `[[servers]]` on a line of its own, reads as an array that holds a word, or as a key that starts with "[". Where a value is expected, `isValue`, it is only a table header when its bracket opens the document, because a table cannot be written as an item of an array inside it.
	*/
	private void DiagnoseTableHeader(bool isValue = false)
	{
		var lineStart = _index == 0 ? 0 : _source.LastIndexOf('\n', _index - 1) + 1;

		if (isValue && SkipSpaces(_source, lineStart) != _documentStart)
		{
			return;
		}

		var line = _source.AsSpan(lineStart, Math.Min(FindLineEnd(_source, _index), lineStart + MaxDiagnosedLength) - lineStart);
		var name = MatchTableHeader(line);

		if (name is not null)
		{
			throw Failure($"There are no table headers. Write the table as an object, as in {Message.Abbreviate(name)}: {{…}}");
		}
	}

	/*
	A YAML literal block scalar indicator, as in `key: |` or `key: |-`, at the end of its line.
	*/
	private bool IsBlockScalarIndicator()
	{
		// A folded block scalar, `>`, joins its lines, which a block string does not, so only `|` gets the hint.
		if (Code() != '|')
		{
			return false;
		}

		var chomping = Code(_index + 1);
		var end = SkipSpaces(_source, chomping is '-' or '+' ? _index + 2 : _index + 1);
		return Code(end) is '\n' or End;
	}

	/*
	The hint for a `//` comment, as in JavaScript.
	*/
	private string SlashCommentHint() => Code() == '/' && Code(_index + 1) == '/' ? ". A comment starts with “#”" : "";

	private string DescribeHere()
	{
		if (IsAtEnd)
		{
			return "the end of the document";
		}

		var code = Message.CodePointAt(_source, _index);
		return code == '\n' ? "a line break" : Message.DescribeCharacter(code);
	}

	/*
	Reads a string. Without `isBuilding`, it is only checked, and the result is empty.
	*/
	private string ParseString(bool isBuilding)
	{
		var code = Code();

		if (Code(_index + 1) == code && Code(_index + 2) == code)
		{
			return ParseBlockString((char)code, isBuilding);
		}

		return code == '\'' ? ParseLiteralString(isBuilding) : ParseEscapedString(isBuilding);
	}

	/*
	`'...'` has no escapes, so its value is the source text between the quotes.
	*/
	private string ParseLiteralString(bool isBuilding = true)
	{
		var start = _index;
		// Stops at the first quote or line break, so that a one-line document with many strings stays linear.
		var length = _source.AsSpan(start + 1).IndexOfAny('\n', '\'');

		if (length == -1 || _source[start + 1 + length] == '\n')
		{
			throw Failure("Unterminated string. A '...' string must end on the line it starts on; use a block string (''') for multiple lines", start);
		}

		_index = start + 2 + length;
		return isBuilding ? _source.Substring(start + 1, length) : "";
	}

	private string ParseEscapedString(bool isBuilding = true)
	{
		var start = _index;
		var chunkStart = start + 1;
		StringBuilder? value = null;

		while (true)
		{
			var length = _source.AsSpan(chunkStart).IndexOfAny(EscapedStringSpecial);
			var special = length == -1 ? -1 : chunkStart + length;

			if (special == -1 || _source[special] == '\n')
			{
				throw Failure("Unterminated string. A \"...\" string must end on the line it starts on; use a block string (\"\"\") for multiple lines", start);
			}

			if (_source[special] == '"')
			{
				_index = special + 1;

				if (!isBuilding)
				{
					return "";
				}

				if (value is null)
				{
					return _source[chunkStart..special];
				}

				return value.Append(_source, chunkStart, special - chunkStart).ToString();
			}

			if (isBuilding)
			{
				value ??= new StringBuilder();
				value.Append(_source, chunkStart, special - chunkStart);
			}

			chunkStart = ParseEscape(special, value);
		}
	}

	/*
	Decodes the escape at `offset`, which is a backslash, into `value`, or only checks it when `value` is `null`, and returns the index after it.
	*/
	private int ParseEscape(int offset, StringBuilder? value)
	{
		var character = Code(offset + 1);

		switch (character)
		{
			case '\\':
			{
				value?.Append('\\');
				return offset + 2;
			}
			case '"':
			{
				value?.Append('"');
				return offset + 2;
			}
			case 'n':
			{
				value?.Append('\n');
				return offset + 2;
			}
			case 't':
			{
				value?.Append('\t');
				return offset + 2;
			}
			case 'u':
			{
				break;
			}
			case 'r':
			{
				throw Failure("There is no \\r escape, because a carriage return cannot be represented", offset);
			}
			case '\'':
			{
				throw Failure("A ' needs no escape inside \"...\"", offset);
			}
			case '\n' or End:
			{
				// A backslash at the end of the document is the same mistake as one at the end of a line.
				throw Failure("A backslash must be followed by an escape character. Use \\\\ for a literal backslash, or a '...' string", offset);
			}
			default:
			{
				throw Failure($"Unknown escape “\\{char.ConvertFromUtf32(Message.CodePointAt(_source, offset + 1))}”. The escapes are \\\\, \\\", \\n, \\t, and \\u{{…}}; use a '...' string for literal backslashes", offset);
			}
		}

		// `u{` followed by one to six hexadecimal digits and `}`. Uppercase digits too, so that the value of such an escape is checked before its spelling.
		var hexStart = offset + 3;
		var hexEnd = hexStart;

		if (Code(offset + 2) == '{')
		{
			while (hexEnd < hexStart + 6 && IsHexDigit(Code(hexEnd)))
			{
				hexEnd++;
			}
		}

		if (Code(offset + 2) != '{' || hexEnd == hexStart || Code(hexEnd) != '}')
		{
			// These are the same 12 characters after the backslash that the reference looks at, which hold a JSON surrogate pair such as `uD83D\uDE00`.
			throw Failure(DescribeBadUnicodeEscape(_source.AsSpan(offset + 1, Math.Min(12, _source.Length - offset - 1))), offset);
		}

		// Note: Not using int.Parse with AllowHexSpecifier because these one-to-six digits are already validated and it offered no clear speedup on .NET 10 ARM64.
		var hex = _source.AsSpan(hexStart, hexEnd - hexStart);
		var codePoint = 0;

		foreach (var digit in hex)
		{
			codePoint = (codePoint * 16) + (digit <= '9' ? digit - '0' : (digit | 0x20) - 'a' + 10);
		}

		// The value is checked before the spelling, so that the uppercase and leading zeros errors never point to an escape that is not allowed either.
		if (codePoint == 0x0D)
		{
			throw Failure($"A carriage return (U+000D) cannot be represented, so \\u{{{hex}}} is not allowed", offset);
		}

		if (codePoint is >= 0xD800 and <= 0xDFFF)
		{
			throw Failure($"\\u{{{hex}}} is a surrogate, which is not a Unicode scalar value", offset);
		}

		if (codePoint > 0x10FFFF)
		{
			throw Failure($"\\u{{{hex}}} is above U+10FFFF, the largest Unicode scalar value", offset);
		}

		if (hex.IndexOfAnyInRange('A', 'F') != -1)
		{
			throw Failure("A Unicode escape uses lowercase hexadecimal digits", offset);
		}

		if (hex.Length > 1 && hex[0] == '0')
		{
			var trimmed = hex.TrimStart('0');
			throw Failure($"A Unicode escape may not have leading zeros; write \\u{{{(trimmed.Length > 0 ? trimmed : "0".AsSpan())}}}", offset);
		}

		if (codePoint > 0xFFFF)
		{
			if (value is not null)
			{
				Span<char> surrogatePair = stackalloc char[2];
				var length = new Rune(codePoint).EncodeToUtf16(surrogatePair);
				value.Append(surrogatePair[..length]);
			}
		}
		else
		{
			value?.Append((char)codePoint);
		}

		return hexEnd + 1;
	}

	/*
	A block string opens with a run of three or more quotes and a line break, and closes at the first line whose first non-whitespace content is a run of exactly that many quotes. The closing line's indentation is removed from every content line, except a blank one, which holds only spaces and tabs and becomes an empty line.
	*/
	private string ParseBlockString(char quote, bool isBuilding)
	{
		var source = _source;
		var start = _index;
		var delimiterLength = QuoteRunLength(source, start, quote);

		_index = start + delimiterLength;

		if (IsAtEnd)
		{
			throw Failure("Unterminated block string", start);
		}

		// As in Swift, the opening delimiter is followed directly by a line break, not even by trailing whitespace.
		if (Code() != '\n')
		{
			throw Failure("A block string's opening delimiter must be followed directly by a line break, and its content starts on the next line");
		}

		var contentStart = _index + 1;
		var closing = FindBlockStringEnd(source, contentStart, quote, delimiterLength);

		if (closing is null)
		{
			throw Failure($"Unterminated block string{DescribeInlineClosingDelimiter(contentStart, quote, delimiterLength)}", start);
		}

		var (closingLineStart, delimiterStart) = closing.Value;
		var indentation = source.AsSpan(closingLineStart, delimiterStart - closingLineStart);
		_index = delimiterStart + delimiterLength;

		var value = isBuilding ? new StringBuilder(closingLineStart - contentStart) : null;
		// Blank lines directly after the opening delimiter and directly before the closing one are not content, so a blank line is only written when a content line follows it.
		var hasContent = false;
		var blankLineCount = 0;
		// Every line's indentation is checked before its escapes, as the reference does, so a bad escape is only thrown after the last line.
		ParseFailure? escapeFailure = null;

		var lineEnd = contentStart;

		for (var lineStart = contentStart; lineStart < closingLineStart; lineStart = lineEnd + 1)
		{
			lineEnd = FindLineEnd(source, lineStart);
			var line = source.AsSpan(lineStart, lineEnd - lineStart);

			// A blank line, which is empty or holds only spaces and tabs, may leave out the indentation, and it becomes an empty line. Swift is stricter here: there only a completely empty line may leave it out.
			if (IsBlankLine(line))
			{
				blankLineCount += hasContent ? 1 : 0;
				continue;
			}

			if (!line.StartsWith(indentation))
			{
				throw Failure("This line does not start with the indentation of its block string's closing delimiter. Every line except a blank one must start with exactly the same spaces and tabs", lineStart);
			}

			if (escapeFailure is not null)
			{
				continue;
			}

			if (hasContent)
			{
				value?.Append('\n', blankLineCount + 1);
			}

			hasContent = true;
			blankLineCount = 0;

			if (quote == '\'')
			{
				value?.Append(source, lineStart + indentation.Length, lineEnd - lineStart - indentation.Length);
				continue;
			}

			try
			{
				UnescapeLine(lineStart + indentation.Length, lineEnd, value);
			}
			catch (ParseFailure failure)
			{
				escapeFailure = failure;
			}
		}

		if (escapeFailure is not null)
		{
			throw escapeFailure;
		}

		return value?.ToString() ?? "";
	}

	/*
	The hint for a content line that ends with the delimiter, as TOML allows. Adding a closing line after it would keep the delimiter as content.
	*/
	private string DescribeInlineClosingDelimiter(int contentStart, char quote, int delimiterLength)
	{
		var source = _source;
		var line = source.AsSpan(0, contentStart).Count('\n') + 1;

		var lineEnd = contentStart;

		for (var lineStart = contentStart; lineStart < source.Length; lineStart = lineEnd + 1)
		{
			lineEnd = FindLineEnd(source, lineStart);
			var textStart = SkipSpaces(source, lineStart);
			var textEnd = Math.Max(textStart, SkipSpacesBack(source, lineEnd));
			var text = source.AsSpan(textStart, textEnd - textStart);

			// A line of only quotes, longer than the delimiter, is content.
			if (text.Length >= delimiterLength && !text[^delimiterLength..].ContainsAnyExcept(quote) && text.ContainsAnyExcept(quote))
			{
				return $". Its closing delimiter must start a line, so move the {Message.Abbreviate(source.AsSpan(contentStart - delimiterLength - 1, delimiterLength))} at the end of line {line} to a new line";
			}

			line++;
		}

		return "";
	}

	private void UnescapeLine(int start, int end, StringBuilder? value)
	{
		var chunkStart = start;

		while (true)
		{
			var length = _source.AsSpan(chunkStart, end - chunkStart).IndexOf('\\');

			if (length == -1)
			{
				value?.Append(_source, chunkStart, end - chunkStart);
				return;
			}

			value?.Append(_source, chunkStart, length);
			chunkStart = ParseEscape(chunkStart + length, value);
		}
	}

	private SomlValue ParseNumberOrTemporal(int firstCode)
	{
		var plain = ParsePlainNumber(firstCode);

		if (plain is not null)
		{
			return plain.Value;
		}

		var start = _index;
		var end = FindNumberEnd(_source, start);
		var text = _source.AsSpan(start, end - start);
		_index = end;

		if (HasDatePrefix(text))
		{
			// Preserve instant separator guidance only for a space followed by HH:MM. An ordinary next scalar gets the collection separator diagnostic.
			var timeStart = _index + 1;
			var hasTime = CodeAt(_source, _index) == ' ' && IsDigits(_source, timeStart, 2) && CodeAt(_source, timeStart + 2) == ':' && IsDigits(_source, timeStart + 3, 2);

			if (text.Length == 10 && !hasTime)
			{
				var year = ReadDigits(text, 0, 4);
				var month = ReadDigits(text, 5, 2);
				var day = ReadDigits(text, 8, 2);
				var problem = DateProblem(year, month, day);

				if (problem is not null)
				{
					throw Failure($"Invalid date {text}: {problem}", start);
				}

				return new DateOnly(year, month, day);
			}

			return ParseInstant(text, start);
		}

		var kind = ClassifyNumber(text);

		if (kind == NumberKind.None && IsDurationLike(text))
		{
			return ParseDuration(text, start);
		}

		switch (kind)
		{
			case NumberKind.Integer:
			{
				if (text.SequenceEqual("-0"))
				{
					throw Failure("“-0” is not allowed, because zero has one spelling: 0", start);
				}

				return ParseInteger(text, start);
			}
			case NumberKind.Float:
			{
				var value = ParseFloat(text);

				if (double.IsInfinity(value))
				{
					throw Failure($"{Message.Abbreviate(text)} is too large to be a finite float. Use {(value < 0 ? "-infinity" : "infinity")} if you mean it", start);
				}

				if (value == 0)
				{
					// A nonzero digit before the exponent means the literal is not zero, so it underflowed.
					var exponent = text.IndexOf('e');

					if ((exponent == -1 ? text : text[..exponent]).IndexOfAnyInRange('1', '9') != -1)
					{
						throw Failure($"{Message.Abbreviate(text)} is too small to be told apart from zero. Write 0.0 if you mean zero", start);
					}

					// Negative zero is the same value as zero.
					return SomlValue.FromFloat(0);
				}

				return SomlValue.FromFloat(value);
			}
			default:
			{
				throw Failure(DescribeThousandsSeparator(text, start) ?? DescribeBadNumber(text, UnquotedText(start)), start);
			}
		}
	}

	/*
	Reads a float whose grammar is already checked, removing digit separators only when present. Short tokens use a stack buffer; longer ones need one character buffer instead of temporary strings.
	*/
	private static double ParseFloat(ReadOnlySpan<char> text)
	{
		const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

		if (!text.Contains('_'))
		{
			return double.Parse(text, styles, CultureInfo.InvariantCulture);
		}

		var rented = text.Length <= 256 ? null : ArrayPool<char>.Shared.Rent(text.Length);
		Span<char> digits = rented is null ? stackalloc char[text.Length] : rented;
		var length = 0;

		try
		{
			// Note: Not using span Split plus CopyTo because it was slower than filtering these digit separators on .NET 10 ARM64.
			foreach (var character in text)
			{
				if (character != '_')
				{
					digits[length++] = character;
				}
			}

			return double.Parse(digits[..length], styles, CultureInfo.InvariantCulture);
		}
		finally
		{
			if (rented is not null)
			{
				digits[..length].Clear();
				ArrayPool<char>.Shared.Return(rented);
			}
		}
	}

	/*
	The common case, a short decimal int or float such as `8080`, `-3`, or `30.5`, without the general path's maximal-run scan and classification. Anything else, including every error, returns `null` and is left to the general path.
	*/
	private SomlValue? ParsePlainNumber(int firstCode)
	{
		var source = _source;
		var start = _index;
		var index = firstCode == '-' ? start + 1 : start;
		var integerStart = index;
		ulong integer = 0;

		while (IsDigit(Code(index)))
		{
			// At most 19 digits, so the unsigned magnitude cannot overflow.
			if (index - integerStart >= 19)
			{
				return null;
			}

			integer = (integer * 10) + (uint)(source[index] - '0');
			index++;
		}

		var integerLength = index - integerStart;

		// No digits, or a leading zero followed by more digits, which the general path reports as an error or reads as an instant before the year 1000.
		if (integerLength == 0 || (integerLength > 1 && source[integerStart] == '0'))
		{
			return null;
		}

		var isFloat = false;

		if (Code(index) == '.')
		{
			var fractionStart = index + 1;
			index = fractionStart;

			while (IsDigit(Code(index)))
			{
				index++;
			}

			if (index == fractionStart)
			{
				return null;
			}

			isFloat = true;
		}

		// A character that may continue a number, such as `e`, `_`, or `-`, needs the general path.
		if (index - integerStart > 30 || !IsValueEnd(Code(index)))
		{
			return null;
		}

		if (isFloat)
		{
			var value = double.Parse(source.AsSpan(start, index - start), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
			_index = index;

			// Negative zero is the same value as zero.
			return SomlValue.FromFloat(value + 0.0);
		}

		// `-0` is an error, which the general path reports.
		if (integer == 0 && index - start > 1)
		{
			return null;
		}

		var isNegative = integerStart > start;

		if (integer > (isNegative ? (ulong)long.MaxValue + 1 : long.MaxValue))
		{
			return null;
		}

		_index = index;
		return isNegative ? unchecked((long)(0UL - integer)) : (long)integer;
	}

	/*
	An int in any radix, which the grammar already matched. Its magnitude is accumulated as unsigned, so that the smallest int, whose magnitude does not fit a long, is read too.

	Note: Not using the radix overload of Convert.ToUInt64 because it needs a normalized string without underscores and was slower on .NET 10 ARM64. This loop detects overflow without copying an arbitrarily long token.
	*/
	private static SomlValue ParseInteger(ReadOnlySpan<char> text, int start)
	{
		var isNegative = text[0] == '-';
		var index = isNegative ? 1 : 0;
		var radix = 10;

		if (text.Length > 1 && text[0] == '0' && text[1] is 'x' or 'o' or 'b')
		{
			radix = text[1] switch
			{
				'x' => 16,
				'o' => 8,
				_ => 2,
			};

			index = 2;
		}

		ulong magnitude = 0;
		var limit = isNegative ? (ulong)long.MaxValue + 1 : long.MaxValue;
		var maximumPrefix = limit / (uint)radix;
		var maximumLastDigit = limit % (uint)radix;

		for (; index < text.Length; index++)
		{
			var code = text[index];

			if (code == '_')
			{
				continue;
			}

			// Hexadecimal digits are uppercase, which `ClassifyNumber` already required.
			var digit = code <= '9' ? code - '0' : code - 'A' + 10;
			if (magnitude > maximumPrefix || (magnitude == maximumPrefix && (uint)digit > maximumLastDigit))
			{
				throw Failure($"The integer {Message.Abbreviate(text)} is outside the 64-bit range {Message.IntRange}", start);
			}

			magnitude = (magnitude * (uint)radix) + (uint)digit;
		}

		return isNegative ? unchecked((long)(0UL - magnitude)) : (long)magnitude;
	}

	/*
	An offset on its own after spaces, as in `2026-09-19T14:00:00 Z`, which belongs directly after the time, or `null`.
	*/
	private string? FindOffsetAfterSpace()
	{
		var offsetStart = SkipSpaces(_source, _index);

		if (offsetStart == _index)
		{
			return null;
		}

		var offset = Code(offsetStart) == 'Z' ? "Z" : Code(offsetStart) is '+' or '-' && IsDigits(_source, offsetStart + 1, 2) && Code(offsetStart + 3) == ':' && IsDigits(_source, offsetStart + 4, 2) ? _source.Substring(offsetStart, 6) : null;
		return offset is not null && IsValueEndAt(_source, offsetStart + offset.Length) ? offset : null;
	}

	private SomlValue ParseInstant(ReadOnlySpan<char> text, int start)
	{
		if (text.Length > MaxDiagnosedLength || !MatchInstant(text, out var instant))
		{
			// A date, a space, and a time, as TOML and Python write a date and time.
			var timeStart = _index + 1;
			var end = Code() == ' ' && IsDigit(Code(timeStart)) ? FindNumberEnd(_source, timeStart) : _index;
			var time = end > timeStart ? Message.Abbreviate(_source.AsSpan(timeStart, end - timeStart), MaxDiagnosedLength + 1) : "";
			// More of the instant may follow, as in `14:00:00,5Z` or `14:00:00[Europe/Oslo]`, so it may have an offset.
			var isWholeValue = IsValueEndAt(_source, end) && !(Code(end) == ',' && IsDigit(Code(end + 1)));
			throw Failure(DescribeBadInstant(text, time, isWholeValue, FindOffsetAfterSpace()), start);
		}

		var reason = instant.Validate(out var value);

		if (reason is not null)
		{
			throw Failure($"Invalid instant {Message.Abbreviate(text)}: {reason}", start);
		}

		return value;
	}

	/*
	A scan of the parts in order, so that each error names the part it is about.
	*/
	private static SomlValue ParseDuration(ReadOnlySpan<char> text, int start)
	{
		var isNegative = text[0] == '-';
		var index = isNegative ? 1 : 0;
		var previousSize = long.MaxValue;
		Int128 total = 0;
		// The fraction of the last part, and that part's unit size. Only the last part may have one, so the value checks wait until every part is read.
		ReadOnlySpan<char> fraction = [];
		long size = 0;

		while (index < text.Length)
		{
			var digitsEnd = SkipIntegerPart(text, index);
			var next = CodeAt(text, digitsEnd);

			if (digitsEnd == index)
			{
				var code = CodeAt(text, index);

				if (code == '-' && IsDigit(CodeAt(text, index + 1)))
				{
					throw DurationFailure(text, start, "only the whole duration takes a sign, as in -1h30m");
				}

				throw DurationFailure(text, start, code == '+' ? "a “+” sign is not allowed" : $"expected a number at “{Message.Abbreviate(text[index..], 10)}”");
			}

			if (text[index] == '0' && (next == '_' || IsDigit(next)))
			{
				throw DurationFailure(text, start, "leading zeros are not allowed");
			}

			if (next == '_')
			{
				throw DurationFailure(text, start, "an underscore must be between two digits");
			}

			fraction = [];
			var unitStart = digitsEnd;

			if (next == '.')
			{
				unitStart = SkipDigits(text, digitsEnd + 1);

				if (unitStart == digitsEnd + 1)
				{
					throw DurationFailure(text, start, "a “.” must be followed by a digit");
				}

				if (CodeAt(text, unitStart) == '_')
				{
					throw DurationFailure(text, start, "an underscore must be between two digits");
				}

				// Trailing zeros change nothing, and leaving them out keeps the arithmetic small however many there are.
				fraction = text[(digitsEnd + 1)..unitStart].TrimEnd("0_");
			}

			var unitEnd = unitStart;

			while (IsLetter(CodeAt(text, unitEnd)))
			{
				unitEnd++;
			}

			var unit = text[unitStart..unitEnd];
			var unitSize = DurationUnitSize(unit);

			if (unitSize == 0)
			{
				throw DurationFailure(text, start, DescribeBadDurationUnit(Message.Abbreviate(unit, 10), Message.Abbreviate(text, MaxDiagnosedLength + 1)));
			}

			if (unitSize >= previousSize)
			{
				throw DurationFailure(text, start, "the units are in the order h, m, s, ms, us, ns, and each appears at most once");
			}

			if (next == '.' && IsDigit(CodeAt(text, unitEnd)))
			{
				throw DurationFailure(text, start, "only the last part may have a fraction");
			}

			size = unitSize;
			var digits = text[index..digitsEnd];
			// More than 19 digits is out of range in any unit.
			total += digits.Length - digits.Count('_') > 19 ? (Int128)long.MaxValue * 2 : (Int128)ParseDigits(digits) * size;
			index = unitEnd;
			previousSize = unitSize;
		}

		// More than 13 fraction digits without a trailing zero is finer than a nanosecond in any unit.
		var fractionDigits = fraction.Length - fraction.Count('_');

		if (fractionDigits > 13)
		{
			throw DurationFailure(text, start, "it is not a whole number of nanoseconds");
		}

		if (fraction.Length > 0)
		{
			// Note: Not using Math.Pow plus conversion to Int128 because it was slower for these fourteen exact scales on .NET 10 ARM64.
			Int128 scale = 1;

			for (var digit = 0; digit < fractionDigits; digit++)
			{
				scale *= 10;
			}

			var fractionNanoseconds = (Int128)ParseDigits(fraction) * size;

			if (fractionNanoseconds % scale != 0)
			{
				throw DurationFailure(text, start, "it is not a whole number of nanoseconds");
			}

			total += fractionNanoseconds / scale;
		}

		if (isNegative && total == 0)
		{
			throw DurationFailure(text, start, "“-” is not allowed before zero, because zero has one spelling: 0s");
		}

		if (total > (isNegative ? (Int128)long.MaxValue + 1 : long.MaxValue))
		{
			throw DurationFailure(text, start, "it is outside the 64-bit range of nanoseconds, about 292 years either way");
		}

		return new SomlDuration((long)(isNegative ? -total : total));
	}

	private static ParseFailure DurationFailure(ReadOnlySpan<char> text, int start, string reason) => Failure($"Invalid duration {Message.Abbreviate(text)}: {reason}", start);

	private static ulong ParseDigits(ReadOnlySpan<char> digits)
	{
		// Note: Not using ulong.Parse with an underscore NumberGroupSeparator because it needs another format provider and, despite avoiding copying, was slower in .NET 10 ARM64 probes.
		ulong value = 0;

		foreach (var digit in digits)
		{
			if (digit != '_')
			{
				value = (value * 10) + (uint)(digit - '0');
			}
		}

		return value;
	}

	/*
	The length of a duration unit in nanoseconds, or 0 when the text is not a unit.
	*/
	private static long DurationUnitSize(ReadOnlySpan<char> unit) => unit switch
	{
		"h" => 3_600_000_000_000,
		"m" => 60_000_000_000,
		"s" => 1_000_000_000,
		"ms" => 1_000_000,
		"us" => 1000,
		"ns" => 1,
		_ => 0,
	};

	/*
	Whether `text` is one valid value, so that an error message only suggests a fix that works. Suggestions beyond the diagnosed token limit are best effort and are not reparsed.
	*/
	private static bool IsValidValue(string text)
	{
		if (text.Length > MaxDiagnosedLength)
		{
			return false;
		}

		try
		{
			// A bare value is not a document, so validate it as the only item of an implicit array. Requiring the end rejects text that holds more than one item, such as `1, 2`.
			var parser = new Parser(text, isValidating: true);
			parser.SkipTrivia();
			parser.ParseValue(2);
			parser.SkipTrivia();
			return parser.IsAtEnd;
		}
		catch (ParseFailure)
		{
			return false;
		}
	}

	private enum NumberKind
	{
		None,
		Integer,
		Float,
	}

	/*
	Whether `text` is an int, in any radix, or a float, following the grammar. A run of digits may have single underscores between digits.

	Note: Not using GeneratedRegex for number classification because it preserved sampled classification but was slower in .NET 10 ARM64 probes, and durations still need the shared digit scanners.
	*/
	private static NumberKind ClassifyNumber(ReadOnlySpan<char> text)
	{
		if (text.Length > 1 && text[0] == '0')
		{
			var radix = text[1] switch
			{
				'x' => 16,
				'o' => 8,
				'b' => 2,
				_ => 0,
			};

			if (radix != 0)
			{
				var end = SkipDigits(text, 2, radix);
				return end > 2 && end == text.Length ? NumberKind.Integer : NumberKind.None;
			}
		}

		var index = text[0] == '-' ? 1 : 0;

		// The integer part is `0` or has no leading zero.
		var integerEnd = SkipIntegerPart(text, index);

		if (integerEnd == index)
		{
			return NumberKind.None;
		}

		index = integerEnd;
		var kind = NumberKind.Integer;

		if (CodeAt(text, index) == '.')
		{
			var end = SkipDigits(text, index + 1);

			if (end == index + 1)
			{
				return NumberKind.None;
			}

			index = end;
			kind = NumberKind.Float;
		}

		if (CodeAt(text, index) == 'e')
		{
			var isNegative = CodeAt(text, index + 1) == '-';
			var exponentStart = index + (isNegative ? 2 : 1);
			// The exponent follows the same leading-zero rule as the integer part, and its zero has one spelling, `e0`, so `e-0` is not an exponent.
			var end = isNegative && CodeAt(text, exponentStart) == '0' ? exponentStart : SkipIntegerPart(text, exponentStart);

			if (end == exponentStart)
			{
				return NumberKind.None;
			}

			index = end;
			kind = NumberKind.Float;
		}

		return index == text.Length ? kind : NumberKind.None;
	}

	/*
	Returns the index after a run of digits that may have single underscores between them, or `index` when there is no digit there.
	*/
	private static int SkipDigits(ReadOnlySpan<char> text, int index, int radix = 10)
	{
		if (!IsDigit(CodeAt(text, index), radix))
		{
			return index;
		}

		index++;

		while (true)
		{
			var code = CodeAt(text, index);

			if (IsDigit(code, radix))
			{
				index++;
			}
			else if (code == '_' && IsDigit(CodeAt(text, index + 1), radix))
			{
				index += 2;
			}
			else
			{
				return index;
			}
		}
	}

	/*
	Returns the index after an integer part, which is `0` or digits without a leading zero, or `index` when there is none. Anything after a leading `0`, such as the `5` in `05`, is left for the caller to reject.
	*/
	private static int SkipIntegerPart(ReadOnlySpan<char> text, int index) => CodeAt(text, index) == '0' ? index + 1 : SkipDigits(text, index);

	/*
	Whether a token that is not a number or an instant was meant as a duration: its first letter could begin a unit, or a day or a week, or it is a year unit. A radix prefix, an exponent, and other letters, such as the `T` in `20260919T140000Z` or the `x` in `1.5x`, are left to the number errors.
	*/
	private static bool IsDurationLike(ReadOnlySpan<char> text)
	{
		var start = text[0] == '-' ? 1 : 0;

		if (!IsDigit(CodeAt(text, start)))
		{
			return false;
		}

		var index = start;

		while (IsDigit(CodeAt(text, index)) || CodeAt(text, index) == '_')
		{
			index++;
		}

		if (CodeAt(text, index) == '.')
		{
			index++;

			while (IsDigit(CodeAt(text, index)) || CodeAt(text, index) == '_')
			{
				index++;
			}
		}

		// A year unit, as in `1y` or `2years`, is only one when the token or another part follows it, so that a word such as `100yen` stays a string to quote. The longest unit and the character after it fit in 6 characters.
		return CodeAt(text, index) is 'd' or 'h' or 'm' or 'n' or 's' or 'u' or 'w' or 'D' or 'H' or 'M' or 'N' or 'S' or 'U' or 'W' || YearUnitPrefixRegex().IsMatch(text.Slice(index, Math.Min(6, text.Length - index)));
	}

	private static int CodeAt(ReadOnlySpan<char> text, int index) => (uint)index < (uint)text.Length ? text[index] : End;

	/*
	The number of spaces and tabs before `offset` on its line, or `null` when something else comes before it.
	*/
	private static int? GetIndentation(string source, int offset)
	{
		var lineStart = offset == 0 ? 0 : source.LastIndexOf('\n', offset - 1) + 1;
		return SkipSpaces(source, lineStart) == offset ? offset - lineStart : null;
	}

	private static bool IsWordsWithSpaces(ReadOnlySpan<char> text)
	{
		// Note: Not using another SearchValues set or splitting words to reuse the bare-key set because the former added first-parse allocation and the latter helped long hints but had mixed short-hint results on .NET 10 ARM64.
		foreach (var code in text)
		{
			if (code is not (' ' or '\t') && !IsBareKeyCharacter(code))
			{
				return false;
			}
		}

		return true;
	}

	internal static int DaysInMonth(int year, int month)
	{
		// Note: Not using DateTime.DaysInMonth because its primitive gain did not establish a public parsing benefit in .NET 10 ARM64 comparisons.
		if (month == 2)
		{
			var isLeapYear = year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
			return isLeapYear ? 29 : 28;
		}

		return month is 4 or 6 or 9 or 11 ? 30 : 31;
	}

	/*
	The example in a message that a string must be quoted, which quotes `text` as a `'...'` string. That cannot hold a `'`, so then there is no simple example.
	*/
	private static string QuotingExample(string text) => text.Contains('\'') ? "" : $", as in '{Message.Abbreviate(text)}'";

	internal static bool IsBareKeyCharacter(int code) => code is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-';

	private static bool IsDigit(int code) => code is >= '0' and <= '9';

	private static bool IsLetter(int code) => code is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');

	private static bool IsDigit(int code, int radix) => ((uint)(code - '0') < (uint)radix && code <= '9') || (radix == 16 && code is >= 'A' and <= 'F');

	private static bool IsHexDigit(int code) => char.IsAsciiHexDigit((char)code);

	/*
	Whether a character may directly follow a scalar value: whitespace, a separator, a closing bracket, a comment, or the end.
	*/
	private static bool IsValueEnd(int code) => code is ' ' or '\t' or '\n' or ',' or ']' or '}' or '#' or '/' or End;

	/*
	Whether a value ends at `offset`, so that a suggestion that ends there leaves nothing out. A `/` only starts a comment with a `*` after it, or a `//` comment of another language, which the error for what follows names, so in `km/h` the text goes on.
	*/
	private static bool IsValueEndAt(string source, int offset)
	{
		var code = CodeAt(source, offset);
		return IsValueEnd(code) && (code != '/' || CodeAt(source, offset + 1) is '*' or '/');
	}

	/*
	The characters that may be meant as part of a key: visible ones that have no meaning in the grammar, which includes every bare key character. A `=` is left out, because after a key, as in `name=foo`, it was most likely meant as the `:` of INI and TOML, so a key that holds a `=` gets no quoting hint.
	*/
	private static bool IsQuotableKeyCharacter(int codePoint) => codePoint is not (' ' or '"' or '#' or '\'' or '*' or ',' or '/' or ':' or '=' or '[' or ']' or '{' or '}') && !UnicodeTable.IsInvisible(codePoint);

	/*
	A number, an instant, or a duration is lexed as one maximal run of these characters, and then validated as a whole.
	*/
	private static bool IsNumberCharacter(int code) => IsBareKeyCharacter(code) || code is '.' or ':' or '+';

	/*
	The end of the number, instant, or duration token that starts at `start`, which is a digit, `-`, or the `i` of `infinity`. In a valid document, it is also the end of a keyword, because a keyword is made of these characters and no character that may follow a value is one of them.
	*/
	internal static int FindNumberEnd(string source, int start)
	{
		var end = source.AsSpan(start + 1).IndexOfAnyExcept(NumberTokenCharacters);
		return end < 0 ? source.Length : start + 1 + end;
	}

	/*
	The end of a bare key. The parser and the syntax builder use the same character set and scan.
	*/
	internal static int FindBareKeyEnd(string source, int start)
	{
		var end = source.AsSpan(start).IndexOfAnyExcept(BareKeyCharacters);
		return end < 0 ? source.Length : start + end;
	}

	/*
	Skips spaces, tabs, and line feeds, but not comments.
	*/
	internal static int SkipWhitespace(string source, int start)
	{
		var end = source.AsSpan(start).IndexOfAnyExcept(' ', '\t', '\n');
		return end < 0 ? source.Length : start + end;
	}

	/*
	Counts the consecutive quotes of a block-string delimiter.
	*/
	internal static int QuoteRunLength(string source, int start, char quote)
	{
		var end = source.AsSpan(start).IndexOfAnyExcept(quote);
		return end < 0 ? source.Length - start : end;
	}

	/*
	The end of the comment that starts at `offset` in a document that is already checked: after the closing of a block comment, or at the line feed that ends a line comment. It is -1 when no comment starts there.
	*/
	internal static int FindCommentEnd(string source, int offset) => offset >= source.Length ? -1 : source[offset] switch
	{
		'#' => FindLineEnd(source, offset),
		'/' when offset + 1 < source.Length && source[offset + 1] == '*' => source.IndexOf("*/", offset + 2, StringComparison.Ordinal) + 2,
		_ => -1,
	};

	/*
	The offset of the line feed that ends the line containing `offset`, or the length of `source` on the last line.
	*/
	internal static int FindLineEnd(string source, int offset)
	{
		if (offset >= source.Length)
		{
			return source.Length;
		}

		var end = source.IndexOf('\n', offset);
		return end == -1 ? source.Length : end;
	}

	/*
	Finds the closing delimiter of a block string: the first line from `offset` on that holds, after zero or more spaces and tabs, a run of exactly `length` of the `quote` character. A longer run is content. Returns where that line and its delimiter start, or `null` when no line closes the block.
	*/
	internal static (int LineStart, int DelimiterStart)? FindBlockStringEnd(string source, int offset, char quote, int length)
	{
		for (var lineStart = offset; lineStart < source.Length; lineStart = FindLineEnd(source, lineStart) + 1)
		{
			var delimiterStart = SkipSpaces(source, lineStart);
			var runLength = QuoteRunLength(source, delimiterStart, quote);

			if (runLength == length)
			{
				return (lineStart, delimiterStart);
			}
		}

		// Past the last line, either because the source ended with a line break or because the last line was content.
		return null;
	}

	/*
	The index after the spaces and tabs from `index` on.
	*/
	internal static int SkipSpaces(string text, int index)
	{
		var end = text.AsSpan(index).IndexOfAnyExcept(' ', '\t');
		return end < 0 ? text.Length : index + end;
	}

	/*
	The index after the last character before `index` that is not a space or a tab.
	*/
	internal static int SkipSpacesBack(string text, int index)
	{
		return text.AsSpan(0, index).LastIndexOfAnyExcept(' ', '\t') + 1;
	}

	/*
	Whether a line in a block string is blank: empty, or only spaces and tabs. A blank line becomes an empty line in the value.
	*/
	internal static bool IsBlankLine(ReadOnlySpan<char> line) => !line.ContainsAnyExcept(' ', '\t');
}

/*
A failure inside the parser, with its reason and UTF-16 offset. The entry points turn it into a `SomlParseException`.
*/
internal sealed class ParseFailure(string reason, int offset) : Exception(reason)
{
	public string Reason { get; } = reason;

	public int Offset { get; } = offset;
}
