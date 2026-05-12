using System.Collections.Concurrent;
using Manager.DTO;
using Manager.Models;
using Microsoft.Extensions.Options;
using Shared.DTO;

namespace Manager.Services;

public class ManagerService : IManagerService
{
    private ConcurrentDictionary<Guid, CrackTaskState> _taskStates = new();
    private readonly HttpClient _httpClient;
    private readonly ILogger<ManagerService> _logger;
    private readonly ManagerConfig _config;
    
    private readonly ConcurrentDictionary<Guid, WorkerInfo> _workers = new();

    public ManagerService(
        IOptions<ManagerConfig> config,
        IHttpClientFactory httpClientFactory,
        ILogger<ManagerService> logger)
    {
        _config = config.Value;
        _httpClient = httpClientFactory.CreateClient();
        _logger = logger;
    }

    public async Task<Guid> RegisterWorker(WorkerRegisterRequest request)
    {
        var id = Guid.NewGuid();
        var worker = new WorkerInfo
        {
            WorkerId = id,
            WorkerName = request.WorkerName,
            Url = request.Url
        };

        _workers[id] = worker;

        _logger.LogInformation(
            "Worker registered {WorkerId} with {WorkerName} at {Url}",
            worker.WorkerId,
            worker.WorkerName,
            worker.Url
        );

        await TryDispatchPendingTasks();

        return id;
    }

    public async Task<Guid> CreateCrackTask(ManagerCrackRequest request)
    {
        var id = Guid.NewGuid();

        _logger.LogInformation("Creating crack task {RequestId}", id);

        var total = CalculateTotalCombinations(request.MaxLength);

        _logger.LogInformation("Task {RequestId}: total combinations = {Total}", id, total);

        var task = new CrackTaskState
        {
            RequestId = id,
            Hash = request.Hash,
            TotalCombinations = total,
            CheckedCombinations = 0,
            Status = CrackStatus.PENDING,
            MaxLength = request.MaxLength,
            FoundWords = new List<string>(),
            StartedAt = DateTime.UtcNow,
        };

        _taskStates[id] = task;
        _logger.LogInformation("Task {RequestId} saved to dictionary, workers count = {WorkerCount}", id, _workers.Count);


        if (_workers.Count > 0)
        {
            _logger.LogInformation("Dispatching task {RequestId} to {WorkerCount} workers", id, _workers.Count);
            await DispatchTasks(id, request.Hash, request.MaxLength, total);
            task.Status = CrackStatus.IN_PROGRESS;
            _logger.LogInformation("Task {RequestId} dispatched and set to IN_PROGRESS", id);
        }
            else
        {
            _logger.LogWarning("No workers available for task {RequestId}, status PENDING", id);
        }

        return id;
    }

    public ManagerStatusResponse GetStatus(Guid requestId)
    {
        if (requestId == Guid.Empty)
        {
            _logger.LogError("Empty requestId provided");
            throw new ArgumentException("RequestId cannot be empty", nameof(requestId));
        }

        if (!_taskStates.TryGetValue(requestId, out var task))
        {
            _logger.LogError("Task {RequestId} not found", requestId);
            throw new KeyNotFoundException($"Task {requestId} not found");
        }

        _logger.LogInformation("Task {RequestId} status: {Status}, checked: {Checked}/{Total}", 
            requestId, task.Status, task.CheckedCombinations, task.TotalCombinations);
        
        int progress = (int)(100.0 * task.CheckedCombinations / task.TotalCombinations);

        var status = task.Status switch
        {
            CrackStatus.PENDING => "IN_PROGRESS",
            CrackStatus.IN_PROGRESS => "IN_PROGRESS",
            CrackStatus.READY => "READY",
            CrackStatus.ERROR => "ERROR",
            _ => "IN_PROGRESS"
        };
        return new ManagerStatusResponse(
            status,
            progress,
            task.Status == CrackStatus.READY ? task.FoundWords : null
        );
    }

