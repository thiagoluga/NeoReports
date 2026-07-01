namespace NeoReports.Sources.Join.Pro;

/// <summary>How unmatched left rows are treated in a merge-join.</summary>
public enum JoinKind
{
    /// <summary>Only left rows that have at least one matching right row are emitted.</summary>
    Inner,

    /// <summary>Every left row is emitted; unmatched ones get an empty right group (left-outer join).</summary>
    LeftOuter,
}
