namespace NeoReports.Samples.AsyncJobHangfire;

/// <summary>The report row type for the sample. Lives in a named namespace (not as a top-level
/// type) so it is properly addressable and passes static analysis.</summary>
public sealed record Sale(long Id, string Customer, decimal Amount, DateTime Date);
