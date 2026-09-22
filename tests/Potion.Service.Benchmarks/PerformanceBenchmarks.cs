using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Potion.Service.Infrastructure;
using Potion.Service.Options;

namespace Potion.Service.Benchmarks;

internal sealed class StubOptionsMonitor<T> : IOptionsMonitor<T> where T : class
{
    private readonly T _value;

    public StubOptionsMonitor(T value) => _value = value;

    public T CurrentValue => _value;
    public T Get(string? name) => _value;
    public System.IDisposable? OnChange(System.Action<T, string?> listener) => null;
}

/// <summary>
/// セキュリティホットパス（コマンド許可リスト・引数サニタイズ・URL 検証）の
/// 性能リグレッション検出用ベンチマーク。全コマンド実行時に通る経路を計測する。
/// </summary>
[SimpleJob]
[MemoryDiagnoser]
public class CommandGuardBenchmarks
{
    private CommandGuard _commandGuard = null!;

    [GlobalSetup]
    public void Setup()
    {
        var options = new StubOptionsMonitor<RemediationPolicyOptions>(new RemediationPolicyOptions
        {
            CommandAllowlist = new List<string> { "sfc.exe", "dism.exe", "cleanmgr.exe", "chkdsk.exe" }
        });

        _commandGuard = new CommandGuard(
            new CommandValidator(NullLogger<CommandValidator>.Instance, options),
            new ArgumentSanitizer(NullLogger<ArgumentSanitizer>.Instance),
            new UrlValidator(NullLogger<UrlValidator>.Instance),
            new DomainValidator(NullLogger<DomainValidator>.Instance),
            new RateLimiter(NullLogger<RateLimiter>.Instance));
    }

    [Benchmark]
    public string EnsureCommandIsAllowed()
        => _commandGuard.EnsureCommandIsAllowed("sfc.exe");

    [Benchmark]
    public string SanitizeArguments()
        => _commandGuard.SanitizeArguments("/c echo test; rm -rf / && del /f /s /q c:\\* || format c:");

    [Benchmark]
    public bool IsValidUrl()
        => _commandGuard.IsValidUrl("https://example.com/path?query=value&other=123");
}

/// <summary>
/// レート制限チェックの性能リグレッション検出。操作ごとの呼び出し頻度が高い経路。
/// </summary>
[SimpleJob]
[MemoryDiagnoser]
public class RateLimiterBenchmarks
{
    private RateLimiter _rateLimiter = null!;

    [GlobalSetup]
    public void Setup() => _rateLimiter = new RateLimiter(NullLogger<RateLimiter>.Instance);

    [Benchmark]
    public Task<bool> CheckRateLimit()
        => _rateLimiter.CheckRateLimitAsync("DomainValidation", default);
}
