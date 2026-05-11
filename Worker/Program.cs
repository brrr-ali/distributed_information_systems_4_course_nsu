using Worker.Models;
using Worker.Services;
using Polly;
using Polly.Extensions.Http;


var builder = WebApplication.CreateBuilder(args);

var config = new WorkerConfig
{
    WorkerName = Environment.GetEnvironmentVariable("WORKER_NAME") ?? "MyPrettyName",
    Alphabet = Environment.GetEnvironmentVariable("ALPHABET") ?? "abcdefghijklmnopqrstuvwxyz0123456789",
    ManagerUrl = Environment.GetEnvironmentVariable("MANAGER_SERVICE_URL") ?? "http://localhost:5178",
    WorkerUrl = Environment.GetEnvironmentVariable("WORKER_SERVICE_URL") ?? "http://localhost:5179",
};

// Register Configuration
builder.Services.AddSingleton(config);

// Controllers
builder.Services.AddControllers();

// Swagger 
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();


// Services
builder.Services.AddHostedService<WorkerRegistrationService>();
builder.Services.AddSingleton<IHashCrackService, HashCrackService>();


builder.Services.AddHttpClient<WorkerRegistrationService>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
    })
    .AddPolicyHandler(GetRetryPolicy());

builder.Services.AddHttpClient<HashCrackService>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.BaseAddress = new Uri(config.ManagerUrl);
    })
    .AddPolicyHandler(GetRetryPolicy());


var app = builder.Build();


// Swagger
app.UseSwagger();
// app.UseSwaggerUI();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Worker API V1");
    
});


app.MapControllers();

app.Run();


static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
{
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        .WaitAndRetryAsync(3, retryAttempt =>
            TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));
}