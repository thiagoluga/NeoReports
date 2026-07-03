namespace NeoReports.UI.Services;

/// <summary>Shared display helpers for artifact file names/sizes (job completed, dashboard).</summary>
public static class FileFormatting
{
    /// <summary>Maps a file extension to a Tabler icon name.</summary>
    public static string Icon(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".csv" => "table",
        ".xlsx" => "file-spreadsheet",
        ".json" => "braces",
        ".zip" => "file-alert",
        _ => "file",
    };

    /// <summary>Renders a byte count as "B"/"KB"/"MB", matching the precision used across the UI.</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.##} MB",
        >= 1024 => $"{bytes / 1024.0:0.##} KB",
        _ => $"{bytes} B",
    };
}
