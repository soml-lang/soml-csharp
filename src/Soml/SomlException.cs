using System.Globalization;
using System.Text;

namespace Soml;

/// <summary>
/// The exception that is thrown when a value cannot be read from or written as SOML: a document that is not valid, a value that does not fit the type it is read into, a value that SOML cannot represent, such as NaN, or an edit whose path does not fit the document.
/// </summary>
/// <remarks>
/// A document that is not valid throws the derived <see cref="SomlParseException"/>, which also has the position of the error.
/// </remarks>
public class SomlException : Exception
{
	/// <summary>
	/// Creates an exception with a message.
	/// </summary>
	public SomlException(string message) : base(message)
	{
	}

	/// <summary>
	/// Creates an exception with a message and the exception that caused it.
	/// </summary>
	public SomlException(string message, Exception? innerException) : base(message, innerException)
	{
	}

	/// <summary>
	/// Creates an exception.
	/// </summary>
	public SomlException()
	{
	}

	/*
	An error about the value at a path, whose message ends with the path, and with its line and column in the document when they are known.
	*/
	internal SomlException(string reason, IReadOnlyList<object> pathSegments, bool isAboutKey = false, (int Line, int Column)? location = null, Exception? innerException = null) : this(reason, pathSegments, SomlWriter.FormatPath(pathSegments), isAboutKey, location, innerException)
	{
	}

	/*
	Copies the writer's compact path only when there is an error, shared by document and typed serialization.
	*/
	internal SomlException(string reason, ReadOnlySpan<(string? Key, int Index)> path, bool isAboutKey = false, Exception? innerException = null) : this(reason, CopyPath(path), isAboutKey, innerException: innerException)
	{
	}

	private static object[] CopyPath(ReadOnlySpan<(string? Key, int Index)> path)
	{
		if (path.IsEmpty)
		{
			return [];
		}

		var result = new object[path.Length];

		for (var index = 0; index < path.Length; index++)
		{
			result[index] = path[index].Key ?? (object)path[index].Index;
		}

		return result;
	}

	private SomlException(string reason, IReadOnlyList<object> pathSegments, string? path, bool isAboutKey, (int Line, int Column)? location, Exception? innerException) : base(Compose(reason, path, location), innerException)
	{
		_reason = reason;
		PathSegments = pathSegments;
		IsAboutKey = isAboutKey;
		Path = path;
	}

	private readonly string? _reason;

	/*
	What is wrong, without the path, for an error about a value at a path.
	*/
	internal string? PathlessReason => _reason;

	/// <summary>
	/// The path of the value the error is about, such as <c>servers[0].port</c>, or <c>null</c> when the error is not about one value or is about the document's own collection.
	/// </summary>
	public string? Path { get; }

	/*
	The keys and indexes of the path, or `null` when the error is not about one value.
	*/
	internal IReadOnlyList<object>? PathSegments { get; }

	/*
	Whether the error is about the key at the end of the path, such as an unknown key, rather than about its value.
	*/
	internal bool IsAboutKey { get; }

	private static string Compose(string reason, string? path, (int Line, int Column)? location) => (path, location) switch
	{
		(null, null) => reason,
		(null, var (line, column)) => $"{reason} (at line {line}, column {column})",
		(_, null) => $"{reason} (at {path})",
		(_, var (line, column)) => $"{reason} (at {path}, line {line}, column {column})",
	};

	/*
	The same error with the line and column of its value in the document that it was read from, so that a reader can find it. The document was read without an error, so it is valid.
	*/
	internal SomlException WithLocation(string text)
	{
		if (_reason is null || PathSegments is null)
		{
			return this;
		}

		var offset = PathSegments.Count == 0 ? Parser.FindDocumentStart(text) : DocumentSyntax.ParseValid(text).GetOffsetOfPath(PathSegments, IsAboutKey);
		return new SomlException(_reason, PathSegments, Path, IsAboutKey, SomlParseException.Locate(text, offset), InnerException);
	}
}

