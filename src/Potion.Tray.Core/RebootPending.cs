namespace Potion.Tray.Core;

public sealed record RebootPendingSignals(
    bool ComponentBasedServicingRebootPending,
    bool ComponentBasedServicingRebootInProgress,
    bool ComponentBasedServicingPackagesPending,
    bool WindowsUpdateRebootRequired,
    IReadOnlyList<string>? PendingFileRenameOperations,
    string? ActiveComputerName,
    string? ComputerName);

public static class RebootPendingEvaluator
{
    public static bool IsPending(RebootPendingSignals signals)
    {
        if (signals.ComponentBasedServicingRebootPending ||
            signals.ComponentBasedServicingRebootInProgress ||
            signals.ComponentBasedServicingPackagesPending ||
            signals.WindowsUpdateRebootRequired)
        {
            return true;
        }

        if (signals.PendingFileRenameOperations?.Any(
                operation => !string.IsNullOrWhiteSpace(operation)) == true)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(signals.ActiveComputerName) &&
               !string.IsNullOrWhiteSpace(signals.ComputerName) &&
               !string.Equals(
                   signals.ActiveComputerName,
                   signals.ComputerName,
                   StringComparison.OrdinalIgnoreCase);
    }
}
