using System.Security.Claims;
using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Life_Admin_Autopilot_Backend.Controllers
{
    // The confirmation half of the planning flow. Everything before it - transcription,
    // document staging, drafting, conflict checking - writes nothing to the database;
    // this is the only place a proposed task becomes a real one.
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class PlanningController : ControllerBase
    {
        private readonly IPlanningService _planningService;

        public PlanningController(IPlanningService planningService)
        {
            _planningService = planningService;
        }

        [HttpPost("commit")]
        [ProducesResponseType(typeof(CommitResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(CommitResponse), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Commit(CommitRequest request, CancellationToken cancellationToken)
        {
            // The body carries a userId because the agent sends one, but it is ignored:
            // the owner comes from the token, so a caller cannot write into another
            // user's list by editing the payload (NFR-5).
            var response = await _planningService.CommitAsync(CurrentUserId, request, cancellationToken);

            if (response.Succeeded)
            {
                return Ok(response);
            }

            return StatusCode(MapStatusCode(response.ErrorCode!), response);
        }

        private static int MapStatusCode(string errorCode) => errorCode switch
        {
            "COMMIT_NO_USER" => StatusCodes.Status401Unauthorized,
            "COMMIT_NO_TITLE" => StatusCodes.Status400BadRequest,
            "COMMIT_NO_DUE_DATE" => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status502BadGateway
        };

        private string CurrentUserId =>
            User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? string.Empty;
    }
}
