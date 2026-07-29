using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using NeoReports.Abstractions;
using NeoReports.Licensing;

namespace NeoReports.Sources.Join.Pro;

/// <summary>Fluent entry points for combining several sources into one.</summary>
public static class Join
{
    /// <summary>
    /// Keyset merge-join of two sources that are each ordered by the join key. Streams the merge —
    /// for every left row it emits the (contiguous) group of right rows sharing its key — so memory
    /// stays constant as long as a single key's right multiplicity is bounded. Both sources must be
    /// ordered by their key (the same key domain), matching the v1 keyset ordering requirement.
    /// </summary>
    /// <typeparam name="TLeft">The left (driving) row type.</typeparam>
    /// <typeparam name="TRight">The right row type.</typeparam>
    /// <typeparam name="TKey">The join key type.</typeparam>
    /// <typeparam name="TResult">The joined result row type.</typeparam>
    /// <param name="left">The left source, ordered by <paramref name="keyLeft"/>.</param>
    /// <param name="keyLeft">Extracts the key from a left row.</param>
    /// <param name="right">The right source, ordered by <paramref name="keyRight"/>.</param>
    /// <param name="keyRight">Extracts the key from a right row.</param>
    /// <param name="map">Maps a left row plus its matched right rows (possibly empty) to a result.</param>
    /// <param name="kind">Inner (default) drops unmatched left rows; LeftOuter keeps them with an empty group.</param>
    /// <param name="keyComparer">Key comparer; defaults to <see cref="Comparer{T}.Default"/> (must match the sources' ordering).</param>
    /// <exception cref="NeoReportsLicenseException">No valid NeoReports Pro license is configured (ADR D70).</exception>
    [SuppressMessage(
        "Major Code Smell", "S2436:Types and methods should not have too many generic parameters",
        Justification = "A join needs the left, right, key and result types — the same four-type shape as " +
            "the BCL's Enumerable.GroupJoin<TOuter,TInner,TKey,TResult>.")]
    public static IStreamingSource<TResult> MergeJoin<TLeft, TRight, TKey, TResult>(
        IBatchSource<TLeft> left,
        Func<TLeft, TKey> keyLeft,
        IBatchSource<TRight> right,
        Func<TRight, TKey> keyRight,
        Func<TLeft, IReadOnlyList<TRight>, TResult> map,
        JoinKind kind = JoinKind.Inner,
        IComparer<TKey>? keyComparer = null)
    {
        // Eager (outside the iterator below), so a missing license fails when the report is being
        // defined — startup for a code-first app — not on the first row read (ADR D70).
        ProLicenseGate.EnsureValidated();
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(keyLeft);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(keyRight);
        ArgumentNullException.ThrowIfNull(map);
        IComparer<TKey> comparer = keyComparer ?? Comparer<TKey>.Default;

        async IAsyncEnumerable<TResult> Merge(
            ReportExecutionContext execution, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await using IAsyncEnumerator<TRight> rightEnum =
                Paginate(right, execution, cancellationToken).GetAsyncEnumerator(cancellationToken);
            var rightHasCurrent = await rightEnum.MoveNextAsync().ConfigureAwait(false);

            var group = new List<TRight>();
            TRight[] current = Array.Empty<TRight>();
            TKey? currentKey = default;
            var haveKey = false;

            await foreach (TLeft leftRow in Paginate(left, execution, cancellationToken).ConfigureAwait(false))
            {
                TKey key = keyLeft(leftRow);
                if (!haveKey || comparer.Compare(currentKey!, key) != 0)
                {
                    rightHasCurrent = await GatherGroupAsync(rightEnum, rightHasCurrent, key, keyRight, comparer, group)
                        .ConfigureAwait(false);
                    current = group.Count == 0 ? Array.Empty<TRight>() : group.ToArray();
                    currentKey = key;
                    haveKey = true;
                }

                if (current.Length > 0 || kind == JoinKind.LeftOuter)
                    yield return map(leftRow, current);
            }
        }

        return new DelegatingStreamingSource<TResult>(left.Schema, Merge);
    }

    // Advances the right enumerator past keys below <paramref name="key"/>, then buffers the
    // contiguous group of right rows whose key equals it. Returns whether the right enumerator still
    // has a current row.
    private static async Task<bool> GatherGroupAsync<TRight, TKey>(
        IAsyncEnumerator<TRight> right,
        bool hasCurrent,
        TKey key,
        Func<TRight, TKey> keyRight,
        IComparer<TKey> comparer,
        List<TRight> group)
    {
        group.Clear();
        while (hasCurrent && comparer.Compare(keyRight(right.Current), key) < 0)
            hasCurrent = await right.MoveNextAsync().ConfigureAwait(false);
        while (hasCurrent && comparer.Compare(keyRight(right.Current), key) == 0)
        {
            group.Add(right.Current);
            hasCurrent = await right.MoveNextAsync().ConfigureAwait(false);
        }

        return hasCurrent;
    }

    /// <summary>Reads a batch source page by page as a flat async sequence (O(pageSize) memory).</summary>
    private static async IAsyncEnumerable<T> Paginate<T>(
        IBatchSource<T> source, ReportExecutionContext execution, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        var pageNumber = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pageNumber++;
            BatchResult<T> result = await source
                .ReadBatchAsync(new BatchContext(execution, 1000, cursor, pageNumber), cancellationToken)
                .ConfigureAwait(false);

            foreach (T record in result.Records)
                yield return record;

            if (!result.HasMore)
                break;
            cursor = result.NextCursor;
        }
    }
}
