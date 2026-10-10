using System.Text;
using System.Text.Json.Nodes;

namespace Soml.Tests;

public sealed class ConformanceTests
{
	public static TheoryData<string> ValidCases => Conformance.Cases("valid", ".soml");

	public static TheoryData<string> InvalidCases => Conformance.Cases("invalid", ".soml");

	[Theory]
	[MemberData(nameof(ValidCases))]
	public void ValidCaseHasItsValue(string name)
	{
		var expected = Conformance.FromTagged(JsonNode.Parse(Conformance.ReadText($"valid/{name}.json"), documentOptions: new() { MaxDepth = 200 }));
		var bytes = Conformance.ReadBytes($"valid/{name}.soml");
		var value = SomlValue.Parse(bytes);

		Assert.Equal(expected, value);
		Assert.Equal(value, SomlValue.Parse(Encoding.UTF8.GetString(bytes)));
	}

	[Theory]
	[MemberData(nameof(ValidCases))]
	public void ValidCaseHasItsCanonicalForm(string name)
	{
		var value = SomlValue.Parse(Conformance.ReadBytes($"valid/{name}.soml"));
		var canonical = value.ToDocumentString(canonical: true);

		Assert.Equal(Conformance.ReadText($"valid/{name}.canonical.soml"), canonical);

		// Canonical form is a fixed point.
		Assert.Equal(canonical, SomlValue.Parse(canonical).ToDocumentString(canonical: true));
	}

	[Theory]
	[MemberData(nameof(ValidCases))]
	public void ValidCaseWrittenForPeopleKeepsItsOrder(string name)
	{
		var value = SomlValue.Parse(Conformance.ReadBytes($"valid/{name}.soml"));
		var written = value.ToDocumentString(canonical: false);

		Assert.True(Conformance.HaveSameOrder(value, SomlValue.Parse(written)), written);
	}

	[Theory]
	[MemberData(nameof(ValidCases))]
	public void ValidCaseHasItsFormattedForm(string name)
	{
		var formatted = SomlFormatter.Format(Conformance.ReadText($"valid/{name}.soml"));

		Assert.Equal(Conformance.ReadText($"valid/{name}.formatted.soml"), formatted);

		// Formatting is idempotent, and it keeps the value.
		Assert.Equal(formatted, SomlFormatter.Format(formatted));
		Assert.True(Conformance.HaveSameOrder(SomlValue.Parse(Conformance.ReadBytes($"valid/{name}.soml")), SomlValue.Parse(formatted)));
	}

	[Theory]
	[MemberData(nameof(EditCases))]
	public void EditCaseGivesItsDocument(string name)
	{
		var editCase = JsonNode.Parse(Conformance.ReadText($"edit/{name}.json"))!.AsObject();
		var document = editCase["document"]!.GetValue<string>();
		var path = editCase["path"]!.AsArray().Select(segment => segment!.GetValueKind() == System.Text.Json.JsonValueKind.Number ? (SomlPathSegment)segment.GetValue<int>() : segment.GetValue<string>()).ToArray();
		var value = editCase.ContainsKey("value") ? Conformance.FromTagged(editCase["value"]) : (SomlValue?)null;

		// The cases start from a formatted document.
		Assert.True(SomlFormatter.IsFormatted(document));

		string Edit() => value is null ? SomlEditor.Remove(document, path) : SomlEditor.Set(document, path, value.Value);

		if (editCase["error"]?.GetValue<bool>() == true)
		{
			Assert.Throws<SomlException>(Edit);
			return;
		}

		var expected = editCase["expected"]!.GetValue<string>();
		var edited = Edit();

		Assert.Equal(expected, edited);

		// An edit to a formatted document gives a formatted document.
		Assert.True(SomlFormatter.IsFormatted(edited));
	}

	public static TheoryData<string> EditCases => Conformance.Cases("edit", ".json");

	[Theory]
	[MemberData(nameof(InvalidCases))]
	public void InvalidCaseIsRejectedWithItsReasonAndPosition(string name)
	{
		var bytes = Conformance.ReadBytes($"invalid/{name}.soml");
		var expected = Conformance.InvalidReasons[name]!;
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse(bytes));

		Assert.Equal(expected["reason"]!.GetValue<string>(), error.Reason);
		Assert.Equal((expected["line"]!.GetValue<int>(), expected["column"]!.GetValue<int>()), (error.Line, error.Column));

		// The same from a string, when the bytes are valid UTF-8.
		if (System.Text.Unicode.Utf8.IsValid(bytes))
		{
			var stringError = Assert.Throws<SomlParseException>(() => SomlValue.Parse(Encoding.UTF8.GetString(bytes)));
			Assert.Equal((error.Reason, error.Line, error.Column), (stringError.Reason, stringError.Line, stringError.Column));

			// The formatter and the editor only check the document, without building its value, and give the same error.
			var formatError = Assert.Throws<SomlParseException>(() => SomlFormatter.Format(Encoding.UTF8.GetString(bytes)));
			Assert.Equal((error.Reason, error.Line, error.Column), (formatError.Reason, formatError.Line, formatError.Column));

			var editError = Assert.Throws<SomlParseException>(() => SomlEditor.Set(Encoding.UTF8.GetString(bytes), ["a"], 1));
			Assert.Equal((error.Reason, error.Line, error.Column), (editError.Reason, editError.Line, editError.Column));
		}
	}
}
