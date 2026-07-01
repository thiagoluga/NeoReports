using System.Diagnostics.CodeAnalysis;
using NeoReports.Abstractions;

namespace NeoReports.Sources.Join;

/// <summary>Fluent entry point for enriching a source with a batched per-page lookup.</summary>
public static class Enrichment
{
    /// <summary>
    /// Enriches a primary source: for each page, one batched <paramref name="lookup"/> call resolves
    /// the page's distinct keys, and <paramref name="map"/> combines each primary row with its
    /// looked-up value (or <c>null</c> when absent). O(pageSize); no N+1.
    /// </summary>
    /// <typeparam name="TPrimary">The primary row type.</typeparam>
    /// <typeparam name="TKey">The join key type.</typeparam>
    /// <typeparam name="TLookup">The looked-up value type.</typeparam>
    /// <typeparam name="TResult">The enriched result row type.</typeparam>
    /// <param name="primary">The primary source.</param>
    /// <param name="key">Extracts the join key from a primary row.</param>
    /// <param name="lookup">Batched lookup: given a page's distinct keys, returns their values.</param>
    /// <param name="map">Maps a primary row plus its looked-up value (or <c>null</c>) to the result.</param>
    /// <returns>A source that yields enriched rows through the standard pipeline.</returns>
    [SuppressMessage(
        "Major Code Smell", "S2436:Types and methods should not have too many generic parameters",
        Justification = "A batched join/enrichment API needs the primary, key, lookup-value and result types — " +
            "the same four-type shape as the BCL's Enumerable.GroupJoin<TOuter,TInner,TKey,TResult>.")]
    public static IBatchSource<TResult> Enrich<TPrimary, TKey, TLookup, TResult>(
        this IBatchSource<TPrimary> primary,
        Func<TPrimary, TKey> key,
        Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TLookup>>> lookup,
        Func<TPrimary, TLookup?, TResult> map)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(map);

        return new EnrichingBatchSource<TPrimary, TResult>(primary, async (records, cancellationToken) =>
        {
            var keys = records.Select(key).Distinct().ToList();
            IReadOnlyDictionary<TKey, TLookup> values =
                await lookup(keys, cancellationToken).ConfigureAwait(false) ?? new Dictionary<TKey, TLookup>();

            return records.Select(record =>
            {
                values.TryGetValue(key(record), out TLookup? value);
                return map(record, value);
            }).ToArray();
        });
    }
}
