using System.Collections.Concurrent;
using Manager.Models;
using Microsoft.Extensions.Options;

namespace Manager.Services;

public class WorkerHealthCheckService : BackgroundService
{
    private readonly ILogger<WorkerHealthCheckService> _logger;
    private readonly IManagerService _managerService;
    private readonly IHttpClientFactory _httpClientFactory; 
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);
    private readonly int _maxFailedChecks = 3;

    public WorkerHealthCheckService(
        ILogger<WorkerHealthCheckService> logger,
        IManagerService managerService,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _managerService = managerService;
        _httpClientFactory = httpClientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker Health Check service started");

        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckWorkersHealth(stoppingToken);
                await Task.Delay(_checkInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in health check");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private async Task CheckWorkersHealth(CancellationToken stoppingToken)
    {
        var workers = _managerService.GetAllWorkers();
        
        foreach (var worker in workers)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(_timeout);

                var client = _httpClientFactory.CreateClient();
                var healthUrl = $"{worker.Url}/internal/api/worker/hash/crack/health";
                _logger.LogInformation("Checking health for {WorkerName} at {Url}", 
                    worker.WorkerName, healthUrl);
                var response = await client.GetAsync(healthUrl, cts.Token);
                
                
                _logger.LogInformation("Health check response from {WorkerName}: {StatusCode}", 
                    worker.WorkerName, response.StatusCode);
                bool isAlive = response.IsSuccessStatusCode;
                
                _managerService.UpdateWorkerHealth(worker.WorkerId, isAlive, resetFailedChecks: false);
                

                if (!isAlive && worker.IsAlive)
                {
                    _logger.LogWarning("Worker {WorkerName} ({WorkerUrl}) is not responding", 
                        worker.WorkerName, worker.Url);
                }
                else if (isAlive && !worker.IsAlive)
                {
                    _logger.LogInformation("Worker {WorkerName} is back online", worker.WorkerName);
                    _managerService.UpdateWorkerHealth(worker.WorkerId, false, resetFailedChecks: true);
        
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Health check failed for worker {WorkerName}", worker.WorkerName);
                _managerService.UpdateWorkerHealth(worker.WorkerId, false, resetFailedChecks: false);
            }


        }

        _managerService.RemoveDeadWorkers(_maxFailedChecks);

        var remainingWorkers = _managerService.GetAllWorkers();
        _logger.LogInformation("After cleanup: {Count} workers remaining", remainingWorkers.Count);
    
    }
}