using Life_Admin_Autopilot.DAL.Claude.Models;
using Life_Admin_Autopilot.DAL.Common;

namespace Life_Admin_Autopilot.DAL.Claude
{
    // The only thing in the codebase allowed to call the Claude gateway API directly.
    // No controller, agent, or feature code should construct an HTTP request to it itself.
    public interface IClaudeService
    {
        Task<Result<ClaudeCompletionResult>> GetCompletionAsync(
            ClaudeCompletionRequest request,
            CancellationToken cancellationToken = default);
    }
}