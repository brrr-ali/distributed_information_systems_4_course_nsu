using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using Shared.DTO;
using Worker.Models;

namespace Worker.Services;

public class WorkerRegistrationService : BackgroundService
{
    private readonly HttpClient _httpClient;

    private readonly WorkerConfig _config;
    private readonly ILogger<WorkerRegistrationService> _logger;

    private readonly int _maxRetryAttempts = 5;

    public WorkerRegistrationService(HttpClient httpClient,
        WorkerConfig config,
        ILogger<WorkerRegistrationService> logger)
    {
        _config = config;
        _httpClient = httpClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var managerUrl = _config.ManagerUrl;
        var workerUrl = _config.WorkerUrl;

        var request = new WorkerRegisterRequest(
            _config.WorkerName,
            workerUrl
        );

        for (int attempt = 1; attempt <= _maxRetryAttempts; attempt++)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync(
                    $"{managerUrl}/internal/api/worker/register",
                    request,
                    stoppingToken
                );

                response.EnsureSuccessStatusCode();
                
                var result = await response.Content.ReadFromJsonAsync<WorkerRegisterResponse>();
                
                if (result == null)
                {
                    _logger.LogError("Failed to deserialize worker registration response");
                    return;
                }

                _config.WorkerId = result.WorkerId;

                _logger.LogInformation("Worker registered successfully");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker registration failed");
            }
        }
    }
}