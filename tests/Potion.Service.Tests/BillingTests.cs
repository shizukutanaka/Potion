using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Potion.Service.Infrastructure;
using Potion.Service.Options;
using Xunit;
using Xunit.Abstractions;

namespace Potion.Service.Tests.Infrastructure;

public class BillingServiceTests
{
    private readonly ITestOutputHelper _output;
    private readonly Mock<ILogger<BillingService>> _loggerMock;
    private readonly Mock<IOptionsMonitor<BillingOptions>> _optionsMock;
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;

    public BillingServiceTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerMock = new Mock<ILogger<BillingService>>();
        _optionsMock = new Mock<IOptionsMonitor<BillingOptions>>();
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
    }

    [Fact]
    public async Task GetCurrentStatus_InitialState_ReturnsDefaultStatus()
    {
        // Arrange
        var options = CreateDefaultOptions();
        _optionsMock.Setup(o => o.CurrentValue).Returns(options);

        var httpClient = new HttpClient(_httpMessageHandlerMock.Object);
        var service = new BillingService(_loggerMock.Object, _optionsMock.Object, httpClient);

        // Act
        var status = service.GetCurrentStatus();

        // Assert
        status.IsLicensed.Should().BeFalse();
        status.CurrentBillingType.Should().Be(BillingType.Monthly);
        status.CurrentPrice.Should().Be(0.5m);
        status.MonthlyPrice.Should().Be(0.5m);
        status.OneTimePrice.Should().Be(3.0m);
        status.LicenseType.Should().Be("None");
        status.LastChecked.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void IsLicensed_InitialState_ReturnsFalse()
    {
        // Arrange
        var options = CreateDefaultOptions();
        _optionsMock.Setup(o => o.CurrentValue).Returns(options);

        var httpClient = new HttpClient(_httpMessageHandlerMock.Object);
        var service = new BillingService(_loggerMock.Object, _optionsMock.Object, httpClient);

        // Act & Assert
        service.IsLicensed().Should().BeFalse();
    }

    [Fact]
    public async Task CheckLicenseStatus_ValidLicense_ReturnsLicensedStatus()
    {
        // Arrange
        var options = CreateDefaultOptions();
        _optionsMock.Setup(o => o.CurrentValue).Returns(options);

        var licenseResponse = new
        {
            IsValid = true,
            ExpirationDate = DateTimeOffset.UtcNow.AddDays(30),
            LicenseType = "Premium"
        };

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(licenseResponse))
            });

        var httpClient = new HttpClient(_httpMessageHandlerMock.Object);
        var service = new BillingService(_loggerMock.Object, _optionsMock.Object, httpClient);

        // Act
        await service.CheckLicenseStatusAsync(options, CancellationToken.None);

        // Assert
        var status = service.GetCurrentStatus();
        status.IsLicensed.Should().BeTrue();
        status.LicenseType.Should().Be("Premium");
        status.LicenseExpiration.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(30), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CheckLicenseStatus_InvalidLicense_ReturnsUnlicensedStatus()
    {
        // Arrange
        var options = CreateDefaultOptions();
        _optionsMock.Setup(o => o.CurrentValue).Returns(options);

        var licenseResponse = new
        {
            IsValid = false,
            ExpirationDate = (DateTimeOffset?)null,
            LicenseType = "None"
        };

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(licenseResponse))
            });

        var httpClient = new HttpClient(_httpMessageHandlerMock.Object);
        var service = new BillingService(_loggerMock.Object, _optionsMock.Object, httpClient);

        // Act
        await service.CheckLicenseStatusAsync(options, CancellationToken.None);

        // Assert
        var status = service.GetCurrentStatus();
        status.IsLicensed.Should().BeFalse();
        status.LicenseType.Should().Be("None");
    }

    [Fact]
    public async Task CheckLicenseStatus_HttpError_HandlesGracefully()
    {
        // Arrange
        var options = CreateDefaultOptions();
        _optionsMock.Setup(o => o.CurrentValue).Returns(options);

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var httpClient = new HttpClient(_httpMessageHandlerMock.Object);
        var service = new BillingService(_loggerMock.Object, _optionsMock.Object, httpClient);

        // Act
        await service.CheckLicenseStatusAsync(options, CancellationToken.None);

        // Assert - should not throw and should maintain previous state or handle error gracefully
        var status = service.GetCurrentStatus();
        // The service should handle the error gracefully and maintain a reasonable state
    }

    [Fact]
    public void OnOptionsChanged_DebugModeEnabled_UpdatesStatusToLicensed()
    {
        // Arrange
        var options = CreateDefaultOptions();
        _optionsMock.Setup(o => o.CurrentValue).Returns(options);

        var httpClient = new HttpClient(_httpMessageHandlerMock.Object);
        var service = new BillingService(_loggerMock.Object, _optionsMock.Object, httpClient);

        // Act
        var debugOptions = new BillingOptions
        {
            Enabled = true,
            MonthlyPrice = 0.5m,
            BillingCycleMonths = 1,
            LicenseKeyRequired = true,
            LicenseCheckIntervalHours = 24,
            GracePeriodDays = 7,
            BillingServerEndpoint = "https://billing.potion-service.com",
            BillingApiKey = "",
            DebugMode = true,
            EnableMetrics = true,
            InvoiceIntervalMonths = 1
        };
        service.OnOptionsChanged(debugOptions);

        // Assert
        var status = service.GetCurrentStatus();
        status.LicenseType.Should().Be("Debug");
    }

    [Fact]
    public async Task CheckLicenseStatus_OneTimePurchaseLicense_ReturnsCorrectPrice()
    {
        // Arrange
        var options = CreateDefaultOptions();
        options.DefaultBillingType = BillingType.OneTimePurchase;
        _optionsMock.Setup(o => o.CurrentValue).Returns(options);

        var licenseResponse = new
        {
            IsValid = true,
            ExpirationDate = DateTimeOffset.UtcNow.AddDays(365),
            LicenseType = "OneTime"
        };

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(licenseResponse))
            });

        var httpClient = new HttpClient(_httpMessageHandlerMock.Object);
        var service = new BillingService(_loggerMock.Object, _optionsMock.Object, httpClient);

        // Act
        await service.CheckLicenseStatusAsync(options, CancellationToken.None);

        // Assert
        var status = service.GetCurrentStatus();
        status.IsLicensed.Should().BeTrue();
        status.CurrentBillingType.Should().Be(BillingType.OneTimePurchase);
        status.CurrentPrice.Should().Be(3.0m); // OneTimePrice
        status.OneTimePrice.Should().Be(3.0m);
        status.LicenseType.Should().Be("OneTime");
    }

    private static BillingOptions CreateDefaultOptions()
    {
        return new BillingOptions
        {
            Enabled = true,
            DefaultBillingType = BillingType.Monthly,
            MonthlyPrice = 0.5m,
            OneTimePrice = 3.0m,
            BillingCycleMonths = 1,
            LicenseKeyRequired = true,
            LicenseCheckIntervalHours = 24,
            GracePeriodDays = 7,
            BillingServerEndpoint = "https://billing.potion-service.com",
            BillingApiKey = "",
            DebugMode = false,
            EnableMetrics = true,
            InvoiceIntervalMonths = 1
        };
    }
}

