using Life_Admin_Autopilot.DAL.Kernel.UserData;
using Microsoft.Extensions.Logging;

namespace Life_Admin_Autopilot.BLL.Kernel.UserData;

/// <summary>
/// Runs every registered <see cref="IUserDataEraser"/> in <c>Order</c> sequence.
///
/// <para>
/// <b>Why strictly ordered, dependents-first.</b> There is no Mongo transaction
/// available (the deployment and the test DB are standalone, not replica sets, so
/// sessions throw), so the account row is removed LAST. A mid-cascade failure
/// therefore leaves a still-authenticated user — re-runnable — rather than a
/// logged-out account with orphaned data.
/// </para>
///
/// <para>
/// <b>Best-effort by design.</b> An eraser that throws is logged and the cascade
/// CONTINUES, except for the final account-order eraser. A storage miss (a file
/// already gone) must not abort account deletion.
/// </para>
/// </summary>
public sealed class UserDataErasureService
{
    private readonly IEnumerable<IUserDataEraser> _erasers;
    private readonly ILogger<UserDataErasureService> _logger;

    public UserDataErasureService(IEnumerable<IUserDataEraser> erasers, ILogger<UserDataErasureService> logger)
    {
        _erasers = erasers;
        _logger = logger;
    }

    public async Task EraseAsync(UserErasureContext context, CancellationToken cancellationToken = default)
    {
        foreach (var eraser in _erasers.OrderBy(e => e.Order).ThenBy(e => e.Name, StringComparer.Ordinal))
        {
            try
            {
                await eraser.EraseAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (eraser.Order < UserErasureOrder.Account)
            {
                _logger.LogWarning(
                    ex,
                    "me:delete:eraser-failed eraser={Eraser} userId={UserId}",
                    eraser.Name,
                    context.UserId);
            }
        }
    }
}
