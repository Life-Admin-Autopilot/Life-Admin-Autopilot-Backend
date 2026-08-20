namespace Life_Admin_Autopilot.BLL.Dtos
{
    /// <summary>
    /// The answer to <c>POST /api/devices/register</c>.
    ///
    /// <para>
    /// <b><see cref="ServerDelivers"/> is the point of this type.</b> The app runs two
    /// delivery channels — reminders scheduled onto the device itself, and push sent from
    /// here — and they must never both fire, or every reminder buzzes the phone twice.
    /// The client decides which one is live, and it used to decide by whether this call
    /// succeeded. That only ever proved a row was written. On a deployment with no FCM
    /// credential the phone therefore switched OFF its own working schedule and waited
    /// for pushes that could never be sent — a silent, total blackout that looked
    /// identical to a quiet week.
    /// </para>
    ///
    /// <para>
    /// So the server states plainly whether it can deliver, and the client keeps its
    /// local schedule whenever the answer is no.
    /// </para>
    /// </summary>
    public record DeviceRegistrationResponse(
        RegisteredDeviceResponse Device,
        bool ServerDelivers);
}
