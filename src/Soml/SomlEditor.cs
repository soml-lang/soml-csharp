using System.Globalization;
using System.Text;

namespace Soml;

/// <summary>
/// A key of an object or an index of an array, in a path to a value, such as <c>["servers", 0, "port"]</c>.
/// </summary>
public readonly struct SomlPathSegment : IEquatable<SomlPathSegment>
{
	private readonly string? _key;
	private readonly int _index;

	private SomlPathSegment(string? key, int index)
	{
		_key = key;
		_index = index;
	}

	/// <summary>
	/// The key, or <c>null</c> for an index.
	/// </summary>
	public string? Key => _key;

	/// <summary>
	/// The index, or -1 for a key.
	/// </summary>
	public int Index => _key is null ? _index : -1;

	/// <summary>
	/// Whether the segment is a key rather than an index.
	/// </summary>
	public bool IsKey => _key is not null;

	/// <summary>
	/// A key.
	/// </summary>
	public static implicit operator SomlPathSegment(string key)
	{
		ArgumentNullException.ThrowIfNull(key);
		return new SomlPathSegment(key, -1);
	}

	/// <summary>
	/// An index.
	/// </summary>
	/// <exception cref="ArgumentOutOfRangeException">When the index is negative.</exception>
	public static implicit operator SomlPathSegment(int index)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(index);
		return new SomlPathSegment(null, index);
	}

	// Note: Not using invariant string.Create interpolation because it saved allocations for larger indexes but slowed the compiled ToString on .NET 10 ARM64, including small indexes.
	/// <summary>
	/// The key, or the index in brackets, such as <c>[0]</c>.
	/// </summary>
	public override string ToString() => _key ?? $"[{_index.ToString(CultureInfo.InvariantCulture)}]";

	/// <inheritdoc/>
	public bool Equals(SomlPathSegment other) => _key == other._key && _index == other._index;

	/// <inheritdoc/>
	public override bool Equals(object? obj) => obj is SomlPathSegment other && Equals(other);

	/// <inheritdoc/>
	public override int GetHashCode() => HashCode.Combine(_key, _index);

	/// <inheritdoc/>
	public static bool operator ==(SomlPathSegment left, SomlPathSegment right) => left.Equals(right);

	/// <inheritdoc/>
	public static bool operator !=(SomlPathSegment left, SomlPathSegment right) => !left.Equals(right);
}

/// <summary>
/// Changes one value in a document and keeps everything else as it is written: comments, member order, layout, and the spelling of every other value. For tools that update a config file, such as a <c>set</c> command.
/// </summary>
/// <remarks>
/// <para>Only the changed part of the text is rewritten, and an edit to a formatted document leaves it formatted. The rules are the editing rules of the specification:</para>
/// <list type="bullet">
/// <item><description>A new value is written as <see cref="SomlSerializer"/> writes it, at the indentation of its line. In a container that is on one line, it is written on one line too, so <c>[1, 2]</c> with a new item <c>3</c> becomes <c>[1, 2, 3]</c>.</description></item>
/// <item><description>A new member goes after the last member of its object, and missing objects on the way are created. A new item can be added at the end of an array, with the index that is its length.</description></item>
/// <item><description>A removed member or item takes the comments it owns, which are the ones that the formatter keeps with it, and its comma, or the comma before it when it is the last one.</description></item>
/// <item><description>Removing the only member of a document without braces leaves <c>{}</c>.</description></item>
/// </list>
/// <para>Removing a value that does not exist changes nothing and is not an error, so removing the same path twice is safe.</para>
/// </remarks>
public static class SomlEditor
{
	/// <summary>
	/// Sets the value at a path, replacing it, or adding it when it does not exist yet.
	/// </summary>
	/// <example>
	/// <code>
	/// SomlEditor.Set("name: 'api' # The service\nport: 8080\n", ["port"], 9090);
	/// // "name: 'api' # The service\nport: 9090\n"
	/// </code>
	/// </example>
	/// <param name="text">The document.</param>
	/// <param name="path">The keys and array indexes that lead to the value, such as <c>["servers", 0, "port"]</c>. It must not be empty.</param>
	/// <param name="value">The new value.</param>
	/// <param name="canonical">Whether to sort the members of a new object by key. By default, they keep their order.</param>
	/// <returns>The changed document.</returns>
	/// <exception cref="ArgumentException">When the path is empty, because the document's own collection cannot be replaced or removed.</exception>
	/// <exception cref="SomlParseException">When the document is not valid.</exception>
	/// <exception cref="SomlException">When the path does not fit the document: a key where an array is, an index where an object is, a path through a value that is neither, an index past the end of its array, or an index below a value that does not exist. Also when the value cannot be written, such as a string with a carriage return, or when the change would nest the document more than 100 levels deep.</exception>
	public static string Set(string text, ReadOnlySpan<SomlPathSegment> path, SomlValue value, bool canonical = false) => Edit(text, path, value, canonical, out _);

