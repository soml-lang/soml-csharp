using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Soml.Serialization;

namespace Soml.Tests;

[SomlSerializable(typeof(nint))]
[SomlSerializable(typeof(nuint))]
[SomlSerializable(typeof(GeneratorRoundTwoTests.DuplicateMemberKeys))]
[SomlSerializable(typeof(GeneratorRoundTwoTests.DuplicateParameterKeys))]
[SomlSerializable(typeof(GeneratorRoundTwoTests.RequiredMembers))]
[SomlSerializable(typeof(GeneratorRoundTwoTests.ConstantKeys))]
[SomlSerializable(typeof(GeneratorRoundTwoTests.PlainFields))]
[SomlSerializable(typeof(GeneratorRoundTwoTests.Empty))]
[SomlSerializable(typeof(GeneratorRoundTwoTests.Wrapper))]
internal partial class RoundTwoGeneratorContext : SomlSerializerContext;

public sealed class GeneratorRoundTwoTests
{
	[Fact]
	public void NativeIntegersUseTheirNumericConvertersInGeneratedContexts()
	{
		var context = RoundTwoGeneratorContext.Default;
		Assert.Equal(nint.MinValue, SomlSerializer.Deserialize((SomlValue)(long)nint.MinValue, context.IntPtr));
		Assert.Equal((nuint)nint.MaxValue, SomlSerializer.Deserialize((SomlValue)(long)nint.MaxValue, context.UIntPtr));
		Assert.Equal(-1, SomlSerializer.SerializeToValue((nint)(-1), context.IntPtr).GetInt64());
		Assert.Equal(1, SomlSerializer.SerializeToValue((nuint)1, context.UIntPtr).GetInt64());
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void RequiredMutableMembersPreserveReadOrderAndMissingKeyErrors(bool generated)
	{
		const string document = "required-property: 'wrong type'";
		var error = Assert.Throws<SomlException>(() => generated
			? SomlSerializer.Deserialize(document, RoundTwoGeneratorContext.Default.RequiredMembers)
			: SomlSerializer.Deserialize<RequiredMembers>(document));

		Assert.Contains("Expected an int", error.Message, StringComparison.Ordinal);
		Assert.Equal("required-property", error.Path);

		var missing = Assert.Throws<SomlException>(() => generated
			? SomlSerializer.Deserialize("required-property: 1", RoundTwoGeneratorContext.Default.RequiredMembers)
			: SomlSerializer.Deserialize<RequiredMembers>("required-property: 1"));
		Assert.Contains("required-field", missing.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ExplicitMemberAndUnmatchedParameterNamesBypassTheNamingPolicy()
	{
		var context = new RoundTwoGeneratorContext(new() { PropertyNamingPolicy = new RejectNames(), RejectUnknownKeys = true });
		var value = SomlSerializer.Deserialize("field-name: 7\nsecond-name: 11\nargument-name: 'hello'\nanother-argument: 13", context.ConstantKeys);

		Assert.Equal(7, value.Field);
		Assert.Equal("hello", value.Argument);
		Assert.Equal(11, value.Second);
		Assert.Equal(13, value.Count);
		Assert.Equal("field-name: 7\nsecond-name: 11\n", SomlSerializer.Serialize(value, context.ConstantKeys));
	}

	[Fact]
	public void DefaultStructFieldsRoundTripWithNullOmissionAndMemberOrder()
	{
		var context = new RoundTwoGeneratorContext(new() { OmitNullMembers = true });
		var value = SomlSerializer.Deserialize("first: 2\nlast: 3", context.PlainFields);

		Assert.Equal(2, value.First);
		Assert.Null(value.Optional);
		Assert.Equal(3, value.Last);
		Assert.Equal("first: 2\nlast: 3\n", SomlSerializer.Serialize(value, context.PlainFields));
		Assert.Equal("first: 0\nlast: 0\n", SomlSerializer.Serialize(default(PlainFields), context.PlainFields));
	}

	[Fact]
	public void EmptyGeneratedObjectsStillEnforceStrictUnknownKeysAtTheNestedPath()
	{
		var strict = new RoundTwoGeneratorContext(new() { RejectUnknownKeys = true });
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize("child: {unexpected: 1}", strict.Wrapper));

		Assert.Equal("child.unexpected", error.Path);
		Assert.NotNull(SomlSerializer.Deserialize("child: {unexpected: 1}", RoundTwoGeneratorContext.Default.Wrapper).Child);
		Assert.Equal("{}\n", SomlSerializer.Serialize(new Empty(), strict.Empty));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ConstantDuplicateKeysAreRejectedBeforeWritingOrConstructing(bool generated)
	{
		var write = Assert.Throws<SomlException>(() => generated
			? SomlSerializer.Serialize(new DuplicateMemberKeys(), RoundTwoGeneratorContext.Default.DuplicateMemberKeys)
			: SomlSerializer.Serialize(new DuplicateMemberKeys()));
		Assert.Contains("both have the key 'same'", write.Message, StringComparison.Ordinal);

		var read = Assert.Throws<SomlException>(() => generated
			? SomlSerializer.Deserialize("{}", RoundTwoGeneratorContext.Default.DuplicateParameterKeys)
			: SomlSerializer.Deserialize<DuplicateParameterKeys>("{}"));
		Assert.Contains("Two parameters", read.Message, StringComparison.Ordinal);
	}

	public sealed class DuplicateMemberKeys
	{
		[SomlName("same")]
		public int Left { get; set; }

		[SomlName("same")]
		public int Right { get; set; }
	}

	public sealed class DuplicateParameterKeys
	{
		public DuplicateParameterKeys([SomlName("same")] int left, [SomlName("same")] int right)
		{
			_ = left + right;
		}
	}

	public sealed class RequiredMembers
	{
		[SomlName("required-field")]
		public required int Field;

		[SomlName("required-property")]
		public required int Property { get; set; }

		[SetsRequiredMembers]
		public RequiredMembers() {}
	}

	public sealed class ConstantKeys
	{
		[SomlName("field-name")]
		public int Field;

		[SomlName("second-name")]
		public int Second;

		[SomlIgnore]
		public string Argument { get; }

		[SomlIgnore]
		public int Count { get; }

		public ConstantKeys([SomlName("argument-name")] string text, [SomlName("another-argument")] int number)
		{
			Argument = text;
			Count = number;
		}
	}

	public struct PlainFields
	{
		public int First;
		public string? Optional;
		public int Last;
	}

	public sealed class Empty;

	public sealed class Wrapper
	{
		public Empty? Child { get; set; }
	}

	private sealed class RejectNames : JsonNamingPolicy
	{
		public override string ConvertName(string name) => throw new InvalidOperationException(name);
	}
}
