using Manager.DTO;
using Manager.Models;
using Shared.DTO;

namespace Manager.Services;

public interface IManagerService
{
    Task<Guid> CreateCrackTask(ManagerCrackRequest request);
    ManagerStatusResponse GetStatus(Guid requestId);
    void ProcessWorkerResult(WorkerTaskResponse response);
    Task<Guid> RegisterWorker(WorkerRegisterRequest request);
    List<WorkerInfo> GetAllWorkers();
    void UpdateWorkerHealth(Guid workerId, bool isAlive, bool resetFailedChecks = true);
    void CheckTaskTimeouts(TimeSpan timeout);
    Task CancelTask(Guid taskId);
    List<CrackTaskState> GetTimedOutTasks(TimeSpan timeout);
    void RemoveDeadWorkers(int maxFailedChecks);
}