	/// <summary>
	/// Removes the value at a path, with the comments it owns.
	/// </summary>
	/// <example>
	/// <code>
	/// SomlEditor.Remove("a: 1\nb: 2\n", ["a"]);
	/// // "b: 2\n"
	/// </code>
	/// </example>
	/// <param name="text">The document.</param>
	/// <param name="path">The keys and array indexes that lead to the value. It must not be empty.</param>
	/// <returns>The changed document, or the same text when there is no value at the path.</returns>
	/// <exception cref="ArgumentException">When the path is empty, because the document's own collection cannot be replaced or removed.</exception>
	/// <exception cref="SomlParseException">When the document is not valid.</exception>
	/// <exception cref="SomlException">When the path does not fit the document: a key where an array is, an index where an object is, or a path through a value that is neither.</exception>
	public static string Remove(string text, ReadOnlySpan<SomlPathSegment> path) => Remove(text, path, out _);

	/// <summary>
	/// Removes the value at a path, with the comments it owns, and tells whether there was one.
	/// </summary>
	/// <param name="text">The document.</param>
	/// <param name="path">The keys and array indexes that lead to the value. It must not be empty.</param>
	/// <param name="isRemoved">Whether there was a value to remove.</param>
	/// <returns>The changed document, or the same text when there is no value at the path.</returns>
	/// <exception cref="ArgumentException">When the path is empty, because the document's own collection cannot be replaced or removed.</exception>
	/// <exception cref="SomlParseException">When the document is not valid.</exception>
	/// <exception cref="SomlException">When the path does not fit the document.</exception>
	public static string Remove(string text, ReadOnlySpan<SomlPathSegment> path, out bool isRemoved) => Edit(text, path, null, canonical: false, out isRemoved);

	private static string Edit(string text, ReadOnlySpan<SomlPathSegment> path, SomlValue? value, bool canonical, out bool isChanged)
	{
		ArgumentNullException.ThrowIfNull(text);

		if (path.IsEmpty)
		{
			throw new ArgumentException("The path must not be empty, because the document's own collection cannot be replaced or removed", nameof(path));
		}

		var tree = DocumentSyntax.Parse(text);
		var replacement = new Editor(tree, path, value, canonical).Edit();
		isChanged = replacement is not null;

		if (replacement is not { } change)
		{
			return text;
		}

		if (text.AsSpan(change.Start, change.End - change.Start).SequenceEqual(change.Text))
		{
			isChanged = false;
			return text;
		}

		// An edit follows one path and replaces one contiguous span, including any comments it owns.
		// Note: Not using a string.Create callback because three-span string.Concat already allocates the exact length and copies each slice once, so the callback would repeat that work with more code.
		var edited = string.Concat(text.AsSpan(0, change.Start), change.Text, text.AsSpan(change.End));

		// The result is checked, so that a mistake here throws rather than writing a broken document.
		try
		{
			Parser.Validate(edited);
		}
		catch (SomlParseException exception)
		{
			throw new InvalidOperationException($"SomlEditor made an invalid document for the path {DescribePath(path)}. This is a bug in Soml", exception);
		}

		return edited;
	}

	private readonly record struct Replacement(int Start, int End, string Text);

	private struct Removal(int start, int end, bool isWholeLines)
	{
		public int Start { get; set; } = start;

		public int End { get; set; } = end;

		/*
		Whether it takes whole lines, which may leave a blank line next to another one.
		*/
		public bool IsWholeLines { get; } = isWholeLines;
	}

