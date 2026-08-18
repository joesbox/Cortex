namespace Cortex.Services
{
    public readonly record struct UpdateButtonPresentation(string Text, bool IsHighlighted);

    public static class UpdateButtonPresentationFactory
    {
        public static UpdateButtonPresentation ForFirmware(
            bool isConnected,
            bool commsEstablished,
            bool isChecking,
            string controllerVersion,
            string? availableVersion)
        {
            if (!isConnected || !commsEstablished)
            {
                return new UpdateButtonPresentation("No firmware available", false);
            }

            if (isChecking)
            {
                return new UpdateButtonPresentation("Checking firmware...", false);
            }

            if (!string.IsNullOrWhiteSpace(availableVersion))
            {
                return new UpdateButtonPresentation($"Update {availableVersion}", true);
            }

            return new UpdateButtonPresentation(
                string.IsNullOrWhiteSpace(controllerVersion) ? "No firmware available" : "Firmware up to date",
                false);
        }

        public static UpdateButtonPresentation ForApplication(bool isChecking, string? availableVersion)
        {
            if (isChecking)
            {
                return new UpdateButtonPresentation("Checking...", false);
            }

            if (!string.IsNullOrWhiteSpace(availableVersion))
            {
                return new UpdateButtonPresentation($"Update {availableVersion} available", true);
            }

            return new UpdateButtonPresentation("Check for updates", false);
        }
    }
}
