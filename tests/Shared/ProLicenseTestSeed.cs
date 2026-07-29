using System.Runtime.CompilerServices;
using NeoReports.Licensing;

namespace NeoReports.Pro.TestSupport;

/// <summary>
/// Opens the Pro license gate (ADR D70, Q2) before any test in the containing assembly runs, so a
/// Pro suite exercises the feature itself rather than the licensing check. Seeding the validated
/// token directly is the only option available: signing a token the embedded production key would
/// accept needs the matching private key, which deliberately never lives in this repo.
/// <para>
/// Linked (not copied) into each Pro test project via <c>&lt;Compile Include="..\Shared\..." /&gt;</c>:
/// <c>[ModuleInitializer]</c> fires per module, and a referenced assembly's initializer only runs
/// once that module is first touched — which a Pro suite may never do — so a shared *project*
/// wouldn't work, but a shared *file* compiled into each assembly does.
/// </para>
/// <para>
/// That the gate actually throws without a license is covered separately, in
/// <c>NeoReports.Licensing.UnitTests</c> — the one assembly that does not link this file, and can
/// therefore close the gate.
/// </para>
/// </summary>
internal static class ProLicenseTestSeed
{
    [ModuleInitializer]
    internal static void Seed() =>
        ProLicenseGate.Accept(new LicenseToken(
            "NeoReports test suite",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(100)));
}
