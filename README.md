# Potion - Windows Self-Healing Service

A production-grade Windows system automation and monitoring service with autonomous remediation capabilities.

## Features

- **Autonomous Remediation**: Automatically execute approved remediation commands
- **System Monitoring**: Real-time health monitoring and diagnostics
- **Security**: Signature validation, command allowlist, network security
- **Performance**: Optimized concurrency control and resource management
- **Reliability**: Circuit breaker patterns, retry policies, error handling
- **Observability**: Comprehensive logging and telemetry

## Quick Start

### Prerequisites

- Windows Server 2019/2022 or Windows 10 21H2+
- .NET 8.0 runtime
- Administrator privileges

### Installation

```powershell
# Clone the repository
git clone https://github.com/yourusername/Potion.git
cd Potion

# Build
dotnet build src/Potion.Service/Potion.Service.csproj -c Release

# Install as Windows Service
sc.exe create "PotionService" binPath="C:\Path\To\Potion.Service.exe"

# Start service
sc.exe start "PotionService"
```

### Configuration

Edit `appsettings.json` to configure:
- Command allowlist
- Maintenance windows
- Monitoring thresholds
- Security audit settings

## License

MIT License - See LICENSE file for details

## Contributing

See CONTRIBUTING.md for guidelines

## Support

For issues and questions, please open a GitHub issue

## トレイ常駐アプリ (Potion.Tray)

Windows の状態を定期的に点検し、安全な範囲で自動修復するトレイ常駐アプリです。点検・修復の履歴を保存し、設定に応じて通知します。詳細は [docs/tray-app.md](docs/tray-app.md) を参照してください。
英語、日本語、中国語、韓国語、スペイン語、フランス語、ドイツ語、ポルトガル語、ロシア語に対応しています。
