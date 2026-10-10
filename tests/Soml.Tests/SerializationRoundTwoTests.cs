using Soml.Serialization;
using System.Text.Json;

namespace Soml.Tests;

public sealed class SerializationRoundTwoTests
{
	private sealed class ConstantNamingPolicy(string key) : JsonNamingPolicy
	{
		public override string ConvertName(string name) => key;
	}

	public sealed class NamedMembers
	{
		public int First { get; init; }
		public int Second { get; init; }
	}

	[Theory]
	[InlineData("members")]
	[InlineData("parameters")]
	[InlineData("enum")]
	public void CollidingPolicyKeysDoNotMakeUnboundedMetadataErrors(string kind)
	{
		var key = new string('x', 100_000);
		var options = new SomlSerializerOptions { PropertyNamingPolicy = new ConstantNamingPolicy(key) };
		Action operation = kind switch
		{
			"members" => () => _ = SomlSerializer.SerializeToValue(new NamedMembers(), options),
			"enum" => () => _ = SomlSerializer.SerializeToValue(DayOfWeek.Monday, options),
			_ => () => SomlMetadata.CheckUniqueParameterKeys(typeof(NamedMembers), [key, key]),
		};
		Assert.Throws<SomlException>(operation);
		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var error = Assert.Throws<SomlException>(operation);
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

		Assert.True(error.Message.Length < 2000);
		Assert.True(allocated < 100_000, $"Formatting a bounded metadata error allocated {allocated} bytes");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void NullDomReadsUseTheirDefaultValueWithoutBoxing(bool generated)
	{
		Func<SomlValue> read = generated
			? () => SomlSerializer.Deserialize(SomlValue.Null, GeneratorContext.Default.SomlValue)
			: () => SomlSerializer.Deserialize<SomlValue>(SomlValue.Null);
		_ = read();
		var allocated = GC.GetAllocatedBytesForCurrentThread();
		SomlValue result = default;

		for (var iteration = 0; iteration < 1000; iteration++)
		{
			result = read();
		}

		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
		Assert.True(result.IsNull);
		Assert.InRange(allocated, 0, 40_000);
	}

	public readonly record struct ThrowingText(string Text) : IParsable<ThrowingText>, IFormattable
	{
		public static ThrowingText Parse(string text, IFormatProvider? provider) => throw new FormatException(text);
		public static bool TryParse(string? text, IFormatProvider? provider, out ThrowingText result)
		{
			result = default;
			return false;
		}

		public string ToString(string? format, IFormatProvider? provider) => throw new InvalidOperationException(Text);
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public void ParserAndFormatterFailuresKeepBoundedDiagnosticsAndTheirFullCause(bool generated, bool writing)
	{
		var text = new string('x', 1_000_000);
		var info = new SomlTypeInfo<ThrowingText>(GeneratorContext.Default, SomlConverters.Parsable<ThrowingText>);
		Action operation = writing
			? () => _ = generated ? SomlSerializer.SerializeToValue(new ThrowingText(text), info) : SomlSerializer.SerializeToValue(new ThrowingText(text))
			: () => _ = generated ? SomlSerializer.Deserialize((SomlValue)text, info) : SomlSerializer.Deserialize<ThrowingText>((SomlValue)text);
		Assert.Throws<SomlException>(operation);
		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var error = Assert.Throws<SomlException>(operation);
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

		Assert.True(error.Message.Length < 2000);
		Assert.Equal(text, error.InnerException?.Message);
		Assert.True(allocated < 100_000, $"Formatting a bounded serializer error allocated {allocated} bytes");
	}

	[Fact]
	public void NativeSignedIntegersUseSomlIntegersAndRejectOverflow()
	{
		Assert.Equal(42, SomlSerializer.SerializeToValue((nint)42).GetInt64());
		Assert.Equal(nint.MinValue, SomlSerializer.Deserialize<nint>((SomlValue)(long)nint.MinValue));
		Assert.Equal(nint.MaxValue, SomlSerializer.Deserialize<nint>((SomlValue)(long)nint.MaxValue));
		Assert.Null(SomlSerializer.Deserialize<nint?>(SomlValue.Null));
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<nint>(SomlValue.Null));

		if (IntPtr.Size == 4)
		{
			Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<nint>((SomlValue)((long)int.MaxValue + 1)));
		}
	}

