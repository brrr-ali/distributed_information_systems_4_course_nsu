using Manager.Models;
using Manager.Services;
using Microsoft.Extensions.Options;


public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        var config = new ManagerConfig
        {
            WorkerNumber = int.Parse(Environment.GetEnvironmentVariable("WORKER_NUMBER") ?? "3"),
            Alphabet = Environment.GetEnvironmentVariable("ALPHABET") ?? "abcdefghijklmnopqrstuvwxyz0123456789",
            CheckInterval = TimeSpan.FromSeconds(
                int.Parse(Environment.GetEnvironmentVariable("CHECK_INTERVAL_SEC") ?? "60")),
            TaskTimeout = TimeSpan.FromMinutes(
                int.Parse(Environment.GetEnvironmentVariable("TASK_TIMEOUT_MIN") ?? "2"))
        };

        // Register Configuration
        builder.Services.AddSingleton(Options.Create(config));

        // Controllers
        builder.Services.AddControllers();

        // Swagger
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();


        // Services
        builder.Services.AddSingleton<IManagerService, ManagerService>();
        builder.Services.AddHostedService<WorkerHealthCheckService>();
        builder.Services.AddHostedService<TaskTimeoutService>();

        builder.Services.AddHttpClient();

        var app = builder.Build();

        // Swagger
        app.UseSwagger();
        app.UseSwaggerUI();

        app.UseStaticFiles();
        app.MapControllers();

        app.Run();
    }
}