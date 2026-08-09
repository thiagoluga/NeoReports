using System.IO.Compression;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.ConsumerSmoke;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Pipeline;
using NeoReports.Destinations.Local;
using NeoReports.Licensing;
using NeoReports.QueryBuilder.Pro;
using NeoReports.Sources.Join.Pro;
using NeoReports.Xlsx.Pro;
using static NeoReports.Xlsx.Pro.Format;

// Consumer smoke test (ADR D85) — verifies the PUBLISHED packages, not the working tree.
//
//   dotnet run --project tools/consumer-smoke
//
// Deliberately outside NeoReports.sln and cut off from the repo's Directory.Build.props and Central
// Package Management, so everything below resolves from nuget.org exactly as a customer's build
// would. See README.md for why the samples were NOT converted to do this.

const string ExpectedVersion = "2.0.0";
const string ProductionKey =
    "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEVFPHemBTUrnP9IOObkGNSIy/y5vblPPlirW9o0jk0zG51PzuzLvxf6c+OnQWRxvsWGkF1yU3b/kyZVgAULAT3w==";
const string BurnedPlaceholderKey =
    "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE8CTV2vXjiGkicpdqu6zK5kzHUCAotsIs7ysuwvAP/ZhxSmAcK/ZuZ4w//6XT0I71il/8qeuobgic5csTNd5How==";

var checks = new Checks();

// ---- Level 1: identity. What did NuGet actually give us? ---------------------------------------

Assembly licensing = typeof(ProLicense).Assembly;
Assembly xlsxPro = typeof(NeoReports.Xlsx.Pro.XlsxWorkbookOptions).Assembly;

// NOTE ON WHAT CANNOT BE CHECKED HERE. "Did this come from a package rather than a project?" has no
// honest runtime answer: the SDK copies package assemblies into bin/ at build time, so
// Assembly.Location points at bin/ either way. That guarantee is structural instead — this project
// declares no ProjectReference and its Directory.Build.props cuts it off from the repo's props and
// Central Package Management. Adding a ProjectReference is what would break it, and no assertion
// below would notice; the csproj comment is the real guard.
//
// What the version DOES catch is the honest half: a build against local source carries the repo's
// default version, not 2.0.0, so a mismatch here still means "you are not running the release".
foreach (Assembly asm in new[] { licensing, xlsxPro, typeof(ReportSchema).Assembly })
{
    checks.That(
        asm.GetName().Version?.ToString(3) == ExpectedVersion,
        $"{asm.GetName().Name} is {ExpectedVersion}",
        $"got {asm.GetName().Version?.ToString(3)}");
}

// SourceLink stamps the commit the release was built from, so this pins the artifact to a point in
// history rather than just a version number.
string? informational = licensing
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
checks.That(
    informational?.StartsWith(ExpectedVersion + "+", StringComparison.Ordinal) == true,
    $"NeoReports.Licensing reports a release build ({informational})",
    $"informational version is \"{informational}\" — expected {ExpectedVersion}+<commit>");

// Reading the constant off the loaded assembly, not off source: this is what a customer's process
// will verify signatures against.
checks.That(ProLicense.PublicKeyBase64 == ProductionKey, "the shipped key is the production key");
checks.That(ProLicense.PublicKeyBase64 != BurnedPlaceholderKey, "the shipped key is NOT the burned placeholder");

// ---- Level 2: enforcement. Does the published artifact actually gate? --------------------------
//
// The check that would matter most if it ever broke: a Pro package that works without a license is
// revenue walking out of the door, and it would look perfectly healthy from the outside.
//
// The environment variable is cleared for the duration rather than skipping these when a key is
// present. ProLicenseGate falls back to NEOREPORTS_LICENSE_KEY on first use, so with a key exported
// the Pro calls below legitimately succeed — the gate is process-wide, and "no license registered"
// is not the same state as "no license available". Asserting a refusal without clearing it first is
// a broken test, not a broken product; found the hard way. Restored immediately, and safe to do
// because the gate deliberately does not cache a failure.

