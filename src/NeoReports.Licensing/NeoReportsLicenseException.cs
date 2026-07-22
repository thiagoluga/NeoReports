namespace NeoReports.Licensing;

/// <summary>
/// Thrown when a NeoReports Pro license key is missing, malformed, has an invalid signature, or is
/// outside its validity window (ADR D70). Pro packages let this propagate at DI-registration time
/// rather than catching it — a misconfigured license should fail loudly and immediately, not
/// silently degrade (same posture as D36). Deliberately does NOT inherit
/// <c>NeoReports.Abstractions.NeoReportsException</c>: this package has no dependency on
/// <c>NeoReports.Abstractions</c>/<c>NeoReports.Core</c> (it's consumed by every OSS-only user who
/// never touches Pro, per D70), and inheriting it would introduce one purely for exception-hierarchy
/// uniformity.
/// </summary>
public sealed class NeoReportsLicenseException : Exception
{
    /// <summary>Creates the exception.</summary>
    public NeoReportsLicenseException(LicenseFailureReason reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>Creates the exception with an underlying cause.</summary>
    public NeoReportsLicenseException(LicenseFailureReason reason, string message, Exception inner) : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>Why the license was rejected — lets a caller branch without matching on <see cref="Exception.Message"/> text.</summary>
    public LicenseFailureReason Reason { get; }
}
