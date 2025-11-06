using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Localization;
using Moq;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests
{
    public class InternationalizationServiceTests
    {
        private readonly InternationalizationService _service;
        private readonly Mock<IStringLocalizer<InternationalizationService>> _localizerMock;

        public InternationalizationServiceTests()
        {
            _localizerMock = new Mock<IStringLocalizer<InternationalizationService>>();
            _service = new InternationalizationService(_localizerMock.Object);
        }

        [Theory]
        [InlineData("en", "Hello World")]
        [InlineData("ja", "こんにちは世界")]
        [InlineData("es", "Hola Mundo")]
        [InlineData("fr", "Bonjour le Monde")]
        [InlineData("de", "Hallo Welt")]
        public void GetLocalizedString_ShouldReturnCorrectTranslation(string culture, string expectedValue)
        {
            // Arrange
            var key = "TestKey";
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            CultureInfo.CurrentUICulture = new CultureInfo(culture);

            _localizerMock.Setup(l => l[key]).Returns(new LocalizedString(key, expectedValue));

            // Act
            var result = _service.GetLocalizedString(key);

            // Assert
            result.Should().Be(expectedValue);
            _localizerMock.Verify(l => l[key], Times.Once);
        }

        [Fact]
        public void GetLocalizedString_ShouldReturnKey_WhenTranslationNotFound()
        {
            // Arrange
            var key = "NonExistentKey";
            var expectedValue = key; // Should return the key itself when not found

            _localizerMock.Setup(l => l[key]).Returns(new LocalizedString(key, expectedValue, true));

            // Act
            var result = _service.GetLocalizedString(key);

            // Assert
            result.Should().Be(expectedValue);
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("ja-JP")]
        [InlineData("es-ES")]
        [InlineData("fr-FR")]
        [InlineData("de-DE")]
        public void SetCulture_ShouldSetCurrentCultureAndUICulture(string culture)
        {
            // Act
            _service.SetCulture(culture);

            // Assert
            CultureInfo.CurrentCulture.Name.Should().Be(culture);
            CultureInfo.CurrentUICulture.Name.Should().Be(culture);
        }

        [Fact]
        public void Constructor_ShouldInitializeWithLocalizer()
        {
            // Act & Assert
            _service.Should().NotBeNull();
            _localizerMock.VerifyNoOtherCalls();
        }
    }
}
