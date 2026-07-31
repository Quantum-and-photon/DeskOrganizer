using System;

namespace DeskOrganizer;

public enum IndexStatus
{
    Idle,
    Indexing,
    Complete,
    Stopped,
    Error
}

public class IndexProgressEventArgs : EventArgs
{
    public int CurrentCount { get; }
    public int TotalFiles { get; }
    public IndexStatus Status { get; }

    public IndexProgressEventArgs(int currentCount, int totalFiles, IndexStatus status)
    {
        CurrentCount = currentCount;
        TotalFiles = totalFiles;
        Status = status;
    }
}
