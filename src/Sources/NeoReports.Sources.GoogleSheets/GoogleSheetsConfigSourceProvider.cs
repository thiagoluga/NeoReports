using System.Text.Json;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.GoogleSheets;

/// <summary>
/// Config-driven Google Sheets source for the dynamic path (<c>type: "googlesheets"</c>, ADR D66).
/// Reads its settings from the source <c>properties</c> (<see cref="GoogleSheetsConfigProperties"/>)
/// — required <c>spreadsheetId</c>/<c>sheet</c>/<c>apiKey</c>/<c>firstColumn</c>/<c>lastColumn</c>,
/// plus optional <c>headerRow</c>. Produces positional <see cref="ReportRecord"/>s matched against
/// the report's own declared schema by header column name
/// (<see cref="GoogleSheetsReportRecordMaterializer"/>).
/// </summary>
public sealed class GoogleSheetsConfigSourceProvider : IConfigSourceProvider
{
    /// <inheritdoc />
    public string Type => "googlesheets";

    /// <inheritdoc />
    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(schema);

        string spreadsheetId = GoogleSheetsConfigProperties.RequireSpreadsheetId(source.Properties);
        string sheet = GoogleSheetsConfigProperties.RequireSheet(source.Properties);
        GoogleSheetsSourceOptions options = GoogleSheetsConfigProperties.ReadOptions(source.Properties);
        HttpClient client = HttpClients.ResolveFrom(services);

        ReportRecord Materialize(IReadOnlyDictionary<string, int> ordinalByName, JsonElement[] row) =>
            GoogleSheetsReportRecordMaterializer.Materialize(ordinalByName, row, schema);

        return new GoogleSheetsBatchSource<ReportRecord>(client, spreadsheetId, sheet, options, schema, Materialize);
    }
}
