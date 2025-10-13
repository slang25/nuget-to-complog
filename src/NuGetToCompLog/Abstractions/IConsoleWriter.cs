namespace NuGetToCompLog.Abstractions;

/// <summary>
/// Service for writing console output.
/// </summary>
public interface IConsoleWriter
{
    /// <summary>
    /// Writes a line of markup text.
    /// </summary>
    void MarkupLine(string markup);
    
    /// <summary>
    /// Writes a blank line.
    /// </summary>
    void WriteLine();
    
    /// <summary>
    /// Writes an exception with formatting.
    /// </summary>
    void WriteException(Exception exception);
    
    /// <summary>
    /// Writes a panel with a header and content.
    /// </summary>
    void WritePanel(string header, string content, string? borderColor = null);
    
    /// <summary>
    /// Writes a tree structure.
    /// </summary>
    void WriteTree(string rootLabel, Dictionary<string, List<string>> nodes);
    
    /// <summary>
    /// Writes a table with headers and rows.
    /// </summary>
    void WriteTable(string[] headers, List<string[]> rows);
    
    /// <summary>
    /// Executes an action with a status/spinner display.
    /// </summary>
    Task ExecuteWithStatusAsync(string status, Func<Task> action);
}
