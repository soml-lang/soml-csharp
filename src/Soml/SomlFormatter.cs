using System.Runtime.InteropServices;
using System.Text;

namespace Soml;

/// <summary>
/// Formats SOML documents: normalizes their layout and keeps everything their author chose.
/// </summary>
public static class SomlFormatter
{
	/// <summary>
	/// Formats a document. Returns the document with its layout normalized, ending with one line feed.
	/// </summary>
	/// <remarks>
	/// <para>The layout follows the formatter in the specification: one tab per level, every member and item on its own line with no commas, and no trailing whitespace or runs of blank lines. An object or an array whose brackets are on one line stays on one line, as in <c>ports: [80, 443]</c>, with a comma and a space between its members or items. To give a one-line container one member or item per line, put a line break anywhere inside it.</para>
	/// <para>Comments, member order, block strings, and the spelling of every value stay as they are, so the value never changes. The rules are normative, so every conforming formatter gives the same bytes, and a document is already formatted when it equals its formatted form.</para>
	/// </remarks>
	/// <example>
	/// <code>
	/// SomlFormatter.Format("pool: {min: 2,   max: 16,} # Connections");
	/// // "pool: {min: 2, max: 16} # Connections\n"
	/// </code>
	/// </example>
	/// <exception cref="SomlParseException">When the document is not valid.</exception>
	public static string Format(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		return new Printer(DocumentSyntax.Parse(text)).Print();
	}

	/// <summary>
	/// Whether a document is already formatted, which is when it equals its formatted form.
	/// </summary>
	/// <exception cref="SomlParseException">When the document is not valid.</exception>
	public static bool IsFormatted(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		return new Printer(DocumentSyntax.Parse(text)).IsFormatted();
	}

