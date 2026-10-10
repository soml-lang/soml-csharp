namespace Soml.Tests;

public sealed class ValuePerformanceTests
{
	[Fact]
	public void CanonicalSortingKeepsInsertionOrderAfterSuccessfulAndFailedWrites()
	{
		SomlObject values = [new("z", new SomlObject { ["bad\rkey"] = 1 }), new("a", 2)];
		var error = Assert.Throws<SomlException>(() => values.ToDocumentString(canonical: true));
		Assert.Equal("z.'bad\\u{d}key'", error.Path);
		Assert.Equal(["z", "a"], values.Keys);
		values["z"] = 3;
		Assert.Equal("a: 2\nz: 3\n", values.ToDocumentString(canonical: true));
		Assert.Equal("z: 3\na: 2\n", values.ToDocumentString());
	}

	[Fact]
	public void ReadOnlyCountedInputsAllocateStorageOnceAndEnumerateOnce()
	{
		var arrayItems = new CountedSequence<SomlValue>([1, 2, 3]);
		var objectItems = new CountedSequence<KeyValuePair<string, SomlValue>>([new("b", 2), new("a", 1)]);
		Assert.Equal("[\n\t1\n\t2\n\t3\n]", new SomlArray(arrayItems).ToString());
		Assert.Equal("b: 2\na: 1\n", new SomlObject(objectItems).ToDocumentString());
		Assert.Equal(1, arrayItems.CountReads);
		Assert.Equal(1, arrayItems.Enumerations);
		Assert.Equal(1, objectItems.CountReads);
		Assert.Equal(1, objectItems.Enumerations);
	}

	private sealed class CountedSequence<T>(T[] items) : IReadOnlyCollection<T>
	{
		public int CountReads { get; private set; }
		public int Enumerations { get; private set; }
		public int Count
		{
			get
			{
				CountReads++;
				return items.Length;
			}
		}

		public IEnumerator<T> GetEnumerator()
		{
			Enumerations++;
			return ((IEnumerable<T>)items).GetEnumerator();
		}

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}

	[Fact]
	public void ArrayCollectionExpressionsCopyTheirStorageAndRemainMutable()
	{
		SomlValue[] source = [1, 2, 3, 4, 5];
		SomlArray array = [0, .. source, 6];
		source[0] = 99;
		array.Insert(1, -1);
		array.RemoveAt(3);
		Assert.Equal("[\n\t0\n\t-1\n\t1\n\t3\n\t4\n\t5\n\t6\n]", array.ToString());
		Assert.Empty(SomlArray.Create([]));
	}

	[Fact]
	public void ObjectCollectionExpressionsKeepOrderAndRejectDuplicates()
	{
		KeyValuePair<string, SomlValue>[] source = [new("second", 2), new("first", 1)];
		SomlObject result = [.. source, new("third", 3)];
		source[0] = new("changed", 99);
		Assert.Equal("second: 2\nfirst: 1\nthird: 3\n", result.ToDocumentString());
		Assert.Equal("first: 1\nsecond: 2\nthird: 3\n", result.ToDocumentString(canonical: true));
		Assert.Throws<ArgumentException>(() => SomlObject.Create([new("same", 1), new("same", 2)]));
		Assert.Throws<ArgumentNullException>(() => SomlObject.Create([new(null!, 1)]));
		Assert.Empty(SomlObject.Create([]));
	}

	[Fact]
	public void LiteralAstralStringsAllocateOnlyTheirResult()
	{
		var text = string.Concat(Enumerable.Repeat("a😀b", 1024));
		SomlValue value = text;
		value.ToString();
		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var result = value.ToString();
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
		Assert.Equal("'" + text + "'", result);
		Assert.True(allocated < (text.Length * sizeof(char)) + 128, $"A literal astral string allocated {allocated} bytes");
	}

	[Theory]
	[InlineData("\0")]
	[InlineData(" ")]
	[InlineData("\t")]
	[InlineData("\n")]
	[InlineData("\uFEFF")]
	[InlineData("😀")]
	[InlineData("_")]
	[InlineData("+")]
	public void DurationSpanParsingRejectsTrailingCharacters(string suffix)
	{
		var text = "!1h30m" + suffix + "!";
		Assert.False(SomlDuration.TryParse(text.AsSpan(1, text.Length - 2), null, out _));
	}

	[Theory]
	[InlineData("# outside\n[ {a: 1} ]", "# outside\n[{a: 1}]\n")]
	[InlineData("[\n{a: 1}\n{b: 2}\n]", "[\n\t{a: 1}\n\t{b: 2}\n]\n")]
	[InlineData("[{a: 1 /* comment\ncontinued */}]", "[\n\t{\n\t\ta: 1 /* comment\ncontinued */\n\t}\n]\n")]
	public void ContainerLineMetadataKeepsNestedFormatting(string source, string expected)
	{
		var result = SomlFormatter.Format(source);
		Assert.Equal(expected, result);
		Assert.Equal(SomlValue.Parse(source), SomlValue.Parse(result));
		Assert.Same(result, SomlFormatter.Format(result));
	}
}
