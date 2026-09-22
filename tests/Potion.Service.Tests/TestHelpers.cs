namespace Potion.Service.Tests;

/// <summary>
/// OS-dependent test gates. The product is a Windows service and several tests
/// exercise cmd.exe/process semantics that do not exist on other platforms.
/// </summary>
public static class TestEnvironment
{
    public static bool IsWindows => System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
        System.Runtime.InteropServices.OSPlatform.Windows);
}
