using NeoReports.Abstractions;

namespace NeoReports.Sources.Join;

/// <summary>
/// Wraps a primary <see cref="IBatchSource{T}"/> and enriches each page with data looked up from a
/// secondary source. For every page it collects the (distinct) keys and makes <b>one batched lookup
/// call</b> — never one per row — then maps each primary row together with its looked-up value. Memory
/// stays O(pageSize); the batched-per-page shape structurally prevents the N+1 trap.
/// </summary>
/// <typeparam name="TPrimary">The primary row type.</typeparam>
/// <typeparam name="TKey">The join key type.</typeparam>
/// <typeparam name="TLookup">The looked-up value type.</typeparam>
/// <typeparam name="TResult">The enriched result row type.</typeparam>
public sealed class EnrichingBatchSource<TPrimary, TKey, TLookup, TResult> : IBatchSource<TResult>
    where TKey : notnull
{
    private readonly IBatchSource<TPrimary> _primary;
    private readonly Func<TPrimary, TKey> _key;
    private readonly Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TLookup>>> _lookup;
    private readonly Func<TPrimary, TLookup?, TResult> _map;

    /// <summary>Creates an enriching source.</summary>
    /// <param name="primary">The primary source, read page by page.</param>
    /// <param name="key">Extracts the join key from a primary row.</param>
    /// <param name="lookup">Batched lookup: given a page's distinct keys, returns their values.</param>
    /// <param name="map">Maps a primary row plus its looked-up value (or <c>null</c> when absent) to the result.</param>
    public EnrichingBatchSource(
        IBatchSource<TPrimary> primary,
        Func<TPrimary, TKey> key,
        Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TLookup>>> lookup,
        Func<TPrimary, TLookup?, TResult> map)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _key = key ?? throw new ArgumentNullException(nameof(key));
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _map = map ?? throw new ArgumentNullException(nameof(map));
    }

    /// <inheritdoc />
    public ReportSchema Schema => _primary.Schema;

    /// <inheritdoc />
    public async Task<BatchResult<TResult>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        BatchResult<TPrimary> page = await _primary.ReadBatchAsync(context, cancellationToken).ConfigureAwait(false);
        if (page.Records.Count == 0)
            return new BatchResult<TResult>(Array.Empty<TResult>(), page.NextCursor, page.HasMore);

        var keys = new List<TKey>(page.Records.Count);
        var seen = new HashSet<TKey>();
        foreach (TPrimary record in page.Records)
        {
            TKey key = _key(record);
            if (seen.Add(key))
                keys.Add(key);
        }

        IReadOnlyDictionary<TKey, TLookup> values =
            await _lookup(keys, cancellationToken).ConfigureAwait(false)
            ?? new Dictionary<TKey, TLookup>();

        var results = new TResult[page.Records.Count];
        for (var i = 0; i < page.Records.Count; i++)
        {
            TPrimary record = page.Records[i];
            values.TryGetValue(_key(record), out TLookup? value);
            results[i] = _map(record, value);
        }

        return new BatchResult<TResult>(results, page.NextCursor, page.HasMore);
    }
}
