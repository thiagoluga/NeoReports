namespace NeoReports.Sources.Common;

/// <summary>
/// Decides whether an ADO command's SQL text actually references a given <c>@name</c> parameter, so
/// the source can skip binding parameters the query does not use (some providers reject a command
/// that carries more parameters than its text names).
/// </summary>
internal static class AdoParameterReference
{
    /// <summary>
    /// Returns whether <paramref name="commandText"/> references <paramref name="token"/> (e.g.
    /// <c>@cursor</c>) as a whole parameter token — the match must not be immediately followed by
    /// another identifier character. This avoids a substring false-positive where <c>@page</c> is
    /// found inside <c>@pageSize</c> (or <c>@cursor</c> inside <c>@cursorId</c>), which would bind an
    /// unreferenced parameter and trip the very "too many parameters" provider error the skip exists
    /// to prevent.
    /// </summary>
    /// <param name="commandText">The SQL command text.</param>
    /// <param name="token">The parameter token including its prefix (e.g. <c>@cursor</c>).</param>
    public static bool IsReferenced(string commandText, string token)
    {
        var from = 0;
        while (true)
        {
            var index = commandText.IndexOf(token, from, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;

            var after = index + token.Length;
            // A parameter reference ends at the token; if the next character continues an identifier
            // (letter, digit or underscore) this was a longer parameter name that merely starts with
            // the token, not a real reference to it.
            if (after >= commandText.Length || !IsIdentifierChar(commandText[after]))
                return true;

            from = index + 1;
        }
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