/// <summary>
/// The exception that is thrown when a document is not valid SOML.
/// </summary>
/// <remarks>
/// <para>The <see cref="Exception.Message"/> includes the position and a code frame. Use <see cref="Reason"/> for the message alone.</para>
/// <code>
/// Unexpected “api-gateway”. A string value must be quoted, as in 'api-gateway' at line 1, column 7
///
/// &gt; 1 | name: api-gateway
///     |       ^
/// </code>
/// </remarks>
public sealed class SomlParseException : SomlException
{
	private const int ContextLines = 2;
	private const int MaxLineWidth = 100;

	internal SomlParseException(string reason, string source, int offset) : this(Sanitize(reason), source, offset, Locate(source, offset))
	{
	}

	private SomlParseException(string reason, string source, int offset, (int Line, int Column) location) : this(reason, offset, location, CreateCodeFrame(source, offset, location.Line))
	{
	}

	private SomlParseException(string reason, int offset, (int Line, int Column) location, string codeFrame) : base($"{reason} at line {location.Line}, column {location.Column}\n\n{codeFrame}")
	{
		Reason = reason;
		Line = location.Line;
		Column = location.Column;
		Offset = offset;
		CodeFrame = codeFrame;
	}

	/// <summary>
	/// What is wrong, without the position.
	/// </summary>
	public string Reason { get; }

	/// <summary>
	/// The 1-based line of the error.
	/// </summary>
	public int Line { get; }

	/// <summary>
	/// The 1-based column of the error, counted in Unicode scalar values, so an emoji is one column.
	/// </summary>
	public int Column { get; }

	/// <summary>
	/// The 0-based index of the error in the text, in UTF-16 code units. For UTF-8 input, it is the index in the decoded text.
	/// </summary>
	public int Offset { get; }

	/// <summary>
	/// Up to three lines ending at the error, with a caret under the position. A long line is clipped around the position.
	/// </summary>
	public string CodeFrame { get; }

	internal static (int Line, int Column) Locate(string source, int offset)
	{
		var before = source.AsSpan(0, offset);
		var line = before.Count('\n') + 1;
		var lineStart = before.LastIndexOf('\n') + 1;
		var column = 1;

		// Note: Not using Encoding.UTF32.GetByteCount because it adds fallback allocations and was slower on .NET 10 ARM64 than counting scalar values directly.
		// Note: Not using EnumerateRunes because it did not consistently improve public diagnostics, and counting columns needs no decoded scalar values.
		for (var index = lineStart; index < offset; index++)
		{
			// A high surrogate followed by a low one is one code point.
			if (char.IsHighSurrogate(source[index]) && index + 1 < source.Length && char.IsLowSurrogate(source[index + 1]))
			{
				index++;
			}

			column++;
		}

		return (line, column);
	}

	private static string CreateCodeFrame(string source, int offset, int line)
	{
		// The error line and up to two lines before it, found by scanning back from the error, so that an error late in a long document does not split the whole document.
		Span<int> lineStarts = stackalloc int[ContextLines + 1];
		var first = ContextLines;
		lineStarts[first] = offset == 0 ? 0 : source.LastIndexOf('\n', offset - 1) + 1;

		while (first > 0 && lineStarts[first] > 0)
		{
			var searchFrom = lineStarts[first] - 2;
			lineStarts[--first] = searchFrom < 0 ? 0 : source.LastIndexOf('\n', searchFrom) + 1;
		}

		lineStarts = lineStarts[first..];
		var firstLine = line - lineStarts.Length + 1;
		Span<char> lineNumber = stackalloc char[10];
		line.TryFormat(lineNumber, out var gutterWidth, default, CultureInfo.InvariantCulture);
		var output = new StringBuilder();

		for (var index = 0; index < lineStarts.Length; index++)
		{
			var lineStart = lineStarts[index];
			var number = firstLine + index;
			var isErrorLine = number == line;
			var lineEnd = source.IndexOf('\n', lineStart);
			var lineText = source.AsSpan(lineStart, (lineEnd == -1 ? source.Length : lineEnd) - lineStart);
			var (text, pointerOffset) = Clip(lineText, isErrorLine ? offset - lineStart : 0);
			number.TryFormat(lineNumber, out var numberLength, default, CultureInfo.InvariantCulture);

			if (index > 0)
			{
				output.Append('\n');
			}

			output.Append(isErrorLine ? '>' : ' ').Append(' ').Append(' ', gutterWidth - numberLength).Append(lineNumber[..numberLength]).Append(" |");

			if (text.Length > 0)
			{
				output.Append(' ').Append(text);
			}

			if (!isErrorLine)
			{
				continue;
			}

			// Keep the tabs from the line, so the caret lines up whatever the tab width is. A surrogate pair is one character on screen, so it gets one space: the text is sanitized, so every low surrogate in it is the second half of a pair.
			output.Append('\n').Append(' ', gutterWidth + 2).Append(" | ");

			foreach (var character in text.AsSpan(0, pointerOffset))
			{
				if (!char.IsLowSurrogate(character))
				{
					output.Append(character == '\t' ? '\t' : ' ');
				}
			}

			output.Append('^');
		}

		return output.ToString();
	}

