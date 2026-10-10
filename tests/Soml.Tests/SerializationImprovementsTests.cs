using Soml.Serialization;

namespace Soml.Tests;

public sealed class SerializationImprovementsTests
{
	private sealed class Context : SomlSerializerContext
	{
		public Context() : base(null)
		{
		}
	}

	public sealed class DuplicateMembers
	{
		[SomlName("\u001b[31m")]
		public int First { get; set; }

		[SomlName("\u001b[31m")]
		public int Second { get; set; }
	}

	public sealed class DuplicateParameters([SomlName("\u202e")] int first, [SomlName("\u202e")] int second)
	{
		[SomlIgnore]
		public int Total { get; } = first + second;
	}

	public enum DuplicateEnumNames
	{
		[SomlName("\u009b31m")]
		First,
		[SomlName("\u009b31m")]
		Second,
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void DuplicateMemberDiagnosticsEscapeTerminalControls(bool generated)
	{
		var error = Assert.Throws<SomlException>(() => generated ? SomlSerializer.Serialize(new DuplicateMembers(), SerializationImprovementsContext.Default.DuplicateMembers) : SomlSerializer.Serialize(new DuplicateMembers()));
		Assert.DoesNotContain('\u001b', error.Message);
		Assert.Contains("\\u{1b}", error.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void DuplicateParameterDiagnosticsEscapeBidiControls(bool generated)
	{
		var error = Assert.Throws<SomlException>(() => generated ? SomlSerializer.Deserialize("{}", SerializationImprovementsContext.Default.DuplicateParameters) : SomlSerializer.Deserialize<DuplicateParameters>("{}"));
		Assert.DoesNotContain('\u202e', error.Message);
		Assert.Contains("\\u{202e}", error.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void DuplicateEnumDiagnosticsEscapeC1Controls(bool generated)
	{
		var error = Assert.Throws<SomlException>(() => generated ? SomlSerializer.Serialize(DuplicateEnumNames.First, SerializationImprovementsContext.Default.DuplicateEnumNames) : SomlSerializer.Serialize(DuplicateEnumNames.First));
		Assert.DoesNotContain('\u009b', error.Message);
		Assert.Contains("\\u{9b}", error.Message, StringComparison.Ordinal);
	}

	public sealed class CaseInsensitiveDictionary : Dictionary<string, int>
	{
		public CaseInsensitiveDictionary() : base(StringComparer.OrdinalIgnoreCase)
		{
		}

		public new bool Add(string key, int value) => TryAdd(key, value);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void DictionaryCreationCannotSilentlyDiscardKeysUnderItsComparer(bool generated)
	{
		var error = Assert.Throws<SomlException>(() => generated ? SomlSerializer.Deserialize("a: 1\nA: 2", SerializationImprovementsContext.Default.CaseInsensitiveDictionary) : SomlSerializer.Deserialize<CaseInsensitiveDictionary>("a: 1\nA: 2"));
		Assert.Contains("Duplicate key", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void NullableConverterRejectsANullInnerConverterAtCreation()
	{
		Assert.Throws<ArgumentNullException>("converter", () => SomlConverters.Nullable<int>(null!));
	}

	[Fact]
	public void ArrayConverterRejectsANullItemConverterAtCreation()
	{
		Assert.Throws<ArgumentNullException>("item", () => SomlConverters.ArrayOf<int>(null!, isItemNullable: false));
	}

	[Fact]
	public void CollectionConverterRejectsANullItemConverterAtCreation()
	{
		Assert.Throws<ArgumentNullException>("item", () => SomlConverters.Collection<List<int>, int>(null!, isItemNullable: false, isSet: false, static items => items));
	}

	[Fact]
	public void DictionaryConverterRejectsNullRequiredArgumentsAtCreation()
	{
		Assert.Throws<ArgumentNullException>("key", () => SomlConverters.Dictionary<Dictionary<string, int>, string, int>(null!, null, SomlConverters.Int32, isValueNullable: false, static members => members));
		Assert.Throws<ArgumentNullException>("value", () => SomlConverters.Dictionary<Dictionary<string, int>, string, int>(SomlConverters.String, null, null!, isValueNullable: false, static members => members));
		Assert.Throws<ArgumentNullException>("create", () => SomlConverters.Dictionary<Dictionary<string, int>, string, int>(SomlConverters.String, null, SomlConverters.Int32, isValueNullable: false, null!));
	}

	[Fact]
	public void EnumConverterRejectsNullOptionsEvenForEmptyMetadata()
	{
		Assert.Throws<ArgumentNullException>("options", () => SomlConverters.Enum<DayOfWeek>(null!, [], isFlags: false));
	}

	private sealed class InvalidPathConverter(bool usesIndex) : SomlConverter<int>
	{
		public override string Expected => "an int";

		public override int Read(SomlValue value, SomlConverterState state)
		{
			if (usesIndex)
			{
				state.Push(-1);
			}
			else
			{
				state.Push((string)null!);
			}

			return 0;
		}

		public override SomlValue Write(int value, SomlConverterState state) => value;
	}

	[Fact]
	public void ConverterPathsRejectNegativeIndexesBeforeChangingThePath()
	{
		var info = new SomlTypeInfo<int>(new Context(), () => new InvalidPathConverter(usesIndex: true));
		Assert.Throws<ArgumentOutOfRangeException>("index", () => SomlSerializer.Deserialize("[]", info));
	}

	[Fact]
	public void ConverterPathsRejectNullKeysBeforeChangingThePath()
	{
		var info = new SomlTypeInfo<int>(new Context(), () => new InvalidPathConverter(usesIndex: false));
		Assert.Throws<ArgumentNullException>("key", () => SomlSerializer.Deserialize("[]", info));
	}

	[Fact]
	public void UnsupportedArrayErrorsKeepTheirActualRank()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<int[,]>("[]"));
		Assert.StartsWith("Cannot read int[,]", error.Message, StringComparison.Ordinal);
	}
}

[SomlSerializable(typeof(SerializationImprovementsTests.DuplicateMembers))]
[SomlSerializable(typeof(SerializationImprovementsTests.DuplicateParameters))]
[SomlSerializable(typeof(SerializationImprovementsTests.DuplicateEnumNames))]
[SomlSerializable(typeof(SerializationImprovementsTests.CaseInsensitiveDictionary))]
internal partial class SerializationImprovementsContext : SomlSerializerContext;
