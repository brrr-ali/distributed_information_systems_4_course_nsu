using System.Collections.Concurrent;

namespace Manager.Models;

public class CrackTaskState
{
    // mb lock needed
    public Guid RequestId { get; init; }

    required public string Hash { get; init; }

    public long TotalCombinations { get; set; }

    public int MaxLength { get; set; }

    public long CheckedCombinations { get; set; }

    public List<string> FoundWords { get; set; } = new();

    public CrackStatus Status { get; set; } = CrackStatus.IN_PROGRESS;

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public List<Guid> AssignedWorkers { get; set; } = new();

    public ConcurrentDictionary<Guid, WorkerProgress> WorkersProgress { get; set; } = new();
    
    public double TotalProgress => AssignedWorkers.Count > 0
        ? WorkersProgress.Values.Average(w => w.ProgressPercent)
        : 0;
}