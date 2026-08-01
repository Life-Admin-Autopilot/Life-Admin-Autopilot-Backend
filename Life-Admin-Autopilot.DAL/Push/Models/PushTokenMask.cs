namespace Life_Admin_Autopilot.DAL.Push.Models
{
    public static class PushTokenMask
    {
        // Device tokens let anyone who holds them push to that device, so logs get an
        // identifiable fragment only - never the whole token.
        public static string Mask(string? token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return "(empty)";
            }

            return token.Length <= 12
                ? "***"
                : $"{token[..6]}...{token[^4..]} (len {token.Length})";
        }
    }
}
