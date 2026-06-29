namespace NeoReports.Samples.SqlToXlsxS3;

/// <summary>The report row type for the sample, in a named namespace (not a top-level type).</summary>
public sealed record Sale(long Id, string Customer, decimal Amount, DateTime Date);
