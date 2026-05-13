using System.Collections.Concurrent;
using System.Text.Json;
using Manager.DTO;
using Manager.Models;
using Microsoft.Extensions.Options;
using Shared.DTO;

namespace Manager.Services;

public class ManagerService : IManagerService
{
    private readonly ConcurrentDictionary<Guid, CrackTaskState> _taskStates = new();
    private readonly ConcurrentDictionary<Guid, WorkerInfo> _workers = new();
    private readonly ConcurrentQueue<Guid> _taskQueue = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _taskCompletion = new();
    private int _isProcessingFlag = 0;

    private readonly HttpClient _httpClient;
    private readonly ILogger<ManagerService> _logger;
    private readonly ManagerConfig _config;

    private const string StateFilePath = "manager_state.json";

    public ManagerService(
        IOptions<ManagerConfig> config,
        IHttpClientFactory httpClientFactory,
        ILogger<ManagerService> logger)
    {
        _config    = config.Value;
        _httpClient = httpClientFactory.CreateClient();
        _logger    = logger;

        RestoreState();
    }

    // ─── Worker registration ──────────────────────────────────────────────

    public async Task<Guid> RegisterWorker(WorkerRegisterRequest request)
    {
        var id     = Guid.NewGuid();
        var worker = new WorkerInfo
        {
            WorkerId   = id,
            WorkerName = request.WorkerName,
            Url        = request.Url
        };

        _workers[id] = worker;

        _logger.LogInformation(
            "Worker registered {WorkerId} with {WorkerName} at {Url}",
            worker.WorkerId, worker.WorkerName, worker.Url);

        // Воркер пришёл — запускаем обработку очереди (там могут быть PENDING задачи)
        _ = Task.Run(ProcessQueue);

        return id;
    }

    // ─── Task creation ────────────────────────────────────────────────────

    public async Task<Guid> CreateCrackTask(ManagerCrackRequest request)
    {
        var id    = Guid.NewGuid();
        var total = CalculateTotalCombinations(request.MaxLength);

        _logger.LogInformation(
            "Creating crack task {RequestId}, total combinations = {Total}", id, total);

        var task = new CrackTaskState
        {
            RequestId           = id,
            Hash                = request.Hash,
            TotalCombinations   = total,
            CheckedCombinations = 0,
            Status              = CrackStatus.PENDING,
            MaxLength           = request.MaxLength,
            FoundWords          = new List<string>(),
            StartedAt           = DateTime.UtcNow,
        };

        _taskStates[id] = task;
        SaveState();

        _taskQueue.Enqueue(id);
        _logger.LogInformation(
            "Task {RequestId} enqueued, queue length = {Length}", id, _taskQueue.Count);

        _ = Task.Run(ProcessQueue);

        return id;
    }

    // ─── Status ───────────────────────────────────────────────────────────

    public ManagerStatusResponse GetStatus(Guid requestId)
    {
        if (requestId == Guid.Empty)
            throw new ArgumentException("RequestId cannot be empty", nameof(requestId));

        if (!_taskStates.TryGetValue(requestId, out var task))
            throw new KeyNotFoundException($"Task {requestId} not found");

        _logger.LogInformation(
            "Task {RequestId} status: {Status}, checked: {Checked}/{Total}",
            requestId, task.Status, task.CheckedCombinations, task.TotalCombinations);

        int progress = task.TotalCombinations > 0
            ? (int)(100.0 * task.CheckedCombinations / task.TotalCombinations)
            : 0;

        var statusString = task.Status switch
        {
            CrackStatus.PENDING     => "IN_PROGRESS",
            CrackStatus.IN_PROGRESS => "IN_PROGRESS",
            CrackStatus.READY       => "READY",
            CrackStatus.ERROR       => "ERROR",
            CrackStatus.PARTITIAL   => "PARTIAL",
            _                       => "IN_PROGRESS"
        };

        List<string>? data = task.Status is CrackStatus.READY or CrackStatus.PARTITIAL
            ? task.FoundWords
            : null;

        return new ManagerStatusResponse(statusString, progress, data);
    }