	/*
	A port of the `Editor` in `edit.ts`. A `null` value removes.
	*/
	private ref struct Editor
	{
		private readonly string _text;
		private readonly ReadOnlySpan<SomlPathSegment> _path;
		private readonly SomlValue? _value;
		private readonly bool _canonical;

		/*
		The comments in source order. They do not overlap, so their ends are in order too, and a binary search finds a comment by its start or by its end.
		*/
		private readonly List<CommentSyntax> _comments;

		private Replacement? _replacement;

		/*
		Whether the container being edited is inside one that stays on one line, so that it stays on one line too. An edit follows one path, so one flag is enough.
		*/
		private bool _isInsideOneLine;

		public Editor(DocumentSyntax tree, ReadOnlySpan<SomlPathSegment> path, SomlValue? value, bool canonical)
		{
			_text = tree.Text;
			_path = path;
			_value = value;
			_canonical = canonical;
			_comments = tree.Comments;
			Body = tree.Body;
		}

		private SyntaxNode Body { get; }

		/*
		The replacement that makes the edit, from the document's collection, which is level 1, or null when nothing changes.
		*/
		public Replacement? Edit()
		{
			Descend(Body, 0, 1);
			return _replacement;
		}

		private void EditInArray(ArraySyntax array, int index, int depth)
		{
			if (_path[index].IsKey)
			{
				throw new SomlException($"Cannot edit {DescribePath(_path)}, because {DescribeParent(index)} is an array, so it needs an index, not a key");
			}

			var position = _path[index].Index;

			if (position >= array.Elements.Count)
			{
				// Removing an item that is not there changes nothing, at the end of the array or past it, and so does removing something below it.
				if (_value is null)
				{
					return;
				}

				var count = array.Elements.Count;

				if (position != count)
				{
					var items = count == 1 ? "1 item" : $"{count} items";
					var owner = index == 0 ? "the document" : $"the array at {DescribeParent(index)}";
					throw new SomlException($"Cannot edit {DescribePath(_path)}, because {owner} has {items}. Add an item at index {count}");
				}

				var newValue = Nest(index + 1, _value.Value);
				Insert(array, array.Elements.Count > 0 ? array.Elements[^1] : null, newValue, depth + 1);
				return;
			}

			var element = array.Elements[position];

			if (index < _path.Length - 1)
			{
				_isInsideOneLine = IsOneLine(array);
				Descend(element, index + 1, depth + 1);
			}
			else if (_value is null)
			{
				RemoveNode(array, element);
			}
			else
			{
				Replace(element, depth + 1, array);
			}
		}

		private void EditInObject(ObjectSyntax container, int index, int depth)
		{
			if (_path[index].Key is not { } key)
			{
				throw new SomlException($"Cannot edit {DescribePath(_path)}, because {DescribeParent(index)} is an object, so it needs a key, not an index");
			}

			var member = container.FindMember(_text, key);

			if (member is not null)
			{
				if (index < _path.Length - 1)
				{
					_isInsideOneLine = IsOneLine(container);
					Descend(member.Value, index + 1, depth + 1);
				}
				else if (_value is null)
				{
					RemoveMember(container, member);
				}
				else
				{
					ReplaceMemberValue(member, depth + 1, container);
				}

				return;
			}

			if (_value is null)
			{
				return;
			}

			var newValue = Nest(index + 1, _value.Value);
			Insert(container, container.Members.Count > 0 ? container.Members[^1] : null, newValue, depth + 1, key);
		}

		/*
		Edits the path from `index` on, inside `node`, whose own depth is `depth`.
		*/
		private void Descend(SyntaxNode node, int index, int depth)
		{
			switch (node)
			{
				case ArraySyntax array:
				{
					EditInArray(array, index, depth);
					break;
				}
				case ObjectSyntax container:
				{
					EditInObject(container, index, depth);
					break;
				}
				default:
				{
					throw new SomlException($"Cannot edit {DescribePath(_path)}, because {DescribeParent(index)} is not an object or an array");
				}
			}
		}

		private void Replace(SyntaxNode node, int depth, SyntaxNode container) => _replacement = new Replacement(node.Start, node.End, ValueText(_value!.Value, depth, Indentation(node.Start), container));

		/*
		Replaces the value of `member`. A block string begins on the line after its key, and the new value is never a block string, so when only whitespace comes between the `:` and a block string, the new value goes on the key's line, as the formatter writes it.
		*/
		private void ReplaceMemberValue(MemberSyntax member, int depth, ObjectSyntax container)
		{
			var colonEnd = member.KeyEnd + 1;

			if (member.Value is not ScalarSyntax { IsBlockString: true } || SkipWhitespace(colonEnd) != member.Value.Start)
			{
				Replace(member.Value, depth, container);
				return;
			}

			_replacement = new Replacement(colonEnd, member.Value.End, $" {ValueText(_value!.Value, depth, Indentation(member.Start), container)}");
		}

		/*
		Skips spaces, tabs, and line feeds, but not comments.
		*/
		private int SkipWhitespace(int offset) => Parser.SkipWhitespace(_text, offset);

		/*
		Removes `member`. Removing the only member of a document without braces leaves `{}` in its place, because a document is never empty, and the comments that the member owned stay.
		*/
		private void RemoveMember(ObjectSyntax container, MemberSyntax member)
		{
			if (container.IsBraced || container.Members.Count > 1)
			{
				RemoveNode(container, member);
				return;
			}

			_replacement = new Replacement(member.Start, member.End, "{}");
		}

		/*
		Removes `node` from `container`.

		The removal also takes a blank line that would be left next to another one or directly inside a bracket. When the only item of a braced container goes and no comment is left inside, its brackets close up, as `[]`.
		*/
		private void RemoveNode(SyntaxNode container, SyntaxNode node)
		{
			var count = container is ArraySyntax array ? array.Elements.Count : ((ObjectSyntax)container).Members.Count;
			var last = container is ArraySyntax lastArray ? lastArray.Elements[^1] : ((ObjectSyntax)container).Members[^1];

			// When the removed item is the last one and has no comma after it, it takes the comma before it, so that no trailing comma is left, as `[1, 2]` becomes `[1]`.
			var shouldTakeCommaBefore = node == last && Code(SkipTrivia(node.End)) != ',';
			var removal = NodeRemoval(node, shouldTakeCommaBefore);

			if (removal.IsWholeLines)
			{
				TakeBlankLine(ref removal, container, ContentStart(container));
			}

			if (count == 1 && IsBraced(container) && !IsCommentLeft(container, removal))
			{
				_replacement = new Replacement(container.Start + 1, container.End - 1, "");
				return;
			}

			_replacement = new Replacement(removal.Start, removal.End, "");
		}

		/*
		Whether a comment inside `container` is outside `removal`.
		*/
		private bool IsCommentLeft(SyntaxNode container, Removal removal)
		{
			for (var index = FindComment(container.Start + 1, isByEnd: false); index < _comments.Count && _comments[index].Start < container.End; index++)
			{
				var start = _comments[index].Start;

				if (start < removal.Start || start >= removal.End)
				{
					return true;
				}
			}

			return false;
		}

		/*
		What removing a member or an item, with the comments it owns and its comma, takes out of the text. With `shouldTakeCommaBefore`, a comma directly before it goes too.
		*/
		private Removal NodeRemoval(SyntaxNode node, bool shouldTakeCommaBefore)
		{
			var start = OwnedStart(node.Start);
			var end = OwnedEnd(node.End);
			var next = Parser.SkipSpaces(_text, end);
			// Only a braced container can have commas.
			var hasComma = Code(next) == ',';

			// The comma before it is only taken when spaces alone are between them and the comments it owns. The scan back only crosses spaces, so it stays short on a long line.
			var before = Parser.SkipSpacesBack(_text, start);
			var removalStart = shouldTakeCommaBefore && Code(before - 1) == ',' ? before - 1 : start;
			// The comments after its comma go with it when they end its line, as the formatter keeps them after it.
			var removalEnd = hasComma ? FindLineEndAfterTrivia(next + 1) ?? (next + 1) : end;
			var removal = CreateRemoval(removalStart, removalEnd);

			// When it takes the comma before it, the spaces before a comment after it stay, so that the comment does not join the item before the comma.
			if (removalStart != start && !removal.IsWholeLines && CommentEndAt(removal.End) is not null)
			{
				removal.End = Parser.SkipSpacesBack(_text, removal.End);
			}

			return removal;
		}

		/*
		The start of the block comments that a member or an item at `offset` owns before it: those on its line after the comma or the opening bracket before it. It is `offset` when there are none.
		*/
		private int OwnedStart(int offset)
		{
			while (BlockCommentStartAt(Parser.SkipSpacesBack(_text, offset)) is { } commentStart)
			{
				offset = commentStart;
			}

			return offset;
		}

		/*
		The end of the comments that a member or an item ending at `offset` owns after it: those on its line before its comma or the closing bracket, and a line comment at the end of its line. It is `offset` when there are none.
		*/
		private int OwnedEnd(int offset)
		{
			while (CommentEndAt(Parser.SkipSpaces(_text, offset)) is { } commentEnd)
			{
				offset = commentEnd;
			}

			return offset;
		}

		/*
		The removal of the text from `start` to `end`. When nothing else is on its lines, it takes the lines, including a comment at the end of the last one.
		*/
		private Removal CreateRemoval(int start, int end)
		{
			var lineEnd = FindLineEndAfterTrivia(end);

			// The line start is only looked for when nothing follows on the line, because the scan back to it is long for an item in the middle of a long line, and doing it for every item would be quadratic.
			if (lineEnd is not null)
			{
				var lineStart = LineStart(start);

				if (Parser.SkipSpaces(_text, lineStart) == start)
				{
					return new Removal(lineStart, Math.Min(lineEnd.Value + 1, _text.Length), isWholeLines: true);
				}
			}

			// The spaces after the text go too, and the spaces before it when nothing follows it on the line or in its container.
			var after = Parser.SkipSpaces(_text, end);
			var code = Code(after);
			var isLast = after == _text.Length || code is '\n' or '}' or ']';
			return new Removal(isLast ? Parser.SkipSpacesBack(_text, start) : start, after, isWholeLines: false);
		}

		/*
		Widens a removal of whole lines by a blank line that would otherwise be left next to another blank line, or directly inside a bracket. `contentStart` is the container's, from `ContentStart()`.
		*/
		private void TakeBlankLine(ref Removal removal, SyntaxNode container, int? contentStart)
		{
			var isBlankBefore = removal.Start > 0 && IsBlankLine(LineStart(removal.Start - 1));
			var isBlankAfter = removal.End < _text.Length && IsBlankLine(removal.End);

			if (isBlankBefore && (isBlankAfter || IsAtClosing(removal.End, container)))
			{
				removal.Start = LineStart(removal.Start - 1);
			}
			else if (isBlankAfter && removal.Start == contentStart)
			{
				removal.End = _text.IndexOf('\n', removal.End) + 1;
			}
		}

		/*
		The offset after the blank lines that start at `offset`.
		*/
		private int SkipBlankLines(int offset)
		{
			while (offset < _text.Length && IsBlankLine(offset))
			{
				offset = _text.IndexOf('\n', offset) + 1;
			}

			return offset;
		}

		/*
		Whether only blank lines come between the line at `offset` and the line that closes `container`, or the end of the text.
		*/
		private bool IsAtClosing(int offset, SyntaxNode container)
		{
			offset = SkipBlankLines(offset);
			return offset == _text.Length || (IsBraced(container) && Parser.SkipSpaces(_text, offset) == container.End - 1);
		}

		/*
		The start of the first line that is not blank after the line that opens `container`, or after the start of the text. The line that opens a container ends after the comments that follow its bracket, and when an item follows on it, this is `null`.
		*/
		private int? ContentStart(SyntaxNode container)
		{
			if (!IsBraced(container))
			{
				return SkipBlankLines(0);
			}

			var lineEnd = FindLineEndAfterTrivia(container.Start + 1);
			return lineEnd is null ? null : SkipBlankLines(lineEnd.Value + 1);
		}

		/*
		A last line of only spaces and tabs, with no line feed after it, is not blank here. So a blank line always ends with a line feed, which `TakeBlankLine()` and `SkipBlankLines()` search for and step past.
		*/
		private bool IsBlankLine(int lineStart) => Code(Parser.SkipSpaces(_text, lineStart)) == '\n';

		private int LineStart(int offset) => offset == 0 ? 0 : _text.LastIndexOf('\n', offset - 1) + 1;

		/*
		Adds an item after `previous`, or into the empty `container` when there is none, at the indentation of the item's line. `key` names a new object member.

		An item on its own line needs no comma, so only an item added on the line of the item before it gets one, as in a one-line container.
		*/
		private void Insert(SyntaxNode container, SyntaxNode? previous, SomlValue value, int depth, string? key = null)
		{
			if (previous is null)
			{
				var closing = container.End - 1;

				// A container that stays on one line keeps the item on that line, so `[/* note */]` becomes `[/* note */ 1]`, and `[1, []]` becomes `[1, [2]]`.
				if (IsOneLine(container))
				{
					var start = Parser.SkipSpacesBack(_text, closing);
					_replacement = new Replacement(start, closing, $"{(start > container.Start + 1 ? " " : "")}{ValueText(value, depth, "", container, key)}");
					return;
				}

				var outer = Indentation(container.Start);
				var inner = $"{outer}\t";
				var lineStart = LineStart(closing);

				// A closing bracket on its own line keeps that line, and the item goes on a line before it.
				if (lineStart > container.Start && Parser.SkipSpaces(_text, lineStart) == closing)
				{
					_replacement = new Replacement(lineStart, lineStart, $"{inner}{ValueText(value, depth, inner, container, key)}\n");
				}
				else
				{
					_replacement = new Replacement(Parser.SkipSpacesBack(_text, closing), closing, $"\n{inner}{ValueText(value, depth, inner, container, key)}\n{outer}");
				}

				return;
			}

			// The new item goes after the comments that the one before it owns, so that they stay with that one.
			var previousEnd = OwnedEnd(previous.End);
			var next = SkipTrivia(previousEnd);
			var hasComma = Code(next) == ',';
			var afterComma = hasComma ? next + 1 : previousEnd;
			var lineEnd = FindLineEndAfterTrivia(afterComma);

			// Something follows on the same line, such as the closing bracket of a one-line object, so the item goes on that line too, and takes its indentation.
			if (lineEnd is null)
			{
				var item = ValueText(value, depth, Indentation(afterComma), container, key);
				_replacement = hasComma ? new Replacement(afterComma, afterComma, $" {item},") : new Replacement(previousEnd, previousEnd, $", {item}");
				return;
			}

			var indentation = Indentation(previous.Start);
			_replacement = new Replacement(lineEnd.Value, lineEnd.Value, $"\n{indentation}{ValueText(value, depth, indentation, container, key)}");
		}

		/*
		Whether `container` stays on one line: its brackets are on one line and something is between them, which the formatter keeps, or it is inside a container that stays on one line. An empty `[]` or `{}` on its own gets its new item on a line of its own.
		*/
		private bool IsOneLine(SyntaxNode container)
		{
			if (_isInsideOneLine)
			{
				return true;
			}

			return (container is ObjectSyntax { IsBraced: true, IsOneLine: true } or ArraySyntax { IsOneLine: true }) && Parser.SkipSpaces(_text, container.Start + 1) < container.End - 1;
		}

		/*
		A new value inside `container`. In a container on one line, the value is on one line too.
		*/
		private string ValueText(SomlValue value, int depth, ReadOnlySpan<char> indentation, SyntaxNode container, string? key = null)
		{
			var builder = new StringBuilder();

			if (key is not null)
			{
				SomlWriter.AppendKey(builder, key);
				builder.Append(": ");
			}

			SomlWriter.WriteValue(builder, value, _canonical, depth, indentation, IsOneLine(container));
			return builder.ToString();
		}

		/*
		The spaces and tabs at the start of the line that holds `offset`.
		*/
		private ReadOnlySpan<char> Indentation(int offset)
		{
			var lineStart = LineStart(offset);

			// A line that a block comment from an earlier line runs into, as in `/* a\n b */ x: 1`, is indented as the line where that comment starts.
			for (var comment = CommentAcross(lineStart); comment is not null; comment = CommentAcross(lineStart))
			{
				lineStart = LineStart(comment.Value.Start);
			}

			return _text.AsSpan(lineStart, Parser.SkipSpaces(_text, lineStart) - lineStart);
		}

		/*
		The index of the first comment that starts, or with `isByEnd` ends, at or after `offset`.

		Note: Not using List.BinarySearch because it was slower in .NET 10 ARM64 probes and needed cached comparers plus a synthetic CommentSyntax search value.
		*/
		private int FindComment(int offset, bool isByEnd)
		{
			var low = 0;
			var high = _comments.Count;

			while (low < high)
			{
				var middle = (low + high) / 2;

				if ((isByEnd ? _comments[middle].End : _comments[middle].Start) < offset)
				{
					low = middle + 1;
				}
				else
				{
					high = middle;
				}
			}

			return low;
		}

		/*
		The end of the comment that starts at `offset`, so that the trivia between tokens can be skipped.
		*/
		private int? CommentEndAt(int offset)
		{
			var index = FindComment(offset, isByEnd: false);
			return index < _comments.Count && _comments[index].Start == offset ? _comments[index].End : null;
		}

		/*
		The start of the block comment that ends at `offset`, so that the comments before a member or an item can be found.
		*/
		private int? BlockCommentStartAt(int offset)
		{
			var index = FindComment(offset, isByEnd: true);
			return index < _comments.Count && _comments[index].End == offset && _comments[index].IsBlock ? _comments[index].Start : null;
		}

		/*
		The comment that starts before `offset` and ends after it, if any: the last one that starts before `offset`.
		*/
		private CommentSyntax? CommentAcross(int offset)
		{
			var index = FindComment(offset, isByEnd: false);

			if (index == 0)
			{
				return null;
			}

			var comment = _comments[index - 1];
			return comment.End > offset ? comment : null;
		}

		/*
		Skips whitespace, line breaks, and comments.
		*/
		private int SkipTrivia(int offset)
		{
			offset = SkipLineTrivia(offset);

			while (Code(offset) == '\n')
			{
				offset = SkipLineTrivia(offset + 1);
			}

			return offset;
		}

		/*
		Skips spaces, tabs, and comments, but not a line break outside a comment.
		*/
		private int SkipLineTrivia(int offset)
		{
			while (true)
			{
				offset = Parser.SkipSpaces(_text, offset);

				if (CommentEndAt(offset) is not { } commentEnd)
				{
					return offset;
				}

				offset = commentEnd;
			}
		}

		/*
		The offset of the line break that ends the line at `offset`, or the end of the text, when only whitespace and comments come before it. Otherwise `null`.
		*/
		private int? FindLineEndAfterTrivia(int offset)
		{
			offset = SkipLineTrivia(offset);
			return offset == _text.Length || Code(offset) == '\n' ? offset : null;
		}

		private int Code(int offset) => (uint)offset < (uint)_text.Length ? _text[offset] : -1;

		/*
		`value` inside new objects for the keys of the path from `index` on. An index there names an item of an array that does not exist yet, so it is an error.
		*/
		private SomlValue Nest(int index, SomlValue value)
		{
			if (_path.Length > Parser.MaxDepth)
			{
				throw new SomlException($"Cannot serialize a value nested more than {Parser.MaxDepth} levels deep");
			}

			var nested = value;

			for (var position = _path.Length - 1; position >= index; position--)
			{
				if (!_path[position].IsKey)
				{
					throw new SomlException($"Cannot edit {DescribePath(_path)}, because {DescribeParent(position)} does not exist, so it has no index {_path[position].Index}");
				}

				nested = new SomlObject { [_path[position].Key!] = nested };
			}

			return nested;
		}

		/*
		The value that holds `path[index]`.
		*/
		private string DescribeParent(int index) => index == 0 ? "the document" : DescribePath(_path[..index]);
	}

	private static bool IsBraced(SyntaxNode container) => container is ArraySyntax || ((ObjectSyntax)container).IsBraced;

	private static string DescribePath(ReadOnlySpan<SomlPathSegment> path)
	{
		var description = new StringBuilder();

		for (var index = 0; index < path.Length && description.Length <= 200; index++)
		{
			if (!path[index].IsKey)
			{
				// Note: Not using StringBuilder.Append(int) because it had the same allocation cost and no consistent public editor gain in the comparisons.
				description.Append(CultureInfo.InvariantCulture, $"[{path[index].Index}]");
			}
			else
			{
				description.Append(index == 0 ? "" : ".").Append(Message.DescribeKey(path[index].Key!));
			}
		}

		// The path comes from the caller, perhaps from user input, so it is cut short, and the characters that a terminal acts on become escapes.
		return Message.DescribeText(Message.Abbreviate(description.ToString(), 200));
	}
}
