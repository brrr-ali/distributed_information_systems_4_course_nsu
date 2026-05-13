using Microsoft.AspNetCore.Mvc;
using Manager.DTO;
using System.Text.Json;
using Shared.DTO;
using Manager.Services;


namespace Manager.ManagerController
{
    [ApiController]
    [Route("/internal/api/worker")]
    public class InternalWorkerController : ControllerBase
    {

        private readonly IManagerService _managerService;
        private readonly ILogger<InternalWorkerController> _logger;

        public InternalWorkerController(IManagerService managerService,
            ILogger<InternalWorkerController> logger)
        {
            _managerService = managerService;
            _logger = logger;
        }
        

        [HttpPost("register")]
        public async Task<ActionResult<WorkerRegisterResponse>> RegisterWorker([FromBody] WorkerRegisterRequest request)
        {
            var workerUid = await _managerService.RegisterWorker(request);

            return Ok(new WorkerRegisterResponse(workerUid));
        }


        // POST result
        [HttpPost("result")]
        public IActionResult ReceiveResult([FromBody] WorkerTaskResponse response)
        {
            _managerService.ProcessWorkerResult(response);

            return Ok();
        }

        [HttpGet("health")]
        public IActionResult Health()
        {
            return Ok();
        }

        [HttpPost("task/cancel")]
        public async Task<IActionResult> CancelTask([FromBody] CancelTaskRequest request)
        {
            _logger.LogInformation("Forwarding cancel request for task {TaskId} to workers", request.TaskId);
            await _managerService.CancelTask(request.TaskId);
            return Ok();
        }

        [HttpGet("register/{workerId}")]
        public IActionResult IsRegistered(Guid workerId)
        {
            var workers = _managerService.GetAllWorkers();
            var known   = workers.Any(w => w.WorkerId == workerId);
            return known ? Ok() : NotFound();
        }

    }
}