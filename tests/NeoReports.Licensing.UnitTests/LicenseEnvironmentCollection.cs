using Xunit;

namespace NeoReports.Licensing.UnitTests;

/// <summary>
/// Serializes the test classes that mutate process-global state — the
/// <c>NEOREPORTS_LICENSE_KEY</c> environment variable and <see cref="ProLicenseGate"/>'s cached
/// token. xUnit runs test classes in parallel by default, so without this two classes clearing and
/// setting the same variable would interleave and flake.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LicenseEnvironmentCollection
{
    /// <summary>The collection name applied to every test class touching license-related global state.</summary>
    public const string Name = "license-environment";
}
