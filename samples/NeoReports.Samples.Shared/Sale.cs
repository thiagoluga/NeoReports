namespace NeoReports.Samples.Shared;

/// <summary>The canonical report row type shared by the typed-path samples (was copy-pasted with
/// minor doc-comment drift across 01/02/03/06 before Epic H).</summary>
public sealed record Sale(long Id, string Customer, decimal Amount, DateTime Date);
