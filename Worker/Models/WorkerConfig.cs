namespace Worker.Models;

public class WorkerConfig
{
    public string WorkerName { get; init; } = "unknown name";
    public string Alphabet { get; init; } = "abcdefghijklmnopqrstuvwxyz0123456789";
    public string WorkerUrl { get; set; } = "http://worker";
    public string ManagerUrl { get; set; } = "http://manager";
    // actually, it shouldn't be here, but I just want make it fast...
    public Guid WorkerId { get; set; }
}