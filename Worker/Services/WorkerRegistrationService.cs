using Shared.DTO;
using Worker.Models;

namespace Worker.Services;

public class WorkerRegistrationService : BackgroundService
{
    private readonly HttpClient _httpClient;
    private readonly WorkerConfig _config;
    private readonly ILogger<WorkerRegistrationService> _logger;

    private const int RetryDelaySeconds = 5;
    private const int HeartbeatSeconds  = 10;

    public WorkerRegistrationService(
        HttpClient httpClient,
        WorkerConfig config,
        ILogger<WorkerRegistrationService> logger)
    {
        _httpClient = httpClient;
        _config     = config;
        _logger     = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Первичная регистрация — крутимся пока не зарегистрируемся
        await RegisterWithRetry(stoppingToken);

        // Периодически проверяем жив ли менеджер.
        // Если он перезапустился — перерегистрируемся, иначе воркер будет простаивать.
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(HeartbeatSeconds), stoppingToken);

            if (!await IsRegisteredAtManager(stoppingToken))
            {   
                _logger.LogWarning("Worker is not registered at manager (manager restarted?), re-registering...");
                await RegisterWithRetry(stoppingToken);
            }
        }
    }

    private async Task RegisterWithRetry(CancellationToken stoppingToken)
    {
        var request = new WorkerRegisterRequest(_config.WorkerName, _config.WorkerUrl);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync(
                    $"{_config.ManagerUrl}/internal/api/worker/register",
                    request,
                    stoppingToken);

                response.EnsureSuccessStatusCode();

                var result = await response.Content
                    .ReadFromJsonAsync<WorkerRegisterResponse>(stoppingToken);

                if (result is null)
                {
                    _logger.LogError(
                        "Failed to deserialize registration response, retrying...");
                    await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds), stoppingToken);
                    continue;
                }

                _config.WorkerId = result.WorkerId;
                _logger.LogInformation(
                    "Worker registered successfully with id {WorkerId}", result.WorkerId);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Registration failed, retrying in {Delay}s...", RetryDelaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds), stoppingToken);
            }
        }
    }

    private async Task<bool> IsRegisteredAtManager(CancellationToken stoppingToken)
    {
        // Если WorkerId пустой — точно не зарегистрированы
        if (_config.WorkerId == Guid.Empty)
            return false;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var response = await _httpClient.GetAsync(
                $"{_config.ManagerUrl}/internal/api/worker/register/{_config.WorkerId}",
                cts.Token);

            return response.IsSuccessStatusCode;
        }
        catch
        {
            // Менеджер недоступен — вернём false, уйдём в RegisterWithRetry
            return false;
        }
    }
}