string? licenseKey = Environment.GetEnvironmentVariable("NEOREPORTS_LICENSE_KEY");
Environment.SetEnvironmentVariable("NEOREPORTS_LICENSE_KEY", null);
try
{
    checks.Throws<NeoReportsLicenseException>(
        () => XlsxWorkbook(o => o.AutoFilter()),
        "Xlsx.Pro refuses to build a workbook writer without a license");

    checks.Throws<NeoReportsLicenseException>(
        () => new ServiceCollection().AddXlsxWorkbook(),
        "Xlsx.Pro refuses the DI registration without a license");

    checks.Throws<NeoReportsLicenseException>(
        () => new ServiceCollection().AddMergeJoinConfigSource(),
        "Sources.Join.Pro refuses without a license");

    checks.Throws<NeoReportsLicenseException>(
        () => new ServiceCollection().AddQueryBuilder(),
        "QueryBuilder.Pro refuses without a license");
}
finally
{
    Environment.SetEnvironmentVariable("NEOREPORTS_LICENSE_KEY", licenseKey);
}

// ---- Level 3: a real report, only when a key is available --------------------------------------

if (string.IsNullOrWhiteSpace(licenseKey))
{
    Console.WriteLine();
    Console.WriteLine("NEOREPORTS_LICENSE_KEY is not set — skipping the end-to-end Pro report.");
    Console.WriteLine("Everything above is what can be proven without a license; see README.md.");
}
else
{
    // Trimmed because the usual way to get a key into this variable is a shell redirect, and a stray
    // newline, BOM or CR is otherwise indistinguishable from a corrupt signature in the error.
    // PowerShell's `>` writes UTF-16, which arrives here as embedded NULs — stripped for the same
    // reason: a wrong-encoding file should not read as "your license is malformed".
    licenseKey = licenseKey.Replace("\0", string.Empty, StringComparison.Ordinal).Trim().TrimStart('﻿');

    LicenseToken? token = null;
    try
    {
        ProLicenseGate.Register(licenseKey);
        token = ProLicenseGate.Current;
    }
    catch (NeoReportsLicenseException ex)
    {
        // Reported, not thrown. This harness exists to say what is wrong with a release; crashing
        // with a stack trace on the most ordinary operator mistake would make it useless at that.
        checks.That(false, "the license was accepted", $"{ex.Reason}: {ex.Message}");
    }

    checks.That(token is not null, $"license accepted (licensee \"{token?.Licensee}\")");

    string outputDirectory = Path.Combine(Path.GetTempPath(), "neoreports-consumer-smoke");
    Directory.CreateDirectory(outputDirectory);
    foreach (string stale in Directory.GetFiles(outputDirectory, "*.xlsx"))
        File.Delete(stale);

    var services = new ServiceCollection();
    services.AddLogging(); // Core resolves ILoggerFactory; without this the provider throws
    services.AddReport<Sale>("smoke", b => b
        .From(new InMemorySales(20))
        .Column(v => v.Id, "Sale ID")
        .Column(v => v.Customer, "Customer")
        .Column(v => v.Amount, "Amount")
        // The Pro feature under test: one workbook, a worksheet per section, from one source read.
        .ToSections(XlsxWorkbook(o => o.AutoFilter()), s => s
            .Section("Approved", v => v.Where(x => x.Amount > 0))
            .Section("Rejected", v => v.Where(x => x.Amount <= 0)))
        .UploadTo(Destination.Local(Path.Combine(outputDirectory, "{name}.{ext}"))));

    await using ServiceProvider provider = services.BuildServiceProvider();
    ReportRunResult result = await provider.GetRequiredService<IReportRunner>().RunAsync("smoke");

    checks.That(result.Status == ReportRunStatus.Completed, $"the run completed (status {result.Status})");
    checks.That(result.Stats.RecordsWritten > 0, $"rows were written ({result.Stats.RecordsWritten})");

    string[] produced = Directory.GetFiles(outputDirectory, "*.xlsx");
    checks.That(produced.Length == 1, $"exactly one workbook was produced ({produced.Length})");

    if (produced.Length == 1)
    {
        // Open the file rather than trusting the run's own report of success — an .xlsx is a zip, and
        // "the pipeline said Completed" is not the same claim as "Excel can open this".
        using ZipArchive archive = ZipFile.OpenRead(produced[0]);
        int worksheets = archive.Entries.Count(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal));
        checks.That(worksheets == 2, $"the workbook holds 2 worksheets ({worksheets}) — the sectioning is real");
    }
}

return checks.Report();
