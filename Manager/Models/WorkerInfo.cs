namespace Manager.Models;

public class WorkerInfo
{
    // mb it would be better to make some 
    public Guid WorkerId { get; init; }

    // I'm sure it this field is really needed
    public string WorkerName { get; init; } = "unknown worker";

    public string Url { get; init; } = "http://worker"; // ?

    // for health check
    public bool IsAlive { get; set; } = true;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public DateTime RegisteredAt { get; init; } = DateTime.UtcNow;
    public int FailedHealthChecks { get; set; } = 0;
}
