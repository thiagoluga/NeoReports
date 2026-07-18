using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace NeoReports.Sources.Xlsx;

/// <summary>
/// Resolves, once per workbook, whether a cell's style marks it as a date/time so a numeric cell
/// value can be interpreted as an Excel date serial rather than a plain number.
/// </summary>
/// <remarks>
/// Excel does not tag date cells with a data type — a date is just a number (days since 1899-12-30)
/// whose <em>style</em> carries a date <c>NumberFormatId</c>. A cell's <c>StyleIndex</c> points into
/// <c>Stylesheet.CellFormats</c>; the resolved <see cref="CellFormat.NumberFormatId"/> is either a
/// built-in id (a fixed set the spec reserves for dates) or a custom id (&gt;= 164) whose
/// <c>FormatCode</c> we inspect. We flatten all of that into a <c>bool[]</c> keyed by style index at
/// construction time so the per-cell hot path (<see cref="IsDateFormat"/>) is a single array read.
/// Its size is O(distinct cell styles), bounded by the workbook, not its row count — so it respects
/// the constant-memory-per-row rule.
/// </remarks>
internal sealed class NumberFormatCache
{
    // Built-in NumberFormatId values the OOXML/ECMA-376 spec reserves for date/time formats. These
    // never appear in the file's own <numFmts> custom-format table (they are spec-defined, not
    // per-file), so a cell using one with no matching NumberingFormat entry must still be classified
    // as a date by id alone:
    //  - 14-22: en-US short/long date, time, and date-time formats.
    //  - 27-36: CJK long-date and time-of-day formats (Japanese era, Chinese, Korean calendars) —
    //    a modern Excel with a CJK locale still writes bare cells using these ids today.
    //  - 45-47: elapsed/duration time formats (mm:ss, [h]:mm:ss, mm:ss.0).
    //  - 50-58: further CJK date/time formats, alternate locale-specific era variants.
    // This project's own writer's custom "yyyy-mm-dd" format comes through the FormatCode inspection
    // path below instead, since it is a custom (not built-in) format.
    private static readonly HashSet<uint> BuiltInDateFormatIds = new()
    {
        14, 15, 16, 17, 18, 19, 20, 21, 22,
        27, 28, 29, 30, 31, 32, 33, 34, 35, 36,
        45, 46, 47,
        50, 51, 52, 53, 54, 55, 56, 57, 58,
    };

    private readonly bool[] _isDateByStyleIndex;

    private NumberFormatCache(bool[] isDateByStyleIndex) => _isDateByStyleIndex = isDateByStyleIndex;

    /// <summary>
    /// Builds the date-style lookup for <paramref name="workbookPart"/>. A workbook with no styles
    /// yields a cache where nothing is a date (every numeric cell is a plain number).
    /// </summary>
    /// <param name="workbookPart">The workbook part to read the stylesheet from.</param>
    public static NumberFormatCache Build(WorkbookPart workbookPart)
    {
        ArgumentNullException.ThrowIfNull(workbookPart);

        var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet;
        var cellFormats = stylesheet?.CellFormats?.Elements<CellFormat>().ToArray();
        if (cellFormats is null || cellFormats.Length == 0)
            return new NumberFormatCache(Array.Empty<bool>());

        // Custom format codes, keyed by their NumberFormatId, for the codes-based date detection.
        var customFormatCodes = new Dictionary<uint, string>();
        var numberingFormats = stylesheet!.NumberingFormats?.Elements<NumberingFormat>();
        if (numberingFormats is not null)
        {
            foreach (var nf in numberingFormats)
            {
                if (nf.NumberFormatId?.Value is { } id && nf.FormatCode?.Value is { } code)
                    customFormatCodes[id] = code;
            }
        }

        var isDate = new bool[cellFormats.Length];
        for (var i = 0; i < cellFormats.Length; i++)
        {
            var numberFormatId = cellFormats[i].NumberFormatId?.Value;
            if (numberFormatId is not { } id)
                continue;

            if (BuiltInDateFormatIds.Contains(id))
            {
                isDate[i] = true;
            }
            else if (customFormatCodes.TryGetValue(id, out var code))
            {
                // Custom format (typically id >= 164), e.g. this project's writer's "yyyy-mm-dd".
                isDate[i] = IsDateFormatCode(code);
            }
        }

        return new NumberFormatCache(isDate);
    }

