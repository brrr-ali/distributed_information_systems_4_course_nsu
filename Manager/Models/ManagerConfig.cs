namespace Manager.Models;

public class ManagerConfig
{
    public int WorkerNumber { get; set; } = 3;
    public string Alphabet { get; set; } = "abcdefghijklmnopqrstuvwxyz0123456789";
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan TaskTimeout { get; set; } = TimeSpan.FromMinutes(2);
    
}