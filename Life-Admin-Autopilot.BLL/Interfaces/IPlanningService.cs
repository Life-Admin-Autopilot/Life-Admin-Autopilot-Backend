using Life_Admin_Autopilot.BLL.Dtos;

namespace Life_Admin_Autopilot.BLL.Interfaces
{
    public interface IPlanningService
    {
        // Saves a confirmed draft: the task, the document it came with if any, and the
        // vector that makes it findable by Copilot Chat. Everything before this point -
        // transcription, staging, conflict checking - writes nothing to the database.
        Task<CommitResponse> CommitAsync(
            string userId,
            CommitRequest request,
            CancellationToken cancellationToken = default);
    }
}
