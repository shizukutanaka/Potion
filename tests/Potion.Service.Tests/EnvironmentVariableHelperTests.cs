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

    [Fact]
    public void GetInt_PositiveValue_ReturnsParsed()
    {
        Environment.SetEnvironmentVariable(VarName, "7");

        Assert.Equal(7, EnvironmentVariableHelper.GetIntFromEnvironment(VarName, 3));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("abc")]
    public void GetInt_NonPositiveOrUnparsable_ReturnsDefault(string value)
    {
        Environment.SetEnvironmentVariable(VarName, value);

        Assert.Equal(3, EnvironmentVariableHelper.GetIntFromEnvironment(VarName, 3));
    }

    [Fact]
    public void GetTimeSpan_ValidValue_ReturnsParsed()
    {
        Environment.SetEnvironmentVariable(VarName, "00:05:00");

        Assert.Equal(TimeSpan.FromMinutes(5),
            EnvironmentVariableHelper.GetTimeSpanFromEnvironment(VarName, TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:05:00")]
    [InlineData("not-a-timespan")]
    public void GetTimeSpan_NonPositiveOrUnparsable_ReturnsDefault(string value)
    {
        Environment.SetEnvironmentVariable(VarName, value);

        Assert.Equal(TimeSpan.FromMinutes(1),
            EnvironmentVariableHelper.GetTimeSpanFromEnvironment(VarName, TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    public void GetBool_ParsableValue_ReturnsParsed(string value, bool expected)
    {
        Environment.SetEnvironmentVariable(VarName, value);

        Assert.Equal(expected, EnvironmentVariableHelper.GetBoolFromEnvironment(VarName, !expected));
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    public void GetBool_Unparsable_ReturnsDefault(string value)
    {
        // bool.TryParse accepts only true/false — "1"/"yes" fall back.
        Environment.SetEnvironmentVariable(VarName, value);

        Assert.True(EnvironmentVariableHelper.GetBoolFromEnvironment(VarName, true));
    }

    [Fact]
    public void GetString_Value_ReturnsRaw()
    {
        Environment.SetEnvironmentVariable(VarName, "  padded  ");

        // Whitespace check is IsNullOrWhiteSpace — leading/trailing spaces
        // inside a non-empty value are preserved.
        var padded = EnvironmentVariableHelper.GetStringFromEnvironment(VarName, "fallback");
        Environment.SetEnvironmentVariable(VarName, "value");

        Assert.Equal("  padded  ", padded);
        Assert.Equal("value", EnvironmentVariableHelper.GetStringFromEnvironment(VarName, "fallback"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void GetString_UnsetOrWhitespace_ReturnsDefault(string? value)
    {
        Environment.SetEnvironmentVariable(VarName, value);

        Assert.Equal("fallback", EnvironmentVariableHelper.GetStringFromEnvironment(VarName, "fallback"));
    }
}
