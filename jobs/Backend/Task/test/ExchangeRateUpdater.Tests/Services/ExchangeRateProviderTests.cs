using ExchangeRateUpdater.Models;
using ExchangeRateUpdater.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text;

namespace ExchangeRateUpdater.Tests.Services
{
    public class ExchangeRateProviderTests
    {
        private readonly Mock<HttpMessageHandler> _handlerMock;
        private readonly Mock<ILogger<ExchangeRateProvider>> _loggerMock;
        private readonly Mock<IOptions<CNBConfigurationOptions>> _optionsMock;
        private readonly Mock<IMemoryCache> _memoryCacheMock;

        private readonly ExchangeRateProvider _provider;

        public ExchangeRateProviderTests()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            _handlerMock = new Mock<HttpMessageHandler>();
            var httpClient = new HttpClient(_handlerMock.Object);

            _optionsMock = new Mock<IOptions<CNBConfigurationOptions>>();
            _optionsMock.Setup(o => o.Value).Returns(new CNBConfigurationOptions
            {
                DataURL = "https://www.cnb.cz/cs/financni_trhy/devizovy_trh/kurzy_devizoveho_trhu/denni_kurz.xml"
            });

            _loggerMock = new Mock<ILogger<ExchangeRateProvider>>();
            _memoryCacheMock = new Mock<IMemoryCache>();

            _provider = new ExchangeRateProvider(_optionsMock.Object, _memoryCacheMock.Object, httpClient, _loggerMock.Object);
        }

        [Fact]
        public async Task GetExchangeRates_ReturnsCachedData_WhenCacheHit()
        {
            //Arrange
            var fakeRates = new List<ExchangeRate>() { new ExchangeRate(new("CZK"), new("USD"), 0.045m)};
            object outValue = fakeRates;

            var currencies = new List<Currency> { new("USD") };

            _memoryCacheMock
                .Setup(cache => cache.TryGetValue("ExchangeRatesCache", out outValue))
                .Returns(true);

            //Act
            var rates = await _provider.GetExchangeRates(currencies);

            //Assert
            rates.Should().ContainSingle()
                .Which.TargetCurrency.Code.Should().Be("USD");
        }

        [Fact]
        public async Task GetExchangeRates_FetchesDataAndStoresInCache_WhenCacheMiss()
        {
            //Arrange
            SettingUpFetchesDataAndStoresInCache();
            BuildingRequestSucceed();
            var currencies = new List<Currency> { new("USD") };

            //Act
            var rates = await _provider.GetExchangeRates(currencies);

            //Assert
            rates.Should().ContainSingle()
                .Which.TargetCurrency.Code.Should().Be("USD");
            rates.Single().Value.Should().Be(22.222m);
        }

        [Fact]
        public async Task GetExchangeRates_EmptyCurrency_ReturnsEmpty()
        {
            //Arrange
            var currencies = new List<Currency>();

            //Act & Assert
            var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => _provider.GetExchangeRates(currencies));
            Assert.Equal("currencies", exception.ParamName);

            _loggerMock.Verify(logger => logger.Log(LogLevel.Error, 
                It.IsAny<EventId>(), 
                It.Is<It.IsAnyType>((v, t) => true), 
                It.IsAny<Exception>(), 
                It.IsAny<Func<It.IsAnyType, Exception, string>>()), Times.Once());
        }

        [Fact]
        public async Task GetExchangeRates_NoMatchingCurrencies_ReturnsEmpty()
        {
            //Arrange
            SettingUpFetchesDataAndStoresInCache();
            BuildingRequestSucceed();
            var currencies = new List<Currency> { new("CAD")};

            //Act
            var rates = await _provider.GetExchangeRates(currencies);

            // Assert
            Assert.Empty(rates);
        }

        [Fact]
        public async Task GetExchangeRates_HttpRequestFailure_LogsErrorAndReturnsEmpty()
        {
            // Arrange
            var response = new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.NotFound
            };
            _handlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);

            var currencies = new List<Currency> { new ("USD") };

            // Act
            var rates = await _provider.GetExchangeRates(currencies);

            // Assert
            rates.Should().BeEmpty();
            _loggerMock.Verify(x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("HTTP request failed")),
                It.IsAny<HttpRequestException>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()), Times.Once());
        }

        private void BuildingRequestSucceed()
        {
            var xmlContent = @"<?xml version=""1.0"" encoding=""UTF-8""?>
                <kurzy banka=""CNB"" datum=""23.07.2025"" poradi=""141"">
                    <tabulka typ=""XML_TYP_CNB_KURZY_DEVIZOVEHO_TRHU"">
                        <radek kod=""USD"" mena=""dolar"" mnozstvi=""1"" kurz=""22.222"" zeme=""USA""/>
                    </tabulka>
                </kurzy>";

            var response = new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(xmlContent)
            };

            _handlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);
        }

        private void SettingUpFetchesDataAndStoresInCache()
        {
            object randomObject;
            _memoryCacheMock
                .Setup(cache => cache.TryGetValue("ExchangeRatesCache", out randomObject))
                .Returns(false);

            _memoryCacheMock
                .Setup(m => m.CreateEntry(It.IsAny<object>()))
                .Returns(Mock.Of<ICacheEntry>);
        }
    }
}