    // ─── Worker results ───────────────────────────────────────────────────

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
                if (wp.RangeEnd == 0)
                {
                    wp.RangeStart = response.RangeStart;
                    wp.RangeEnd   = response.RangeEnd;
                    _logger.LogInformation(
                        "Worker {Name} range set to [{Start}-{End}]",
                        wp.WorkerName, wp.RangeStart, wp.RangeEnd);
                }

                wp.CheckedCount  += response.CheckedCount;
                wp.CurrentIndex   = response.CurrentIndex;
                wp.LastReportTime = DateTime.UtcNow;

                if (response.IsRequestDone)
                {
                    wp.IsCompleted = true;
                    _logger.LogInformation(
                        "Worker {Name} completed its range for task {TaskId}",
                        wp.WorkerName, task.RequestId);
                }
            }
            else
            {
                _logger.LogWarning(
                    "No progress record for worker {WorkerId} in task {TaskId}",
                    response.WorkerId, response.TaskRequestId);
            }

            task.CheckedCombinations += response.CheckedCount;

            foreach (var word in response.FoundWords)
            {
                if (!task.FoundWords.Contains(word))
                {
                    task.FoundWords.Add(word);
                    _logger.LogInformation(
                        "ADDED WORD '{Word}' to task {TaskId}", word, task.RequestId);
                }
            }

            if (task.WorkersProgress.Values.All(w => w.IsCompleted) ||
                task.CheckedCombinations >= task.TotalCombinations)
            {
                task.Status      = CrackStatus.READY;
                task.CompletedAt = DateTime.UtcNow;
                _logger.LogInformation(
                    "TASK {TaskId} COMPLETED! Found {Count} words",
                    task.RequestId, task.FoundWords.Count);

                SaveState();
                SignalTaskCompletion(task.RequestId);
            }
        }
    }

    // ─── Worker health ────────────────────────────────────────────────────

    public List<WorkerInfo> GetAllWorkers() => _workers.Values.ToList();

    public void UpdateWorkerHealth(Guid workerId, bool isAlive, bool resetFailedChecks = true)
    {
        if (!_workers.TryGetValue(workerId, out var worker))
            return;

        worker.IsAlive  = isAlive;
        worker.LastSeen = DateTime.UtcNow;

        if (isAlive)
            worker.FailedHealthChecks = 0;
        else if (!resetFailedChecks)
            worker.FailedHealthChecks++;

        _logger.LogDebug(
            "Worker {WorkerName} health updated: IsAlive={IsAlive}, FailedChecks={FailedChecks}",
            worker.WorkerName, isAlive, worker.FailedHealthChecks);
    }

    public void RemoveDeadWorkers(int maxFailedChecks)
    {
        var deadWorkers = _workers.Values
            .Where(w => !w.IsAlive && w.FailedHealthChecks >= maxFailedChecks)
            .ToList();

        foreach (var worker in deadWorkers)
        {
            var affectedTasks = _taskStates.Values
                .Where(t => t.AssignedWorkers.Contains(worker.WorkerId)
                            && t.Status == CrackStatus.IN_PROGRESS)
                .ToList();

            if (_workers.TryRemove(worker.WorkerId, out _))
            {
                _logger.LogWarning(
                    "Worker {WorkerName} ({WorkerUrl}) removed after {FailedChecks} failed health checks",
                    worker.WorkerName, worker.Url, worker.FailedHealthChecks);
            }

            foreach (var task in affectedTasks)
            {
                task.AssignedWorkers.Remove(worker.WorkerId);
                _logger.LogWarning(
                    "Task {TaskId} lost worker {WorkerName}, reassigning...",
                    task.RequestId, worker.WorkerName);
                _ = Task.Run(() => ReassignTask(task, worker));
            }
        }
    }

    // ─── Timeouts ─────────────────────────────────────────────────────────

    public void CheckTaskTimeouts(TimeSpan timeout)
    {
        var now     = DateTime.UtcNow;
        var timedOut = _taskStates.Values
            .Where(t => t.Status == CrackStatus.IN_PROGRESS
                        && t.StartedAt.HasValue
                        && now - t.StartedAt.Value > timeout)
            .ToList();

        foreach (var task in timedOut)
        {
            lock (task)
            {
                task.Status = task.FoundWords.Count > 0
                    ? CrackStatus.PARTITIAL
                    : CrackStatus.ERROR;
                _logger.LogWarning("Task {TaskId} timed out", task.RequestId);
            }

            SaveState();
            SignalTaskCompletion(task.RequestId);
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
            if (task.Status is CrackStatus.IN_PROGRESS or CrackStatus.PENDING)
            {
                task.Status = task.FoundWords.Count > 0
                    ? CrackStatus.PARTITIAL
                    : CrackStatus.ERROR;
            }
            else
            {
                _logger.LogInformation(
                    "Task {TaskId} already in state {Status}, skipping cancel",
                    taskId, task.Status);
                return;
            }
        }

        SaveState();
        SignalTaskCompletion(taskId);

        var cancelTasks = _workers.Values
            .Where(w => w.IsAlive)
            .Select(w => SendCancelToWorker(w, taskId))
            .ToList();

        await Task.WhenAll(cancelTasks);

        _logger.LogInformation("Task {TaskId} cancelled", taskId);
    }

    // ─── Queue processing ─────────────────────────────────────────────────

    private async Task ProcessQueue()
    {
        if (Interlocked.Exchange(ref _isProcessingFlag, 1) == 1)
            return;

        try
        {
            while (_taskQueue.TryPeek(out var taskId))
            {
                if (!_taskStates.TryGetValue(taskId, out var task))
                {
                    _taskQueue.TryDequeue(out _);
                    continue;
                }

                // Задача уже завершена (например PARTIAL после падения воркеров) — пропускаем
                if (task.Status is CrackStatus.READY or CrackStatus.ERROR or CrackStatus.PARTITIAL)
                {
                    _taskQueue.TryDequeue(out _);
                    continue;
                }

                // Ждём живого воркера
                while (!_workers.Values.Any(w => w.IsAlive))
                {
                    _logger.LogWarning(
                        "No alive workers, waiting for task {TaskId}", taskId);
                    await Task.Delay(5000);
                }

                var tcs = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _taskCompletion[taskId] = tcs;

                if (task.Status == CrackStatus.PENDING)
                {
                    _logger.LogInformation("Dispatching queued task {TaskId}", taskId);
                    await DispatchTasks(
                        task.RequestId, task.Hash,
                        task.MaxLength, task.TotalCombinations);
                    task.Status = CrackStatus.IN_PROGRESS;
                    SaveState();
                }
                else if (task.Status == CrackStatus.IN_PROGRESS)
                {
                    _logger.LogInformation(
                        "Task {TaskId} already IN_PROGRESS, waiting for workers to finish",
                        taskId);
                }

                _taskQueue.TryDequeue(out _);

                await tcs.Task.WaitAsync(TimeSpan.FromHours(1));
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isProcessingFlag, 0);
        }
    }

    private void SignalTaskCompletion(Guid taskId)
    {
        if (_taskCompletion.TryRemove(taskId, out var tcs))
            tcs.TrySetResult(true);
    }

    // ─── Dispatch / Reassign ──────────────────────────────────────────────

    private async Task DispatchTasks(
        Guid requestId, string hash, int maxLength, long total)
    {
        var aliveWorkers = _workers.Values.Where(w => w.IsAlive).ToList();
        if (aliveWorkers.Count == 0)
            return;

        int partCount = aliveWorkers.Count;

        for (int i = 0; i < partCount; i++)
        {
            var worker     = aliveWorkers[i];
            var workerTask = new WorkerTaskRequest(requestId, hash, maxLength, i, partCount);

            var taskState = _taskStates[requestId];
            taskState.AssignedWorkers.Add(worker.WorkerId);
            taskState.WorkersProgress[worker.WorkerId] = new WorkerProgress
            {
                WorkerId       = worker.WorkerId,
                WorkerName     = worker.WorkerName,
                RangeStart     = 0,
                RangeEnd       = 0,
                CheckedCount   = 0,
                LastReportTime = DateTime.UtcNow
            };

            try
            {
                await _httpClient.PostAsJsonAsync(
                    $"{worker.Url}/internal/api/worker/hash/crack/task", workerTask);
                _logger.LogInformation(
                    "Dispatched part {Part}/{Count} to {Worker}",
                    i, partCount, worker.WorkerName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dispatch to {Worker}", worker.WorkerName);
            }
        }
    }

    private async Task ReassignTask(CrackTaskState task, WorkerInfo deadWorker)
    {
        if (!task.WorkersProgress.TryGetValue(deadWorker.WorkerId, out var dead))
            return;

        long remainingStart = dead.CurrentIndex + 1;
        long remainingEnd   = dead.RangeEnd;

        if (remainingStart > remainingEnd)
        {
            _logger.LogInformation(
                "Worker {Name} finished its range before dying, nothing to reassign",
                deadWorker.WorkerName);
            task.WorkersProgress.TryRemove(deadWorker.WorkerId, out _);
            return;
        }

        var aliveWorkers = _workers.Values.Where(w => w.IsAlive).ToList();
        if (aliveWorkers.Count == 0)
        {
            _logger.LogError(
                "No alive workers to reassign task {TaskId}", task.RequestId);

            lock (task)
            {
                task.Status      = CrackStatus.PARTITIAL;
                task.CompletedAt = DateTime.UtcNow;
            }

            SaveState();
            SignalTaskCompletion(task.RequestId);
            return;
        }

        long remaining = remainingEnd - remainingStart + 1;
        long chunkSize = remaining / aliveWorkers.Count;
        long current   = remainingStart;

        for (int i = 0; i < aliveWorkers.Count; i++)
        {
            var worker    = aliveWorkers[i];
            long newStart = current;
            long newEnd   = i == aliveWorkers.Count - 1
                ? remainingEnd
                : current + chunkSize - 1;

            var req = new WorkerTaskRequest(
                task.RequestId, task.Hash, task.MaxLength,
                PartNumber: 0, PartCount: 1,
                StartIndex: newStart, EndIndex: newEnd);

            try
            {
                await _httpClient.PostAsJsonAsync(
                    $"{worker.Url}/internal/api/worker/hash/crack/task", req);

                task.AssignedWorkers.Add(worker.WorkerId);
                task.WorkersProgress[worker.WorkerId] = new WorkerProgress
                {
                    WorkerId       = worker.WorkerId,
                    WorkerName     = worker.WorkerName,
                    RangeStart     = newStart,
                    RangeEnd       = newEnd,
                    CheckedCount   = 0,
                    CurrentIndex   = newStart,
                    LastReportTime = DateTime.UtcNow
                };

                _logger.LogInformation(
                    "Reassigned [{Start}-{End}] to {Worker}",
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

        SaveState();
    }

    // ─── Cancel helper ────────────────────────────────────────────────────

    private async Task SendCancelToWorker(WorkerInfo worker, Guid taskId)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{worker.Url}/internal/api/worker/hash/crack/cancel",
                new { TaskId = taskId });

            if (response.IsSuccessStatusCode)
                _logger.LogDebug(
                    "Cancel signal sent to worker {WorkerName} for task {TaskId}",
                    worker.WorkerName, taskId);
            else
                _logger.LogWarning(
                    "Worker {WorkerName} returned {StatusCode} for cancel task {TaskId}",
                    worker.WorkerName, response.StatusCode, taskId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to send cancel to worker {WorkerName} for task {TaskId}",
                worker.WorkerName, taskId);
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private long CalculateTotalCombinations(int maxLength)
    {
        long baseLen = _config.Alphabet.Length;
        long total   = 0;
        long power   = 1;
        for (int i = 1; i <= maxLength; i++)
        {
            power *= baseLen;
            total += power;
        }
        return total;
    }

    // ─── State persistence ────────────────────────────────────────────────

    private void SaveState()
    {
        try
        {
            var snapshot = new ManagerStateSnapshot
            {
                Tasks = _taskStates.Values
                    .Select(t => new TaskSnapshot
                    {
                        RequestId           = t.RequestId,
                        Hash                = t.Hash,
                        MaxLength           = t.MaxLength,
                        TotalCombinations   = t.TotalCombinations,
                        CheckedCombinations = t.CheckedCombinations,
                        Status              = t.Status,
                        FoundWords          = t.FoundWords.ToList(),
                        CreatedAt           = t.CreatedAt,
                        StartedAt           = t.StartedAt,
                        CompletedAt         = t.CompletedAt,
                    })
                    .ToList(),

                // Сохраняем очередь чтобы восстановить после рестарта
                TaskQueue = _taskQueue.ToList()
            };

            var json = JsonSerializer.Serialize(
                snapshot,
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(StateFilePath, json);

            _logger.LogDebug("Manager state saved ({Count} tasks)", snapshot.Tasks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save manager state");
        }
    }

    private void RestoreState()
    {
        try
        {
            if (!File.Exists(StateFilePath))
            {
                _logger.LogInformation("No state file found, starting fresh");
                return;
            }

            var json     = File.ReadAllText(StateFilePath);
            var snapshot = JsonSerializer.Deserialize<ManagerStateSnapshot>(json);
            if (snapshot is null)
                return;

            foreach (var t in snapshot.Tasks)
            {
                var status = t.Status is CrackStatus.IN_PROGRESS or CrackStatus.PENDING
                    ? (t.FoundWords.Count > 0 ? CrackStatus.PARTITIAL : CrackStatus.ERROR)
                    : t.Status;

                _taskStates[t.RequestId] = new CrackTaskState
                {
                    RequestId           = t.RequestId,
                    Hash                = t.Hash,
                    MaxLength           = t.MaxLength,
                    TotalCombinations   = t.TotalCombinations,
                    CheckedCombinations = t.CheckedCombinations,
                    Status              = status,
                    FoundWords          = t.FoundWords,
                    StartedAt           = t.StartedAt,
                    CompletedAt         = t.CompletedAt,
                };
            }

            // Восстанавливаем очередь — только те задачи, что реально остались PENDING
            foreach (var taskId in snapshot.TaskQueue)
            {
                if (_taskStates.TryGetValue(taskId, out var task)
                    && task.Status == CrackStatus.PENDING)
                {
                    _taskQueue.Enqueue(taskId);
                    _logger.LogInformation(
                        "Task {TaskId} restored to queue", taskId);
                }
            }

            _logger.LogInformation(
                "State restored: {TaskCount} tasks, {QueueCount} tasks in queue",
                snapshot.Tasks.Count, _taskQueue.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore state");
        }
    }

    // ─── Snapshot DTOs ────────────────────────────────────────────────────

    internal sealed class ManagerStateSnapshot
    {
        public List<TaskSnapshot> Tasks     { get; set; } = new();
        public List<Guid>         TaskQueue { get; set; } = new();
    }

    internal sealed class TaskSnapshot
    {
        public Guid            RequestId           { get; set; }
        public string          Hash                { get; set; } = string.Empty;
        public int             MaxLength           { get; set; }
        public long            TotalCombinations   { get; set; }
        public long            CheckedCombinations { get; set; }
        public CrackStatus     Status              { get; set; }
        public List<string>    FoundWords          { get; set; } = new();
        public DateTime        CreatedAt           { get; set; }
        public DateTime?       StartedAt           { get; set; }
        public DateTime?       CompletedAt         { get; set; }
    }
}