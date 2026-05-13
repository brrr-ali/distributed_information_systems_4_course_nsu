using Microsoft.AspNetCore.Mvc;
using Shared.DTO;
using Worker.Models;
using Worker.Services;

namespace Worker.Controllers;

[ApiController]
[Route("internal/api/worker/hash/crack")]
public class WorkerController : ControllerBase
{
    private readonly IHashCrackService _service;
    private readonly ILogger<WorkerController> _logger;
    private readonly WorkerConfig _config;

    public WorkerController(IHashCrackService service,
        ILogger<WorkerController> logger,
        WorkerConfig config)
    {
        _service = service;
        _logger = logger;
        _config = config;
    }

    [HttpPost("task")]
    public IActionResult Crack([FromBody] WorkerTaskRequest request)
    {
        _service.StartTask(request);
        return Ok();
    }

    [HttpPost("cancel")]
    public IActionResult CancelTask([FromBody] CancelTaskRequest request)
    {
        _logger.LogInformation("Received cancel request for task {TaskId}", request.TaskId);
        _service.CancelTask(request.TaskId);
        return Ok();
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        _logger.LogDebug("Health check requested");
        return Ok(new { status = "alive", worker = _config.WorkerName, timestamp = DateTime.UtcNow });
    }
}