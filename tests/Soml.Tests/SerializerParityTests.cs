using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Soml.Serialization;

namespace Soml.Tests;

[SomlSerializable(typeof(SerializerParityTests.OverrideDerived))]
[SomlSerializable(typeof(SerializerParityTests.Hider))]
[SomlSerializable(typeof(SerializerParityTests.PrivateHider))]
[SomlSerializable(typeof(SerializerParityTests.StaticHider))]
[SomlSerializable(typeof(SerializerParityTests.RetypingHider))]
[SomlSerializable(typeof(SerializerParityTests.FieldHider))]
[SomlSerializable(typeof(SerializerParityTests.GenericHider))]
[SomlSerializable(typeof(SerializerParityTests.GenericOverrider))]
[SomlSerializable(typeof(SerializerParityTests.NamedParameter))]
[SomlSerializable(typeof(SerializerParityTests.NonNullBoxed))]
[SomlSerializable(typeof(SerializerParityTests.NullableBoxed))]
[SomlSerializable(typeof(SerializerParityTests.Box<string>))]
[SomlSerializable(typeof(SerializerParityTests.Chained))]
[SomlSerializable(typeof(SerializerParityTests.DisallowNullBox<string>))]
[SomlSerializable(typeof(SerializerParityTests.Outer<int>.NestedHider<string>))]
[SomlSerializable(typeof(SerializerParityTests.GenericNewHider<string>))]
[SomlSerializable(typeof(SerializerParityTests.PrivateGenericNewHider<string>))]
[SomlSerializable(typeof(SerializerParityTests.OtherPositionHider<string, int>))]
[SomlSerializable(typeof(SerializerParityTests.NotNullBox<string>))]
[SomlSerializable(typeof(SerializerParityTests.NullableClassBox<string>))]
[SomlSerializable(typeof(SerializerParityTests.GenericBoxed<string>))]
[SomlSerializable(typeof(SerializerParityTests.TwoParametersWithOneKey))]
[SomlSerializable(typeof(SerializerParityTests.InitRecord))]
[SomlSerializable(typeof(SerializerParityTests.PositionalNamed))]
[SomlSerializable(typeof(SerializerParityTests.NullableItems))]
[SomlSerializable(typeof(SerializerParityTests.RequiredInitChild))]
[SomlSerializable(typeof(SerializerParityTests.ConstructorDefaults))]
[SomlSerializable(typeof(SerializerParityTests.FieldStruct))]
[SomlSerializable(typeof(SerializerParityTests.Annotated))]
[SomlSerializable(typeof(SerializerParityTests.Numeric))]
[SomlSerializable(typeof(SerializerParityTests.WithSignedFlags))]
[SomlSerializable(typeof(SerializerParityTests.Naming))]
[SomlSerializable(typeof(SerializerParityTests.Shapes))]
[SomlSerializable(typeof(SerializerParityTests.WithMap))]
[SomlSerializable(typeof(SerializerParityTests.OrderDerived))]
[SomlSerializable(typeof(SerializerParityTests.Pair<int, string?>))]
[SomlSerializable(typeof(SerializerParityTests.RequiredField))]
[SomlSerializable(typeof(SerializerParityTests.SetsSome))]
[SomlSerializable(typeof(SerializerParityTests.WithAliases))]
[SomlSerializable(typeof(SerializerParityTests.WithClashingEnum))]
[SomlSerializable(typeof(SerializerParityTests.Linked))]
[SomlSerializable(typeof(SerializerParityTests.Validating))]
[SomlSerializable(typeof(SerializerParityTests.ValidatingRecord))]
[SomlSerializable(typeof(SerializerParityTests.DefaultStructParameter))]
[SomlSerializable(typeof(SerializerParityTests.DefaultEnumParameter))]
[SomlSerializable(typeof(SerializerParityTests.DateTimeDefaults))]
[SomlSerializable(typeof(SerializerParityTests.NonoptionalMetadataDefaults))]
[SomlSerializable(typeof(SerializerParityTests.NonoptionalMetadataEnum))]
[SomlSerializable(typeof(SerializerParityTests.OptionalMetadataDefaults))]
[SomlSerializable(typeof(SerializerParityTests.IgnoredMetadataDefaults))]
[SomlSerializable(typeof(SerializerParityTests.RequiredNullableParameter))]
[SomlSerializable(typeof(SerializerParityTests.RequiredDefaultedParameter))]
[SomlSerializable(typeof(SerializerParityTests.IgnoredPropertyHider))]
[SomlSerializable(typeof(SerializerParityTests.IgnoredFieldHider))]
[SomlSerializable(typeof(SerializerParityTests.NamedHiddenMemberParameter))]
[SomlSerializable(typeof(SerializerParityTests.SetterAllowsNull))]
[SomlSerializable(typeof(SerializerParityTests.SetterDisallowsNull))]
[SomlSerializable(typeof(SerializerParityTests.NullableValueAnnotations))]
[SomlSerializable(typeof(SerializerParityTests.DisallowNullValueParameter))]
[SomlSerializable(typeof(SerializerParityTests.NestedTypeHider))]
[SomlSerializable(typeof(SerializerParityTests.ConstructorLinked))]
[SomlSerializable(typeof(SerializerParityTests.OverloadedList))]
[SomlSerializable(typeof(SerializerParityTests.OverloadedDictionary))]
[SomlSerializable(typeof(SerializerParityTests.HiddenListAdd))]
[SomlSerializable(typeof(SerializerParityTests.StaticListAdd))]
[SomlSerializable(typeof(SerializerParityTests.GenericListAdd))]
[SomlSerializable(typeof(SerializerParityTests.StructCollection))]
[SomlSerializable(typeof(SerializerParityTests.ConversionCollection))]
[SomlSerializable(typeof(SerializerParityTests.ConversionConstructorCollection))]
[SomlSerializable(typeof(SerializerParityTests.CovariantOverride))]
[SomlSerializable(typeof(SerializerParityTests.CovariantNamedOverride))]
[SomlSerializable(typeof(SerializerParityTests.CovariantInheritedName))]
[SomlSerializable(typeof(SerializerParityTests.CovariantInheritedIgnore))]
[SomlSerializable(typeof(SerializerParityTests.ParsableCollections))]
[SomlSerializable(typeof(SerializerParityTests.Stacks))]
internal sealed partial class ParityContext : SomlSerializerContext;

public sealed class SerializerParityTests
{
	public sealed class ParsableList : List<int>, IParsable<ParsableList>, IFormattable
	{
		static ParsableList IParsable<ParsableList>.Parse(string text, IFormatProvider? provider)
		{
			var result = new ParsableList();
			result.AddRange(text.Split(',').Select(item => int.Parse(item, provider)));
			return result;
		}

		static bool IParsable<ParsableList>.TryParse(string? text, IFormatProvider? provider, out ParsableList result)
		{
			result = new();

			foreach (var item in (text ?? "").Split(','))
			{
				if (!int.TryParse(item, provider, out var value))
				{
					return false;
				}

				result.Add(value);
			}

			return true;
		}

		string IFormattable.ToString(string? format, IFormatProvider? provider) => string.Join(",", this);
	}

	public sealed class ParsableMap : Dictionary<string, int>, IParsable<ParsableMap>, IFormattable
	{
		static ParsableMap IParsable<ParsableMap>.Parse(string text, IFormatProvider? provider) => new() { ["a"] = int.Parse(text, provider) };

		static bool IParsable<ParsableMap>.TryParse(string? text, IFormatProvider? provider, out ParsableMap result)
		{
			var success = int.TryParse(text, provider, out var value);
			result = new() { ["a"] = value };
			return success;
		}

		string IFormattable.ToString(string? format, IFormatProvider? provider) => this["a"].ToString(provider);
	}

	public sealed class ParsableCollections
	{
		public ParsableList List { get; set; } = [];

		public ParsableMap Map { get; set; } = [];
	}

