using Soml;
using Soml.Serialization;

public sealed class GlobalFactoryCollection<T> : IEnumerable<T>
{
	private readonly List<T> _items;

	internal GlobalFactoryCollection(IEnumerable<T> items)
	{
		_items = [.. items];
	}

	public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

	System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class GlobalFactoryCollection
{
	public static GlobalFactoryCollection<T> CreateRange<T>(IEnumerable<T> items) => new(items);
}

public sealed class OverloadedFactoryCollection<T> : List<T>
{
	internal OverloadedFactoryCollection(IEnumerable<T> items) : base(items)
	{
	}
}

public static class OverloadedFactoryCollection
{
	public static OverloadedFactoryCollection<T> CreateRange<T>(IEnumerable<string> items) => throw new InvalidOperationException("The incompatible overload must not be called");

	public static OverloadedFactoryCollection<T> CreateRange<T>(IEnumerable<T> items) => new(items);
}

public sealed class CovariantFactoryCollection<T> : List<T>
{
	internal CovariantFactoryCollection(IEnumerable<T> items, string selected) : base(items)
	{
		Selected = selected;
	}

	public string Selected { get; }
}

public static class CovariantFactoryCollection
{
	public static CovariantFactoryCollection<T> CreateRange<T>(IEnumerable<object> items) => new(items.Cast<T>(), "object");

	public static CovariantFactoryCollection<T> CreateRange<T>(IEnumerable<T> items) => new(items, "generic");
}

public sealed class CompatibleFactoryCollection<T> : List<T>
{
	internal CompatibleFactoryCollection(IEnumerable<T> items) : base(items)
	{
	}
}

public static class CompatibleFactoryCollection
{
	public static CompatibleFactoryCollection<T> CreateRange<T>(ref IEnumerable<object> items) => throw new InvalidOperationException("The by-reference overload must not be called");

	public static int CreateRange<T>(IEnumerable<object> items) => throw new InvalidOperationException("The incompatible result overload must not be called");

	public static CompatibleFactoryCollection<T> CreateRange<T>(IEnumerable<T> items) => new(items);
}

namespace Soml.Tests
{
	[SomlSerializable(typeof(GlobalFactoryCollection<int>), TypeInfoPropertyName = "Items")]
	[SomlSerializable(typeof(OverloadedFactoryCollection<int>), TypeInfoPropertyName = "OverloadedItems")]
	[SomlSerializable(typeof(CovariantFactoryCollection<string>), TypeInfoPropertyName = "CovariantItems")]
	[SomlSerializable(typeof(CompatibleFactoryCollection<string>), TypeInfoPropertyName = "CompatibleItems")]
	internal partial class GlobalFactoryContext : SomlSerializerContext;

	public sealed class GlobalNamespaceFactoryTests
	{
		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void UsesACollectionFactoryInTheGlobalNamespace(bool generated)
		{
			var value = generated ? SomlSerializer.Deserialize("[1, 2, 3]", GlobalFactoryContext.Default.Items) : SomlSerializer.Deserialize<GlobalFactoryCollection<int>>("[1, 2, 3]");
			Assert.Equal(new[] { 1, 2, 3 }, value);
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void SelectsAFactoryOverloadThatAcceptsTheItemType(bool generated)
		{
			var value = generated ? SomlSerializer.Deserialize("[1, 2, 3]", GlobalFactoryContext.Default.OverloadedItems) : SomlSerializer.Deserialize<OverloadedFactoryCollection<int>>("[1, 2, 3]");
			Assert.Equal(new[] { 1, 2, 3 }, value);
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void InvokesTheSelectedFactoryOverload(bool generated)
		{
			var value = generated ? SomlSerializer.Deserialize("['a', 'b']", GlobalFactoryContext.Default.CovariantItems) : SomlSerializer.Deserialize<CovariantFactoryCollection<string>>("['a', 'b']");
			Assert.Equal(new[] { "a", "b" }, value);
			Assert.Equal("object", value.Selected);
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void SkipsFactoriesWithByReferenceParametersOrIncompatibleResults(bool generated)
		{
			var value = generated ? SomlSerializer.Deserialize("['a', 'b']", GlobalFactoryContext.Default.CompatibleItems) : SomlSerializer.Deserialize<CompatibleFactoryCollection<string>>("['a', 'b']");
			Assert.Equal(new[] { "a", "b" }, value);
		}
	}
}
