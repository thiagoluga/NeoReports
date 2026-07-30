using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml.Spreadsheet;

namespace NeoReports.Formats.Xlsx;

/// <summary>A worksheet part staged on a temp file, ready to be copied into the assembled package.</summary>
/// <param name="Name">The sheet name written into the workbook part.</param>
/// <param name="TempPath">The temp file holding the streamed <c>&lt;worksheet&gt;</c> XML.</param>
internal readonly record struct XlsxSheetPart(string Name, string TempPath);

/// <summary>
/// Assembles an <c>.xlsx</c> (OPC package) by hand with <see cref="ZipArchive"/> in
/// <see cref="ZipArchiveMode.Create"/> mode, streaming each entry straight to the destination stream.
/// </summary>
/// <remarks>
/// This deliberately bypasses <c>System.IO.Packaging</c>: <c>SpreadsheetDocument.Create</c> routes
/// every part through <c>ZipPackage</c>, which opens the container in <c>ZipArchiveMode.Update</c> and
/// buffers each entry's entire uncompressed content in memory until the document is disposed — so the
/// worksheet part grows O(rows) in RAM regardless of where the final zip lands. <c>ZipArchive</c> in
/// Create mode instead deflates each entry to the output as it is written and supports a write-only,
/// non-seekable stream, so no part is ever fully buffered. Worksheet XML is streamed to per-sheet temp
/// files up front (constant RAM) and copied into their entries here; the small fixed parts
/// (content-types, rels, workbook, styles) are tiny and do not scale with rows.
/// </remarks>
internal static class XlsxOpcPackage
{
    private const string ContentTypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string PackageRelsNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string OfficeDocRelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private const string RelOfficeDocument = OfficeDocRelNs + "/officeDocument";
    private const string RelWorksheet = OfficeDocRelNs + "/worksheet";
    private const string RelStyles = OfficeDocRelNs + "/styles";

    private const string CtWorkbook = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
    private const string CtStyles = "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml";
    private const string CtWorksheet = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";
    private const string CtRels = "application/vnd.openxmlformats-package.relationships+xml";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Returns a unique temp-file path for a worksheet part being streamed.</summary>
    public static string CreateTempPath() =>
        Path.Join(Path.GetTempPath(), $"neoreports-xlsx-{Guid.NewGuid():N}.tmp");

