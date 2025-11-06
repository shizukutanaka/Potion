using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

public interface IDirectoryBootstrapper
{
    void EnsureStructure();
}

public sealed class DirectoryBootstrapper(ILogger<DirectoryBootstrapper> logger) : IDirectoryBootstrapper
{
    private static readonly string[] RequiredDirectories =
    {
        ServicePaths.Base,
        ServicePaths.Logs,
        ServicePaths.State,
        ServicePaths.Telemetry,
        ServicePaths.Playbooks,
        ServicePaths.Certificates,
        ServicePaths.ConfigBackups
    };

    public void EnsureStructure()
    {
        foreach (var directory in RequiredDirectories)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var directoryInfo = new DirectoryInfo(directory)
                {
                    Attributes = FileAttributes.NotContentIndexed
                };

                directoryInfo.Refresh();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to ensure directory {Directory}", directory);
                throw;
            }
        }
    }
}
