namespace Manager.Models;

public class WorkerInfo
{
    public Guid WorkerId { get; init; }
    public string WorkerName { get; init; } = "unknown worker";
    public string Url { get; init; } = "http://worker";
    public bool IsAlive { get; set; } = true;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public DateTime RegisteredAt { get; init; } = DateTime.UtcNow;
    public int FailedHealthChecks { get; set; } = 0;
}
