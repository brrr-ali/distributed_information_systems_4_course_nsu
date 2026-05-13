using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shared.DTO
{
    public record WorkerRegisterRequest
    (
        string WorkerName,
        string Url
    );

    public record WorkerRegisterResponse
    (
        Guid WorkerId
    );

    /*
    * EndIndex is included in check by worker!!!
    */
    public record WorkerTaskRequest
    (
        Guid TaskRequestId,
        string Hash,
        int MaxLength,
        int PartNumber,
        int PartCount,
        long? StartIndex = null,
        long? EndIndex = null
    );

    public record WorkerTaskResponse
    (
        Guid WorkerId,
        Guid TaskRequestId,
        List<string> FoundWords,
        long CheckedCount,
        long CurrentIndex,
        long RangeStart,
        long RangeEnd,
        bool IsRequestDone
    );

    public record CancelTaskRequest(Guid TaskId);

}