namespace Manager.Models;

public class WorkerProgress
{
    public Guid WorkerId { get; init; }
    public string WorkerName { get; init; } = "unknown";
    
    public long RangeStart { get; set; }
    public long RangeEnd { get; set; }
    public long CheckedCount { get; set; }
    public long CurrentIndex { get; set; }  
    public DateTime LastReportTime { get; set; } = DateTime.UtcNow;
    
    public bool IsCompleted { get; set; } = false;
    
    public double ProgressPercent => RangeEnd - RangeStart > 0 
        ? 100.0 * CheckedCount / (RangeEnd - RangeStart + 1) 
        : 0;
}