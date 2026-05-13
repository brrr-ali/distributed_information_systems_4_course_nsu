using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Manager.DTO
{
    public record ManagerCrackRequest
    (
        string Hash,
        int MaxLength
    );

    public record ManagerCrackResponse
    (
        Guid requestId
    );

    public record ManagerStatusResponse
    (
        string status,
        int progress,
        List<string>? data
    );
}