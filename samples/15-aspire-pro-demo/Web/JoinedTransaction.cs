namespace NeoReports.Samples.AspireProDemo.Web;

/// <summary>
/// One PostgreSQL transaction merged with its MongoDB counterpart — the result row type of the
/// <c>transactions-postgres-joined-mongodb</c> report, which demonstrates
/// <c>NeoReports.Sources.Join.Pro</c>'s streaming merge-join across two different databases.
/// </summary>
/// <param name="TransactionId">The join key, ordered identically on both sides.</param>
/// <param name="CustomerName">From the PostgreSQL (left) row.</param>
/// <param name="TotalAmount">From the PostgreSQL (left) row.</param>
/// <param name="Currency">From the PostgreSQL (left) row.</param>
/// <param name="MongoProductName">From the matched MongoDB (right) row; <c>null</c> when unmatched.</param>
/// <param name="MongoWarehouseId">From the matched MongoDB (right) row; <c>null</c> when unmatched.</param>
internal sealed record JoinedTransaction(
    Guid TransactionId,
    string CustomerName,
    decimal TotalAmount,
    string Currency,
    string? MongoProductName,
    long? MongoWarehouseId);
