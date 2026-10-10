namespace Soml;

/*
A syntax tree of a document, for the formatter and the editor, a port of `tree.ts` in the JS reference implementation. Every node has its span in the text as UTF-16 offsets, `Start` inclusive and `End` exclusive. Comments are not in the tree but in one list in source order, as the formatter reads them in order.
*/
internal abstract class SyntaxNode(int start, int end)
{
	public int Start { get; } = start;

	public int End { get; } = end;
}

internal sealed class ObjectSyntax(int start, int end, List<MemberSyntax> members, bool isBraced, bool isOneLine) : SyntaxNode(start, end)
{
	public List<MemberSyntax> Members { get; } = members;

	public bool IsOneLine { get; } = isOneLine;

	/*
	`false` only for a top-level object written without braces.
	*/
	public bool IsBraced { get; } = isBraced;

	/*
	The member with a key, which is decoded from `source` only when it is a `"..."` key with escapes.
	*/
	public MemberSyntax? FindMember(string source, string key)
	{
		// Note: Not using CollectionsMarshal.AsSpan for member lookup because public editor timings were mixed without allocation savings on .NET 10 ARM64.
		foreach (var member in Members)
		{
			var start = member.Start;
			var spelling = source.AsSpan(start, member.KeyEnd - start);

			var isMatch = spelling[0] switch
			{
				'"' => spelling[1..^1].Contains('\\') ? Parser.DecodeEscapedString(source, start) == key : spelling[1..^1].SequenceEqual(key),
				'\'' => spelling[1..^1].SequenceEqual(key),
				_ => spelling.SequenceEqual(key),
			};

			if (isMatch)
			{
				return member;
			}
		}

		return null;
	}
}

internal sealed class ArraySyntax(int start, int end, List<SyntaxNode> elements, bool isOneLine) : SyntaxNode(start, end)
{
	public List<SyntaxNode> Elements { get; } = elements;

	public bool IsOneLine { get; } = isOneLine;
}

/*
A member starts at its key, which ends at `KeyEnd`, before the `:`.
*/
internal sealed class MemberSyntax(int start, int keyEnd, SyntaxNode value) : SyntaxNode(start, value.End)
{
	public int KeyEnd { get; } = keyEnd;

	public SyntaxNode Value { get; } = value;
}

/*
A string, a number, a date, an instant, a duration, or a keyword.
*/
internal sealed class ScalarSyntax(int start, int end, bool isBlockString) : SyntaxNode(start, end)
{
	public bool IsBlockString { get; } = isBlockString;
}

internal readonly record struct CommentSyntax(int Start, int End, bool IsBlock);

internal sealed class DocumentSyntax(string text, SyntaxNode body, List<CommentSyntax> comments)
{
	public string Text { get; } = text;

	/*
	The document's collection: an `ObjectSyntax` or an `ArraySyntax`.
	*/
	public SyntaxNode Body { get; } = body;

	/*
	Every comment, in source order.
	*/
	public List<CommentSyntax> Comments { get; } = comments;

	/*
	Parses a document into a tree. The document is checked as a whole first, so the builder can trust it.
	*/
	public static DocumentSyntax Parse(string text)
	{
		Parser.Validate(text);
		return ParseValid(text);
	}

	/*
	Parses a document that is known to be valid, such as one that was just read, without checking it again.
	*/
	public static DocumentSyntax ParseValid(string text) => new Builder(text).Build();

	/*
	The offset of the value at a path, or of its key with `isAboutKey`. Where the document has no value at the path, as for a missing member, it is the offset of the deepest value on the way.
	*/
	public int GetOffsetOfPath(IReadOnlyList<object> path, bool isAboutKey)
	{
		SyntaxNode node = Body;

		for (var index = 0; index < path.Count; index++)
		{
			if (path[index] is string key && node is ObjectSyntax result && result.FindMember(Text, key) is { } member)
			{
				if (isAboutKey && index == path.Count - 1)
				{
					return member.Start;
				}

				node = member.Value;
			}
			else if (path[index] is int position && node is ArraySyntax array && (uint)position < (uint)array.Elements.Count)
			{
				node = array.Elements[position];
			}
			else
			{
				break;
			}
		}

		return node.Start;
	}

