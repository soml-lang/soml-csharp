using System.Text.Json;
using Soml.Serialization;

namespace Soml.Tests;

public sealed class CustomConverterTests
{
	private sealed class Context(SomlSerializerOptions? options = null) : SomlSerializerContext(options);

	private readonly record struct Amount(int Value);

	private sealed class AmountConverter : SomlConverter<Amount>
	{
		public int Reads { get; private set; }

		public int Writes { get; private set; }

		public override string Expected => "an int";

		public override Amount Read(SomlValue value, SomlConverterState state)
		{
			Reads++;
			var number = state.Read(value, SomlConverters.Int32, isNullable: false);

			if (number < 0)
			{
				throw state.Error("An amount must not be negative");
			}

			return new(number * 10);
		}

		public override SomlValue Write(Amount value, SomlConverterState state)
		{
			Writes++;

			if (value.Value < 0)
			{
				throw state.Error("An amount must not be negative");
			}

			return value.Value / 10;
		}
	}

	[Fact]
	public void CreatesTheConverterAfterParsingAndReusesItWithTheContextsOptions()
	{
		var context = new Context(new SomlSerializerOptions { Canonical = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseUpper });
		var creations = 0;
		var typeInfo = new SomlTypeInfo<Dictionary<string, int>>(context, () =>
		{
			creations++;
			return SomlConverters.Dictionary<Dictionary<string, int>, string, int>(SomlConverters.String, null, SomlConverters.Int32, isValueNullable: false, static members => members);
		});

		Assert.Throws<SomlParseException>(() => SomlSerializer.Deserialize("a: [1,, 2]", typeInfo));
		Assert.Equal(0, creations);

		var value = SomlSerializer.Deserialize("lower-key: 1\nUpperKey: 2", typeInfo);
		Assert.Equal(1, value["lower-key"]);
		Assert.Equal(2, value["UpperKey"]);
		Assert.Equal("UpperKey: 2\nlower-key: 1\n", SomlSerializer.Serialize(value, typeInfo));
		Assert.Equal(value, SomlSerializer.Deserialize("UpperKey: 2\nlower-key: 1", typeInfo));
		Assert.Equal(1, creations);
	}

	[Fact]
	public void ComposesNullableCustomConvertersAndPreservesNestedErrorPaths()
	{
		var converter = new AmountConverter();
		var nullable = SomlConverters.Nullable(converter);
		var list = SomlConverters.Collection<List<Amount?>, Amount?>(nullable, isItemNullable: true, isSet: false, static items => items);
		var dictionary = SomlConverters.Dictionary<Dictionary<string, List<Amount?>>, string, List<Amount?>>(SomlConverters.String, null, list, isValueNullable: false, static members => members);
		var context = new Context();
		var typeInfo = new SomlTypeInfo<Dictionary<string, List<Amount?>>>(context, () => dictionary);
		var value = SomlSerializer.Deserialize("map: [1, null, 2]", typeInfo);

		Assert.Equal([new Amount(10), null, new Amount(20)], value["map"]);
		Assert.Equal("map: [\n\t1\n\tnull\n\t2\n]\n", SomlSerializer.Serialize(value, typeInfo));

		var scalar = new SomlTypeInfo<Amount?>(context, () => nullable);
		Assert.Null(SomlSerializer.Deserialize(SomlValue.Null, scalar));
		Assert.True(SomlSerializer.SerializeToValue((Amount?)null, scalar).IsNull);
		Assert.Equal(new Amount(30), SomlSerializer.Deserialize((SomlValue)3, scalar));
		Assert.Equal((SomlValue)3, SomlSerializer.SerializeToValue((Amount?)new Amount(30), scalar));
		Assert.Equal(3, converter.Reads);
		Assert.Equal(3, converter.Writes);

		var readError = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize("map: [1, -2]", typeInfo));
		Assert.Equal("map[1]", readError.Path);
		Assert.Equal("An amount must not be negative (at map[1], line 1, column 10)", readError.Message);

		var writeError = Assert.Throws<SomlException>(() => SomlSerializer.Serialize(new Dictionary<string, List<Amount?>> { ["map"] = [new(10), new(-20)] }, typeInfo));
		Assert.Equal("map[1]", writeError.Path);
		Assert.Equal("An amount must not be negative (at map[1])", writeError.Message);
	}
}
