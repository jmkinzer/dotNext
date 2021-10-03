using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotNext.Collections.Specialized;

/// <summary>
/// Represents thread-safe implementation of <see cref="ITypeMap{TValue}"/> interface.
/// </summary>
/// <typeparam name="TValue">The type of the value.</typeparam>
public class ConcurrentTypeMap<TValue> : ITypeMap<TValue>
{
    private sealed class Entry
    {
        private bool hasValue;
        private TValue? value;

        internal bool HasValue => hasValue;

        internal TValue? Value
        {
            get => value;
            set
            {
                hasValue = true;
                this.value = value;
            }
        }

        internal bool TryGetValue([MaybeNullWhen(false)]out TValue value)
        {
            value = this.value;
            return hasValue;
        }

        internal void Clear()
        {
            hasValue = false;
            value = default;
        }
    }

    // Assuming that the map will not contain hunders or thousands for entries.
    // If so, we can keep the lock for each entry instead of buckets as in ConcurrentDictionaryMap.
    // As a result, we don't need the concurrency level
    private volatile Entry[] entries;

    /// <summary>
    /// Initializes a new map.
    /// </summary>
    /// <param name="capacity">The initial capacity of the map.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is less than zero.</exception>
    public ConcurrentTypeMap(int capacity)
    {
        if (capacity < 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        var entries = capacity == 0 ? Array.Empty<Entry>() : new Entry[capacity];
        entries.InstantiateElements();
        this.entries = entries;
    }

    /// <summary>
    /// Initializes a new map of recommended capacity.
    /// </summary>
    public ConcurrentTypeMap()
    {
        var entries = new Entry[ITypeMap<TValue>.RecommendedCapacity];
        entries.InstantiateElements();
        this.entries = entries;
    }

    private void Resize(Entry[] entries)
    {
        // the thread that first obtains the first lock will be the one doing the resize operation
        lock (this)
        {
            // make sure nobody resized the table while we were waiting for the first lock
            if (!ReferenceEquals(entries, this.entries))
                return;

            // do resize
            Resize(ref entries);

            // commit resized storage
            this.entries = entries;
        }

        static void Resize(ref Entry[] entries)
        {
            var firstUnitialized = entries.Length;
            Array.Resize(ref entries, ITypeMap<TValue>.RecommendedCapacity);

            // initializes the rest of the array
            for (var i = firstUnitialized; i < entries.Length; i++)
                entries[i] = new();
        }
    }

    /// <inheritdoc />
    void ITypeMap<TValue>.Add<TKey>(TValue value)
    {
        if (!TryAdd<TKey>(value))
            throw new GenericArgumentException<TKey>(ExceptionMessages.KeyAlreadyExists);
    }

    private bool TryAdd(int index, TValue value)
    {
        for (bool added; ;)
        {
            var entries = this.entries;

            if (index >= entries.Length)
            {
                Resize(entries);
                continue;
            }

            var entry = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(entries), index);

            lock (entry)
            {
                if (!ReferenceEquals(entries, this.entries))
                    continue;

                if (entry.HasValue)
                {
                    added = false;
                }
                else
                {
                    added = true;
                    entry.Value = value;
                }
            }

            return added;
        }
    }

    /// <summary>
    /// Attempts to associate a value with the type.
    /// </summary>
    /// <typeparam name="TKey">The type acting as a key.</typeparam>
    /// <param name="value">The value associated with the type.</param>
    /// <returns><see langword="true"/> if the value is added; otherwise, <see langword="false"/>.</returns>
    public bool TryAdd<TKey>(TValue value)
        => TryAdd(ITypeMap<TValue>.GetIndex<TKey>(), value);

