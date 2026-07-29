using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.QueryBuilder.Pro;
using NeoReports.Sources.Join.Pro;
using NeoReports.Xlsx.Pro;
using Shouldly;
using Xunit;

namespace NeoReports.Licensing.UnitTests;

/// <summary>
/// ADR D70 (Q2): proves every gated NeoReports Pro entry point actually refuses to run without a
/// license — the enforcement the three Pro suites can't test, since each seeds the gate in a module
/// initializer so it can exercise the features themselves.
/// </summary>
[Collection(LicenseEnvironmentCollection.Name)]
public sealed class ProLicenseGateTests : IDisposable
{
    private readonly string? _originalKey =
        Environment.GetEnvironmentVariable(ServiceCollectionExtensions.LicenseKeyEnvironmentVariable);

    public ProLicenseGateTests()
    {
        Environment.SetEnvironmentVariable(ServiceCollectionExtensions.LicenseKeyEnvironmentVariable, null);
        ProLicenseGate.Reset();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ServiceCollectionExtensions.LicenseKeyEnvironmentVariable, _originalKey);
        ProLicenseGate.Reset();
    }

    /// <summary>A trivially valid source, so a gate test can pass real arguments instead of <c>null!</c>.</summary>
    private sealed class EmptyBatchSource : IBatchSource<int>
    {
        public ReportSchema Schema { get; } = new([new ReportColumn("Id", ColumnType.Integer)]);

        public Task<BatchResult<int>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new BatchResult<int>(Array.Empty<int>(), null, false));
    }

    private static EmptyBatchSource EmptySource() => new();

    private static string SignedKey(ECDsa keyPair, string licensee = "Acme Corp") =>
        LicenseSigner.Sign(
            new LicenseToken(licensee, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30)),
            keyPair);

    [Fact]
    public void Xlsx_workbook_format_refuses_to_build_without_a_license()
    {
        NeoReportsLicenseException ex = Should.Throw<NeoReportsLicenseException>(() => Format.XlsxWorkbook());

        ex.Reason.ShouldBe(LicenseFailureReason.Missing);
        ex.Message.ShouldContain("NeoReports Pro license");
    }

    [Fact]
    public void Merge_join_refuses_to_build_without_a_license()
    {
        // Valid arguments throughout, so this can only fail on the licence check — it must not
        // silently depend on the gate happening to run before the argument null-checks.
        Should.Throw<NeoReportsLicenseException>(() => Join.MergeJoin<int, int, int, int>(
            EmptySource(), _ => 0, EmptySource(), _ => 0, (_, _) => 0)).Reason.ShouldBe(LicenseFailureReason.Missing);
    }

    [Fact]
    public void Enrichment_refuses_to_build_without_a_license()
    {
        Should.Throw<NeoReportsLicenseException>(() => EmptySource().Enrich(
            _ => 0,
            (_, _) => Task.FromResult<IReadOnlyDictionary<int, int>>(new Dictionary<int, int>()),
            (_, _) => 0)).Reason.ShouldBe(LicenseFailureReason.Missing);
    }

    [Fact]
    public void Query_sql_generator_refuses_to_construct_without_a_license()
    {
        Should.Throw<NeoReportsLicenseException>(() => new QuerySqlGenerator())
            .Reason.ShouldBe(LicenseFailureReason.Missing);
    }

    [Fact]
    public void Keyset_sql_generator_refuses_to_generate_without_a_license()
    {
        // Gated separately from QuerySqlGenerator: it's a public static type, reachable without ever
        // constructing the IQuerySqlGenerator implementation. Valid arguments, so only the licence
        // check can be what throws.
        var key = new QueryColumnRef("s", "Id");
        var model = new QueryModel(
            From: new QueryTable("public", "Sales", "s"),
            Joins: [],
            Select: [new QuerySelectColumn(key, "Id", "integer")],
            Where: [],
            GroupBy: [],
            Key: key,
            KeyDataType: "integer");

        Should.Throw<NeoReportsLicenseException>(() => KeysetSqlGenerator.Generate(model, SqlDialect.ForType("postgres")!))
            .Reason.ShouldBe(LicenseFailureReason.Missing);
    }

    [Fact]
    public void Pro_di_registration_refuses_without_a_license()
    {
        Should.Throw<NeoReportsLicenseException>(() => new ServiceCollection().AddXlsxWorkbook())
            .Reason.ShouldBe(LicenseFailureReason.Missing);
        Should.Throw<NeoReportsLicenseException>(() => new ServiceCollection().AddMergeJoinConfigSource())
            .Reason.ShouldBe(LicenseFailureReason.Missing);
        Should.Throw<NeoReportsLicenseException>(() => new ServiceCollection().AddQueryBuilder())
            .Reason.ShouldBe(LicenseFailureReason.Missing);
    }

    [Fact]
    public void The_missing_license_message_names_every_way_to_supply_one()
    {
        string message = Should.Throw<NeoReportsLicenseException>(() => Format.XlsxWorkbook()).Message;

        message.ShouldContain(ServiceCollectionExtensions.LicenseKeyEnvironmentVariable);
        message.ShouldContain(nameof(ProLicenseGate.Register));
        message.ShouldContain("AddNeoReportsProLicense");
    }

    [Fact]
    public void A_key_in_the_environment_signed_by_the_wrong_pair_is_rejected()
    {
        using ECDsa keyPair = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(ServiceCollectionExtensions.LicenseKeyEnvironmentVariable, SignedKey(keyPair));

        Should.Throw<NeoReportsLicenseException>(() => Format.XlsxWorkbook())
            .Reason.ShouldBe(LicenseFailureReason.SignatureInvalid);
    }

    [Fact]
    public void An_expired_key_is_rejected_on_its_expiry_not_its_signature()
    {
        // Drives the throwaway pair through real validation (its own public key), so the signature
        // passes and expiry is genuinely what rejects it — otherwise this would silently duplicate
        // the wrong-signature test above and never exercise the validity window at all.
        using ECDsa keyPair = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string expired = LicenseSigner.Sign(
            new LicenseToken("Acme Corp", DateTimeOffset.UtcNow.AddDays(-60), DateTimeOffset.UtcNow.AddDays(-30)),
            keyPair);

        Should.Throw<NeoReportsLicenseException>(
                () => ServiceCollectionExtensions.AddNeoReportsProLicenseCore(new ServiceCollection(), expired, keyPair))
            .Reason.ShouldBe(LicenseFailureReason.OutOfValidityWindow);
    }

    [Fact]
    public void A_license_that_expires_while_the_process_runs_closes_the_gate_again()
    {
        // The gate caches the validated token, but re-checks its window on every call — otherwise a
        // long-running host started inside a 30-day trial would keep working indefinitely.
        ProLicenseGate.Accept(new LicenseToken("Acme Corp", DateTimeOffset.UtcNow.AddDays(-60), DateTimeOffset.UtcNow.AddSeconds(-1)));

        Should.Throw<NeoReportsLicenseException>(() => Format.XlsxWorkbook())
            .Reason.ShouldBe(LicenseFailureReason.OutOfValidityWindow);
    }

    [Fact]
    public void Register_validates_the_key_and_rejects_a_bad_one()
    {
        using ECDsa keyPair = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Should.Throw<NeoReportsLicenseException>(() => ProLicenseGate.Register("not-a-real-key"))
            .Reason.ShouldBe(LicenseFailureReason.Malformed);
        // A validly-signed key still fails against the embedded production public key.
        Should.Throw<NeoReportsLicenseException>(() => ProLicenseGate.Register(SignedKey(keyPair)))
            .Reason.ShouldBe(LicenseFailureReason.SignatureInvalid);

        ProLicenseGate.Current.ShouldBeNull();
    }

    [Fact]
    public void An_accepted_license_opens_every_gated_entry_point()
    {
        ProLicenseGate.Accept(new LicenseToken("Acme Corp", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30)));

        Should.NotThrow(() => Format.XlsxWorkbook());
        Should.NotThrow(() => new QuerySqlGenerator());
        Should.NotThrow(() => new ServiceCollection().AddMergeJoinConfigSource());
        ProLicenseGate.Current!.Licensee.ShouldBe("Acme Corp");
    }

    [Fact]
    public void A_di_supplied_license_also_opens_the_static_gate()
    {
        // The two routes share one process-wide license state, so a host that configures its key
        // through DI can still use the static fluent Pro APIs, which have no container to read from.
        using ECDsa keyPair = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var services = new ServiceCollection();
        ServiceCollectionExtensions.AddNeoReportsProLicenseCore(services, SignedKey(keyPair, "DI Corp"), keyPair);

        ProLicenseGate.Current!.Licensee.ShouldBe("DI Corp");
        Should.NotThrow(() => Format.XlsxWorkbook());
    }

    [Fact]
    public void A_statically_registered_license_satisfies_a_later_di_registration()
    {
        ProLicenseGate.Accept(new LicenseToken("Static Corp", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30)));
        var services = new ServiceCollection();

        services.AddNeoReportsProLicense();

        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<LicenseToken>().Licensee.ShouldBe("Static Corp");
    }
}