	/*
	The rejected text can hold characters that a terminal acts on rather than shows: control characters such as an escape or a carriage return, bidirectional controls, and lone surrogates. Each becomes U+FFFD, which is also one code unit, so the caret still lines up. Tabs are kept.
	*/
	private static string Sanitize(string text)
	{
		var first = FindUnprintable(text, 0);

		if (first < 0)
		{
			return text;
		}

		return string.Create(text.Length, (text, first), static (characters, state) =>
		{
			state.text.CopyTo(characters);
			Sanitize(characters, state.first);
		});
	}

	private static void Sanitize(Span<char> text, int start = 0)
	{
		for (var index = FindUnprintable(text, start); index >= 0; index = FindUnprintable(text, index + 1))
		{
			text[index] = (char)0xFFFD;
		}
	}

	private static int FindUnprintable(ReadOnlySpan<char> text, int start)
	{
		for (var index = start; index < text.Length; index++)
		{
			var next = text[index..].IndexOfAnyExcept(Soml.Message.PrintableAsciiCharacters);

			if (next < 0)
			{
				return -1;
			}

			index += next;
			var code = text[index];

			if (char.IsHighSurrogate(code) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
			{
				index++;
				continue;
			}

			if (char.IsSurrogate(code) || (char.IsControl(code) && code != '\t') || UnicodeTable.IsBidiControl(code))
			{
				return index;
			}
		}

		return -1;
	}

	/*
	A long line, such as a minified document, is cut to a window around the pointer, and sanitized. The cut never splits a surrogate pair.
	*/
	private static (string Text, int PointerOffset) Clip(ReadOnlySpan<char> line, int pointerOffset)
	{
		if (line.Length <= MaxLineWidth)
		{
			Span<char> shortLine = stackalloc char[line.Length];
			line.CopyTo(shortLine);
			Sanitize(shortLine);
			return (shortLine.ToString(), pointerOffset);
		}

		// Note: Not using Math.Clamp because public error-formatting results were mixed in .NET 10 ARM64 comparisons.
		var start = Math.Max(0, Math.Min(pointerOffset - (MaxLineWidth / 2), line.Length - MaxLineWidth));
		var end = start + MaxLineWidth;

		// Only the window is sanitized, so that an error on a long line copies none of it. Sanitizing keeps the length, and whether a character is half of a pair depends only on its neighbors, so two more characters on each side give the window the same result as sanitizing the whole line.
		var textStart = Math.Max(0, start - 2);
		var window = line[textStart..Math.Min(line.Length, end + 2)];
		Span<char> buffer = stackalloc char[MaxLineWidth + 6];
		var text = buffer.Slice(1, window.Length);
		window.CopyTo(text);
		Sanitize(text);
		start -= textStart;
		end -= textStart;

		if (start < text.Length && char.IsLowSurrogate(text[start]))
		{
			start++;
		}

		if (end < text.Length && char.IsLowSurrogate(text[end]))
		{
			end--;
		}

		var hasPrefix = textStart + start > 0;
		var hasSuffix = textStart + end < line.Length;
		var outputStart = 1 + start;
		var outputEnd = 1 + end;

		if (hasPrefix)
		{
			buffer[--outputStart] = '…';
		}

		if (hasSuffix)
		{
			buffer[outputEnd++] = '…';
		}

		return (buffer[outputStart..outputEnd].ToString(), pointerOffset - textStart - start + (hasPrefix ? 1 : 0));
	}
}