    /// <summary>
    /// True when the cell format at <paramref name="styleIndex"/> is a date/time format, so the cell's
    /// numeric value should be read as an Excel date serial. A null or out-of-range index is not a date.
    /// </summary>
    /// <param name="styleIndex">The cell's <c>StyleIndex</c> (<c>null</c> means style 0 / the default).</param>
    public bool IsDateFormat(uint? styleIndex)
    {
        var index = styleIndex ?? 0;
        return index < (uint)_isDateByStyleIndex.Length && _isDateByStyleIndex[index];
    }

    /// <summary>
    /// Heuristic: a format code is a date/time format when, after removing quoted literal text and
    /// non-elapsed-time bracketed sections, any of the date/time token letters (y, m, d, h, s) remains.
    /// Purely numeric/currency/percentage codes like <c>#,##0.00</c>, <c>0.00%</c>, or
    /// <c>[$USD-409]#,##0.00</c> contain none of those letters once their locale/color/condition
    /// brackets are stripped, so this has no false positives on the common non-date numeric formats.
    /// (<c>m</c> is shared between month and minute; either way it is a date/time format, which is all
    /// this method needs to decide.)
    /// </summary>
    /// <param name="formatCode">The number format code to classify.</param>
    internal static bool IsDateFormatCode(string? formatCode)
    {
        if (string.IsNullOrEmpty(formatCode))
            return false;

        var stripped = StripLiteralsAndNonElapsedBrackets(formatCode);
        foreach (var c in stripped)
        {
            switch (char.ToLowerInvariant(c))
            {
                case 'y':
                case 'm':
                case 'd':
                case 'h':
                case 's':
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Removes double-quoted literal substrings, backslash-escaped characters, and non-elapsed-time
    /// bracketed sections from a format code, so only genuine date/time tokens can trigger
    /// <see cref="IsDateFormatCode"/>.
    /// </summary>
    /// <remarks>
    /// A format code's <c>[...]</c> sections are not all equivalent: <c>[h]</c>/<c>[hh]</c>,
    /// <c>[m]</c>/<c>[mm]</c>, and <c>[s]</c>/<c>[ss]</c> are elapsed-time tokens (e.g. <c>[h]:mm:ss</c>
    /// for a duration past 24 hours) and must still count as date/time letters. Every other bracketed
    /// section — a locale/currency prefix (<c>[$USD-409]</c>), a color (<c>[Red]</c>), or a conditional
    /// threshold (<c>[&gt;=100]</c>) — is literal decoration, not a token, and is discarded whole; left
    /// unstripped, a currency code like <c>USD</c> or a color like <c>Red</c> would trip the date-token
    /// scan on its own trailing <c>d</c>, exactly the false positive this method exists to avoid.
    /// </remarks>
    private static string StripLiteralsAndNonElapsedBrackets(string formatCode)
    {
        var builder = new System.Text.StringBuilder(formatCode.Length);
        var inQuotes = false;
        for (var i = 0; i < formatCode.Length; i++)
        {
            var c = formatCode[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (inQuotes)
                continue;

            if (c == '\\')
            {
                i++; // skip the escaped character — it is a literal, not a token
                continue;
            }

            if (c == '[')
            {
                var close = formatCode.IndexOf(']', i + 1);
                if (close < 0)
                    break; // malformed/truncated bracket — nothing meaningful left to scan

                var inner = formatCode.Substring(i + 1, close - i - 1);
                if (IsElapsedTimeToken(inner))
                    builder.Append(inner);
                i = close;
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>True when a bracketed section's content is only elapsed-time letters (h, m, s).</summary>
    private static bool IsElapsedTimeToken(string bracketContent)
    {
        if (bracketContent.Length == 0)
            return false;

        foreach (var c in bracketContent)
        {
            if (char.ToLowerInvariant(c) is not ('h' or 'm' or 's'))
                return false;
        }

        return true;
    }
}