	[Fact]
	public void NativeUnsignedIntegersUseSomlIntegersAndRejectOverflow()
	{
		Assert.Equal(42, SomlSerializer.SerializeToValue((nuint)42).GetInt64());
		Assert.Equal((nuint)42, SomlSerializer.Deserialize<nuint>((SomlValue)42));
		Assert.Equal((nuint)0, SomlSerializer.Deserialize<nuint>((SomlValue)0));
		Assert.Null(SomlSerializer.Deserialize<nuint?>(SomlValue.Null));
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<nuint>((SomlValue)(-1)));
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<nuint>(SomlValue.Null));

		if (IntPtr.Size == 8)
		{
			Assert.Throws<SomlException>(() => SomlSerializer.SerializeToValue(nuint.MaxValue));
		}
		else
		{
			Assert.Equal((long)uint.MaxValue, SomlSerializer.SerializeToValue(nuint.MaxValue).GetInt64());
			Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<nuint>((SomlValue)((long)uint.MaxValue + 1)));
		}
	}

	public sealed class CustomEnumeration : List<int>, IEnumerable<int>
	{
		IEnumerator<int> IEnumerable<int>.GetEnumerator() => ((IEnumerable<int>)new[] { 99 }).GetEnumerator();
	}

	[Fact]
	public void DerivedListsRetainTheirCustomEnumeration()
	{
		var value = SomlSerializer.SerializeToValue(new CustomEnumeration { 1 });
		Assert.Equal(99, value[0].GetInt64());
	}

	[Fact]
	public void GenericScalarReadsAvoidBoxingTheirResults()
	{
		SomlValue value = 42;

		for (var iteration = 0; iteration < 100; iteration++)
		{
			_ = SomlSerializer.Deserialize<int>(value);
		}

		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var sum = 0;

		for (var iteration = 0; iteration < 1000; iteration++)
		{
			sum += SomlSerializer.Deserialize<int>(value);
		}

		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
		Assert.Equal(42_000, sum);
		Assert.InRange(allocated, 0, 70_000);
	}

	[Fact]
	public void GeneratedScalarReadsDoNotAllocateUnusedPathStorage()
	{
		var info = new SomlTypeInfo<int>(GeneratorContext.Default, static () => SomlConverters.Int32);
		SomlValue value = 42;

		for (var iteration = 0; iteration < 100; iteration++)
		{
			_ = SomlSerializer.Deserialize(value, info);
		}

		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var sum = 0;

		for (var iteration = 0; iteration < 1000; iteration++)
		{
			sum += SomlSerializer.Deserialize(value, info);
		}

		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
		Assert.Equal(42_000, sum);
		Assert.InRange(allocated, 0, 40_000);
	}

	[Fact]
	public void GenericScalarWritesAvoidBoxingTheirInputs()
	{
		for (var iteration = 0; iteration < 100; iteration++)
		{
			_ = SomlSerializer.SerializeToValue(42);
		}

		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var sum = 0L;

		for (var iteration = 0; iteration < 1000; iteration++)
		{
			sum += SomlSerializer.SerializeToValue(42).GetInt64();
		}

		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
		Assert.Equal(42_000, sum);
		Assert.InRange(allocated, 0, 70_000);
	}

	[Fact]
	public void NonVectorArrayTypesAreRejectedWithAConversionError()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize("[]", typeof(int).MakeArrayType(1)));
		Assert.Contains("not supported", error.Message, StringComparison.Ordinal);
	}

	public class Parent
	{
		public int First { get; init; }
	}

	public sealed class Child : Parent
	{
		public int Second { get; init; }
	}

	public sealed class DerivedUri() : Uri("https://example.com/")
	{
		public int Marker => 7;
	}

	[Fact]
	public void BuiltInScalarBaseTypesRetainRuntimeMembers()
	{
		Uri source = new DerivedUri();
		var value = SomlSerializer.SerializeToValue(source);
		Assert.Equal(7, value["marker"].GetInt64());
		Assert.Equal(SomlSerializer.SerializeToValue((object)source), value);
		Assert.Equal(source.OriginalString, SomlSerializer.SerializeToValue(new Uri(source.OriginalString)).GetString());
		Assert.True(SomlSerializer.SerializeToValue<Uri?>(null).IsNull);
	}

	[Fact]
	public void ArrayItemsRetainTheirRuntimeMembers()
	{
		Parent[] source = [new Child { First = 1, Second = 2 }];
		var value = SomlSerializer.SerializeToValue(source);
		Assert.Equal(2, value[0]["second"].GetInt64());
		Assert.Equal(1, value[0]["first"].GetInt64());
	}

	public sealed class InvalidMembers
	{
		[SomlName("same")]
		public int First { get; init; }

		[SomlName("same")]
		public int Second { get; init; }
	}

	[Fact]
	public void NullValuesDoNotDiscoverUnusedConverters()
	{
		Assert.Null(SomlSerializer.Deserialize<InvalidMembers>(SomlValue.Null));
		Assert.True(SomlSerializer.SerializeToValue<InvalidMembers?>(null).IsNull);
	}
}
