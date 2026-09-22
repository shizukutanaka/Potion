using System.Runtime.Versioning;

// Potion.Service is a Windows-only service (Windows Service host, WMI, registry, ACL APIs).
// Declaring the supported platform silences CA1416 for intentional Windows-only call sites.
[assembly: SupportedOSPlatform("windows")]
