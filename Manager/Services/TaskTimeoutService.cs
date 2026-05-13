using Manager.Models;
using Manager.Services;
using Microsoft.Extensions.Options;

public class TaskTimeoutService : BackgroundService
{
    private readonly ILogger<TaskTimeoutService> _logger;
    private readonly IManagerService _managerService;
    private readonly TimeSpan _checkInterval;
    private readonly TimeSpan _taskTimeout;
    private readonly ManagerConfig _config;

    public TaskTimeoutService(
        ILogger<TaskTimeoutService> logger,
        IManagerService managerService,
        IOptions<ManagerConfig> config)
    {
        _config = config.Value;
        _logger = logger;
        _managerService = managerService;

        _checkInterval = _config.CheckInterval;
        _taskTimeout = _config.TaskTimeout;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckTimeouts(stoppingToken);
                await Task.Delay(_checkInterval, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking timeouts");
            }
        }
    }
    private async Task CheckTimeouts(CancellationToken stoppingToken)
    {
        var timedOutTasks = _managerService.GetTimedOutTasks(_taskTimeout);
        foreach (var task in timedOutTasks)
        {
            _logger.LogWarning("Task {TaskId} timed out, cancelling...", task.RequestId);
            await _managerService.CancelTask(task.RequestId);
        }
    }
}