	/*
	Writes the formatted document from the tree and the text, a port of the `Printer` in `format.ts`.

	All line splitting here is LF-only. `EnumerateLines` also splits valid U+0085/U+2028/U+2029 content, which would change formatting or string values.
	*/
	private ref struct Printer(DocumentSyntax tree)
	{
		private readonly string _text = tree.Text;
		private readonly SyntaxNode _body = tree.Body;
		private readonly List<CommentSyntax> _comments = tree.Comments;
		private readonly StringBuilder _output = new(tree.Text.Length + 16);
		private int _commentIndex;

		/*
		Starts a new line at `level`, with one blank line before it when the source had one.
		*/
		private void NewLine(int level, bool isBlank)
		{
			if (_output.Length == 0)
			{
				return;
			}

			_output.Append(isBlank ? "\n\n" : "\n").Append('\t', level);
		}

		private bool IsSameLine(int start, int end) => end <= start || !_text.AsSpan(start, end - start).Contains('\n');

		/*
		Whether a line between `start` and `end` holds only spaces and tabs, if anything.
		*/
		private bool HasBlankLine(int start, int end)
		{
			var gap = _text.AsSpan(start, Math.Max(0, end - start));

			// Note: Not using a generated blank-line regex because it did not consistently improve Format/IsFormatted over this small LF-only span predicate.
			foreach (var range in gap.Split('\n'))
			{
				if (range.Start.Value > 0 && range.End.Value < gap.Length && Parser.IsBlankLine(gap[range]))
				{
					return true;
				}
			}

			return false;
		}

		/*
		The next comment that ends at or before `end`, without consuming it.
		*/
		private CommentSyntax? PeekComment(int end) => _commentIndex < _comments.Count && _comments[_commentIndex].End <= end ? _comments[_commentIndex] : null;

		private void WriteComment(CommentSyntax comment)
		{
			_commentIndex++;
			var text = _text.AsSpan(comment.Start, comment.End - comment.Start);

			if (!comment.IsBlock)
			{
				_output.Append(TrimTrailingWhitespace(text));
				return;
			}

			// The lines of a block comment keep their own indentation. Trailing whitespace is removed, and runs of blank lines collapse to one, as everywhere outside a block string.
			var isFirst = true;
			var isPreviousEmpty = false;

			foreach (var range in text.Split('\n'))
			{
				var line = TrimTrailingWhitespace(text[range]);

				if (line.IsEmpty && isPreviousEmpty)
				{
					continue;
				}

				if (!isFirst)
				{
					_output.Append('\n');
				}

				_output.Append(line);
				isFirst = false;
				isPreviousEmpty = line.IsEmpty;
			}
		}

		/*
		The offset of the `,` after an item in the source, or `null` when there is none.
		*/
		private int? FindComma(int start, int end)
		{
			// Between two items there is only whitespace, comments, and at most one comma.
			for (var index = _commentIndex; index < _comments.Count && _comments[index].Start < end; index++)
			{
				var comment = _comments[index];
				var comma = _text.AsSpan(start, comment.Start - start).IndexOf(',');

				if (comma >= 0)
				{
					return start + comma;
				}

				// The document is valid, so a comment is closed. Its indexed end skips the content without rescanning it.
				start = comment.End;
			}

			var remaining = _text.AsSpan(start, end - start).IndexOf(',');
			return remaining < 0 ? null : start + remaining;
		}

		/*
		Writes `items` one per line at `level`, with the comments between them, from `start` to `end` in the source.

		A comment on the line of the item before it, or of the opening bracket, stays at the end of that line. The exception is a block comment after the comma that the next item follows on the same line, as in `[1, /* note *\/ 2]`: it stays in front of that item. Every other comment starts a new line, and a comment or an item that follows a block comment on its line stays on that line.
		*/
		private void List<TNode>(scoped ReadOnlySpan<TNode> items, int start, int end, int level, bool isBracketed) where TNode : SyntaxNode
		{
			var previousEnd = start;
			int? itemEnd = null;
			// Whether a comment may stay at the end of the line before it, which needs an item or an opening bracket there.
			var canTrail = isBracketed;
			var hasWritten = false;
			var isOnSameLine = false;

			for (var itemIndex = 0; itemIndex <= items.Length; itemIndex++)
			{
				var item = itemIndex < items.Length ? items[itemIndex] : null;
				var itemStart = item?.Start ?? end;
				var hasComments = PeekComment(itemStart) is not null;
				var comma = isBracketed && hasComments ? FindComma(previousEnd, itemStart) : null;
				// Found once per item rather than once per comment, which would be quadratic in the number of comments on one line.
				int? itemLineStart = item is null || !hasComments ? null : previousEnd + _text.AsSpan(previousEnd, itemStart - previousEnd).LastIndexOf('\n') + 1;

				while (PeekComment(itemStart) is { } comment)
				{
					var (commentStart, commentEnd, isBlock) = comment;
					var isTrailing = canTrail && !isOnSameLine && IsTrailing(comment, previousEnd, itemEnd, itemLineStart, comma);

					if (isTrailing || isOnSameLine)
					{
						_output.Append(' ');
					}
					else
					{
						NewLine(level, hasWritten && HasBlankLine(previousEnd, commentStart));
					}

					WriteComment(comment);
					hasWritten |= !isTrailing;

					// A block comment that the next comment or item follows on the same line keeps it on that line.
					var nextStart = PeekComment(itemStart)?.Start ?? itemStart;
					isOnSameLine = isBlock && !isTrailing && IsSameLine(commentEnd, nextStart);
					previousEnd = commentEnd;
				}

				if (item is null)
				{
					return;
				}

				if (isOnSameLine)
				{
					_output.Append(' ');
				}
				else
				{
					NewLine(level, hasWritten && HasBlankLine(previousEnd, itemStart));
				}

				Item(item, level);

				previousEnd = item.End;
				itemEnd = previousEnd;
				canTrail = true;
				hasWritten = true;
				isOnSameLine = false;
			}
		}

		/*
		Whether a comment stays at the end of the line before it. That line ends at the item before it, or at the comma after that item.
		*/
		private bool IsTrailing(CommentSyntax comment, int previousEnd, int? itemEnd, int? itemLineStart, int? comma)
		{
			var isAfterComma = comma is not null && comment.Start > comma;

			// A block comment after the comma that the next item follows on the same line stays in front of that item. The comment ends on the item's line when it ends after the start of that line.
			if (itemLineStart is not null && comment.IsBlock && (comma is null || isAfterComma) && comment.End >= itemLineStart)
			{
				return false;
			}

			// A comment after the comma ends the comma's line, which is later than the item's when a block comment that spans lines comes between them. The comma is removed, so the comment moves to the end of the item's line, which only holds when nothing came between them.
			var lineEnd = isAfterComma && previousEnd == itemEnd ? comma!.Value + 1 : previousEnd;
			return IsSameLine(lineEnd, comment.Start);
		}

		/*
		Writes a container that is on one line in the source on one line, with a comma and a space between its members or items and no trailing comma. Only a block comment can be inside it, and each one stays where it is among the items and commas.
		*/
		private void OneLine<TNode>(scoped ReadOnlySpan<TNode> items, int start, int end, int level, char opening, char closing) where TNode : SyntaxNode
		{
			_output.Append(opening);
			var previousEnd = start + 1;
			var hasWrittenToken = false;

			for (var itemIndex = 0; itemIndex <= items.Length; itemIndex++)
			{
				var item = itemIndex < items.Length ? items[itemIndex] : null;
				var itemStart = item?.Start ?? (end - 1);
				// The comma after the last item is left out.
				var comma = item is null ? null : FindComma(previousEnd, itemStart);
				var hasWrittenComma = false;

				while (PeekComment(itemStart) is { } comment)
				{
					if (!hasWrittenComma && comma is not null && comma < comment.Start)
					{
						_output.Append(',');
						hasWrittenComma = true;
					}

					if (hasWrittenToken)
					{
						_output.Append(' ');
					}

					WriteComment(comment);
					hasWrittenToken = true;
				}

				if (item is null)
				{
					break;
				}

				if (comma is not null && !hasWrittenComma)
				{
					_output.Append(", ");
				}
				else if (hasWrittenToken)
				{
					_output.Append(' ');
				}

				Item(item, level);
				previousEnd = item.End;
				hasWrittenToken = true;
			}

			_output.Append(closing);
		}

		private void Item(SyntaxNode item, int level)
		{
			if (item is MemberSyntax member)
			{
				Member(member, level);
			}
			else
			{
				Value(item, level);
			}
		}

		private void Member(MemberSyntax member, int level)
		{
			var value = member.Value;
			_output.Append(_text, member.Start, member.KeyEnd - member.Start).Append(':');
			var isBlockString = value is ScalarSyntax { IsBlockString: true };

			if (PeekComment(value.Start) is null)
			{
				// A block string begins on the next line, one level deeper, so its delimiters and content line up.
				if (isBlockString)
				{
					NewLine(level + 1, false);
					Value(value, level + 1);
					return;
				}

				_output.Append(' ');
				Value(value, level);
				return;
			}

			// With comments between the `:` and the value, the line breaks between them are kept, but not the blank lines, and a value on a new line is one level deeper.
			var previousEnd = member.KeyEnd + 1;
			var valueLevel = level;

			while (PeekComment(value.Start) is { } comment)
			{
				if (WriteSeparator(previousEnd, comment.Start, level + 1))
				{
					valueLevel = level + 1;
				}

				WriteComment(comment);
				previousEnd = comment.End;
			}

			if (isBlockString)
			{
				NewLine(level + 1, false);
				valueLevel = level + 1;
			}
			else if (WriteSeparator(previousEnd, value.Start, level + 1))
			{
				valueLevel = level + 1;
			}

			Value(value, valueLevel);
		}

		/*
		A space when `end` is on the line of `start` in the source, and otherwise a new line at `level`. Returns whether it started a new line.
		*/
		private bool WriteSeparator(int start, int end, int level)
		{
			if (IsSameLine(start, end))
			{
				_output.Append(' ');
				return false;
			}

			NewLine(level, false);
			return true;
		}

		private void Value(SyntaxNode node, int level)
		{
			var (start, end) = (node.Start, node.End);

			if (node is ObjectSyntax result)
			{
				Container<MemberSyntax>(CollectionsMarshal.AsSpan(result.Members), start, end, level, '{', '}', result.IsOneLine);
				return;
			}

			if (node is ArraySyntax array)
			{
				Container<SyntaxNode>(CollectionsMarshal.AsSpan(array.Elements), start, end, level, '[', ']', array.IsOneLine);
				return;
			}

			var text = _text.AsSpan(start, end - start);

			if (node is ScalarSyntax { IsBlockString: true })
			{
				ReindentBlockString(text, level);
			}
			else
			{
				_output.Append(text);
			}
		}

		private void Container<TNode>(scoped ReadOnlySpan<TNode> items, int start, int end, int level, char opening, char closing, bool isOneLine) where TNode : SyntaxNode
		{
			if (items.IsEmpty && PeekComment(end) is null)
			{
				_output.Append(opening).Append(closing);
				return;
			}

			if (isOneLine)
			{
				OneLine(items, start, end, level, opening, closing);
				return;
			}

			_output.Append(opening);
			List(items, start + 1, end - 1, level + 1, isBracketed: true);
			NewLine(level, false);
			_output.Append(closing);
		}

		/*
		Moves a block string to a new indentation. Its content is relative to the closing delimiter's indentation, so replacing that indentation on every line keeps the value. A blank line is left as it is, because it becomes an empty line either way.
		*/
		private void ReindentBlockString(ReadOnlySpan<char> text, int level)
		{
			var closingLine = text[(text.LastIndexOf('\n') + 1)..];
			var oldIndentation = closingLine.Length - closingLine.TrimStart(" \t").Length;
			var openingEnd = text.IndexOf('\n');
			_output.Append(text[..openingEnd]);
			text = text[(openingEnd + 1)..];

			foreach (var range in text.Split('\n'))
			{
				var line = text[range];

				_output.Append('\n');

				if (Parser.IsBlankLine(line))
				{
					_output.Append(line);
				}
				else
				{
					// The closing line sets the old indentation, and the parser checked that every content line that is not blank starts with it.
					_output.Append('\t', level).Append(line[oldIndentation..]);
				}
			}
		}

		private void WriteDocument()
		{
			// The document is a list without brackets: the members of a brace-less object, or one braced collection.
			if (_body is ObjectSyntax { IsBraced: false } result)
			{
				List<MemberSyntax>(CollectionsMarshal.AsSpan(result.Members), 0, _text.Length, 0, isBracketed: false);
			}
			else
			{
				List<SyntaxNode>([_body], 0, _text.Length, 0, isBracketed: false);
			}
			_output.Append('\n');
		}

		public string Print()
		{
			WriteDocument();
			return _output.Equals(_text.AsSpan()) ? _text : _output.ToString();
		}

		public bool IsFormatted()
		{
			WriteDocument();
			return _output.Equals(_text.AsSpan());
		}

		/*
		Only spaces and tabs are whitespace in SOML. Any other character, such as a no-break space, is content.

		Note: Not using LastIndexOfAnyExcept because it helped long trailing runs but slowed ordinary short-comment Format and IsFormatted calls on .NET 10 ARM64.
		*/
		private static ReadOnlySpan<char> TrimTrailingWhitespace(ReadOnlySpan<char> text) => text.TrimEnd(" \t");
	}
}
