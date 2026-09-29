using System;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// EnvironmentVariableHelper is the env-var override layer (e.g.
/// POTION_PROCESS_MAX_MEMORY_MB). Its contract: unset, whitespace, unparsable,
/// or non-positive values all fall back to the caller's default — only a
/// strictly positive parsed value wins.
/// </summary>
public sealed class EnvironmentVariableHelperTests : IDisposable
{
    private const string VarName = "POTION_TEST_ENV_HELPER";

    public EnvironmentVariableHelperTests() =>
        Environment.SetEnvironmentVariable(VarName, null);

    public void Dispose() =>
        Environment.SetEnvironmentVariable(VarName, null);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("3.5")]
    public void GetLong_UnsetOrUnparsable_ReturnsDefault(string? value)
    {
        Environment.SetEnvironmentVariable(VarName, value);

        Assert.Equal(42L, EnvironmentVariableHelper.GetLongFromEnvironment(VarName, 42L));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-7")]
    public void GetLong_ZeroOrNegative_ReturnsDefault(string value)
    {
        // Positive-only contract: 0/negative fall back like an invalid value.
        Environment.SetEnvironmentVariable(VarName, value);

        Assert.Equal(42L, EnvironmentVariableHelper.GetLongFromEnvironment(VarName, 42L));
    }

    [Fact]
    public void GetLong_PositiveValue_ReturnsParsed()
    {
        Environment.SetEnvironmentVariable(VarName, "512");

        Assert.Equal(512L, EnvironmentVariableHelper.GetLongFromEnvironment(VarName, 42L));
    }
}
