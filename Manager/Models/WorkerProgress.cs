namespace Manager.Models;

public class WorkerProgress
{
    public Guid WorkerId { get; init; }
    public string WorkerName { get; init; } = "unknown";
    
    public double RangeStart { get; set; }
    public double RangeEnd { get; set; }
    public double CheckedCount { get; set; }
    public DateTime LastReportTime { get; set; } = DateTime.UtcNow;
    
    public bool IsCompleted { get; set; } = false;
    
    public double ProgressPercent => RangeEnd - RangeStart > 0 
        ? 100.0 * CheckedCount / (RangeEnd - RangeStart + 1) 
        : 0;
}