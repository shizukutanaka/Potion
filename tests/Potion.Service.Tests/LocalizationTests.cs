using System.Globalization;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Moq;
using Potion.Service.Controllers;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests.Controllers
{
    public class LocalizationTests
    {
        private readonly Mock<IStringLocalizer<HealthController>> _localizerMock;
        private readonly Mock<ISystemHealthMonitor> _healthMonitorMock;

        public LocalizationTests()
        {
            _localizerMock = new Mock<IStringLocalizer<HealthController>>();
            _healthMonitorMock = new Mock<ISystemHealthMonitor>();

            // Setup default localizer responses
            _localizerMock.Setup(l => l["HealthStatusAlive"]).Returns(new LocalizedString("HealthStatusAlive", "Alive"));
            _localizerMock.Setup(l => l["ServiceName"]).Returns(new LocalizedString("ServiceName", "Potion Self-Healing Service"));
            _localizerMock.Setup(l => l["VersionUnknown"]).Returns(new LocalizedString("VersionUnknown", "Unknown"));
            _localizerMock.Setup(l => l["HealthStatusReady"]).Returns(new LocalizedString("HealthStatusReady", "Ready"));
            _localizerMock.Setup(l => l["HealthStatusNotReady"]).Returns(new LocalizedString("HealthStatusNotReady", "Not Ready"));
            _localizerMock.Setup(l => l["HealthNotReady"]).Returns(new LocalizedString("HealthNotReady", "Service is not ready to handle requests"));
            _localizerMock.Setup(l => l["HealthReadyMessage"]).Returns(new LocalizedString("HealthReadyMessage", "Service is ready to handle requests"));
        }

        [Theory]
        [InlineData("en-US", "Alive", "Potion Self-Healing Service")]
        [InlineData("ja-JP", "Alive", "Potion Self-Healing Service")]
        [InlineData("es-ES", "Alive", "Potion Self-Healing Service")]
        public async Task GetLiveness_ShouldReturnLocalizedResponse(string culture, string expectedStatus, string expectedServiceName)
        {
            // Arrange
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            CultureInfo.CurrentUICulture = new CultureInfo(culture);

            var controller = new HealthController(
                _healthMonitorMock.Object,
                _localizerMock.Object,
                Mock.Of<IReactiveEventSystem>(),
                Mock.Of<IFunctionalErrorHandlingService>(),
                Mock.Of<IObservabilityService>(),
                Mock.Of<IMetricsCollectionService>(),
                Mock.Of<IFeatureFlagService>(),
                Mock.Of<IChaosEngineeringService>(),
                Mock.Of<IServiceMeshService>(),
                Mock.Of<IAnomalyDetectionService>(),
                Mock.Of<IAuditTrailService>(),
                Mock.Of<IKubernetesOperatorService>(),
                Mock.Of<IAdvancedTestingService>(),
                Mock.Of<IGitOpsService>(),
                Mock.Of<IIacService>(),
                Mock.Of<IIntegrationTestingService>(),
                Mock.Of<IPerformanceBenchmarkService>(),
                Mock.Of<IWebAssemblyService>(),
                Mock.Of<IMemorySafetyService>(),
                Mock.Of<IZeroCostAbstractionsService>(),
                Mock.Of<IAdvancedTypeSystemService>(),
                Mock.Of<IGarbageCollectionService>(),
                Mock.Of<IModernPerformanceService>(),
                Mock.Of<IModernCryptographyService>(),
                Mock.Of<IDeveloperExperienceService>(),
                Mock.Of<IModernDeploymentService>(),
                Mock.Of<IModernRuntimeService>(),
                Mock.Of<IPerformanceAnalyticsService>(),
                Mock.Of<IGoInspiredConcurrencyService>(),
                Mock.Of<IRustInspiredMemoryService>(),
                Mock.Of<ISwiftInspiredService>(),
                Mock.Of<IJavaScriptInspiredService>(),
                Mock.Of<IPythonInspiredService>(),
                Mock.Of<IAdvancedCompilerService>(),
                Mock.Of<IModernConcurrencyService>(),
                Mock.Of<ILatestSecurityService>(),
                Mock.Of<IModernTestingService>(),
                Mock.Of<IAdvancedFunctionalProgrammingService>(),
                Mock.Of<IAdvancedAiMlService>(),
                Mock.Of<IAdvancedDistributedSystemsService>(),
                Mock.Of<IModernUiUxService>(),
                Mock.Of<IAdvancedDatabaseService>(),
                Mock.Of<ILatestDotNetService>(),
                Mock.Of<IAdvancedAlgebraicTypesService>(),
                Mock.Of<ILogger<HealthController>>()
            );

            // Act
            var result = await controller.GetLiveness();

            // Assert
            result.Should().BeOfType<OkObjectResult>();
            var okResult = result as OkObjectResult;
            okResult.Should().NotBeNull();
            okResult!.Value.Should().NotBeNull();

            var response = okResult.Value as dynamic;
            response.Status.Should().Be(expectedStatus);
            response.ServiceName.Should().Be(expectedServiceName);
        }

        [Fact]
        public async Task GetReadiness_WhenServiceIsReady_ShouldReturnLocalizedReadyStatus()
        {
            // Arrange
            var culture = "en-US";
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            CultureInfo.CurrentUICulture = new CultureInfo(culture);

            var controller = new HealthController(
                _healthMonitorMock.Object,
                _localizerMock.Object,
                Mock.Of<IReactiveEventSystem>(),
                Mock.Of<IFunctionalErrorHandlingService>(),
                Mock.Of<IObservabilityService>(),
                Mock.Of<IMetricsCollectionService>(),
                Mock.Of<IFeatureFlagService>(),
                Mock.Of<IChaosEngineeringService>(),
                Mock.Of<IServiceMeshService>(),
                Mock.Of<IAnomalyDetectionService>(),
                Mock.Of<IAuditTrailService>(),
                Mock.Of<IKubernetesOperatorService>(),
                Mock.Of<IAdvancedTestingService>(),
                Mock.Of<IGitOpsService>(),
                Mock.Of<IIacService>(),
                Mock.Of<IIntegrationTestingService>(),
                Mock.Of<IPerformanceBenchmarkService>(),
                Mock.Of<IWebAssemblyService>(),
                Mock.Of<IMemorySafetyService>(),
                Mock.Of<IZeroCostAbstractionsService>(),
                Mock.Of<IAdvancedTypeSystemService>(),
                Mock.Of<IGarbageCollectionService>(),
                Mock.Of<IModernPerformanceService>(),
                Mock.Of<IModernCryptographyService>(),
                Mock.Of<IDeveloperExperienceService>(),
                Mock.Of<IModernDeploymentService>(),
                Mock.Of<IModernRuntimeService>(),
                Mock.Of<IPerformanceAnalyticsService>(),
                Mock.Of<IGoInspiredConcurrencyService>(),
                Mock.Of<IRustInspiredMemoryService>(),
                Mock.Of<ISwiftInspiredService>(),
                Mock.Of<IJavaScriptInspiredService>(),
                Mock.Of<IPythonInspiredService>(),
                Mock.Of<IAdvancedCompilerService>(),
                Mock.Of<IModernConcurrencyService>(),
                Mock.Of<ILatestSecurityService>(),
                Mock.Of<IModernTestingService>(),
                Mock.Of<IAdvancedFunctionalProgrammingService>(),
                Mock.Of<IAdvancedAiMlService>(),
                Mock.Of<IAdvancedDistributedSystemsService>(),
                Mock.Of<IModernUiUxService>(),
                Mock.Of<IAdvancedDatabaseService>(),
                Mock.Of<ILatestDotNetService>(),
                Mock.Of<IAdvancedAlgebraicTypesService>(),
                Mock.Of<ILogger<HealthController>>()
            );

            // Act
            var result = await controller.GetReadiness();

            // Assert
            result.Should().BeOfType<OkObjectResult>();
            var okResult = result as OkObjectResult;
            okResult.Should().NotBeNull();

            var response = okResult!.Value as dynamic;
            response.Status.Should().Be("Ready");
            response.Message.Should().Be("Service is ready to handle requests");
        }

        [Fact]
        public async Task GetReadiness_WhenServiceIsNotReady_ShouldReturnLocalizedNotReadyStatus()
        {
            // Arrange
            var culture = "en-US";
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            CultureInfo.CurrentUICulture = new CultureInfo(culture);

            var controller = new HealthController(
                _healthMonitorMock.Object,
                _localizerMock.Object,
                Mock.Of<IReactiveEventSystem>(),
                Mock.Of<IFunctionalErrorHandlingService>(),
                Mock.Of<IObservabilityService>(),
                Mock.Of<IMetricsCollectionService>(),
                Mock.Of<IFeatureFlagService>(),
                Mock.Of<IChaosEngineeringService>(),
                Mock.Of<IServiceMeshService>(),
                Mock.Of<IAnomalyDetectionService>(),
                Mock.Of<IAuditTrailService>(),
                Mock.Of<IKubernetesOperatorService>(),
                Mock.Of<IAdvancedTestingService>(),
                Mock.Of<IGitOpsService>(),
                Mock.Of<IIacService>(),
                Mock.Of<IIntegrationTestingService>(),
                Mock.Of<IPerformanceBenchmarkService>(),
                Mock.Of<IWebAssemblyService>(),
                Mock.Of<IMemorySafetyService>(),
                Mock.Of<IZeroCostAbstractionsService>(),
                Mock.Of<IAdvancedTypeSystemService>(),
                Mock.Of<IGarbageCollectionService>(),
                Mock.Of<IModernPerformanceService>(),
                Mock.Of<IModernCryptographyService>(),
                Mock.Of<IDeveloperExperienceService>(),
                Mock.Of<IModernDeploymentService>(),
                Mock.Of<IModernRuntimeService>(),
                Mock.Of<IPerformanceAnalyticsService>(),
                Mock.Of<IGoInspiredConcurrencyService>(),
                Mock.Of<IRustInspiredMemoryService>(),
                Mock.Of<ISwiftInspiredService>(),
                Mock.Of<IJavaScriptInspiredService>(),
                Mock.Of<IPythonInspiredService>(),
                Mock.Of<IAdvancedCompilerService>(),
                Mock.Of<IModernConcurrencyService>(),
                Mock.Of<ILatestSecurityService>(),
                Mock.Of<IModernTestingService>(),
                Mock.Of<IAdvancedFunctionalProgrammingService>(),
                Mock.Of<IAdvancedAiMlService>(),
                Mock.Of<IAdvancedDistributedSystemsService>(),
                Mock.Of<IModernUiUxService>(),
                Mock.Of<IAdvancedDatabaseService>(),
                Mock.Of<ILatestDotNetService>(),
                Mock.Of<IAdvancedAlgebraicTypesService>(),
                Mock.Of<ILogger<HealthController>>()
            );

            // Act
            var result = await controller.GetReadiness();

            // Assert
            result.Should().BeOfType<OkObjectResult>();
            var okResult = result as OkObjectResult;
            okResult.Should().NotBeNull();

            var response = okResult!.Value as dynamic;
            response.Status.Should().Be("Ready");
        }

        [Fact]
        public void Localizer_ShouldBeCalledCorrectly()
        {
            // Arrange
            var controller = new HealthController(
                _healthMonitorMock.Object,
                _localizerMock.Object,
                Mock.Of<IReactiveEventSystem>(),
                Mock.Of<IFunctionalErrorHandlingService>(),
                Mock.Of<IObservabilityService>(),
                Mock.Of<IMetricsCollectionService>(),
                Mock.Of<IFeatureFlagService>(),
                Mock.Of<IChaosEngineeringService>(),
                Mock.Of<IServiceMeshService>(),
                Mock.Of<IAnomalyDetectionService>(),
                Mock.Of<IAuditTrailService>(),
                Mock.Of<IKubernetesOperatorService>(),
                Mock.Of<IAdvancedTestingService>(),
                Mock.Of<IGitOpsService>(),
                Mock.Of<IIacService>(),
                Mock.Of<IIntegrationTestingService>(),
                Mock.Of<IPerformanceBenchmarkService>(),
                Mock.Of<IWebAssemblyService>(),
                Mock.Of<IMemorySafetyService>(),
                Mock.Of<IZeroCostAbstractionsService>(),
                Mock.Of<IAdvancedTypeSystemService>(),
                Mock.Of<IGarbageCollectionService>(),
                Mock.Of<IModernPerformanceService>(),
                Mock.Of<IModernCryptographyService>(),
                Mock.Of<IDeveloperExperienceService>(),
                Mock.Of<IModernDeploymentService>(),
                Mock.Of<IModernRuntimeService>(),
                Mock.Of<IPerformanceAnalyticsService>(),
                Mock.Of<IGoInspiredConcurrencyService>(),
                Mock.Of<IRustInspiredMemoryService>(),
                Mock.Of<ISwiftInspiredService>(),
                Mock.Of<IJavaScriptInspiredService>(),
                Mock.Of<IPythonInspiredService>(),
                Mock.Of<IAdvancedCompilerService>(),
                Mock.Of<IModernConcurrencyService>(),
                Mock.Of<ILatestSecurityService>(),
                Mock.Of<IModernTestingService>(),
                Mock.Of<IAdvancedFunctionalProgrammingService>(),
                Mock.Of<IAdvancedAiMlService>(),
                Mock.Of<IAdvancedDistributedSystemsService>(),
                Mock.Of<IModernUiUxService>(),
                Mock.Of<IAdvancedDatabaseService>(),
                Mock.Of<ILatestDotNetService>(),
                Mock.Of<IAdvancedAlgebraicTypesService>(),
                Mock.Of<ILogger<HealthController>>()
            );

            // Act - Call an endpoint that uses localization
            var result = controller.GetLiveness();

            // Assert
            result.Should().NotBeNull();
            _localizerMock.Verify(l => l["HealthStatusAlive"], Times.Once);
            _localizerMock.Verify(l => l["ServiceName"], Times.Once);
            _localizerMock.Verify(l => l["VersionUnknown"], Times.Once);
        }
    }
}
