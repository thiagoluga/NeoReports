using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.UI.Services;
using NeoReports.UI.UnitTests.Fakes;

namespace NeoReports.UI.UnitTests;

/// <summary>
/// Shared bUnit fixture for every NeoReports.UI page/component test. Registers a fresh
/// <see cref="FakeNeoReportsApiClient"/> and <see cref="BuilderState"/> per test (mirroring the
/// real app's scoped-per-circuit lifetime — <c>NeoReportsUIExtensions</c>) and puts JSInterop in
/// loose mode since none of the pages under test make asserted JS calls.
/// </summary>
public abstract class NeoReportsTestContext : BunitContext
{
    protected FakeNeoReportsApiClient Api { get; } = new();

    protected BuilderState Wizard { get; } = new();

    protected NeoReportsTestContext()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<INeoReportsApiClient>(Api);
        Services.AddSingleton(Wizard);
    }
}