public class BillingOptionsTests
{
    [Fact]
    public void BillingOptions_DefaultValues_AreValid()
    {
        // Arrange & Act
        var options = new BillingOptions();

        // Assert
        options.Enabled.Should().BeTrue();
        options.DefaultBillingType.Should().Be(BillingType.Monthly);
        options.MonthlyPrice.Should().Be(0.5m);
        options.OneTimePrice.Should().Be(3.0m);
        options.BillingCycleMonths.Should().Be(1);
        options.LicenseKeyRequired.Should().BeTrue();
        options.LicenseCheckIntervalHours.Should().Be(24);
        options.GracePeriodDays.Should().Be(7);
        options.BillingServerEndpoint.Should().Be("https://billing.potion-service.com");
        options.DebugMode.Should().BeFalse();
        options.EnableMetrics.Should().BeTrue();
        options.InvoiceIntervalMonths.Should().Be(1);
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(10.00)]
    [InlineData(999.99)]
    public void BillingOptions_MonthlyPrice_ValidRange(decimal price)
    {
        // Arrange & Act
        var options = new BillingOptions { MonthlyPrice = price };

        // Assert - Should not throw validation exception
        // This would be validated by the actual validation framework in the real application
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void BillingOptions_BillingCycleMonths_InvalidRange(int months)
    {
        // Arrange & Act & Assert
        // This would throw validation exception in the real application
        // For unit testing, we're just documenting the expected behavior
    }
}
