namespace NeoReports.UI.Services;

/// <summary>
/// A single editable key/value row for a source's dynamic-config property bag (ADR D42) — shared
/// between the Sources page's "Registered sources" form and the Builder wizard's Configure step for
/// any source type outside the ADO/keyset SQL family (see <see cref="BuilderState.AdoSqlShapeTypes"/>).
/// </summary>
public sealed class PropertyRow
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