	[Theory]
	[InlineData(false, "list", "ParsableList", "an array")]
	[InlineData(true, "list", "ParsableList", "an array")]
	[InlineData(false, "map", "ParsableMap", "an object")]
	[InlineData(true, "map", "ParsableMap", "an object")]
	public void ReportsTheCollectionShapeForANullParsableCollection(bool generated, string key, string type, string expected)
	{
		var input = $"{key}: null";
		var error = Assert.Throws<SomlException>(() => generated ? SomlSerializer.Deserialize(input, ParityContext.Default.ParsableCollections) : SomlSerializer.Deserialize<ParsableCollections>(input));

		Assert.Equal($"Expected {expected} for {type}, but found null (at {key}, line 1, column {key.Length + 3})", error.Message);
		Assert.Equal(key, error.Path);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void UsesCollectionConvertersBeforeParsableConverters(bool generated)
	{
		const string input = "list: [1, 2, 3]\nmap: {a: 42}";
		var value = generated ? SomlSerializer.Deserialize(input, ParityContext.Default.ParsableCollections) : SomlSerializer.Deserialize<ParsableCollections>(input);

		Assert.Equal(new[] { 1, 2, 3 }, value.List);
		Assert.Equal(42, value.Map["a"]);
		Assert.Equal("list: [\n\t1\n\t2\n\t3\n]\nmap: {\n\ta: 42\n}\n", generated ? SomlSerializer.Serialize(value, ParityContext.Default.ParsableCollections) : SomlSerializer.Serialize(value));
	}

	public sealed class Stacks
	{
		public Stack<int> Stack { get; set; } = [];

		public ConcurrentStack<int> Concurrent { get; set; } = [];

		public ImmutableStack<int> Immutable { get; set; } = [];
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void KeepsTheOrderOfAStackOnARoundTrip(bool generated)
	{
		const string input = "stack: [3, 2, 1]\nconcurrent: [3, 2, 1]\nimmutable: [3, 2, 1]";
		var value = generated ? SomlSerializer.Deserialize(input, ParityContext.Default.Stacks) : SomlSerializer.Deserialize<Stacks>(input);

		Assert.Equal(3, value.Stack.Peek());
		Assert.True(value.Concurrent.TryPeek(out var top) && top == 3);
		Assert.Equal(3, value.Immutable.Peek());
		Assert.Equal(input.Replace(", ", "\n\t").Replace("[", "[\n\t").Replace("]", "\n]") + "\n", generated ? SomlSerializer.Serialize(value, ParityContext.Default.Stacks) : SomlSerializer.Serialize(value));
	}

	[Fact]
	public void UsesTheParsableConverterForCollectionDictionaryKeys()
	{
		var listKeys = SomlSerializer.Deserialize<Dictionary<ParsableList, int>>("'1,2': 7");
		Assert.Equal(new[] { 1, 2 }, Assert.Single(listKeys).Key);
		Assert.Equal(7, Assert.Single(listKeys).Value);
		Assert.Equal("'1,2': 7\n", SomlSerializer.Serialize(listKeys));

		var mapKeys = SomlSerializer.Deserialize<Dictionary<ParsableMap, int>>("'42': 7");
		Assert.Equal(42, Assert.Single(mapKeys).Key["a"]);
		Assert.Equal(7, Assert.Single(mapKeys).Value);
		Assert.Equal("42: 7\n", SomlSerializer.Serialize(mapKeys));
	}

	public class NamedHiddenMemberBase
	{
		[SomlName("base-value")]
		public string Value { get; set; } = "";
	}

	public sealed class NamedHiddenMemberParameter([SomlName("value")] int derivedValue) : NamedHiddenMemberBase
	{
		public new int Value { get; } = derivedValue;
	}

	public class PetInfo
	{
		public string Name { get; set; } = "";
	}

	public sealed class DogInfo : PetInfo;

	public class CovariantBase(PetInfo value)
	{
		public virtual PetInfo Value { get; } = value;
	}

	public sealed class CovariantOverride(DogInfo value) : CovariantBase(value)
	{
		public override DogInfo Value { get; } = value;
	}

	public class CovariantNamedBase(PetInfo value)
	{
		[SomlName("base-value")]
		public virtual PetInfo Value { get; } = value;
	}

	public sealed class CovariantNamedOverride(DogInfo value) : CovariantNamedBase(value)
	{
		[SomlName("pet")]
		public override DogInfo Value { get; } = value;
	}

	public sealed class CovariantInheritedName(DogInfo value) : CovariantNamedBase(value)
	{
		public override DogInfo Value { get; } = value;
	}

	public class CovariantIgnoredBase(PetInfo? value)
	{
		[SomlIgnore]
		public virtual PetInfo? Value { get; } = value;
	}

	public sealed class CovariantInheritedIgnore(DogInfo? value) : CovariantIgnoredBase(value)
	{
		public override DogInfo? Value { get; } = value;
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ReadsAndWritesOnlyTheCovariantOverride(bool generated)
	{
		var value = generated ? SomlSerializer.Deserialize("value: {name: 'Fido'}", ParityContext.Default.CovariantOverride) : SomlSerializer.Deserialize<CovariantOverride>("value: {name: 'Fido'}");

		Assert.Equal("Fido", value.Value.Name);
		Assert.Equal("value: {\n\tname: 'Fido'\n}\n", generated ? SomlSerializer.Serialize(value, ParityContext.Default.CovariantOverride) : SomlSerializer.Serialize(value));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void UsesTheCovariantOverridesName(bool generated)
	{
		var value = generated ? SomlSerializer.Deserialize("pet: {name: 'Fido'}", ParityContext.Default.CovariantNamedOverride) : SomlSerializer.Deserialize<CovariantNamedOverride>("pet: {name: 'Fido'}");

		Assert.Equal("Fido", value.Value.Name);
		Assert.Equal("pet: {\n\tname: 'Fido'\n}\n", generated ? SomlSerializer.Serialize(value, ParityContext.Default.CovariantNamedOverride) : SomlSerializer.Serialize(value));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void InheritsTheNameOfACovariantOverride(bool generated)
	{
		var value = generated ? SomlSerializer.Deserialize("base-value: {name: 'Fido'}", ParityContext.Default.CovariantInheritedName) : SomlSerializer.Deserialize<CovariantInheritedName>("base-value: {name: 'Fido'}");

		Assert.Equal("Fido", value.Value.Name);
		Assert.Equal("base-value: {\n\tname: 'Fido'\n}\n", generated ? SomlSerializer.Serialize(value, ParityContext.Default.CovariantInheritedName) : SomlSerializer.Serialize(value));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void InheritsIgnoreOnACovariantOverride(bool generated)
	{
		var value = generated ? SomlSerializer.Deserialize("value: {name: 'Fido'}", ParityContext.Default.CovariantInheritedIgnore) : SomlSerializer.Deserialize<CovariantInheritedIgnore>("value: {name: 'Fido'}");

		Assert.Null(value.Value);
		Assert.Equal("{}\n", generated ? SomlSerializer.Serialize(value, ParityContext.Default.CovariantInheritedIgnore) : SomlSerializer.Serialize(value));
	}

	public sealed class HiddenListAdd : List<int>
	{
		public void Add(long item)
		{
			base.Add(-(int)item);
		}
	}

	public sealed class StaticListAdd : List<int>
	{
		public new static void Add(int item)
		{
			throw new InvalidOperationException("Static Add must not be called");
		}
	}

	public sealed class GenericListAdd : List<int>
	{
		public void Add<TUnused>(int item)
		{
			throw new InvalidOperationException("Generic Add must not be called");
		}
	}

	public struct StructCollection : IEnumerable<int>
	{
		private List<int>? _items;

		public void Add(int item)
		{
			(_items ??= []).Add(item);
		}

		public readonly IEnumerator<int> GetEnumerator() => (_items ?? []).GetEnumerator();

		readonly System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void IgnoresStaticAddWhenPopulatingACollection(bool generated)
	{
		var value = generated ? SomlSerializer.Deserialize("[1, 2, 3]", ParityContext.Default.StaticListAdd) : SomlSerializer.Deserialize<StaticListAdd>("[1, 2, 3]");
		Assert.Equal(new[] { 1, 2, 3 }, value);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void IgnoresGenericAddWhenPopulatingACollection(bool generated)
	{
		var value = generated ? SomlSerializer.Deserialize("[1, 2, 3]", ParityContext.Default.GenericListAdd) : SomlSerializer.Deserialize<GenericListAdd>("[1, 2, 3]");
		Assert.Equal(new[] { 1, 2, 3 }, value);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void PopulatesACollectionStructWithoutAnExplicitConstructor(bool generated)
	{
		var value = generated ? SomlSerializer.Deserialize("[1, 2, 3]", ParityContext.Default.StructCollection) : SomlSerializer.Deserialize<StructCollection>("[1, 2, 3]");
		Assert.Equal(new[] { 1, 2, 3 }, value);
	}

	public sealed class ConversionCollection : IEnumerable<int>
	{
		private readonly List<int> _items = [];

		public void Add(int item)
		{
			_items.Add(item);
		}

		public static implicit operator ConversionCollection(List<int> items)
		{
			var result = new ConversionCollection();

			foreach (var item in items)
			{
				result.Add(-item);
			}

			return result;
		}

		public IEnumerator<int> GetEnumerator() => _items.GetEnumerator();

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}

	public sealed class ConvertedItems(List<int> items)
	{
		public List<int> Items { get; } = items;

		public static implicit operator ConvertedItems(List<int> items) => new(items);
	}

	public sealed class ConversionConstructorCollection : List<int>
	{
		public ConversionConstructorCollection()
		{
		}

		public ConversionConstructorCollection(ConvertedItems items) : base(items.Items.Select(item => -item))
		{
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void UsesAddInsteadOfAUserDefinedCollectionConversion(bool generated)
	{
		var value = generated ? SomlSerializer.Deserialize("[1, 2, 3]", ParityContext.Default.ConversionCollection) : SomlSerializer.Deserialize<ConversionCollection>("[1, 2, 3]");
		Assert.Equal(new[] { 1, 2, 3 }, value);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void UsesAddInsteadOfAConstructorParameterConversion(bool generated)
	{
		var value = generated ? SomlSerializer.Deserialize("[1, 2, 3]", ParityContext.Default.ConversionConstructorCollection) : SomlSerializer.Deserialize<ConversionConstructorCollection>("[1, 2, 3]");
		Assert.Equal(new[] { 1, 2, 3 }, value);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ChoosesTheMatchingInheritedAddMethod(bool generated)
	{
		var value = generated ? SomlSerializer.Deserialize("[1, 2, 3]", ParityContext.Default.HiddenListAdd) : SomlSerializer.Deserialize<HiddenListAdd>("[1, 2, 3]");
		Assert.Equal(new[] { 1, 2, 3 }, value);
	}

	public sealed class OverloadedList : List<int>
	{
		public OverloadedList(IEnumerable<int> items) : base(items)
		{
			Constructor = "enumerable";
		}

		public OverloadedList(IList<int> items) : base(items)
		{
			Constructor = "list";
		}

		public string Constructor { get; }
	}

	public sealed class OverloadedDictionary : Dictionary<string, int>
	{
		public OverloadedDictionary(IEnumerable<KeyValuePair<string, int>> members) : base(members)
		{
			Constructor = "enumerable";
		}

		public OverloadedDictionary(IDictionary<string, int> members) : base(members)
		{
			Constructor = "dictionary";
		}

		public string Constructor { get; }
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ChoosesTheFirstCompatibleListConstructor(bool generated)
	{
		var value = generated ? SomlSerializer.Deserialize("[1, 2, 3]", ParityContext.Default.OverloadedList) : SomlSerializer.Deserialize<OverloadedList>("[1, 2, 3]");
		Assert.Equal("enumerable", value.Constructor);
		Assert.Equal([1, 2, 3], value);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ChoosesTheFirstCompatibleDictionaryConstructor(bool generated)
	{
		var value = generated ? SomlSerializer.Deserialize("first: 1\nsecond: 2", ParityContext.Default.OverloadedDictionary) : SomlSerializer.Deserialize<OverloadedDictionary>("first: 1\nsecond: 2");
		Assert.Equal("enumerable", value.Constructor);
		Assert.Equal(1, value["first"]);
		Assert.Equal(2, value["second"]);
	}

	public struct InitializedStruct
	{
		public InitializedStruct()
		{
			Value = 42;
		}

		public int Value { get; set; }
	}

	public sealed record DefaultStructParameter(InitializedStruct Value = default, InitializedStruct? MaybeValue = default, [property: SomlIgnore] InitializedStruct Ignored = default);

	public sealed record DefaultEnumParameter(Level? Value = Level.High);

	public sealed class DateTimeDefaults
	{
		public DateTimeDefaults([System.Runtime.InteropServices.Optional, System.Runtime.CompilerServices.DateTimeConstant(1234567)] DateTime value, [System.Runtime.InteropServices.Optional, System.Runtime.CompilerServices.DateTimeConstant(7654321)] DateTime? maybeValue)
		{
			Value = value;
			MaybeValue = maybeValue;
		}

		public DateTime Value { get; }

		public DateTime? MaybeValue { get; }
	}

	public sealed class NonoptionalMetadataDefaults([System.Runtime.CompilerServices.DateTimeConstant(1234)] DateTime instant, [System.Runtime.CompilerServices.DecimalConstant(1, 0, 0, 0, 5)] decimal amount, [System.Runtime.InteropServices.DefaultParameterValue(42)] int count)
	{
		public DateTime Instant { get; } = instant;

		public decimal Amount { get; } = amount;

		public int Count { get; } = count;
	}

	public sealed class NonoptionalMetadataEnum([System.Runtime.InteropServices.DefaultParameterValue(Level.High)] Level? value)
	{
		public Level? Value { get; } = value;
	}

	public sealed class OptionalMetadataDefaults([System.Runtime.InteropServices.Optional, System.Runtime.CompilerServices.DecimalConstant(1, 0, 0, 0, 5)] decimal amount, [System.Runtime.InteropServices.Optional, System.Runtime.InteropServices.DefaultParameterValue(42)] int count, [System.Runtime.InteropServices.Optional, System.Runtime.InteropServices.DefaultParameterValue(Level.High)] Level? value)
	{
		public decimal Amount { get; } = amount;

		public int Count { get; } = count;

		public Level? Value { get; } = value;
	}

	public sealed record IgnoredMetadataDefaults([property: SomlIgnore] [System.Runtime.InteropServices.DefaultParameterValue(42)] int Nonoptional, [property: SomlIgnore] [System.Runtime.InteropServices.Optional, System.Runtime.InteropServices.DefaultParameterValue(24)] int Optional);

	[Theory]
	[InlineData(false, "{}", "instant")]
	[InlineData(true, "{}", "instant")]
	[InlineData(false, "instant: 1970-01-01T00:00:00Z", "amount")]
	[InlineData(true, "instant: 1970-01-01T00:00:00Z", "amount")]
	[InlineData(false, "instant: 1970-01-01T00:00:00Z\namount: 1", "count")]
	[InlineData(true, "instant: 1970-01-01T00:00:00Z\namount: 1", "count")]
	public void RequiresNonoptionalMetadataDefaultParameters(bool generated, string input, string key)
	{
		var error = Assert.Throws<SomlException>(() => generated ? SomlSerializer.Deserialize(input, ParityContext.Default.NonoptionalMetadataDefaults) : SomlSerializer.Deserialize<NonoptionalMetadataDefaults>(input));
		Assert.StartsWith($"Missing the key {key}, which NonoptionalMetadataDefaults requires", error.Message, StringComparison.Ordinal);

		const string supplied = "instant: 1970-01-01T00:00:00Z\namount: 1\ncount: 2";
		var value = generated ? SomlSerializer.Deserialize(supplied, ParityContext.Default.NonoptionalMetadataDefaults) : SomlSerializer.Deserialize<NonoptionalMetadataDefaults>(supplied);
		Assert.Equal(DateTime.UnixEpoch, value.Instant);
		Assert.Equal(1M, value.Amount);
		Assert.Equal(2, value.Count);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void OmitsANonoptionalNullableMetadataDefaultAsNull(bool generated)
	{
		var value = generated ? SomlSerializer.Deserialize("{}", ParityContext.Default.NonoptionalMetadataEnum) : SomlSerializer.Deserialize<NonoptionalMetadataEnum>("{}");
		Assert.Null(value.Value);

		var supplied = generated ? SomlSerializer.Deserialize("value: 'low'", ParityContext.Default.NonoptionalMetadataEnum) : SomlSerializer.Deserialize<NonoptionalMetadataEnum>("value: 'low'");
		Assert.Equal(Level.Low, supplied.Value);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void PreservesOptionalMetadataDefaults(bool generated)
	{
		var value = generated ? SomlSerializer.Deserialize("{}", ParityContext.Default.OptionalMetadataDefaults) : SomlSerializer.Deserialize<OptionalMetadataDefaults>("{}");
		Assert.Equal(0.5M, value.Amount);
		Assert.Equal(42, value.Count);
		Assert.Equal(Level.High, value.Value);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void UsesOnlyOptionalDefaultsForIgnoredMetadataParameters(bool generated)
	{
		var value = generated ? SomlSerializer.Deserialize("{}", ParityContext.Default.IgnoredMetadataDefaults) : SomlSerializer.Deserialize<IgnoredMetadataDefaults>("{}");
		Assert.Equal(0, value.Nonoptional);
		Assert.Equal(24, value.Optional);
	}

	public sealed class RequiredNullableParameter
	{
		[SetsRequiredMembers]
		public RequiredNullableParameter(string? name)
		{
			Name = name;
		}

		public required string? Name { get; init; }
	}

	public sealed class RequiredDefaultedParameter
	{
		[SetsRequiredMembers]
		public RequiredDefaultedParameter(string name = "default")
		{
			Name = name;
		}

		public required string Name { get; init; }
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void RequiresAMemberMatchedToANullableConstructorParameter(bool generated)
	{
		var error = Assert.Throws<SomlException>(() => generated ? SomlSerializer.Deserialize("{}", ParityContext.Default.RequiredNullableParameter) : SomlSerializer.Deserialize<RequiredNullableParameter>("{}"));

		Assert.Equal("Missing the key name, which RequiredNullableParameter requires (at line 1, column 1)", error.Message);
		var value = generated ? SomlSerializer.Deserialize("name: null", ParityContext.Default.RequiredNullableParameter) : SomlSerializer.Deserialize<RequiredNullableParameter>("name: null");
		Assert.Null(value.Name);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void RequiresAMemberMatchedToADefaultedConstructorParameter(bool generated)
	{
		var error = Assert.Throws<SomlException>(() => generated ? SomlSerializer.Deserialize("{}", ParityContext.Default.RequiredDefaultedParameter) : SomlSerializer.Deserialize<RequiredDefaultedParameter>("{}"));

		Assert.Equal("Missing the key name, which RequiredDefaultedParameter requires (at line 1, column 1)", error.Message);
		var value = generated ? SomlSerializer.Deserialize("name: 'provided'", ParityContext.Default.RequiredDefaultedParameter) : SomlSerializer.Deserialize<RequiredDefaultedParameter>("name: 'provided'");
		Assert.Equal("provided", value.Name);
	}

	[Fact]
	public void AllowsAnOmittedNullableConstructorParameterWithoutARequiredMember()
	{
		var reflection = SomlSerializer.Deserialize<Pair<int, string?>>("first: 1");
		var generated = SomlSerializer.Deserialize("first: 1", ParityContext.Default.PairInt32String);

		Assert.Equal(1, reflection.First);
		Assert.Null(reflection.Second);
		Assert.Equal(reflection.First, generated.First);
		Assert.Null(generated.Second);
	}

	[Fact]
	public void UsesTheEnumValueForAnOptionalNullableEnumParameter()
	{
		Assert.Equal(Level.High, SomlSerializer.Deserialize<DefaultEnumParameter>("{}").Value);
		Assert.Equal(Level.High, SomlSerializer.Deserialize("{}", ParityContext.Default.DefaultEnumParameter).Value);
		AssertReadsTheSame(context => context.DefaultEnumParameter, "value: null");
		AssertReadsTheSame(context => context.DefaultEnumParameter, "value: 'low'");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void PreservesDateTimeConstructorDefaults(bool generated)
	{
		var value = generated ? SomlSerializer.Deserialize("{}", ParityContext.Default.DateTimeDefaults) : SomlSerializer.Deserialize<DateTimeDefaults>("{}");

		Assert.Equal(new DateTime(1234567), value.Value);
		Assert.Equal(new DateTime(7654321), value.MaybeValue);

		var explicitNull = generated ? SomlSerializer.Deserialize("maybe-value: null", ParityContext.Default.DateTimeDefaults) : SomlSerializer.Deserialize<DateTimeDefaults>("maybe-value: null");
		Assert.Equal(new DateTime(1234567), explicitNull.Value);
		Assert.Null(explicitNull.MaybeValue);
	}

	[Fact]
	public void UsesTheZeroValueForAnOptionalStructParameter()
	{
		Assert.Equal(0, new DefaultStructParameter().Value.Value);
		var reflection = SomlSerializer.Deserialize<DefaultStructParameter>("{}");
		var generated = SomlSerializer.Deserialize("{}", ParityContext.Default.DefaultStructParameter);

		Assert.Equal(0, reflection.Value.Value);
		Assert.Equal(0, reflection.Ignored.Value);
		Assert.Null(reflection.MaybeValue);
		Assert.Equal(reflection, generated);
		Assert.Equal(42, SomlSerializer.Deserialize<DefaultStructParameter>("value: {}").Value.Value);
		Assert.Equal(42, SomlSerializer.Deserialize("value: {}", ParityContext.Default.DefaultStructParameter).Value.Value);
	}

	public enum Level
	{
		Low,
		High,
		VeryHigh,
	}

	[Flags]
	public enum SignedFlags
	{
		None = 0,
		Low = 1,
		High = int.MinValue,
	}

	[Flags]
	public enum ShortFlags : short
	{
		Low = 1,
		High = short.MinValue,
	}

	[Flags]
	public enum SByteFlags : sbyte
	{
		Low = 1,
		High = sbyte.MinValue,
	}

	[Flags]
	public enum LongFlags : long
	{
		Low = 1,
		High = long.MinValue,
	}

	public class OverrideBase
	{
		[SomlIgnore]
		public virtual int Secret { get; set; }

		[SomlName("label")]
		public virtual string Title { get; set; } = "";
	}

	public class HiddenBase
	{
		[SomlName("base-name")]
		public virtual int X { get; set; }

		public virtual int Z { get; set; }
	}

	public sealed class Hider : HiddenBase
	{
		public new int X { get; set; }
	}

	public sealed class PrivateHider : HiddenBase
	{
		private new int X { get; set; } = 9;

		public int Shown => X;
	}

	public sealed class StaticHider : HiddenBase
	{
		public static new int X { get; set; }
	}

	public class IgnoredPropertyBase
	{
		[SomlIgnore]
		public int Value { get; set; }
	}

	public sealed class IgnoredPropertyHider(int value) : IgnoredPropertyBase
	{
		public new int Value { get; } = value;
	}

	public class IgnoredFieldBase
	{
		[SomlIgnore]
		public int Value;
	}

	public sealed class IgnoredFieldHider(int value) : IgnoredFieldBase
	{
		public new readonly int Value = value;
	}

	public class NestedTypeContainer<T>
	{
		public sealed class Item
		{
			public int Count { get; set; }
		}
	}

	public class NestedTypeBase
	{
		[SomlName("base-item")]
		public NestedTypeContainer<int>.Item Item { get; set; } = new();
	}

	public sealed class NestedTypeHider : NestedTypeBase
	{
		public new NestedTypeContainer<string>.Item Item { get; set; } = new();
	}

	public sealed class RetypingHider : HiddenBase
	{
		public new string Z { get; set; } = "";
	}

	public class FieldBase
	{
		public int F = 1;
	}

	public sealed class FieldHider : FieldBase
	{
		public new int F = 2;
	}

	public class GenericBase<T>
	{
		public virtual T Value { get; set; } = default!;
	}

	public sealed class GenericHider : GenericBase<string>
	{
		public new string Value { get; set; } = "";
	}

	public sealed class GenericOverrider : GenericBase<string>
	{
		public override string Value { get; set; } = "";
	}

	public sealed class TwoParametersWithOneKey([SomlName("a")] int first, [SomlName("a")] int second)
	{
		public int Sum { get; } = first + second;
	}

	public class Box<T>
	{
		public T Value { get; set; } = default!;
	}

	public sealed class NonNullBoxed : Box<string>;

	public sealed class NullableBoxed : Box<string?>;

	public class MiddleBox<T> : Box<T>;

	public sealed class Chained : MiddleBox<string>;

	public sealed class GenericBoxed<T> : Box<T>;

	public sealed class GenericNewHider<T> : Box<T>
	{
		public new T Value { get; set; } = default!;
	}

	public sealed class PrivateGenericNewHider<T> : Box<T>
	{
		private new T Value { get; set; } = default!;

		public string Other { get; set; } = "";
	}

	public sealed class OtherPositionHider<TFirst, TSecond> : Box<TFirst>
	{
		public new TSecond Value { get; set; } = default!;
	}

	public class DisallowNullBox<T>
	{
		[DisallowNull]
		public T Value { get; set; } = default!;
	}

	public class Outer<TOuter>
	{
		public sealed class NestedHider<TInner> : Box<TInner>
		{
			public new TInner Value { get; set; } = default!;
		}
	}

	public class NotNullBox<T> where T : notnull
	{
		public T Value { get; set; } = default!;
	}

	public class NullableClassBox<T> where T : class?
	{
		public T Value { get; set; } = default!;
	}

	public sealed class NamedParameter
	{
		[SomlConstructor]
		public NamedParameter([SomlName("port")] int listenPort)
		{
			Port = listenPort + 1;
		}

		public int Port { get; set; }
	}

	public sealed class OverrideDerived : OverrideBase
	{
		public override int Secret { get; set; } = 7;

		public override string Title { get; set; } = "t";
	}

	public sealed record InitRecord(int A)
	{
		public int B { get; init; } = 5;

		public required string C { get; init; }

		public string? D { get; init; }
	}

	public sealed record PositionalNamed([SomlName("the-id")] int Id, [property: SomlIgnore] string Secret = "s", string? Note = null, Level Level = Level.High);

	public sealed class NullableItems
	{
		public List<int?> Numbers { get; set; } = [];

		public List<string?> Maybe { get; set; } = [];

		public List<string> Plain { get; set; } = [];

		public Dictionary<string, string> Map { get; set; } = [];

		public int?[] Array { get; set; } = [];
	}

	public class RequiredInitBase
	{
		public required string Name { get; init; }

		public int Rank { get; init; }
	}

	public sealed class RequiredInitChild(int extra) : RequiredInitBase
	{
		public int Extra { get; } = extra;
	}

	public sealed class ConstructorDefaults(int port = 80, string host = "localhost", TimeSpan? timeout = null, Level level = Level.VeryHigh, double ratio = 0.5, Guid? id = null)
	{
		public int Port { get; } = port;

		public string Host { get; } = host;

		public TimeSpan? Timeout { get; } = timeout;

		public Level Level { get; } = level;

		public double Ratio { get; } = ratio;

		public Guid? Id { get; } = id;
	}

	public struct FieldStruct
	{
		public int X;

		public string? Name;

		public readonly int Y;

		public FieldStruct(int x, int y)
		{
			X = x;
			Y = y;
		}
	}

	public sealed class Annotated
	{
		[AllowNull]
		public string Name { get; set; } = "";

		[DisallowNull]
		public string? Other { get; set; } = "o";
	}

	public sealed class SetterAllowsNull
	{
		private string _value = "initial";

		public string Value
		{
			get => _value;
			[param: AllowNull]
			set => _value = value ?? "fallback";
		}
	}

	public sealed class SetterDisallowsNull
	{
		private string? _value = "initial";

		public string? Value
		{
			get => _value;
			[param: DisallowNull]
			set => _value = value;
		}
	}

	public sealed class NullableValueAnnotations
	{
		[DisallowNull]
		public int? Property { get; set; } = 1;

		[DisallowNull]
		public int? Field = 2;

		public int? Setter
		{
			get;
			[param: DisallowNull]
			set;
		} = 3;

		public int? Maybe { get; set; } = 4;
	}

	public sealed class DisallowNullValueParameter([DisallowNull] int? value)
	{
		public int? Value { get; } = value;
	}

	public sealed class Numeric
	{
		public byte Byte { get; set; }

		public int Int { get; set; }

		public float Single { get; set; }

		public double Double { get; set; }

		public decimal Decimal { get; set; }

		public Half Half { get; set; }

		public ulong ULong { get; set; }

		public UInt128 Big { get; set; }
	}

	public sealed class WithSignedFlags
	{
		public SignedFlags Flags { get; set; }
	}

	public sealed class Naming
	{
		public string HTTPServer { get; set; } = "";

		public int MaxRetryCount { get; set; }

		public Level DefaultLevel { get; set; }

		[SomlName("Explicit")]
		public int Renamed { get; set; }
	}

	public sealed class Shapes
	{
		public Naming Inner { get; set; } = new();

		public List<int> Items { get; set; } = [];

		public Dictionary<string, int> Map { get; set; } = [];

		public string Text { get; set; } = "";

		public bool Flag { get; set; }

		public SomlInstant When { get; set; }

		public TimeSpan Span { get; set; }

		public DateOnly Day { get; set; }

		public Level Level { get; set; }

		public IReadOnlyList<Naming> Many { get; set; } = [];
	}

	public sealed class ThrowingMap : IReadOnlyDictionary<string, int>
	{
		public int this[string key] => throw new InvalidOperationException("broken");

		public IEnumerable<string> Keys => throw new InvalidOperationException("broken");

		public IEnumerable<int> Values => throw new InvalidOperationException("broken");

		public int Count => 1;

		public bool ContainsKey(string key) => false;

		public bool TryGetValue(string key, [MaybeNullWhen(false)] out int value) => throw new InvalidOperationException("broken");

		public IEnumerator<KeyValuePair<string, int>> GetEnumerator() => throw new InvalidOperationException("broken");

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}

	public sealed class WithMap
	{
		public IReadOnlyDictionary<string, int> Map { get; set; } = new Dictionary<string, int>();
	}

	public class OrderBase
	{
		public int BaseField = 1;

		public int BaseProperty { get; set; } = 2;
	}

	public sealed class OrderDerived : OrderBase
	{
		public int DerivedField = 3;

		public int DerivedProperty { get; set; } = 4;

		public int Computed => DerivedProperty * 2;
	}

	public sealed record Pair<TFirst, TSecond>(TFirst First, TSecond Second);

	public sealed class RequiredField
	{
		public required int Count;

		public string? Note;
	}

	public sealed class SetsSome
	{
		[SetsRequiredMembers]
		public SetsSome(string name)
		{
			Name = name;
			Other = "set";
		}

		public required string Name { get; init; }

		public required string Other { get; init; }
	}

	public enum Aliased
	{
		First,
		Alias = First,
		Second,
	}

	public sealed class WithAliases
	{
		public Aliased Value { get; set; }

		public List<Aliased> Values { get; set; } = [];
	}

	public enum Clashing
	{
		FooBar,
		[SomlName("foo-bar")]
		Other,
	}

	public sealed class WithClashingEnum
	{
		public Clashing Value { get; set; }
	}

	public sealed class Linked
	{
		public string Name { get; set; } = "";

		public Linked? Next { get; set; }
	}

	public sealed class ConstructorLinked(ConstructorLinked? next)
	{
		public ConstructorLinked? Next { get; } = next;
	}

	public sealed class Validating
	{
		private int _port = 1;

		public int Port
		{
			get => _port;
			set => _port = value > 0 ? value : throw new SomlException("The port must be positive");
		}

		public int Broken => Port > 5 ? throw new SomlException("Too large to show") : Port;
	}

	public sealed record ValidatingRecord
	{
		public ValidatingRecord(string host)
		{
			Host = host.Length > 0 ? host : throw new SomlException("The host must not be empty");
		}

		public string Host { get; }
	}

	private static string Outcome(Func<string> action)
	{
		try
		{
			return action();
		}
		catch (SomlException exception)
		{
			return $"error: {exception.Message} [{exception.Path}] {exception.InnerException?.GetType().Name}";
		}
	}

	private static void AssertReadsTheSame<T>(Func<ParityContext, SomlTypeInfo<T>> select, string text, SomlSerializerOptions? options = null)
	{
		var context = options is null ? ParityContext.Default : new ParityContext(options);
		var reflection = Outcome(() => SomlSerializer.Serialize(SomlSerializer.Deserialize<T>(text, options), options));
		var generated = Outcome(() => SomlSerializer.Serialize(SomlSerializer.Deserialize(text, select(context)), select(context)));
		Assert.Equal(reflection, generated);
	}

	private static void AssertWritesTheSame<T>(Func<ParityContext, SomlTypeInfo<T>> select, T value, SomlSerializerOptions? options = null)
	{
		var context = options is null ? ParityContext.Default : new ParityContext(options);
		var reflection = Outcome(() => SomlSerializer.Serialize(value, options));
		var generated = Outcome(() => SomlSerializer.Serialize(value, select(context)));
		Assert.Equal(reflection, generated);
	}

	private static readonly SomlSerializerOptions Strict = new() { RejectUnknownKeys = true };

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ReadsAConstructorParameterWhoseIgnoredBasePropertyIsHidden(bool generated)
	{
		var value = generated ? SomlSerializer.Deserialize("value: 42", ParityContext.Default.IgnoredPropertyHider) : SomlSerializer.Deserialize<IgnoredPropertyHider>("value: 42");

		Assert.Equal(42, value.Value);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void MatchesANonignoredConstructorMemberBeforeAnIgnoredBaseField(bool generated)
	{
		var value = generated ? SomlSerializer.Deserialize("value: 42", ParityContext.Default.IgnoredFieldHider) : SomlSerializer.Deserialize<IgnoredFieldHider>("value: 42");

		Assert.Equal(42, value.Value);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void BindsANamedParameterWithoutMatchingAnAmbiguousInheritedMember(bool generated)
	{
		const string text = "base-value: 'base'\nvalue: 42\n";
		var value = generated ? SomlSerializer.Deserialize(text, ParityContext.Default.NamedHiddenMemberParameter) : SomlSerializer.Deserialize<NamedHiddenMemberParameter>(text);

		Assert.Equal("base", ((NamedHiddenMemberBase)value).Value);
		Assert.Equal(42, value.Value);
		Assert.Equal(text, generated ? SomlSerializer.Serialize(value, ParityContext.Default.NamedHiddenMemberParameter) : SomlSerializer.Serialize(value));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void KeepsMembersWhoseNestedTypesHaveDifferentContainingTypeArguments(bool generated)
	{
		const string text = "base-item: {count: 1}\nitem: {count: 2}\n";
		var value = generated ? SomlSerializer.Deserialize(text, ParityContext.Default.NestedTypeHider) : SomlSerializer.Deserialize<NestedTypeHider>(text);

		Assert.Equal(1, ((NestedTypeBase)value).Item.Count);
		Assert.Equal(2, value.Item.Count);
		var serialized = generated ? SomlSerializer.Serialize(value, ParityContext.Default.NestedTypeHider) : SomlSerializer.Serialize(value);
		Assert.Equal("base-item: {\n\tcount: 1\n}\nitem: {\n\tcount: 2\n}\n", serialized);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ReadsANullableConstructorParameterOfTheDeclaringType(bool generated)
	{
		var empty = generated ? SomlSerializer.Deserialize("{}", ParityContext.Default.ConstructorLinked) : SomlSerializer.Deserialize<ConstructorLinked>("{}");
		Assert.Null(empty.Next);

		var linked = generated ? SomlSerializer.Deserialize("next: {next: {}}", ParityContext.Default.ConstructorLinked) : SomlSerializer.Deserialize<ConstructorLinked>("next: {next: {}}");
		Assert.NotNull(linked.Next);
		Assert.NotNull(linked.Next.Next);
		Assert.Null(linked.Next.Next.Next);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void AllowsNullWhenTheSetterParameterAllowsIt(bool generated)
	{
		var value = generated ? SomlSerializer.Deserialize("value: null", ParityContext.Default.SetterAllowsNull) : SomlSerializer.Deserialize<SetterAllowsNull>("value: null");

		Assert.Equal("fallback", value.Value);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void RejectsNullWhenTheSetterParameterDisallowsIt(bool generated)
	{
		var error = Assert.Throws<SomlException>(() => generated ? SomlSerializer.Deserialize("value: null", ParityContext.Default.SetterDisallowsNull) : SomlSerializer.Deserialize<SetterDisallowsNull>("value: null"));

		Assert.Equal("value", error.Path);
		Assert.StartsWith("Expected a string for string, but found null", error.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(false, "property")]
	[InlineData(true, "property")]
	[InlineData(false, "field")]
	[InlineData(true, "field")]
	[InlineData(false, "setter")]
	[InlineData(true, "setter")]
	public void RejectsNullForDisallowNullNullableValueMembers(bool generated, string key)
	{
		var error = Assert.Throws<SomlException>(() => generated ? SomlSerializer.Deserialize($"{key}: null", ParityContext.Default.NullableValueAnnotations) : SomlSerializer.Deserialize<NullableValueAnnotations>($"{key}: null"));

		Assert.Equal(key, error.Path);
		Assert.StartsWith("Expected an int for int?, but found null", error.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void KeepsNullableValueMemberDefaultsAndAcceptsTheirNonNullValues(bool generated)
	{
		var omitted = generated ? SomlSerializer.Deserialize("{}", ParityContext.Default.NullableValueAnnotations) : SomlSerializer.Deserialize<NullableValueAnnotations>("{}");
		Assert.Equal((1, 2, 3, 4), (omitted.Property, omitted.Field, omitted.Setter, omitted.Maybe));

		const string text = "property: 5\nfield: 6\nsetter: 7\nmaybe: null";
		var supplied = generated ? SomlSerializer.Deserialize(text, ParityContext.Default.NullableValueAnnotations) : SomlSerializer.Deserialize<NullableValueAnnotations>(text);
		Assert.Equal((5, 6, 7, null), (supplied.Property, supplied.Field, supplied.Setter, supplied.Maybe));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void RequiresANullableValueConstructorParameterMarkedDisallowNull(bool generated)
	{
		var missing = Assert.Throws<SomlException>(() => generated ? SomlSerializer.Deserialize("{}", ParityContext.Default.DisallowNullValueParameter) : SomlSerializer.Deserialize<DisallowNullValueParameter>("{}"));
		Assert.StartsWith("Missing the key value, which DisallowNullValueParameter requires", missing.Message, StringComparison.Ordinal);

		var explicitNull = Assert.Throws<SomlException>(() => generated ? SomlSerializer.Deserialize("value: null", ParityContext.Default.DisallowNullValueParameter) : SomlSerializer.Deserialize<DisallowNullValueParameter>("value: null"));
		Assert.Equal("value", explicitNull.Path);
		Assert.StartsWith("Expected an int for int?, but found null", explicitNull.Message, StringComparison.Ordinal);

		var supplied = generated ? SomlSerializer.Deserialize("value: 5", ParityContext.Default.DisallowNullValueParameter) : SomlSerializer.Deserialize<DisallowNullValueParameter>("value: 5");
		Assert.Equal(5, supplied.Value);
	}

	[Fact]
	public void LeavesOutAPropertyHiddenWithNewLikeTheReflectionPath()
	{
		var value = new Hider { X = 1, Z = 2 };
		((HiddenBase)value).X = 3;

		Assert.Equal("z: 2\nx: 1\n", SomlSerializer.Serialize(value));
		AssertWritesTheSame(context => context.Hider, value);
		AssertReadsTheSame(context => context.Hider, "x: 4\nz: 5\nbase-name: 6");
	}

	[Fact]
	public void ChoosesHiddenMembersLikeTheReflectionPath()
	{
		// A private property hides the base one, a static one does not, a property of another type or a field does not either, so their keys clash, and a property of a generic base is hidden only by an override.
		var privateHider = new PrivateHider { Z = 2 };
		((HiddenBase)privateHider).X = 3;
		var staticHider = new StaticHider { Z = 2 };
		((HiddenBase)staticHider).X = 3;

		Assert.Equal("z: 2\nshown: 9\n", SomlSerializer.Serialize(privateHider));
		AssertWritesTheSame(context => context.PrivateHider, privateHider);
		AssertReadsTheSame(context => context.PrivateHider, "base-name: 4\nz: 5\nx: 6");
		Assert.Equal("base-name: 3\nz: 2\n", SomlSerializer.Serialize(staticHider));
		AssertWritesTheSame(context => context.StaticHider, staticHider);
		AssertReadsTheSame(context => context.StaticHider, "base-name: 4\nz: 5");
		AssertWritesTheSame(context => context.RetypingHider, new RetypingHider());
		AssertWritesTheSame(context => context.FieldHider, new FieldHider());
		AssertWritesTheSame(context => context.GenericHider, new GenericHider());
		AssertWritesTheSame(context => context.GenericOverrider, new GenericOverrider { Value = "v" });
		AssertReadsTheSame(context => context.GenericOverrider, "value: 'w'");
		Assert.Contains("both have the key 'z'", Outcome(() => SomlSerializer.Serialize(new RetypingHider())), StringComparison.Ordinal);
		Assert.Contains("both have the key 'f'", Outcome(() => SomlSerializer.Serialize(new FieldHider())), StringComparison.Ordinal);
		Assert.Contains("both have the key 'value'", Outcome(() => SomlSerializer.Serialize(new GenericHider())), StringComparison.Ordinal);
	}

	[Fact]
	public void HidesWithTheTypeParameterAtTheSamePositionLikeTheReflectionPath()
	{
		// Reflection compares a type parameter by its position, so `new T Value` in `GenericNewHider<T> : Box<T>` hides the base one, and `new TSecond Value` over `Box<TFirst>` does not.
		AssertWritesTheSame(context => context.GenericNewHiderString, new GenericNewHider<string> { Value = "a" });
		AssertReadsTheSame(context => context.GenericNewHiderString, "value: 'b'");
		AssertWritesTheSame(context => context.PrivateGenericNewHiderString, new PrivateGenericNewHider<string> { Other = "o" });
		AssertReadsTheSame(context => context.PrivateGenericNewHiderString, "value: 'v'\nother: 'o'");
		AssertWritesTheSame(context => context.OtherPositionHiderStringInt32, new OtherPositionHider<string, int>());

		// The position of a nested class's type parameter counts those of the classes around it, so `TInner` is not the `T` of `Box<T>`, and the keys of the two members clash.
		AssertWritesTheSame(context => context.NestedHiderString, new Outer<int>.NestedHider<string>());
		Assert.Contains("both have the key 'value'", Outcome(() => SomlSerializer.Serialize(new Outer<int>.NestedHider<string>())), StringComparison.Ordinal);
		Assert.Equal("value: 'a'\n", SomlSerializer.Serialize(new GenericNewHider<string> { Value = "a" }));
	}

	[Fact]
	public void ReadsNullForAConstrainedTypeParameterLikeTheReflectionPath()
	{
		// `notnull` makes reflection refuse null, and `class?` lets it allow null.
		AssertReadsTheSame(context => context.NotNullBoxString, "value: null");
		AssertReadsTheSame(context => context.NullableClassBoxString, "value: null");
		AssertReadsTheSame(context => context.DisallowNullBoxString, "value: null");
		Assert.StartsWith("error: Expected a string for string, but found null", Outcome(() => SomlSerializer.Serialize(SomlSerializer.Deserialize<DisallowNullBox<string>>("value: null"))), StringComparison.Ordinal);
		Assert.StartsWith("error: Expected a string for string, but found null", Outcome(() => SomlSerializer.Serialize(SomlSerializer.Deserialize<NotNullBox<string>>("value: null"))), StringComparison.Ordinal);
		Assert.Equal("value: null\n", SomlSerializer.Serialize(SomlSerializer.Deserialize<NullableClassBox<string>>("value: null")));
	}

	[Fact]
	public void ReportsAnInvalidDocumentBeforeAnInvalidTypeLikeTheReflectionPath()
	{
		// The members of FieldHider have one key, which both paths find only when they first use the type, so an invalid document is reported first.
		AssertReadsTheSame(context => context.FieldHider, "");
		AssertReadsTheSame(context => context.FieldHider, "f: 1 2");
		Assert.StartsWith("error: A document must contain an object or an array", Outcome(() => SomlSerializer.Serialize(SomlSerializer.Deserialize("", new ParityContext().FieldHider), new ParityContext().FieldHider)), StringComparison.Ordinal);
	}

	[Fact]
	public void RefusesTwoConstructorParametersWithOneKeyLikeTheReflectionPath()
	{
		Assert.Equal("error: Two parameters of the constructor of TwoParametersWithOneKey have the key 'a' [] ", Outcome(() => SomlSerializer.Serialize(SomlSerializer.Deserialize<TwoParametersWithOneKey>("a: 1"))));
		AssertReadsTheSame(context => context.TwoParametersWithOneKey, "a: 1");
	}

	[Fact]
	public void ReadsNullForAGenericMemberLikeTheReflectionPath()
	{
		AssertReadsTheSame(context => context.NonNullBoxed, "value: null");
		AssertReadsTheSame(context => context.NullableBoxed, "value: null");
		AssertReadsTheSame(context => context.BoxString, "value: null");
		AssertReadsTheSame(context => context.Chained, "value: null");
		AssertReadsTheSame(context => context.GenericBoxedString, "value: null");

		// Reflection resolves `T` through the base classes of the type that it reads, but not through that type's own type arguments.
		Assert.StartsWith("error: Expected a string for string, but found null", Outcome(() => SomlSerializer.Serialize(SomlSerializer.Deserialize<Chained>("value: null"))), StringComparison.Ordinal);
		Assert.Equal("value: null\n", SomlSerializer.Serialize(SomlSerializer.Deserialize<GenericBoxed<string>>("value: null")));
	}

	[Fact]
	public void SetsAMemberThatAConstructorParameterReadsLikeTheReflectionPath()
	{
		// The parameter has the member's key under another name, so the member is not set again after the constructor.
		Assert.Equal("port: 2\n", SomlSerializer.Serialize(SomlSerializer.Deserialize<NamedParameter>("port: 1")));
		AssertReadsTheSame(context => context.NamedParameter, "port: 1");
	}

	[Fact]
	public void WritesAnOverriddenPropertyWithTheAttributesOfItsBaseLikeTheReflectionPath()
	{
		AssertWritesTheSame(context => context.OverrideDerived, new OverrideDerived());
	}

	[Fact]
	public void ReadsAnOverriddenPropertyWithTheAttributesOfItsBaseLikeTheReflectionPath()
	{
		const string text = "secret: 1\nlabel: 'x'\ntitle: 'y'";
		var reflection = SomlSerializer.Deserialize<OverrideDerived>(text);
		var generated = SomlSerializer.Deserialize(text, ParityContext.Default.OverrideDerived);

		Assert.Equal((7, "x"), (reflection.Secret, reflection.Title));
		Assert.Equal((reflection.Secret, reflection.Title), (generated.Secret, generated.Title));
	}

	[Fact]
	public void ReadsInitOnlyMembersOfARecordLikeTheReflectionPath()
	{
		AssertReadsTheSame(context => context.InitRecord, "a: 1\nb: 2\nc: 'x'\nd: 'y'");
		AssertReadsTheSame(context => context.InitRecord, "a: 1\nc: 'x'");
		AssertReadsTheSame(context => context.InitRecord, "a: 1");
		AssertReadsTheSame(context => context.InitRecord, "c: 'x'");
		AssertReadsTheSame(context => context.InitRecord, "a: 1\nc: null");
		AssertReadsTheSame(context => context.InitRecord, "a: 1\nc: 'x'\ne: 1", Strict);
	}

	[Fact]
	public void ReadsAPositionalRecordWithAttributesLikeTheReflectionPath()
	{
		AssertReadsTheSame(context => context.PositionalNamed, "the-id: 1");
		AssertReadsTheSame(context => context.PositionalNamed, "id: 1");
		AssertReadsTheSame(context => context.PositionalNamed, "the-id: 1\nsecret: 'x'\nnote: 'n'\nlevel: 'low'");
		AssertReadsTheSame(context => context.PositionalNamed, "the-id: 1\nsecret: 'x'", Strict);
		AssertReadsTheSame(context => context.PositionalNamed, "the-id: 1\nnte: 'x'", Strict);
		AssertReadsTheSame(context => context.PositionalNamed, "the-id: 1\nlevel: 'medium'");
	}

	[Fact]
	public void ReadsNullItemsLikeTheReflectionPath()
	{
		AssertReadsTheSame(context => context.NullableItems, "numbers: [1, null]\nmaybe: [null, 'a']\nplain: [null]\nmap: {a: null}\narray: [null, 2]");
		AssertReadsTheSame(context => context.NullableItems, "numbers: [1.5]");
		AssertReadsTheSame(context => context.NullableItems, "array: [null, 'x']");
	}

	[Fact]
	public void RequiresABaseClassMemberLikeTheReflectionPath()
	{
		AssertReadsTheSame(context => context.RequiredInitChild, "extra: 1\nname: 'n'\nrank: 2");
		AssertReadsTheSame(context => context.RequiredInitChild, "extra: 1\nrank: 2");
		AssertReadsTheSame(context => context.RequiredInitChild, "name: 'n'");
	}

	[Fact]
	public void UsesConstructorDefaultsLikeTheReflectionPath()
	{
		AssertReadsTheSame(context => context.ConstructorDefaults, "{}");
		AssertReadsTheSame(context => context.ConstructorDefaults, "port: 1\nhost: 'h'\ntimeout: 1s\nlevel: 'low'\nratio: 2\nid: '7d444840-9dc0-11d1-b245-5ffdce74fad2'");
		AssertReadsTheSame(context => context.ConstructorDefaults, "timeout: null\nid: null");
		AssertReadsTheSame(context => context.ConstructorDefaults, "host: null");
		AssertReadsTheSame(context => context.ConstructorDefaults, "ratio: 9007199254740993");
	}

	[Fact]
	public void ReadsAStructWithFieldsLikeTheReflectionPath()
	{
		AssertReadsTheSame(context => context.FieldStruct, "x: 1\ny: 2\nname: 'n'");
		AssertReadsTheSame(context => context.FieldStruct, "y: 2");
		AssertReadsTheSame(context => context.FieldStruct, "x: 1\ny: 2\nname: null");
	}

	[Fact]
	public void ReadsNullabilityAttributesLikeTheReflectionPath()
	{
		AssertReadsTheSame(context => context.Annotated, "name: null");
		AssertReadsTheSame(context => context.Annotated, "other: null");
	}

	public static TheoryData<string> NumericDocuments =>
	[
		"byte: 256",
		"byte: -1",
		"int: 2147483648",
		"int: 1.0",
		"int: 1e3",
		"single: 16777217",
		"single: 16777216",
		"single: 1e39",
		"single: 1e-50",
		"double: 9007199254740993",
		"double: 9007199254740992",
		"decimal: 0.1",
		"decimal: 1e300",
		"half: 65520",
		"half: 65504",
		"half: 1e-10",
		"u-long: -1",
		"big: -1",
		"int: 'x'",
		"int: true",
		"int: null",
		"double: 'x'",
		"half: [1]",
	];

	[Theory]
	[MemberData(nameof(NumericDocuments))]
	public void ReadsNumbersLikeTheReflectionPath(string text)
	{
		AssertReadsTheSame(context => context.Numeric, text);
	}

	[Fact]
	public void WritesNumbersLikeTheReflectionPath()
	{
		AssertWritesTheSame(context => context.Numeric, new Numeric { ULong = ulong.MaxValue });
		AssertWritesTheSame(context => context.Numeric, new Numeric { Big = UInt128.MaxValue });
		AssertWritesTheSame(context => context.Numeric, new Numeric { Decimal = 9007199254740993m });
		AssertWritesTheSame(context => context.Numeric, new Numeric { Single = float.NaN });
		AssertWritesTheSame(context => context.Numeric, new Numeric { Half = Half.NaN });
	}

	public static TheoryData<string> ShapeDocuments =>
	[
		"inner: 1",
		"inner: []",
		"items: {}",
		"items: [1, 'a']",
		"map: []",
		"map: {a: 'x'}",
		"text: 1",
		"flag: 'true'",
		"when: '2026-01-01T00:00:00Z'",
		"when: 2026-01-01T00:00:00.000000001Z",
		"span: 1ns",
		"span: 5",
		"day: 2026-01-01T00:00:00Z",
		"day: '2026-13-01'",
		"level: 1",
		"level: 'Low'",
		"many: [{max-retry-count: 1}, {max-retry-count: 'x'}]",
		"many: [{}, 1]",
		"inner: {max-retry-count: 1, defaul-level: 'low'}",
	];

	[Theory]
	[MemberData(nameof(ShapeDocuments))]
	public void ReportsWrongShapesLikeTheReflectionPath(string text)
	{
		AssertReadsTheSame(context => context.Shapes, text);
		AssertReadsTheSame(context => context.Shapes, text, Strict);
	}

	public static TheoryData<string> NamingPolicies => ["camel", "none", "kebab-upper", "snake-upper"];

	private static SomlSerializerOptions Policy(string name) => new()
	{
		PropertyNamingPolicy = name switch
		{
			"camel" => JsonNamingPolicy.CamelCase,
			"kebab-upper" => JsonNamingPolicy.KebabCaseUpper,
			"snake-upper" => JsonNamingPolicy.SnakeCaseUpper,
			_ => null,
		},
		RejectUnknownKeys = true,
	};

	[Theory]
	[MemberData(nameof(NamingPolicies))]
	public void UsesANamingPolicyLikeTheReflectionPath(string name)
	{
		var options = Policy(name);
		var value = new Naming { HTTPServer = "h", MaxRetryCount = 3, DefaultLevel = Level.VeryHigh, Renamed = 4 };
		AssertWritesTheSame(context => context.Naming, value, options);
		var text = SomlSerializer.Serialize(value, options);
		AssertReadsTheSame(context => context.Naming, text, options);
		AssertReadsTheSame(context => context.Naming, text.Replace("Explicit", "explicit", StringComparison.Ordinal), options);
	}

	[Fact]
	public void WrapsAnExceptionFromADictionaryThatCannotBeEnumerated()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Serialize(new WithMap { Map = new ThrowingMap() }));

		Assert.Equal("map", error.Path);
		Assert.IsType<InvalidOperationException>(error.InnerException);
	}

	[Fact]
	public void WrapsAnExceptionFromADictionaryThatCannotBeEnumeratedInTheGeneratedPath()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Serialize(new WithMap { Map = new ThrowingMap() }, ParityContext.Default.WithMap));

		Assert.Equal("map", error.Path);
		Assert.IsType<InvalidOperationException>(error.InnerException);
	}

	[Fact]
	public void ReadsAndWritesOtherShapesLikeTheReflectionPath()
	{
		AssertWritesTheSame(context => context.OrderDerived, new OrderDerived());
		AssertReadsTheSame(context => context.OrderDerived, "base-field: 5\nderived-property: 6\ncomputed: 1", Strict);
		AssertReadsTheSame(context => context.PairInt32String, "first: 1\nsecond: null");
		AssertReadsTheSame(context => context.PairInt32String, "first: null\nsecond: 'a'");
		AssertReadsTheSame(context => context.PairInt32String, "second: 'a'");
		AssertReadsTheSame(context => context.RequiredField, "count: 1\nnote: 'n'");
		AssertReadsTheSame(context => context.RequiredField, "note: 'n'");
		AssertReadsTheSame(context => context.SetsSome, "name: 'a'");
		AssertReadsTheSame(context => context.SetsSome, "name: 'a'\nother: 'b'");
		AssertReadsTheSame(context => context.SetsSome, "other: 'b'");
		AssertReadsTheSame(context => context.WithAliases, "value: 'alias'\nvalues: ['first', 'alias', 'second']");
		AssertWritesTheSame(context => context.WithAliases, new WithAliases { Value = Aliased.Alias, Values = [Aliased.Second, (Aliased)7] });
	}

	[Fact]
	public void RefusesAnEnumWithTwoMembersOfOneNameWithASomlException()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Serialize(new WithClashingEnum()));
		Assert.StartsWith("Two members of Clashing are both written as 'foo-bar'", error.Message, StringComparison.Ordinal);
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<WithClashingEnum>("value: 'foo-bar'", new SomlSerializerOptions()));
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<Clashing, int>>("foo-bar: 1", new SomlSerializerOptions()));
	}

	[Fact]
	public void RefusesAnEnumWithTwoMembersOfOneNameWithASomlExceptionInTheGeneratedPath()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Serialize(new WithClashingEnum(), new ParityContext().WithClashingEnum));
		Assert.StartsWith("Two members of Clashing are both written as 'foo-bar'", error.Message, StringComparison.Ordinal);
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize("value: 'foo-bar'", new ParityContext().WithClashingEnum));
	}

	[Fact]
	public void RefusesAReferenceCycleWithAPath()
	{
		var node = new Linked { Name = "a" };
		node.Next = node;

		var error = Assert.Throws<SomlException>(() => SomlSerializer.Serialize(node));
		Assert.StartsWith("Cannot serialize a value nested more than 100 levels deep", error.Message, StringComparison.Ordinal);
		Assert.StartsWith("next.next.", error.Path, StringComparison.Ordinal);
		AssertWritesTheSame(context => context.Linked, node);
	}

	[Fact]
	public void RefusesToReadAValueNestedTooDeepWithAPath()
	{
		// A parsed document stops at 100 levels, but a value built in code does not, and one that holds itself would recurse until the stack overflows, which cannot be caught.
		var root = new SomlObject { ["name"] = "a" };
		var current = root;

		for (var level = 0; level < 150; level++)
		{
			var next = new SomlObject { ["name"] = "a" };
			current["next"] = next;
			current = next;
		}

		var reflection = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Linked>(root));
		var generated = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize(root, ParityContext.Default.Linked));
		Assert.StartsWith("Cannot deserialize a value nested more than 100 levels deep", reflection.Message, StringComparison.Ordinal);
		Assert.Equal(reflection.Message, generated.Message);
		Assert.StartsWith("next.next.next.", reflection.Path, StringComparison.Ordinal);
		Assert.Equal(reflection.Path, generated.Path);

		var array = new SomlArray();
		var innermost = array;

		for (var level = 0; level < 150; level++)
		{
			var next = new SomlArray();
			innermost.Add(next);
			innermost = next;
		}

		Assert.StartsWith("Cannot deserialize a value nested more than 100 levels deep", Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<object>(array)).Message, StringComparison.Ordinal);

		// The deepest document that parses, of 100 levels with a scalar in the last one, still reads.
		var deepest = SomlValue.Parse(string.Concat(Enumerable.Repeat("next: {", 99)) + "name: 'z'" + new string('}', 99));
		Assert.Equal(SomlSerializer.Serialize(SomlSerializer.Deserialize<Linked>(deepest)), SomlSerializer.Serialize(SomlSerializer.Deserialize(deepest, ParityContext.Default.Linked), ParityContext.Default.Linked));
		Assert.IsType<Dictionary<string, object?>>(SomlSerializer.Deserialize<object>(deepest));

		var cycle = new SomlObject { ["name"] = "a" };
		cycle["next"] = cycle;
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Linked>(cycle));
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize(cycle, ParityContext.Default.Linked));
	}

	[Fact]
	public void WrapsASomlExceptionFromASetterLikeTheReflectionPath()
	{
		AssertReadsTheSame(context => context.Validating, "port: 0");
	}

	[Fact]
	public void WrapsASomlExceptionFromAConstructorLikeTheReflectionPath()
	{
		AssertReadsTheSame(context => context.ValidatingRecord, "host: ''");
	}

	[Fact]
	public void WrapsASomlExceptionFromAGetterLikeTheReflectionPath()
	{
		AssertWritesTheSame(context => context.Validating, new Validating { Port = 6 });
	}

	[Fact]
	public void WritesAFlagsMemberWithTheSignBit()
	{
		var value = new WithSignedFlags { Flags = SignedFlags.High | SignedFlags.Low };
		var text = SomlSerializer.Serialize(value);

		Assert.Equal("flags: [\n\t'low'\n\t'high'\n]\n", text);
		Assert.Equal(value.Flags, SomlSerializer.Deserialize<WithSignedFlags>(text).Flags);
	}

	[Fact]
	public void WritesAFlagsMemberWithTheSignBitInTheGeneratedPath()
	{
		var typeInfo = ParityContext.Default.WithSignedFlags;
		var value = new WithSignedFlags { Flags = SignedFlags.High };
		var text = SomlSerializer.Serialize(value, typeInfo);

		Assert.Equal("flags: [\n\t'high'\n]\n", text);
		Assert.Equal(value.Flags, SomlSerializer.Deserialize(text, typeInfo).Flags);
	}

	[Theory]
	[InlineData("long")]
	[InlineData("short")]
	[InlineData("sbyte")]
	public void WritesAFlagsEnumWithTheSignBitOfEverySignedType(string underlyingType)
	{
		var text = underlyingType switch
		{
			"long" => SomlSerializer.Serialize(new Dictionary<string, LongFlags> { ["a"] = LongFlags.High }),
			"short" => SomlSerializer.Serialize(new Dictionary<string, ShortFlags> { ["a"] = ShortFlags.High }),
			_ => SomlSerializer.Serialize(new Dictionary<string, SByteFlags> { ["a"] = SByteFlags.High }),
		};

		Assert.Equal("a: [\n\t'high'\n]\n", text);
	}

	[Fact]
	public void ReadsAFlagsMemberWithTheSignBit()
	{
		Assert.Equal(SignedFlags.High | SignedFlags.Low, SomlSerializer.Deserialize<WithSignedFlags>("flags: ['high', 'low']").Flags);
		Assert.Equal(SignedFlags.High, SomlSerializer.Deserialize("flags: ['high']", ParityContext.Default.WithSignedFlags).Flags);
	}

	[Fact]
	public void ReadsAndWritesDictionaryKeysOfEveryKind()
	{
		var guid = Guid.Parse("7d444840-9dc0-11d1-b245-5ffdce74fad2");
		AssertRoundTrips(new Dictionary<Guid, int> { [guid] = 1 }, "7d444840-9dc0-11d1-b245-5ffdce74fad2: 1\n");
		AssertRoundTrips(new Dictionary<Level, int> { [Level.VeryHigh] = 1 }, "very-high: 1\n");
		AssertRoundTrips(new Dictionary<DateOnly, int> { [new DateOnly(2026, 1, 2)] = 1 }, "2026-01-02: 1\n");
		AssertRoundTrips(new Dictionary<TimeOnly, int> { [new TimeOnly(12, 30)] = 1 }, "'12:30:00': 1\n");
		AssertRoundTrips(new Dictionary<SomlInstant, int> { [SomlInstant.Parse("2026-01-02T00:00:00.5Z")] = 1 }, "'2026-01-02T00:00:00.5Z': 1\n");
		AssertRoundTrips(new Dictionary<DateTime, int> { [new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)] = 1 }, "'2026-01-02T00:00:00Z': 1\n");
		AssertRoundTrips(new Dictionary<TimeSpan, int> { [TimeSpan.FromSeconds(90)] = 1 }, "1m30s: 1\n");
		AssertRoundTrips(new Dictionary<int, int> { [-7] = 1 }, "-7: 1\n");
		AssertRoundTrips(new Dictionary<double, int> { [0.5] = 1 }, "'0.5': 1\n");
		AssertRoundTrips(new Dictionary<char, int> { ['x'] = 1 }, "x: 1\n");

		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<string, Dictionary<Guid, int>>>("a: {\n\t'7D444840-9DC0-11D1-B245-5FFDCE74FAD2': 1\n}"));
		Assert.EndsWith("(at a.7D444840-9DC0-11D1-B245-5FFDCE74FAD2, line 2, column 2)", error.Message, StringComparison.Ordinal);

		var levelError = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<Level, int>>("VeryHigh: 1"));
		Assert.Equal("VeryHigh", levelError.Path);
	}

	private static void AssertRoundTrips<TKey>(Dictionary<TKey, int> value, string expected) where TKey : notnull
	{
		var text = SomlSerializer.Serialize(value);
		Assert.Equal(expected, text);
		Assert.Equal(value, SomlSerializer.Deserialize<Dictionary<TKey, int>>(text));
	}

	[Fact]
	public void ReadsAndWritesAFlagsEnumKeyAsOneMember()
	{
		Assert.Equal("read: 'r'\n", SomlSerializer.Serialize(new Dictionary<SerializerTests.Permissions, string> { [SerializerTests.Permissions.Read] = "r" }));
		Assert.Equal("r", SomlSerializer.Deserialize<Dictionary<SerializerTests.Permissions, string>>("read: 'r'")[SerializerTests.Permissions.Read]);

		// A key is one member, so a value of two flags cannot be a key.
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Serialize(new Dictionary<SerializerTests.Permissions, string> { [SerializerTests.Permissions.Read | SerializerTests.Permissions.Write] = "rw" }));
		Assert.StartsWith("The value 3 is not a member of Permissions", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void SaysTheRealRangeOfUInt128()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<string, UInt128>>("a: -1"));

		Assert.StartsWith("The int -1 is outside the range of UInt128 (0 to 340282366920938463463374607431768211455)", error.Message, StringComparison.Ordinal);
	}

	public sealed class Everything
	{
		public int[] Array { get; set; } = [];

		public List<string> List { get; set; } = [];

		public IReadOnlyList<int> ReadOnlyList { get; set; } = [];

		public ImmutableArray<int> Immutable { get; set; } = [];

		public HashSet<string> Set { get; set; } = [];

		public Dictionary<string, int> Dictionary { get; set; } = [];

		public IReadOnlyDictionary<string, List<int>> ReadOnlyDictionary { get; set; } = new Dictionary<string, List<int>>();

		public FrozenSet<int> Frozen { get; set; } = FrozenSet<int>.Empty;

		public FrozenDictionary<string, int> FrozenDictionary { get; set; } = FrozenDictionary<string, int>.Empty;

		public int? Maybe { get; set; }

		public object? Any { get; set; }

		public DateTimeOffset When { get; set; }

		public DateTime Utc { get; set; } = DateTime.UnixEpoch;

		public TimeSpan Span { get; set; }

		public SerializerTests.Permissions Permissions { get; set; }
	}

	[Fact]
	public void RoundTripsEveryKindOfMember()
	{
		var value = new Everything
		{
			Array = [1, 2],
			List = ["a"],
			ReadOnlyList = [3],
			Immutable = [4, 5],
			Set = ["b", "c"],
			Dictionary = new() { ["d"] = 6 },
			ReadOnlyDictionary = new Dictionary<string, List<int>> { ["e"] = [7] },
			Frozen = new[] { 8 }.ToFrozenSet(),
			FrozenDictionary = new Dictionary<string, int> { ["f"] = 9 }.ToFrozenDictionary(),
			Maybe = 10,
			Any = new Dictionary<string, object?> { ["g"] = new List<object?> { 1L, "h", null, 1.5, true, SomlDuration.Parse("1s") } },
			When = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)),
			Utc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
			Span = TimeSpan.FromMilliseconds(1500),
			Permissions = SerializerTests.Permissions.Read | SerializerTests.Permissions.Execute,
		};

		var text = SomlSerializer.Serialize(value);
		var read = SomlSerializer.Deserialize<Everything>(text);

		Assert.Equal(text, SomlSerializer.Serialize(read));
		Assert.Equal(value.When, read.When);
		Assert.Equal(DateTimeKind.Utc, read.Utc.Kind);
		Assert.Equal(value.Immutable, read.Immutable);
	}

	[Fact]
	public void ThrowsOnlySomlExceptionForUnsupportedTypes()
	{
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<int[,]>("[]"));
		Assert.Throws<SomlException>(() => SomlSerializer.Serialize(new Dictionary<string, Action> { ["a"] = () => { } }));
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<string, Action>>("a: {}"));
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<string, Type>>("a: {}"));
		Assert.Throws<SomlException>(() => SomlSerializer.Serialize(new Dictionary<string, Type> { ["a"] = typeof(int) }));
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<string, Stream>>("a: {}"));
	}

	[Fact]
	public void MakesConvertersFromManyThreadsAtOnce()
	{
		for (var round = 0; round < 20; round++)
		{
			var options = new SomlSerializerOptions();
			var results = new string[16];

			Parallel.For(0, results.Length, index =>
			{
				results[index] = SomlSerializer.Serialize(SomlSerializer.Deserialize<Everything>("array: [1]\nlist: ['a']\ndictionary: {a: 1}\npermissions: ['read']\nany: {a: [1]}", options), options);
			});

			Assert.All(results, result => Assert.Equal(results[0], result));
		}
	}
}
