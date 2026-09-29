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

}
