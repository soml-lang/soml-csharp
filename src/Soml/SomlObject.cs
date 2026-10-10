using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Soml;

/// <summary>
/// A SOML object: a collection of members, each a string key and a value.
/// </summary>
/// <remarks>
/// <para>The members keep the order they were added in, which is the order of the document for a parsed object, so writing an object keeps its order. The order is not part of the value, so two objects with the same members in another order are equal as <see cref="SomlValue"/>s, and canonical form sorts them.</para>
/// <para>Keys are compared ordinally, with no case folding or Unicode normalization, as SOML requires.</para>
/// <para>It supports collection initializers, as in <c>new SomlObject { ["port"] = 8080 }</c>.</para>
/// </remarks>
[DebuggerDisplay("Count = {Count}")]
[DebuggerTypeProxy(typeof(SomlObjectDebugView))]
[CollectionBuilder(typeof(SomlObject), nameof(Create))]
public sealed class SomlObject : IDictionary<string, SomlValue>, IReadOnlyDictionary<string, SomlValue>
{
	/*
	Up to this many members, a key is found by comparing it with each key, which is faster than hashing and needs no index. Most objects in a config file are this small.
	*/
	internal const int MaxLinearCount = 8;

	// Note: Not using OrderedDictionary because it regressed small objects and enumeration in .NET 10 ARM64 probes and lacks the contiguous member span used here.
	private KeyValuePair<string, SomlValue>[] _members;
	private int _count;

	/*
	The index of each member by its key, made by the first search once the object has more than `MaxLinearCount` members, so that an object that is read but never searched, or searched only when it is whole, does not grow one member by member.
	*/
	private Dictionary<string, int>? _index;

	private int _version;

	/// <summary>
	/// Creates an empty object.
	/// </summary>
	public SomlObject()
	{
		_members = [];
	}

