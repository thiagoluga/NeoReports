using NeoReports.Abstractions;

namespace NeoReports.Sources.Join;

/// <summary>Fluent entry point for enriching a source with a batched per-page lookup.</summary>
public static class Enrichment
{
    /// <summary>
    /// Enriches a primary source: for each page, one batched <paramref name="lookup"/> call resolves
    /// the page's keys, and <paramref name="map"/> combines each primary row with its looked-up value.
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
    public static IBatchSource<TResult> Enrich<TPrimary, TKey, TLookup, TResult>(
        this IBatchSource<TPrimary> primary,
        Func<TPrimary, TKey> key,
        Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TLookup>>> lookup,
        Func<TPrimary, TLookup?, TResult> map)
        where TKey : notnull =>
        new EnrichingBatchSource<TPrimary, TKey, TLookup, TResult>(primary, key, lookup, map);
}