	private ref struct Builder(string source)
	{
		private readonly List<CommentSyntax> _comments = [];
		private int _index;
		private int _lastLineBreak = -1;

		private int Code(int offset) => offset < source.Length ? source[offset] : -1;

		private int Code() => Code(_index);

		private void SkipTrivia()
		{
			while (true)
			{
				var code = Code();
				var start = _index;

				if (code is '\n' or ' ' or '\t')
				{
					_index = Parser.SkipWhitespace(source, _index);
				}
				else if (Parser.FindCommentEnd(source, start) is var commentEnd and >= 0)
				{
					_index = commentEnd;
					_comments.Add(new CommentSyntax(start, commentEnd, IsBlock: code == '/'));
				}
				else
				{
					return;
				}

				var lineBreak = source.AsSpan(start, _index - start).LastIndexOf('\n');

				if (lineBreak >= 0)
				{
					_lastLineBreak = start + lineBreak;
				}
			}
		}

		private ObjectSyntax BareObject()
		{
			var members = new List<MemberSyntax>();

			do
			{
				members.Add(Member());
				SkipTrivia();
			}
			while (_index < source.Length);

			return new ObjectSyntax(members[0].Start, members[^1].End, members, isBraced: false, isOneLine: false);
		}

		/*
		The items of an object or an array go from the opening bracket to the closing one, which is consumed too. The parser has already checked the separators, so after each item, `SkipSeparator` skips the trivia and a comma.
		*/
		private ObjectSyntax Object()
		{
			var start = _index++;
			var members = new List<MemberSyntax>();

			for (SkipTrivia(); Code() != '}'; SkipSeparator())
			{
				members.Add(Member());
			}

			_index++;
			return new ObjectSyntax(start, _index, members, isBraced: true, isOneLine: _lastLineBreak < start);
		}

		private ArraySyntax Array()
		{
			var start = _index++;
			var elements = new List<SyntaxNode>();

			for (SkipTrivia(); Code() != ']'; SkipSeparator())
			{
				elements.Add(Value());
			}

			_index++;
			return new ArraySyntax(start, _index, elements, isOneLine: _lastLineBreak < start);
		}

		private void SkipSeparator()
		{
			SkipTrivia();

			if (Code() == ',')
			{
				_index++;
				SkipTrivia();
			}
		}

		/*
		The key is not decoded, as only a lookup needs it, which `FindMember` does.
		*/
		private MemberSyntax Member()
		{
			var start = _index;

			if (Code() is '\'' or '"')
			{
				_index = SingleLineStringEnd();
			}
			else
			{
				_index = Parser.FindBareKeyEnd(source, _index);
			}

			var keyEnd = _index;
			_index++; // `:`
			SkipTrivia();
			return new MemberSyntax(start, keyEnd, Value());
		}

		private SyntaxNode Value()
		{
			var code = Code();

			if (code == '{')
			{
				return Object();
			}

			if (code == '[')
			{
				return Array();
			}

			var start = _index;

			if (code is '\'' or '"')
			{
				if (Code(start + 1) == code && Code(start + 2) == code)
				{
					return BlockString((char)code);
				}

				// The value of a string is not needed, only its end.
				_index = SingleLineStringEnd();
				return new ScalarSyntax(start, _index, isBlockString: false);
			}

			// The document is valid, so every other value, a keyword, a number, a date, an instant, or a duration, is one run of the characters that `FindNumberEnd` takes, which no character that may follow a value is.
			_index = Parser.FindNumberEnd(source, start);
			return new ScalarSyntax(start, _index, isBlockString: false);
		}

		/*
		The index after the `'...'` or `"..."` string that starts here. `'...'` ends at the next `'`. `"..."` ends at the next `"` that is not escaped, and `\` always starts a two-character escape or a `\u{…}`, which holds no quote.
		*/
		private int SingleLineStringEnd()
		{
			var start = _index;

			if (Code() == '\'')
			{
				return source.IndexOf('\'', start + 1) + 1;
			}

			var end = start + 1;

			while (true)
			{
				end += source.AsSpan(end).IndexOfAny('"', '\\');

				if (source[end] == '"')
				{
					return end + 1;
				}

				end += 2;
			}
		}

		/*
		The block ends at the first line whose first non-whitespace content is a run of exactly as many quotes as the opening delimiter.
		*/
		private ScalarSyntax BlockString(char quote)
		{
			var start = _index;
			var delimiterLength = Parser.QuoteRunLength(source, start, quote);

			var closing = Parser.FindBlockStringEnd(source, source.IndexOf('\n', start) + 1, quote, delimiterLength)!.Value;
			_lastLineBreak = closing.LineStart - 1;
			_index = closing.DelimiterStart + delimiterLength;
			return new ScalarSyntax(start, _index, isBlockString: true);
		}

		public DocumentSyntax Build()
		{
			SkipTrivia();
			var code = Code();

			SyntaxNode body = code switch
			{
				'{' => Object(),
				'[' => Array(),
				_ => BareObject(),
			};

			SkipTrivia();
			return new DocumentSyntax(source, body, _comments);
		}
	}
}
