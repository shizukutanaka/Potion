using System;

namespace Potion.Service.Infrastructure;

/// <summary>
/// 環境変数からの設定値読み込みを支援するユーティリティクラス
/// </summary>
public static class EnvironmentVariableHelper
{
    /// <summary>
    /// 環境変数からlong型の値を読み込みます。読み込みに失敗した場合はデフォルト値を返します。
    /// </summary>
    /// <param name="variableName">環境変数名</param>
    /// <param name="defaultValue">デフォルト値</param>
    /// <returns>環境変数の値またはデフォルト値</returns>
    public static long GetLongFromEnvironment(string variableName, long defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (!string.IsNullOrWhiteSpace(value) &&
            long.TryParse(value, out var parsed) &&
            parsed > 0)
        {
            return parsed;
        }

        return defaultValue;
    }

    /// <summary>
    /// 環境変数からint型の値を読み込みます。読み込みに失敗した場合はデフォルト値を返します。
    /// </summary>
    /// <param name="variableName">環境変数名</param>
    /// <param name="defaultValue">デフォルト値</param>
    /// <returns>環境変数の値またはデフォルト値</returns>
    public static int GetIntFromEnvironment(string variableName, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (!string.IsNullOrWhiteSpace(value) &&
            int.TryParse(value, out var parsed) &&
            parsed > 0)
        {
            return parsed;
        }

        return defaultValue;
    }

    /// <summary>
    /// 環境変数からTimeSpan型の値を読み込みます。読み込みに失敗した場合はデフォルト値を返します。
    /// </summary>
    /// <param name="variableName">環境変数名</param>
    /// <param name="defaultValue">デフォルト値</param>
    /// <returns>環境変数の値またはデフォルト値</returns>
    public static TimeSpan GetTimeSpanFromEnvironment(string variableName, TimeSpan defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (!string.IsNullOrWhiteSpace(value) &&
            TimeSpan.TryParse(value, out var parsed) &&
            parsed > TimeSpan.Zero)
        {
            return parsed;
        }

        return defaultValue;
    }

    /// <summary>
    /// 環境変数からbool型の値を読み込みます。読み込みに失敗した場合はデフォルト値を返します。
    /// </summary>
    /// <param name="variableName">環境変数名</param>
    /// <param name="defaultValue">デフォルト値</param>
    /// <returns>環境変数の値またはデフォルト値</returns>
    public static bool GetBoolFromEnvironment(string variableName, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (!string.IsNullOrWhiteSpace(value) &&
            bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return defaultValue;
    }

    /// <summary>
    /// 環境変数からstring型の値を読み込みます。読み込みに失敗した場合はデフォルト値を返します。
    /// </summary>
    /// <param name="variableName">環境変数名</param>
    /// <param name="defaultValue">デフォルト値</param>
    /// <returns>環境変数の値またはデフォルト値</returns>
    public static string GetStringFromEnvironment(string variableName, string defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }
}
