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

    /// <summary>
    /// True when <see cref="Value"/> is the JSON text of an object or array rather than a plain
    /// scalar — an HTTP source's <c>headers</c>, a merge-join source's child sources. The editor is
    /// a one-line text box either way, so the flag is what tells the save path to parse the text
    /// back instead of storing the whole subtree as a JSON <em>string</em>.
    /// </summary>
    public bool IsStructured { get; set; }
}
