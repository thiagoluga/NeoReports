namespace NeoReports.Sources.Salesforce;

/// <summary>
/// Rewrites a SOQL query's <c>SELECT</c> clause into <c>SELECT COUNT()</c> for
/// <see cref="SalesforceRowCounter"/> (ADR D67) — keeps everything from the top-level <c>FROM</c>
/// onward (<c>WHERE</c>/<c>ORDER BY</c>/etc.) completely unchanged, so the count reflects the exact
/// same filtered result set the real query would return. Paren-depth-aware so a subquery's own
/// nested <c>FROM</c> (e.g. <c>SELECT Id, (SELECT Name FROM Contacts) FROM Account</c>) is not
/// mistaken for the outer query's <c>FROM</c>. Declines (returns <c>null</c>) rather than guessing
/// when the query's shape isn't recognized — <see cref="NeoReports.Core.Sources.ISourceRowCounter"/>'s
/// documented best-effort contract.
/// </summary>
internal static class SalesforceCountQuery
{
    /// <summary>Builds the <c>COUNT()</c> variant of <paramref name="soql"/>, or <c>null</c> when its shape isn't recognized.</summary>
    public static string? TryBuildCountQuery(string soql)
    {
        int selectIndex = IndexOfKeyword(soql, "SELECT", 0);
        if (selectIndex < 0)
            return null;

        var depth = 0;
        for (int i = selectIndex + "SELECT".Length; i < soql.Length; i++)
        {
            char c = soql[i];
            if (c == '(')
                depth++;
            else if (c == ')')
                depth--;
            else if (depth == 0 && IsKeywordAt(soql, i, "FROM"))
                return string.Concat(soql.AsSpan(0, selectIndex), "SELECT COUNT() ", soql.AsSpan(i));
        }

        return null;
    }

    private static int IndexOfKeyword(string text, string keyword, int startIndex)
    {
        for (int i = startIndex; i <= text.Length - keyword.Length; i++)
        {
            if (IsKeywordAt(text, i, keyword))
                return i;
        }

        return -1;
    }

    private static bool IsKeywordAt(string text, int index, string keyword)
    {
        if (index + keyword.Length > text.Length)
            return false;

        if (string.Compare(text, index, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;

        // '_' counts as a word character here (code-review finding): Salesforce field API names
        // routinely contain underscores (e.g. "Migrated_From_System__c"), so without this, "FROM"
        // embedded inside such a name — bounded by '_' on both sides, itself not alphanumeric — was
        // misdetected as the keyword, corrupting the rewrite for an otherwise perfectly valid query.
        bool boundaryBefore = index == 0 || !IsWordChar(text[index - 1]);
        bool boundaryAfter = index + keyword.Length == text.Length || !IsWordChar(text[index + keyword.Length]);
        return boundaryBefore && boundaryAfter;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
