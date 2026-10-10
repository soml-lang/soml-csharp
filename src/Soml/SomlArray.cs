using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Soml;

/// <summary>
/// A SOML array: an ordered list of values, which may be of different types.
/// </summary>
/// <remarks>
/// It supports collection expressions and collection initializers, as in <c>SomlArray ports = [80, 443];</c>.
/// </remarks>
[DebuggerDisplay("Count = {Count}")]
[DebuggerTypeProxy(typeof(SomlArrayDebugView))]
[CollectionBuilder(typeof(SomlArray), nameof(Create))]
public sealed class SomlArray : IList<SomlValue>, IReadOnlyList<SomlValue>
{
	private readonly List<SomlValue> _items;

	/// <summary>
	/// Creates an empty array.
	/// </summary>
	public SomlArray()
	{
		_items = [];
	}

	/// <summary>
	/// Creates an empty array with room for a number of items.
	/// </summary>
	public SomlArray(int capacity)
	{
		_items = new List<SomlValue>(capacity);
	}

	/// <summary>
	/// Creates an array of the items.
	/// </summary>
	public SomlArray(IEnumerable<SomlValue> items)
	{
		ArgumentNullException.ThrowIfNull(items);

		if (items is IReadOnlyCollection<SomlValue> counted && items is not ICollection<SomlValue>)
		{
			_items = new List<SomlValue>(counted.Count);
			_items.AddRange(items);
			return;
		}

		// Note: Not using Enumerable.ToList because it improved LINQ sources but slowed the measured yielding-source constructor on .NET 10 ARM64.
		_items = items is SomlArray source ? new List<SomlValue>(source._items) : new List<SomlValue>(items);
	}

	private SomlArray(ReadOnlySpan<SomlValue> items)
	{
		_items = [.. items];
	}

	/// <summary>
	/// Creates an array by copying the items. Collection expressions use this method to allocate room for every item at once.
	/// </summary>
	public static SomlArray Create(ReadOnlySpan<SomlValue> items) => new(items);

	/// <summary>
	/// The number of items.
	/// </summary>
	public int Count => _items.Count;

	bool ICollection<SomlValue>.IsReadOnly => false;

	/// <summary>
	/// Gets or sets the item at an index.
	/// </summary>
	public SomlValue this[int index]
	{
		get => _items[index];
		set => _items[index] = value;
	}

	/// <summary>
	/// Adds an item at the end.
	/// </summary>
	public void Add(SomlValue item) => _items.Add(item);

	/// <summary>
	/// Inserts an item at an index.
	/// </summary>
	public void Insert(int index, SomlValue item) => _items.Insert(index, item);

	/// <summary>
	/// Removes the first item that is equal to <paramref name="item"/>, and returns whether there was one.
	/// </summary>
	public bool Remove(SomlValue item) => _items.Remove(item);

	/// <summary>
	/// Removes the item at an index.
	/// </summary>
	public void RemoveAt(int index) => _items.RemoveAt(index);

	/// <summary>
	/// Removes every item.
	/// </summary>
	public void Clear() => _items.Clear();

	// Note: Not using span Contains/IndexOf because they helped larger arrays but slowed the measured empty/single-item search workload on .NET 10 ARM64.
	/// <summary>
	/// Whether the array has an item that is equal to <paramref name="item"/>.
	/// </summary>
	public bool Contains(SomlValue item) => _items.Contains(item);

	/// <summary>
	/// The index of the first item that is equal to <paramref name="item"/>, or -1.
	/// </summary>
	public int IndexOf(SomlValue item) => _items.IndexOf(item);

	/// <inheritdoc/>
	public void CopyTo(SomlValue[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

	/// <summary>
	/// Returns an enumerator over the items.
	/// </summary>
	public List<SomlValue>.Enumerator GetEnumerator() => _items.GetEnumerator();

	IEnumerator<SomlValue> IEnumerable<SomlValue>.GetEnumerator() => GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

	/// <summary>
	/// The array as SOML text, as <see cref="SomlValue.ToString"/> writes it.
	/// </summary>
	public override string ToString() => ((SomlValue)this).ToString();

	/// <inheritdoc cref="SomlValue.ToDocumentString(bool)"/>
	public string ToDocumentString(bool canonical = false) => ((SomlValue)this).ToDocumentString(canonical);

	internal ReadOnlySpan<SomlValue> AsSpan() => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_items);

	internal bool ContentEquals(SomlArray other)
	{
		if (ReferenceEquals(this, other))
		{
			return true;
		}

		return AsSpan().SequenceEqual(other.AsSpan());
	}

	internal int ContentHashCode()
	{
		var hash = new HashCode();

		foreach (var item in AsSpan())
		{
			hash.Add(item);
		}

		return hash.ToHashCode();
	}

	private sealed class SomlArrayDebugView(SomlArray array)
	{
		[DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
		public SomlValue[] Items => [.. array._items];
	}
}
