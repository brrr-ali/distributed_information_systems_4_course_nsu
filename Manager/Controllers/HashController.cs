using Microsoft.AspNetCore.Mvc;
using Manager.DTO;
using System.Text.Json;
using Manager.Services;



namespace Manager.ManagerController
{
    [ApiController]
    [Route("api/hash")]
    public class HashController : ControllerBase
    {
        private readonly ILogger<HashController> _logger;
        private readonly IManagerService _managerService;
        public HashController(
            IManagerService tableManager,
            ILogger<HashController> logger)
        {
            _managerService = tableManager;
            _logger = logger;
        }

        // POST crack
        [HttpPost("crack")]
        public async Task<ActionResult<ManagerCrackResponse>> CrackHash([FromBody] ManagerCrackRequest request)
        {
            var id = await _managerService.CreateCrackTask(request);

            return Ok(new ManagerCrackResponse(id));
        }

        [HttpGet("status")]
        public async Task<ActionResult<ManagerStatusResponse>> GetCrackStatus([FromQuery] Guid requestId)
        {
            _logger.LogInformation("GetCrackStatus called with requestId: {requestId}", requestId);
            
            try
            {
                if (requestId == Guid.Empty)
                {

                _logger.LogWarning("Empty requestId received");
                    return BadRequest("Invalid requestId");
                }
                
                var status = _managerService.GetStatus(requestId);
                return Ok(status);
            }
            catch (KeyNotFoundException ex)
            {

                _logger.LogWarning(ex, "Task {requestId} not found", requestId);
                return NotFound(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting status for {requestId}", requestId);
                return StatusCode(500, "Internal server error");
            }
        }
    }
    
}