	/// <summary>
	/// Creates an empty object with room for a number of members.
	/// </summary>
	public SomlObject(int capacity)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(capacity);
		_members = capacity == 0 ? [] : new KeyValuePair<string, SomlValue>[capacity];
	}

	/// <summary>
	/// Creates an object of the members, in their order.
	/// </summary>
	/// <exception cref="ArgumentException">When two members have the same key.</exception>
	public SomlObject(IEnumerable<KeyValuePair<string, SomlValue>> members) : this()
	{
		ArgumentNullException.ThrowIfNull(members);

		if (members is SomlObject source)
		{
			_members = source.AsSpan().ToArray();
			_count = source.Count;
			return;
		}

		var count = members is IReadOnlyCollection<KeyValuePair<string, SomlValue>> counted ? counted.Count : members.TryGetNonEnumeratedCount(out var knownCount) ? knownCount : 0;

		if (count > 0)
		{
			_members = new KeyValuePair<string, SomlValue>[count];
		}

		foreach (var (key, value) in members)
		{
			Add(key, value);
		}
	}

	/// <summary>
	/// Creates an object by copying the members in their order. Collection expressions use this method to allocate room for every member at once.
	/// </summary>
	/// <exception cref="ArgumentException">When two members have the same key.</exception>
	public static SomlObject Create(ReadOnlySpan<KeyValuePair<string, SomlValue>> members)
	{
		var result = new SomlObject(members.Length);

		foreach (var (key, value) in members)
		{
			result.Add(key, value);
		}

		return result;
	}

	/// <summary>
	/// The number of members.
	/// </summary>
	public int Count => _count;

	/// <summary>
	/// The keys, in order.
	/// </summary>
	public KeyCollection Keys => new(this);

	/// <summary>
	/// The values, in order.
	/// </summary>
	public ValueCollection Values => new(this);

	ICollection<string> IDictionary<string, SomlValue>.Keys => Keys;

	ICollection<SomlValue> IDictionary<string, SomlValue>.Values => Values;

	IEnumerable<string> IReadOnlyDictionary<string, SomlValue>.Keys => Keys;

	IEnumerable<SomlValue> IReadOnlyDictionary<string, SomlValue>.Values => Values;

	bool ICollection<KeyValuePair<string, SomlValue>>.IsReadOnly => false;

	/// <summary>
	/// Gets or sets the value of a member. Setting a new key adds the member at the end.
	/// </summary>
	/// <exception cref="KeyNotFoundException">When getting a key that the object does not have.</exception>
	public SomlValue this[string key]
	{
		get
		{
			var index = IndexOf(key);
			return index >= 0 ? _members[index].Value : throw new KeyNotFoundException($"The object has no member {Message.DescribeText(Message.DescribeKey(key))}");
		}
		set
		{
			var index = IndexOf(key);

			// Replacing a value keeps the members and their order, so an enumeration goes on, as with `Dictionary<TKey, TValue>`.
			if (index >= 0)
			{
				_members[index] = new KeyValuePair<string, SomlValue>(key, value);
			}
			else
			{
				Append(key, value);
			}
		}
	}

	/// <summary>
	/// Adds a member at the end.
	/// </summary>
	/// <exception cref="ArgumentException">When the object already has a member with the key.</exception>
	public void Add(string key, SomlValue value)
	{
		if (!TryAdd(key, value))
		{
			throw DuplicateKey(key);
		}
	}

	private static ArgumentException DuplicateKey(string key) => new($"The object already has a member {Message.DescribeText(Message.DescribeKey(key))}", nameof(key));

	/// <summary>
	/// Adds a member at the end, unless the object already has a member with the key. Returns whether it was added.
	/// </summary>
	public bool TryAdd(string key, SomlValue value)
	{
		if (IndexOf(key) >= 0)
		{
			return false;
		}

		Append(key, value);
		return true;
	}

	/*
	Adds a member whose key the object does not have, as the parser, which checks for duplicates itself, adds them.
	*/
	internal void Append(string key, SomlValue value)
	{
		if (_count == _members.Length)
		{
			Array.Resize(ref _members, _count == 0 ? 4 : _count * 2);
		}

		_members[_count] = new KeyValuePair<string, SomlValue>(key, value);
		_index?.Add(key, _count);
		_count++;
		_version++;
	}

	private Dictionary<string, int> BuildIndex()
	{
		// Note: Not using Enumerable.Index().ToDictionary because it loses the known count and adds iterator and dictionary-growth allocations.
		var result = new Dictionary<string, int>(_count, StringComparer.Ordinal);

		for (var index = 0; index < _count; index++)
		{
			result.Add(_members[index].Key, index);
		}

		// The index is set only when it is whole, so that a search on another thread, which reading an object allows, finds a whole index or none, and then builds its own.
		Volatile.Write(ref _index, result);
		return result;
	}

	/// <summary>
	/// Inserts a member at an index.
	/// </summary>
	/// <exception cref="ArgumentException">When the object already has a member with the key.</exception>
	/// <exception cref="ArgumentOutOfRangeException">When the index is negative or past the end.</exception>
	public void Insert(int index, string key, SomlValue value)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(index);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(index, _count);

		if (IndexOf(key) >= 0)
		{
			throw DuplicateKey(key);
		}

		// Appending first grows the array and updates the count, the version, and the key index. Only the shifted members then need their indexes updated.
		Append(key, value);

		if (index == _count - 1)
		{
			return;
		}

		Array.Copy(_members, index, _members, index + 1, _count - 1 - index);
		_members[index] = new KeyValuePair<string, SomlValue>(key, value);

		UpdateIndexes(index);
	}

	/// <summary>
	/// The member at an index.
	/// </summary>
	/// <exception cref="ArgumentOutOfRangeException">When the index is negative or not less than <see cref="Count"/>.</exception>
	public KeyValuePair<string, SomlValue> GetAt(int index)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(index);
		ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
		return _members[index];
	}

	/// <summary>
	/// The index of the member with the key, or -1.
	/// </summary>
	public int IndexOf(string key)
	{
		ArgumentNullException.ThrowIfNull(key);
		// An acquire read pairs with `Volatile.Write` to publish a whole index to concurrent readers.
		var keyIndex = Volatile.Read(ref _index) ?? (_count > MaxLinearCount ? BuildIndex() : null);

		if (keyIndex is not null)
		{
			return keyIndex.TryGetValue(key, out var found) ? found : -1;
		}

		for (var index = 0; index < _count; index++)
		{
			if (string.Equals(_members[index].Key, key, StringComparison.Ordinal))
			{
				return index;
			}
		}

		return -1;
	}

	/// <summary>
	/// Whether the object has a member with the key.
	/// </summary>
	public bool ContainsKey(string key) => IndexOf(key) >= 0;

	/// <summary>
	/// Gets the value of the member with the key, when there is one.
	/// </summary>
	public bool TryGetValue(string key, out SomlValue value)
	{
		var index = IndexOf(key);
		value = index >= 0 ? _members[index].Value : default;
		return index >= 0;
	}

	/// <summary>
	/// Removes the member with the key, and returns whether there was one. The other members keep their order.
	/// </summary>
	public bool Remove(string key)
	{
		// Note: Not using Dictionary.Remove(key, out index) because avoiding RemoveAt's lookup would require a separate mutation path.
		var index = IndexOf(key);

		if (index < 0)
		{
			return false;
		}

		RemoveAt(index);
		return true;
	}

	/// <summary>
	/// Removes the member at an index. The other members keep their order.
	/// </summary>
	/// <exception cref="ArgumentOutOfRangeException">When the index is negative or not less than <see cref="Count"/>.</exception>
	public void RemoveAt(int index)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(index);
		ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
		_index?.Remove(_members[index].Key);
		_count--;
		Array.Copy(_members, index + 1, _members, index, _count - index);
		_members[_count] = default;
		_version++;

		// The members after `index` moved down, so their indexes are updated, or dropped when the object is small enough to search one by one.
		if (_count <= MaxLinearCount)
		{
			_index = null;
		}
		else
		{
			UpdateIndexes(index);
		}
	}

	/*
	Updates the existing key index for members shifted by an insertion or removal.

	Note: Not using CollectionsMarshal.GetValueRefOrNullRef because it did not consistently beat the setter across .NET 10 ARM64 JIT modes.
	*/
	private void UpdateIndexes(int start)
	{
		if (_index is null)
		{
			return;
		}

		for (var index = start; index < _count; index++)
		{
			_index[_members[index].Key] = index;
		}
	}

	/// <summary>
	/// Removes every member.
	/// </summary>
	public void Clear()
	{
		// Note: Not using Span.Clear because it uses the same clearing kernel and public Clear comparisons did not establish consistent non-regression on .NET 10 ARM64.
		Array.Clear(_members, 0, _count);
		_count = 0;
		_index = null;
		_version++;
	}

	/// <summary>
	/// Returns an enumerator over the members, in order.
	/// </summary>
	public Enumerator GetEnumerator() => new(this);

	IEnumerator<KeyValuePair<string, SomlValue>> IEnumerable<KeyValuePair<string, SomlValue>>.GetEnumerator() => GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

	void ICollection<KeyValuePair<string, SomlValue>>.Add(KeyValuePair<string, SomlValue> item) => Add(item.Key, item.Value);

	bool ICollection<KeyValuePair<string, SomlValue>>.Contains(KeyValuePair<string, SomlValue> item) => TryGetValue(item.Key, out var value) && value.Equals(item.Value);

	void ICollection<KeyValuePair<string, SomlValue>>.CopyTo(KeyValuePair<string, SomlValue>[] array, int arrayIndex)
	{
		// Note: Not using Array.Copy for CopyTo because public probes on .NET 10 ARM64 had mixed size-dependent timings and slower small copies, without consistent improvement.
		CheckCopyTarget(array, arrayIndex, _count);
		AsSpan().CopyTo(array.AsSpan(arrayIndex));
	}

	bool ICollection<KeyValuePair<string, SomlValue>>.Remove(KeyValuePair<string, SomlValue> item)
	{
		var index = IndexOf(item.Key);

		if (index < 0 || !_members[index].Value.Equals(item.Value))
		{
			return false;
		}

		RemoveAt(index);
		return true;
	}

	bool IReadOnlyDictionary<string, SomlValue>.TryGetValue(string key, [MaybeNullWhen(false)] out SomlValue value) => TryGetValue(key, out value);

	bool IDictionary<string, SomlValue>.TryGetValue(string key, [MaybeNullWhen(false)] out SomlValue value) => TryGetValue(key, out value);

	/// <summary>
	/// The object as SOML text, as <see cref="SomlValue.ToString"/> writes it.
	/// </summary>
	public override string ToString() => ((SomlValue)this).ToString();

	/// <inheritdoc cref="SomlValue.ToDocumentString(bool)"/>
	public string ToDocumentString(bool canonical = false) => ((SomlValue)this).ToDocumentString(canonical);

	internal ReadOnlySpan<KeyValuePair<string, SomlValue>> AsSpan() => _members.AsSpan(0, _count);

	internal bool ContentEquals(SomlObject other)
	{
		if (ReferenceEquals(this, other))
		{
			return true;
		}

		if (_count != other._count)
		{
			return false;
		}

		for (var index = 0; index < _count; index++)
		{
			var (key, value) = _members[index];
			// Objects read or built in the same order need no key index for equality.
			var otherValue = other._members[index].Value;

			if ((key != other._members[index].Key && !other.TryGetValue(key, out otherValue)) || !value.Equals(otherValue))
			{
				return false;
			}
		}

		return true;
	}

	// The members are combined in a way that does not depend on their order, as equality does not.
	internal int ContentHashCode()
	{
		// Note: Not using Enumerable.Sum because it checks overflow, while hash aggregation must wrap rather than throw when the signed sum exceeds its range.
		var hash = 0;

		foreach (var (key, value) in AsSpan())
		{
			hash += HashCode.Combine(key, value);
		}

		return hash;
	}

	/// <summary>
	/// An enumerator over the members of an object, in order.
	/// </summary>
	public struct Enumerator : IEnumerator<KeyValuePair<string, SomlValue>>
	{
		private readonly SomlObject _owner;
		private readonly int _version;
		private int _index;

		internal Enumerator(SomlObject owner)
		{
			_owner = owner;
			_version = owner._version;
			_index = -1;
		}

		/// <inheritdoc/>
		public readonly KeyValuePair<string, SomlValue> Current => _owner._members[_index];

		readonly object IEnumerator.Current => _index >= 0 && _index < _owner._count ? Current : throw new InvalidOperationException("The enumerator is not positioned on a member");

		/// <inheritdoc/>
		public bool MoveNext()
		{
			CheckVersion();
			return ++_index < _owner._count;
		}

		/// <inheritdoc/>
		public void Reset()
		{
			CheckVersion();
			_index = -1;
		}

		private readonly void CheckVersion()
		{
			if (_version != _owner._version)
			{
				throw new InvalidOperationException("The object was changed while it was enumerated");
			}
		}

		/// <inheritdoc/>
		public readonly void Dispose()
		{
		}
	}

	/// <summary>
	/// The keys of an object, in order. It is a view: it changes with the object.
	/// </summary>
	public readonly struct KeyCollection : IList<string>, IReadOnlyList<string>
	{
		private readonly SomlObject _owner;

		internal KeyCollection(SomlObject owner)
		{
			_owner = owner;
		}

		/// <inheritdoc/>
		public int Count => _owner._count;

		/// <inheritdoc/>
		public bool IsReadOnly => true;

		/// <inheritdoc/>
		public string this[int index]
		{
			get => _owner.GetAt(index).Key;
			set => throw new NotSupportedException("The keys of an object cannot be set one by one");
		}

		/// <inheritdoc/>
		public bool Contains(string item) => _owner.ContainsKey(item);

		/// <inheritdoc/>
		public int IndexOf(string item) => _owner.IndexOf(item);

		/// <inheritdoc/>
		public void CopyTo(string[] array, int arrayIndex)
		{
			CheckCopyTarget(array, arrayIndex, Count);

			foreach (var (key, _) in _owner.AsSpan())
			{
				array[arrayIndex++] = key;
			}
		}

		/// <inheritdoc/>
		public IEnumerator<string> GetEnumerator()
		{
			// Note: Not using Enumerable.Select for key/value views because it boxed the underlying enumerator and added 32 B per enumeration in public probes.
			foreach (var (key, _) in _owner)
			{
				yield return key;
			}
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

		void ICollection<string>.Add(string item) => throw new NotSupportedException();

		void ICollection<string>.Clear() => throw new NotSupportedException();

		bool ICollection<string>.Remove(string item) => throw new NotSupportedException();

		void IList<string>.Insert(int index, string item) => throw new NotSupportedException();

		void IList<string>.RemoveAt(int index) => throw new NotSupportedException();
	}

	/// <summary>
	/// The values of an object, in order. It is a view: it changes with the object.
	/// </summary>
	public readonly struct ValueCollection : IList<SomlValue>, IReadOnlyList<SomlValue>
	{
		private readonly SomlObject _owner;

		internal ValueCollection(SomlObject owner)
		{
			_owner = owner;
		}

		/// <inheritdoc/>
		public int Count => _owner._count;

		/// <inheritdoc/>
		public bool IsReadOnly => true;

		/// <inheritdoc/>
		public SomlValue this[int index]
		{
			get => _owner.GetAt(index).Value;
			set => throw new NotSupportedException("Set a value by its key instead");
		}

		/// <inheritdoc/>
		public bool Contains(SomlValue item) => IndexOf(item) >= 0;

		/// <inheritdoc/>
		public int IndexOf(SomlValue item)
		{
			var members = _owner.AsSpan();

			for (var index = 0; index < members.Length; index++)
			{
				if (members[index].Value.Equals(item))
				{
					return index;
				}
			}

			return -1;
		}

		/// <inheritdoc/>
		public void CopyTo(SomlValue[] array, int arrayIndex)
		{
			CheckCopyTarget(array, arrayIndex, Count);

			foreach (var (_, value) in _owner.AsSpan())
			{
				array[arrayIndex++] = value;
			}
		}

		/// <inheritdoc/>
		public IEnumerator<SomlValue> GetEnumerator()
		{
			foreach (var (_, value) in _owner)
			{
				yield return value;
			}
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

		void ICollection<SomlValue>.Add(SomlValue item) => throw new NotSupportedException();

		void ICollection<SomlValue>.Clear() => throw new NotSupportedException();

		bool ICollection<SomlValue>.Remove(SomlValue item) => throw new NotSupportedException();

		void IList<SomlValue>.Insert(int index, SomlValue item) => throw new NotSupportedException();

		void IList<SomlValue>.RemoveAt(int index) => throw new NotSupportedException();
	}

	private static void CheckCopyTarget(Array array, int arrayIndex, int count)
	{
		ArgumentNullException.ThrowIfNull(array);
		ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);

		if (array.Length - arrayIndex < count)
		{
			throw new ArgumentException("The array is too small for the items from the index on", nameof(array));
		}
	}

	private sealed class SomlObjectDebugView(SomlObject value)
	{
		[DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
		public KeyValuePair<string, SomlValue>[] Members => value.AsSpan().ToArray();
	}
}
