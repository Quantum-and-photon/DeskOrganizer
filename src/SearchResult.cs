using System;

namespace DeskOrganizer;

public class SearchResult
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime ModifiedDate { get; set; }
    public double Score { get; set; }
    public MatchType MatchType { get; set; }
}
