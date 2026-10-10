namespace Soml.Tests;

public sealed class EditorTests
{
	[Fact]
	public void ReplacesAValueAndKeepsComments()
	{
		Assert.Equal("name: 'api' # The service\nport: 9090\n", SomlEditor.Set("name: 'api' # The service\nport: 8080\n", ["port"], 9090));
	}

	[Fact]
	public void AddsAMemberToAOneLineObject()
	{
		Assert.Equal("postgres: {host: 'db', port: 5432}\n", SomlEditor.Set("postgres: {host: 'db'}\n", ["postgres", "port"], 5432));
	}

	[Fact]
	public void CreatesMissingObjects()
	{
		Assert.Equal("a: 1\nb: {\n\tc: {\n\t\td: true\n\t}\n}\n", SomlEditor.Set("a: 1\n", ["b", "c", "d"], true));
	}

	[Fact]
	public void SortsANewObjectWhenCanonical()
	{
		var value = new SomlObject { ["y"] = 1, ["x"] = 2 };

		Assert.Equal("a: 1\nb: {\n\tx: 2\n\ty: 1\n}\n", SomlEditor.Set("a: 1\n", ["b"], value, canonical: true));
		Assert.Equal("a: 1\nb: {\n\ty: 1\n\tx: 2\n}\n", SomlEditor.Set("a: 1\n", ["b"], value));
	}

	[Fact]
	public void RemovesAndTellsWhetherItRemoved()
	{
		Assert.Equal("b: 2\n", SomlEditor.Remove("a: 1\nb: 2\n", ["a"], out var isRemoved));
		Assert.True(isRemoved);

		Assert.Equal("a: 1\n", SomlEditor.Remove("a: 1\n", ["missing"], out isRemoved));
		Assert.False(isRemoved);

		Assert.Equal("{} # note\n", SomlEditor.Remove("a: 1 # note\n", ["a"]));
	}

	[Theory]
	[InlineData("a: 1\ra: 2")]
	[InlineData("a: 1\na: 2")]
	[InlineData("a: [1,, 2]")]
	public void RejectsAnInvalidDocumentWhenRemovingAMissingValue(string text)
	{
		Assert.Throws<SomlParseException>(() => SomlEditor.Remove(text, ["missing"]));
	}

	[Fact]
	public void FindsAKeyWhateverItsQuoting()
	{
		const string Text = "plain: 1\n'a b': 2\n\"tab\\there\": 3\n\"x\": 4\n";

		Assert.Equal("plain: 9\n'a b': 2\n\"tab\\there\": 3\n\"x\": 4\n", SomlEditor.Set(Text, ["plain"], 9));
		Assert.Equal("plain: 1\n'a b': 9\n\"tab\\there\": 3\n\"x\": 4\n", SomlEditor.Set(Text, ["a b"], 9));
		Assert.Equal("plain: 1\n'a b': 2\n\"tab\\there\": 9\n\"x\": 4\n", SomlEditor.Set(Text, ["tab\there"], 9));
		Assert.Equal("plain: 1\n'a b': 2\n\"tab\\there\": 3\n\"x\": 9\n", SomlEditor.Set(Text, ["x"], 9));

		// The escape is not the key, so a key spelled as it is written is a new member.
		Assert.EndsWith("'tab\\there': 9\n", SomlEditor.Set(Text, ["tab\\there"], 9), StringComparison.Ordinal);

		// The position of an error from the serializer is found through the same lookup.
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>("'a b': {\"t\\tx\": 'no'}"));
		Assert.Equal("'a b'.'t\\u{9}x'", error.Path);
		Assert.EndsWith("line 1, column 17)", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void RejectsAPathThatDoesNotFit()
	{
		var error = Assert.Throws<SomlException>(() => SomlEditor.Set("a: [1]\n", ["a", 3], 1));
		Assert.Equal("Cannot edit a[3], because the array at a has 1 item. Add an item at index 1", error.Message);

		var keyOnArray = Assert.Throws<SomlException>(() => SomlEditor.Set("a: [1]\n", ["a", "b"], 1));
		Assert.Equal("Cannot edit a.b, because a is an array, so it needs an index, not a key", keyOnArray.Message);

		Assert.Throws<ArgumentException>(() => SomlEditor.Set("a: 1\n", [], 1));
		Assert.Throws<ArgumentOutOfRangeException>(() => SomlEditor.Set("a: 1\n", [-1], 1));
	}

	[Fact]
	public void FormatsAsTheSpecificationSays()
	{
		Assert.Equal("pool: {min: 2, max: 16} # Connections\n", SomlFormatter.Format("pool: {min: 2,   max: 16,} # Connections"));
		Assert.Equal("pool: {\n\tmin: 2\n\tmax: 16\n}\n", SomlFormatter.Format("pool: {\nmin: 2, max: 16}"));
		Assert.True(SomlFormatter.IsFormatted("a: 1\n"));
		Assert.False(SomlFormatter.IsFormatted("a: 1"));
	}
}
