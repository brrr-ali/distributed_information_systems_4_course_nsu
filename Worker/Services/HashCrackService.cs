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

    private const int REPORT_INTERVAL = 100000;

    public HashCrackService (
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
        
        task.ContinueWith(t => {
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

    private async Task ProcessTask(WorkerTaskRequest request, CancellationToken cancellationToken)
    {

        try{
            var found = new List<string>();
            double checkedCount = 0;
            long batchStart = (long)request.StartIndex;
            double startIndex = request.StartIndex;
            double endIndex = 0;

            _logger.LogInformation(
                "{WorkerName} got task {TaskId}: range [{Start}-{End}], maxLength={MaxLength}, hash={Hash}", 
                _config.WorkerName,
                request.TaskRequestId,
                request.StartIndex,
                request.EndIndex,
                request.MaxLength,
                request.Hash
            );
            

            var startTime = DateTime.UtcNow;
            
            for (double index = request.StartIndex; index <= request.EndIndex; index++)
            {
                var word = IndexToWord(index, request.MaxLength);
                checkedCount++;

                if (CalculateMD5(word) == request.Hash)
                {
                    found.Add(word);
                    _logger.LogInformation(
                        "{WorkerName} found word '{Word}' for task {TaskId} (index {Index})",
                        _config.WorkerName,
                        word,
                        request.TaskRequestId,
                        index
                    );
                }



                if (checkedCount % REPORT_INTERVAL == 0)
                {
                    endIndex = index;
                    await SendProgress(request.TaskRequestId, startIndex, endIndex, found, endIndex - startIndex + 1, false, cancellationToken);
                    

                    var elapsed = DateTime.UtcNow - startTime;
                    var speed = checkedCount / elapsed.TotalSeconds;

                    _logger.LogInformation(
                        "{WorkerName} task {TaskId}: checked {CheckedCount}/{TotalRange} words, found {FoundCount} words, speed: {Speed:F0} words/sec",
                        _config.WorkerName,
                        request.TaskRequestId,
                        checkedCount,
                        request.EndIndex - request.StartIndex + 1,
                        found.Count,
                        speed
                    );

                    startIndex = index + 1;
                }
            }


            var totalTime = DateTime.UtcNow - startTime;
            _logger.LogInformation(
                "{WorkerName} completed task {TaskId}: checked {CheckedCount} words, found {FoundCount} words, time: {TotalTime:g}",
                _config.WorkerName,
                request.TaskRequestId,
                checkedCount,
                found.Count,
                totalTime
            );

            await SendProgress( request.TaskRequestId, startIndex, request.EndIndex, found, request.EndIndex - startIndex + 1, true, cancellationToken);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{WorkerName} failed processing task {TaskId}", 
                _config.WorkerName, request.TaskRequestId);
        }
        finally
        {
            _activeTasks.TryRemove(request.TaskRequestId, out _);
        }
    }

    private async Task SendProgress(
        Guid taskId,
        double startIndex,
        double endIndex,
        List<string> foundWords,
        double checkedCount,
        bool isCompleted,
        CancellationToken cancellationToken)
    {

        _logger.LogInformation(
            "SENDING PROGRESS: task={TaskId}, start={Start}, end={End}, checkedCount={CheckedCount}, foundCount={FoundCount}, isCompleted={IsCompleted}",
            taskId,
            startIndex,
            endIndex,
            checkedCount,
            foundWords.Count,
            isCompleted
        );
        var dto = new WorkerTaskResponse(
            _config.WorkerId,
            taskId,
            foundWords,
            startIndex,
            endIndex,
            checkedCount,
            isCompleted
        );

        var response = await _httpClient.PostAsJsonAsync(
            $"{_config.ManagerUrl}/internal/api/worker/result",
            dto,
            cancellationToken
        );

        _logger.LogInformation(
            "SEND PROGRESS RESPONSE: task={TaskId}, statusCode={StatusCode}",
            taskId,
            response.StatusCode
        );
    }

    private string IndexToWord(double index, int maxLength) 
    { 
        var sb = new StringBuilder(); 
        long baseLen = _alphabet.Length; 
        do 
        { 
            sb.Insert(0, _alphabet[(int)index % baseLen]); 
            index /= baseLen; 
        } while (index > 0 && sb.Length < maxLength); 
        
        return sb.ToString(); 
    }

    private static string CalculateMD5(string input)
    {
        using var md5 = MD5.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = md5.ComputeHash(bytes);
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }
}
