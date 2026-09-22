# Potion - Windows Self-Healing Service

A production-grade Windows system automation and monitoring service with autonomous remediation capabilities.

## Features

- **System Monitoring**: Real-time health monitoring via hosted services (memory statistics, ML-based anomaly detection, event correlation, compliance reporting)
- **Security**: Repair commands constrained to a configurable allowlist (`CommandValidator`) and launched without a shell
- **Reliability**: Polly circuit-breaker/retry resilience pipelines, dependency injection validated at startup
- **Observability**: Serilog logging, OpenTelemetry metrics + traces, Prometheus `/metrics` endpoint
- **Collaboration**: SignalR hub at `/collaboration` and a static dashboard (`wwwroot/`)
- **Remediation**: Approved repair commands (sfc/dism/cleanmgr/chkdsk) through the allowlist — the autonomous repair-execution services (`AutoRecoveryManager`, `PerformanceOptimizer`, `EventDrivenRemediationService`, `PredictiveRemediationService`) are wired behind the `FeatureFlags:RepairExecutionEnabled` flag, off by default (see CHANGELOG)

## Quick Start

### Prerequisites

- Windows Server 2019/2022 or Windows 10 21H2+
- .NET 8.0 runtime
- Administrator privileges

### Build & Run

```powershell
git clone https://github.com/shizukutanaka/Potion.git
cd Potion
dotnet build src/Potion.Service/Potion.Service.csproj -c Release
dotnet run --project src/Potion.Service/Potion.Service.csproj
```

The service listens on `http://localhost:5000` by default and serves:

- `GET /index.html` — dashboard
- `GET /health` — liveness probe (`200 Healthy`)
- `GET /api/health`, `/api/health/metrics`, `/api/health/security`, `/api/health/security/summary` — dashboard data endpoints fed by `ISystemHealthMonitor`
- `GET /metrics` — Prometheus metrics
- `POST /collaboration/negotiate` — SignalR hub

Production HTTPS requires the certificate configured in `appsettings.Production.json` (`Kestrel:Endpoints:Https:Certificate`); inject the PFX password via the `Kestrel__Endpoints__Https__Certificate__Password` environment variable.

### Install as Windows Service

```powershell
sc.exe create "PotionService" binPath="C:\Path\To\Potion.Service.exe"
sc.exe start "PotionService"
```

### Configuration

Bound sections in `appsettings.json` (unbound sections were removed — see CHANGELOG):

- `RemediationPolicy` — repair command allowlist and remediation policy options
- `MemoryMonitor`, `PerformanceOptimizer`, `EventCorrelation`, `Compliance`, `Collaboration` — monitoring-service options (thresholds/`Enabled`); can also be set via env vars, e.g. `MemoryMonitor__MonitoringIntervalSeconds=60`
- `FeatureFlags` — feature toggles read at startup; `RepairExecutionEnabled` (default `false`) activates the autonomous repair-execution services
- `Serilog`, `AllowedHosts`, `Kestrel` — framework settings

## Tests

```powershell
dotnet test Potion.sln   # 29/29 tests — every test targets live code
```

## License

MIT License - See LICENSE file for details

## Contributing

See CONTRIBUTING.md for guidelines

## Support

For issues and questions, please open a GitHub issue
