using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Shared.DTO;
using Worker.Models;
using System.Text;
using System.Security.Cryptography;

namespace Worker.Services;

public class HashCrackService : IHashCrackService
{
    private readonly char[] _alphabet;
    private readonly WorkerConfig _config;
    private readonly ILogger<HashCrackService> _logger;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<Guid, (Task Task, CancellationTokenSource Cts)> _activeTasks = new();

    private const int REPORT_INTERVAL = 100_000;

    public HashCrackService(
        WorkerConfig config,
        ILogger<HashCrackService> logger,
        HttpClient httpClient)
    {
        _config = config;
        _alphabet = config.Alphabet.ToCharArray();
        _logger = logger;
        _httpClient = httpClient;
    }

    public void StartTask(WorkerTaskRequest request)
    {
        var cts = new CancellationTokenSource();
        var task = Task.Run(() => ProcessTask(request, cts.Token), cts.Token);

        _activeTasks[request.TaskRequestId] = (task, cts);

        task.ContinueWith(completedTask =>
        {
            _activeTasks.TryRemove(request.TaskRequestId, out _);
            cts.Dispose();
        });
    }

    public void CancelTask(Guid taskId)
    {
        if (_activeTasks.TryRemove(taskId, out var entry))
        {
            _logger.LogInformation("Cancelling task {TaskId}", taskId);
            entry.Cts.Cancel();
            entry.Cts.Dispose();
        }
        else
        {
            _logger.LogWarning("Task {TaskId} not found for cancellation", taskId);
        }
    }

    private async Task ProcessTask(WorkerTaskRequest request, CancellationToken ct)
    {
        try
        {
            long startIndex, endIndex;

            if (request.StartIndex.HasValue && request.EndIndex.HasValue)
            {
                // Режим переназначения — диапазон задан явно менеджером
                startIndex = request.StartIndex.Value;
                endIndex   = request.EndIndex.Value;
                _logger.LogInformation("{Worker} REASSIGNED task {TaskId}: [{Start}-{End}]",
                    _config.WorkerName, request.TaskRequestId, startIndex, endIndex);
            }
            else
            {
                // Обычный режим — воркер считает свой диапазон сам
                long total     = CalculateTotalCombinations(request.MaxLength);
                long chunkSize = total / request.PartCount;
                startIndex = (long)request.PartNumber * chunkSize;
                endIndex   = request.PartNumber == request.PartCount - 1
                    ? total - 1
                    : (long)(request.PartNumber + 1) * chunkSize - 1;

                _logger.LogInformation(
                    "{Worker} task {TaskId}: part {Part}/{Count}, range [{Start}-{End}], total={Total}",
                    _config.WorkerName, request.TaskRequestId,
                    request.PartNumber, request.PartCount,
                    startIndex, endIndex, total);
            }

            var found        = new List<string>();
            long batchCount  = 0;   // дельта за текущий батч — сбрасывается после отправки
            long currentIndex = startIndex;
            var startTime    = DateTime.UtcNow;

            for (long index = startIndex; index <= endIndex; index++)
            {
                ct.ThrowIfCancellationRequested();
                currentIndex = index;

                var word = IndexToWord(index, request.MaxLength);
                batchCount++;

                if (CalculateMD5(word) == request.Hash)
                {
                    found.Add(word);
                    _logger.LogInformation("{Worker} found '{Word}' at index {Index}",
                        _config.WorkerName, word, index);
                }

                if (batchCount % REPORT_INTERVAL == 0)
                {
                    await SendProgress(
                        request.TaskRequestId, found,
                        batchCount, currentIndex,
                        startIndex, endIndex,
                        isCompleted: false, ct);

                    var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                    var totalChecked = currentIndex - startIndex + 1;
                    var speed = elapsed > 0 ? totalChecked / elapsed : 0;

                    _logger.LogInformation(
                        "{Worker} task {TaskId}: {Checked}/{Total}, speed {Speed:F0} w/s",
                        _config.WorkerName, request.TaskRequestId,
                        totalChecked, endIndex - startIndex + 1, speed);

                    batchCount = 0; // сбрасываем батч — менеджер накапливает сам через +=
                }
            }

            // Финальный батч (остаток после последней кратной REPORT_INTERVAL итерации)
            await SendProgress(
                request.TaskRequestId, found,
                batchCount, endIndex,
                startIndex, endIndex,
                isCompleted: true, ct);

            _logger.LogInformation(
                "{Worker} completed task {TaskId}: checked {Checked}, found {Found}, time {Time:g}",
                _config.WorkerName, request.TaskRequestId,
                endIndex - startIndex + 1, found.Count,
                DateTime.UtcNow - startTime);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("{Worker} task {TaskId} cancelled",
                _config.WorkerName, request.TaskRequestId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Worker} failed task {TaskId}",
                _config.WorkerName, request.TaskRequestId);
        }
        finally
        {
            _activeTasks.TryRemove(request.TaskRequestId, out _);
        }
    }

    // Считаем суммарное число комбинаций для всех длин от 1 до maxLength
    private long CalculateTotalCombinations(int maxLength)
    {
        long baseLen = _alphabet.Length;
        long total   = 0;
        long power   = 1;
        for (int i = 1; i <= maxLength; i++)
        {
            power *= baseLen;   // точное целочисленное возведение в степень
            total += power;
        }
        return total;
    }

    // Переводит линейный индекс в слово с учётом всех длин от 1 до maxLength
    private string IndexToWord(long index, int maxLength)
    {
        long baseLen    = _alphabet.Length;
        long levelStart = 0;
        long power      = 1;

        for (int wordLength = 1; wordLength <= maxLength; wordLength++)
        {
            power *= baseLen;               // baseLen^wordLength, без погрешности
            long levelSize = power;

            if (index < levelStart + levelSize)
            {
                long localIndex = index - levelStart;
                char[] chars    = new char[wordLength];

                for (int i = wordLength - 1; i >= 0; i--)
                {
                    chars[i]   = _alphabet[localIndex % baseLen];
                    localIndex /= baseLen;
                }

                return new string(chars);
            }

            levelStart += levelSize;
        }

        return string.Empty;
    }

    private async Task SendProgress(
        Guid taskId,
        List<string> foundWords,
        long batchCheckedCount,     // дельта за батч, не накопленная сумма
        long currentIndex,
        long rangeStart,
        long rangeEnd,
        bool isCompleted,
        CancellationToken ct)
    {
        var dto = new WorkerTaskResponse(
            _config.WorkerId,
            taskId,
            new List<string>(foundWords),   // копия, чтобы не гонять одну коллекцию
            batchCheckedCount,
            currentIndex,
            rangeStart,
            rangeEnd,
            isCompleted
        );

        var response = await _httpClient.PostAsJsonAsync(
            $"{_config.ManagerUrl}/internal/api/worker/result",
            dto,
            ct
        );

        _logger.LogInformation(
            "SendProgress task={TaskId} batch={Batch} currentIndex={Index} completed={Done} status={Status}",
            taskId, batchCheckedCount, currentIndex, isCompleted, response.StatusCode);
    }

    private static string CalculateMD5(string input)
    {
        using var md5      = MD5.Create();
        var bytes          = Encoding.UTF8.GetBytes(input);
        var hashBytes      = md5.ComputeHash(bytes);
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }
}