    private long CalculateTotalCombinations(int maxLength)
    {
        long baseLen = _config.Alphabet.Length;
        long total = 0;
        for (int i = 1; i <= maxLength; i++)
            total += (long)Math.Pow(baseLen, i);
        return total;
    }

    private async Task DispatchTasks(Guid requestId, string hash, int maxLength, long total)
    {
        var aliveWorkers = _workers.Values.Where(w => w.IsAlive).ToList();
        if (!aliveWorkers.Any()) return;

        int partCount = aliveWorkers.Count;

        for (int i = 0; i < partCount; i++)
        {
            var worker = aliveWorkers[i];
            var task = new WorkerTaskRequest(requestId, hash, maxLength, i, partCount);
            // StartIndex/EndIndex не передаём — воркер считает сам

            var taskState = _taskStates[requestId];
            taskState.AssignedWorkers.Add(worker.WorkerId);

            // RangeStart/RangeEnd — заглушки, обновятся после первого отчёта
            taskState.WorkersProgress[worker.WorkerId] = new WorkerProgress
            {
                WorkerId = worker.WorkerId,
                WorkerName = worker.WorkerName,
                RangeStart = 0,
                RangeEnd = 0,
                CheckedCount = 0,
                LastReportTime = DateTime.UtcNow
            };

            try
            {
                await _httpClient.PostAsJsonAsync(
                    $"{worker.Url}/internal/api/worker/hash/crack/task", task);
                _logger.LogInformation("Dispatched part {Part}/{Count} to {Worker}",
                    i, partCount, worker.WorkerName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dispatch to {Worker}", worker.WorkerName);
            }
        }
    }
    public void ProcessWorkerResult(WorkerTaskResponse response)
    {
        _logger.LogInformation(
            "PROCESS RESULT: task={TaskId}, batch={Batch}, currentIndex={Index}, foundWords=[{Words}]",
            response.TaskRequestId, response.CheckedCount,
            response.CurrentIndex, string.Join(",", response.FoundWords));

        if (!_taskStates.TryGetValue(response.TaskRequestId, out var task))
            return;

        lock (task)
        {
            if (task.WorkersProgress.TryGetValue(response.WorkerId, out var wp))
            {
                // Обновляем реальный диапазон из первого отчёта воркера (заглушки были 0)
                if (wp.RangeEnd == 0)
                {
                    wp.RangeStart = response.RangeStart;
                    wp.RangeEnd   = response.RangeEnd;
                    _logger.LogInformation(
                        "Worker {Name} range set to [{Start}-{End}]",
                        wp.WorkerName, wp.RangeStart, wp.RangeEnd);
                }

                wp.CheckedCount   += response.CheckedCount;   // дельта батча
                wp.CurrentIndex    = response.CurrentIndex;
                wp.LastReportTime  = DateTime.UtcNow;

                if (response.IsRequestDone)
                {
                    wp.IsCompleted = true;
                    _logger.LogInformation("Worker {Name} completed its range for task {TaskId}",
                        wp.WorkerName, task.RequestId);
                }
            }
            else
            {
                _logger.LogWarning("No progress record for worker {WorkerId} in task {TaskId}",
                    response.WorkerId, response.TaskRequestId);
            }

            task.CheckedCombinations += response.CheckedCount;

            foreach (var word in response.FoundWords)
                if (!task.FoundWords.Contains(word))
                {
                    task.FoundWords.Add(word);
                    _logger.LogInformation("ADDED WORD '{Word}' to task {TaskId}", word, task.RequestId);
                }

            if (task.WorkersProgress.Values.All(w => w.IsCompleted) ||
                task.CheckedCombinations >= task.TotalCombinations)
            {
                task.Status      = CrackStatus.READY;
                task.CompletedAt = DateTime.UtcNow;
                _logger.LogInformation("TASK {TaskId} COMPLETED! Found {Count} words",
                    task.RequestId, task.FoundWords.Count);
            }
        }
    }


    private async Task ReassignTask(CrackTaskState task, WorkerInfo deadWorker)
    {
        if (!task.WorkersProgress.TryGetValue(deadWorker.WorkerId, out var dead)) return;

        // CurrentIndex — последний проверенный, продолжаем со следующего
        long remainingStart = dead.CurrentIndex + 1;
        long remainingEnd   = dead.RangeEnd;

        if (remainingStart > remainingEnd)
        {
            _logger.LogInformation("Worker {Name} finished its range before dying, nothing to reassign",
                deadWorker.WorkerName);
            task.WorkersProgress.TryRemove(deadWorker.WorkerId, out _);
            return;
        }

        var aliveWorkers = _workers.Values.Where(w => w.IsAlive).ToList();
        if (!aliveWorkers.Any())
        {
            _logger.LogError("No alive workers to reassign task {TaskId}", task.RequestId);
            task.Status = CrackStatus.ERROR;
            return;
        }

        long remaining  = remainingEnd - remainingStart + 1;
        long chunkSize  = remaining / aliveWorkers.Count;
        long current    = remainingStart;

        for (int i = 0; i < aliveWorkers.Count; i++)
        {
            var worker   = aliveWorkers[i];
            long newStart = current;
            long newEnd   = i == aliveWorkers.Count - 1 ? remainingEnd : current + chunkSize - 1;

            // переназначение — передаём явный диапазон
            var req = new WorkerTaskRequest(
                task.RequestId, task.Hash, task.MaxLength,
                PartNumber: 0, PartCount: 1,
                StartIndex: newStart, EndIndex: newEnd
            );

            try
            {
                await _httpClient.PostAsJsonAsync(
                    $"{worker.Url}/internal/api/worker/hash/crack/task", req);

                task.AssignedWorkers.Add(worker.WorkerId);
                task.WorkersProgress[worker.WorkerId] = new WorkerProgress
                {
                    WorkerId = worker.WorkerId,
                    WorkerName = worker.WorkerName,
                    RangeStart = newStart,
                    RangeEnd   = newEnd,
                    CheckedCount = 0,
                    CurrentIndex = newStart,
                    LastReportTime = DateTime.UtcNow
                };

                _logger.LogInformation("Reassigned [{Start}-{End}] to {Worker}",
                    newStart, newEnd, worker.WorkerName);

                current = newEnd + 1;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reassign to {Worker}", worker.WorkerName);
            }
        }

        task.WorkersProgress.TryRemove(deadWorker.WorkerId, out _);
        task.AssignedWorkers.Remove(deadWorker.WorkerId);
    }

    private async Task TryDispatchPendingTasks()
    {
        foreach (var task in _taskStates.Values)
        {
            if (task.Status != CrackStatus.PENDING)
                continue;

            _logger.LogInformation(
                "Dispatching pending task {TaskId}",
                task.RequestId
            );

            try
            {
                await DispatchTasks(
                    task.RequestId,
                    task.Hash,
                    task.MaxLength,
                    task.TotalCombinations
                );

                task.Status = CrackStatus.IN_PROGRESS;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dispatch pending task");
            }
        }
    }



    public List<WorkerInfo> GetAllWorkers()
    {
        return _workers.Values.ToList();
    }

    public void UpdateWorkerHealth(Guid workerId, bool isAlive, bool resetFailedChecks = true)
    {
        if (_workers.TryGetValue(workerId, out var worker))
        {
            worker.IsAlive = isAlive;
            worker.LastSeen = DateTime.UtcNow;
            
            if (isAlive)
            {
                worker.FailedHealthChecks = 0;
            }
            else if (!resetFailedChecks)
            {
                worker.FailedHealthChecks++;
            }
            
            _logger.LogDebug("Worker {WorkerName} health updated: IsAlive={IsAlive}, FailedChecks={FailedChecks}", 
                worker.WorkerName, isAlive, worker.FailedHealthChecks);
        }
    }


    public void CheckTaskTimeouts(TimeSpan timeout)
    {
        var now = DateTime.UtcNow;
        var timedOutTasks = _taskStates.Values
            .Where(t => t.Status == CrackStatus.IN_PROGRESS 
                        && t.StartedAt.HasValue 
                        && now - t.StartedAt.Value > timeout)
            .ToList();

        foreach (var task in timedOutTasks)
        {
            lock (task)
            {
                task.Status = CrackStatus.ERROR;
                _logger.LogWarning("Task {TaskId} timed out", task.RequestId);
            }
        }
    }

    public async Task CancelTask(Guid taskId)
    {   
        _logger.LogInformation("CancelTask called for {TaskId}", taskId);

        if (!_taskStates.TryGetValue(taskId, out var task))
        {
            _logger.LogWarning("Task {TaskId} not found for cancellation", taskId);
            return;
        }
    
        lock (task)
        {
            if (task.Status == CrackStatus.IN_PROGRESS || task.Status == CrackStatus.PENDING)
            {
                task.Status = CrackStatus.ERROR;
            }
            else
            {
                _logger.LogInformation("Task {TaskId} already in state {Status}, skipping cancel", 
                    taskId, task.Status);
                return;
            }
        }
    
        // Отправляем сигнал отмены всем воркерам
        var workers = _workers.Values.Where(w => w.IsAlive).ToList();
        var cancelTasks = new List<Task>();
    
        foreach (var worker in workers)
        {
            cancelTasks.Add(SendCancelToWorker(worker, taskId));
        }
    
        await Task.WhenAll(cancelTasks);
        
        _logger.LogInformation("Task {TaskId} cancelled", taskId);
    }

    private async Task SendCancelToWorker(WorkerInfo worker, Guid taskId)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{worker.Url}/internal/api/worker/hash/crack/cancel",
                new { TaskId = taskId }
            );
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Cancel signal sent to worker {WorkerName} for task {TaskId}", 
                    worker.WorkerName, taskId);
            }
            else
            {
                _logger.LogWarning("Worker {WorkerName} returned {StatusCode} for cancel task {TaskId}", 
                    worker.WorkerName, response.StatusCode, taskId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send cancel to worker {WorkerName} for task {TaskId}", 
                worker.WorkerName, taskId);
        }
    }

    public List<CrackTaskState> GetTimedOutTasks(TimeSpan timeout)
    {
        var now = DateTime.UtcNow;
        return _taskStates.Values
            .Where(t => t.Status == CrackStatus.IN_PROGRESS 
                        && t.StartedAt.HasValue 
                        && now - t.StartedAt.Value > timeout)
            .ToList();
    }


    public void RemoveDeadWorkers(int maxFailedChecks)
    {
        var deadWorkers = _workers.Values
            .Where(w => !w.IsAlive && w.FailedHealthChecks >= maxFailedChecks)
            .ToList();

        

        foreach (var worker in deadWorkers)
        {
            // find all tasks that worker has to do
            var affectedTasks = _taskStates.Values
                .Where(t => t.AssignedWorkers.Contains(worker.WorkerId) 
                            && t.Status == CrackStatus.IN_PROGRESS)
                .ToList();

            if (_workers.TryRemove(worker.WorkerId, out var removed))
            {
                _logger.LogWarning("Worker {WorkerName} ({WorkerUrl}) removed after {FailedChecks} failed health checks", 
                    worker.WorkerName, worker.Url, worker.FailedHealthChecks);
            }


            foreach (var task in affectedTasks)
            {
                task.AssignedWorkers.Remove(worker.WorkerId);
                
                _logger.LogWarning("Task {TaskId} lost worker {WorkerName}, reassigning...", 
                    task.RequestId, worker.WorkerName);
                
                _ = Task.Run(() => ReassignTask(task, worker));
            }
        }
    }


    

}