    private void Set(int index, TValue value)
    {
        for (Entry[] entries; ;)
        {
            entries = this.entries;

            if (index >= entries.Length)
            {
                Resize(entries);
                continue;
            }

            var entry = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(entries), index);

            lock (entry)
            {
                if (!ReferenceEquals(entries, this.entries))
                    continue;

                entry.Value = value;
            }

            break;
        }
    }

    /// <summary>
    /// Associates the value with the specified type.
    /// </summary>
    /// <typeparam name="TKey">The type acting as a key.</typeparam>
    /// <param name="value">The value to set.</param>
    public void Set<TKey>(TValue value)
        => Set(ITypeMap<TValue>.GetIndex<TKey>(), value);

    /// <summary>
    /// Determines whether the map has association between the value and the specified type.
    /// </summary>
    /// <typeparam name="TKey">The type acting as a key.</typeparam>
    /// <returns><see langword="true"/> if there is a value associated with <typeparamref name="TKey"/>; otherwise, <see langword="false"/>.</returns>
    public bool ContainsKey<TKey>()
    {
        return ContainsKey(entries, ITypeMap<TValue>.GetIndex<TKey>());

        static bool ContainsKey(Entry[] entries, int index)
            => index < entries.Length && Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(entries), index).HasValue;
    }

    private TValue GetOrAdd(int index, TValue value, out bool added)
    {
        for (Entry[] entries; ;)
        {
            entries = this.entries;

            if (index >= entries.Length)
            {
                Resize(entries);
                continue;
            }

            var entry = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(entries), index);

            lock (entry)
            {
                if (!ReferenceEquals(entries, this.entries))
                    continue;

                if (entry.HasValue)
                {
                    added = false;
                    value = entry.Value!;
                }
                else
                {
                    added = true;
                    entry.Value = value;
                }
            }

            return value;
        }
    }

    /// <summary>
    /// Adds a value to the map if the key does not already exist.
    /// Returns the new value, or the existing value if the key already exists.
    /// </summary>
    /// <typeparam name="TKey">The type acting as a key.</typeparam>
    /// <param name="value">The value associated with the type.</param>
    /// <param name="added"><see langword="true"/> if the value is added; <see langword="false"/> if the value is already exist.</param>
    /// <returns>The existing value; or <paramref name="value"/> if added.</returns>
    public TValue GetOrAdd<TKey>(TValue value, out bool added)
        => GetOrAdd(ITypeMap<TValue>.GetIndex<TKey>(), value, out added);

    private bool AddOrUpdate(int index, TValue value)
    {
        for (bool added; ;)
        {
            var entries = this.entries;

            if (index >= entries.Length)
            {
                Resize(entries);
                continue;
            }

            var entry = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(entries), index);

            lock (entry)
            {
                if (!ReferenceEquals(entries, this.entries))
                    continue;

                added = !entry.HasValue;
                entry.Value = value;
            }

            return added;
        }
    }

    /// <summary>
    /// Adds a value to the map if the key does not already exist, or updates the existing value.
    /// </summary>
    /// <typeparam name="TKey">The type acting as a key.</typeparam>
    /// <param name="value">The value associated with the type.</param>
    /// <returns><see langword="true"/> if <paramref name="value"/> is added;
    /// <see langword="false"/> if the existing value is updated with <paramref name="value"/>.
    /// </returns>
    public bool AddOrUpdate<TKey>(TValue value)
        => AddOrUpdate(ITypeMap<TValue>.GetIndex<TKey>(), value);

    private Optional<TValue> Replace(int index, TValue value)
    {
        for (Optional<TValue> result; ;)
        {
            var entries = this.entries;

            if (index >= entries.Length)
            {
                Resize(entries);
                continue;
            }

            var entry = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(entries), index);

            lock (entry)
            {
                if (!ReferenceEquals(entries, this.entries))
                    continue;

                result = entry.HasValue ? entry.Value : Optional<TValue>.None;
                entry.Value = value;
            }

            return result;
        }
    }

    /// <summary>
    /// Replaces the existing value with a new value.
    /// </summary>
    /// <typeparam name="TKey">The type acting as a key.</typeparam>
    /// <param name="value">A new value.</param>
    /// <returns>The replaced value.</returns>
    public Optional<TValue> Replace<TKey>(TValue value)
        => Replace(ITypeMap<TValue>.GetIndex<TKey>(), value);

    private bool Remove(int index, [MaybeNullWhen(false)] out TValue value)
    {
        bool result;

        for (Entry[] entries; ;)
        {
            entries = this.entries;

            if (index >= entries.Length)
            {
                value = default;
                result = false;
                break;
            }

            var entry = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(entries), index);

            lock (entry)
            {
                if (!ReferenceEquals(entries, this.entries))
                    continue;

                result = entry.TryGetValue(out value);
                entry.Clear();
            }

            break;
        }

        return result;
    }

    /// <summary>
    /// Attempts to remove the value from the map.
    /// </summary>
    /// <typeparam name="TKey">The type acting as a key.</typeparam>
    /// <param name="value">The value of the removed element.</param>
    /// <returns><see langword="true"/> if the element successfully removed; otherwise, <see langword="false"/>.</returns>
    public bool Remove<TKey>([MaybeNullWhen(false)] out TValue value)
        => Remove(ITypeMap<TValue>.GetIndex<TKey>(), out value);

    /// <summary>
    /// Attempts to remove the value from the map.
    /// </summary>
    /// <typeparam name="TKey">The type acting as a key.</typeparam>
    /// <returns><see langword="true"/> if the element successfully removed; otherwise, <see langword="false"/>.</returns>
    public bool Remove<TKey>() => Remove<TKey>(out _);

    private bool TryGetValue(int index, [MaybeNullWhen(false)] out TValue value)
    {
        bool result;

        for (Entry[] entries; ;)
        {
            entries = this.entries;

            if (index >= entries.Length)
            {
                value = default;
                result = false;
                break;
            }

            var entry = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(entries), index);

            lock (entry)
            {
                if (!ReferenceEquals(entries, this.entries))
                    continue;

                result = entry.TryGetValue(out value);
            }

            break;
        }

        return result;
    }

    /// <summary>
    /// Attempts to get the value associated with the specified type.
    /// </summary>
    /// <typeparam name="TKey">The type acting as a key.</typeparam>
    /// <param name="value">The value associated with the type.</param>
    /// <returns><see langword="true"/> if there is a value associated with <typeparamref name="TKey"/>; otherwise, <see langword="false"/>.</returns>
    public bool TryGetValue<TKey>([MaybeNullWhen(false)] out TValue value)
        => TryGetValue(ITypeMap<TValue>.GetIndex<TKey>(), out value);

    /// <summary>
    /// Removes all elements from this map.
    /// </summary>
    public void Clear()
    {
        foreach (var entry in entries)
        {
            lock (entry)
                entry.Clear();
        }
    }
}