    /// <summary>
    /// Opens a read/write temp file for a worksheet part. On non-Windows the file is created 0600
    /// (owner read/write only), matching the hardening applied to the streamed zip in the aspnetcore
    /// download path.
    /// </summary>
    public static FileStream CreateTempFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(
                path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, bufferSize: 81920, useAsync: false);
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = 81920,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        return new FileStream(path, options);
    }

    /// <summary>
    /// Writes the whole package to <paramref name="output"/>: the fixed parts by hand, then each staged
    /// worksheet temp file copied into <c>xl/worksheets/sheetN.xml</c>. The output stream is left open
    /// (the caller owns it).
    /// </summary>
    public static async Task AssembleAsync(
        Stream output, Stylesheet stylesheet, IReadOnlyList<XlsxSheetPart> sheets, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        WriteContentTypes(archive, sheets.Count);
        WriteRootRelationships(archive);
        WriteWorkbook(archive, sheets);
        WriteWorkbookRelationships(archive, sheets.Count);
        WriteStyles(archive, stylesheet);

        for (var i = 0; i < sheets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CopyWorksheetAsync(archive, i + 1, sheets[i].TempPath, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Best-effort deletion of a staged worksheet temp file; never throws.</summary>
    public static void TryDelete(string? tempPath)
    {
        if (string.IsNullOrEmpty(tempPath))
            return;

        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch (IOException)
        {
            // A leftover temp file is harmless; never let cleanup mask the real result.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }

    private static void WriteContentTypes(ZipArchive archive, int sheetCount)
    {
        using XmlWriter xml = OpenEntry(archive, "[Content_Types].xml");
        xml.WriteStartDocument(standalone: true);
        xml.WriteStartElement("Types", ContentTypesNs);

        WriteDefault(xml, "rels", CtRels);
        WriteDefault(xml, "xml", "application/xml");
        WriteOverride(xml, "/xl/workbook.xml", CtWorkbook);
        WriteOverride(xml, "/xl/styles.xml", CtStyles);
        for (var i = 1; i <= sheetCount; i++)
            WriteOverride(xml, $"/xl/worksheets/sheet{i}.xml", CtWorksheet);

        xml.WriteEndElement();
        xml.WriteEndDocument();
    }

    private static void WriteRootRelationships(ZipArchive archive)
    {
        using XmlWriter xml = OpenEntry(archive, "_rels/.rels");
        xml.WriteStartDocument(standalone: true);
        xml.WriteStartElement("Relationships", PackageRelsNs);
        WriteRelationship(xml, "rId1", RelOfficeDocument, "xl/workbook.xml");
        xml.WriteEndElement();
        xml.WriteEndDocument();
    }

    private static void WriteWorkbook(ZipArchive archive, IReadOnlyList<XlsxSheetPart> sheets)
    {
        using XmlWriter xml = OpenEntry(archive, "xl/workbook.xml");
        xml.WriteStartDocument(standalone: true);
        xml.WriteStartElement("workbook", SpreadsheetNs);
        xml.WriteAttributeString("xmlns", "r", null, OfficeDocRelNs);
        xml.WriteStartElement("sheets", SpreadsheetNs);

        for (var i = 0; i < sheets.Count; i++)
        {
            xml.WriteStartElement("sheet", SpreadsheetNs);
            xml.WriteAttributeString("name", sheets[i].Name);
            xml.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("r", "id", OfficeDocRelNs, "rId" + (i + 1).ToString(CultureInfo.InvariantCulture));
            xml.WriteEndElement();
        }

        xml.WriteEndElement(); // sheets
        xml.WriteEndElement(); // workbook
        xml.WriteEndDocument();
    }

    private static void WriteWorkbookRelationships(ZipArchive archive, int sheetCount)
    {
        using XmlWriter xml = OpenEntry(archive, "xl/_rels/workbook.xml.rels");
        xml.WriteStartDocument(standalone: true);
        xml.WriteStartElement("Relationships", PackageRelsNs);

        for (var i = 1; i <= sheetCount; i++)
            WriteRelationship(xml, "rId" + i.ToString(CultureInfo.InvariantCulture), RelWorksheet, $"worksheets/sheet{i}.xml");
        WriteRelationship(xml, "rId" + (sheetCount + 1).ToString(CultureInfo.InvariantCulture), RelStyles, "styles.xml");

        xml.WriteEndElement();
        xml.WriteEndDocument();
    }

    private static void WriteStyles(ZipArchive archive, Stylesheet stylesheet)
    {
        ZipArchiveEntry entry = archive.CreateEntry("xl/styles.xml", CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, Utf8NoBom);
        // The stylesheet is tiny (one entry per distinct format, never per row); serializing its
        // OuterXml keeps it self-contained (its own namespace) with an explicit declaration.
        writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n");
        writer.Write(stylesheet.OuterXml);
    }

    private static async Task CopyWorksheetAsync(
        ZipArchive archive, int sheetNumber, string tempPath, CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry($"xl/worksheets/sheet{sheetNumber}.xml", CompressionLevel.Optimal);
        Stream entryStream = entry.Open();
        await using (entryStream.ConfigureAwait(false))
        {
            // Declaration-form await-using so the dispose is statically visible (CodeQL does not track a
            // FileStream through a ConfigureAwait(false) wrapper); dropping ConfigureAwait on this leaf
            // local dispose is fine.
            await using FileStream source = new FileStream(
                tempPath, FileMode.Open, FileAccess.Read, FileShare.None, bufferSize: 81920, useAsync: true);
            await source.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
        }
    }

    private static XmlWriter OpenEntry(ZipArchive archive, string entryName)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        Stream stream = entry.Open();
        var settings = new XmlWriterSettings { Encoding = Utf8NoBom, CloseOutput = true };
        return XmlWriter.Create(stream, settings);
    }

    private static void WriteDefault(XmlWriter xml, string extension, string contentType)
    {
        xml.WriteStartElement("Default", ContentTypesNs);
        xml.WriteAttributeString("Extension", extension);
        xml.WriteAttributeString("ContentType", contentType);
        xml.WriteEndElement();
    }

    private static void WriteOverride(XmlWriter xml, string partName, string contentType)
    {
        xml.WriteStartElement("Override", ContentTypesNs);
        xml.WriteAttributeString("PartName", partName);
        xml.WriteAttributeString("ContentType", contentType);
        xml.WriteEndElement();
    }

    private static void WriteRelationship(XmlWriter xml, string id, string type, string target)
    {
        xml.WriteStartElement("Relationship", PackageRelsNs);
        xml.WriteAttributeString("Id", id);
        xml.WriteAttributeString("Type", type);
        xml.WriteAttributeString("Target", target);
        xml.WriteEndElement();
    }
}
