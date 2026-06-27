namespace NeoReports.AspNetCore;

/// <summary>Options for the NeoReports endpoint group.</summary>
public sealed class NeoReportsEndpointOptions
{
    /// <summary>
    /// When set, the endpoints require authorization (the host's auth is applied via
    /// <c>RequireAuthorization</c>). Auth itself is inherited from the host — there is no auth chain
    /// in v1. Default <c>false</c> (the host decides via its own middleware/policies).
    /// </summary>
    public bool RequireAuthorization { get; set; }

    /// <summary>Optional authorization policy name applied when <see cref="RequireAuthorization"/> is true.</summary>
    public string? AuthorizationPolicy { get; set; }
}
