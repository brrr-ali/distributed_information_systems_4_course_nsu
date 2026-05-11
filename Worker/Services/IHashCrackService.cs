using Shared.DTO;

namespace Worker.Services;

public interface IHashCrackService
{
    void StartTask(WorkerTaskRequest request);
    void CancelTask(Guid taskId); 
}