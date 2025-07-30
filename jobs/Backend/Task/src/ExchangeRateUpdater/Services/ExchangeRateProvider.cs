using ExchangeRateUpdater.Extensions;
using ExchangeRateUpdater.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Xml;
using System.Xml.Linq;

namespace ExchangeRateUpdater.Services
{
    public interface IExchangeRateProvider
    {
        Task<IEnumerable<ExchangeRate>> GetExchangeRates(IEnumerable<Currency> currencies);
    }

    /// <summary>
    /// Information to retrieve the information was taken from:
    /// https://www.cnb.cz/cs/casto-kladene-dotazy/Kurzy-devizoveho-trhu-na-www-strankach-CNB
    /// There are three different formats to receive the information (HTML, TXT, XML).
    /// </summary>
    public class ExchangeRateProvider : IExchangeRateProvider
    {
        private readonly ILogger<ExchangeRateProvider> _logger;
        private readonly IMemoryCache _cache;
        private readonly HttpClient _httpClient;
        private readonly string _url;

        private const string DefaultExchangeCode = "CZK";
        private const string DefaultCacheKey = "ExchangeRatesCache";

        public ExchangeRateProvider(IOptions<CNBConfigurationOptions> options,
            IMemoryCache cache,
            HttpClient httpClient,
            ILogger<ExchangeRateProvider> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _cache = cache;

            _url = options.Value.DataURL;
        }

        /// <summary>
        /// Should return exchange rates among the specified currencies that are defined by the source. But only those defined
        /// by the source, do not return calculated exchange rates. E.g. if the source contains "CZK/USD" but not "USD/CZK",
        /// do not return exchange rate "USD/CZK" with value calculated as 1 / "CZK/USD". If the source does not provide
        /// some of the currencies, ignore them.
        /// </summary>
        public async Task<IEnumerable<ExchangeRate>> GetExchangeRates(IEnumerable<Currency> currencies)
        {
            if (currencies is null || !currencies.Any())
            {
                _logger.LogError("GetExchangeRates: You must to specify currencies to calculate exchange rates.");
                throw new ArgumentNullException(nameof(currencies));
            }

            try
            {
                if(!_cache.TryGetValue(DefaultCacheKey, out IEnumerable<ExchangeRate> cachedRates))
                {
                    _logger.LogInformation("GetExchangeRates: Getting list of exchange rates from {CNBURL}", _url);
                    var response = await _httpClient.GetAsync(_url);
                    response.EnsureSuccessStatusCode();

                    var responseString = await response.Content.ReadAsStringAsync();
                    var doc = XDocument.Parse(responseString);

                    cachedRates = doc.Descendants("radek")
                        .Select(attr => new ExchangeRate(
                                new(DefaultExchangeCode),
                                new(attr.GetExchangeCode()),
                                attr.GetExchangeRate() / attr.GetExchangeAmount())
                        );

                    // Store in cache for 1 hour
                    _cache.Set(DefaultCacheKey, cachedRates, TimeSpan.FromHours(1));
                }

                var currencyCodes = currencies.Select(c => c.Code).ToHashSet();

                _logger.LogInformation("GetExchangeRates: Calculating exchange rates for {CurrencyCodes}", string.Join(",", currencyCodes));

                var rates = cachedRates
                    .Where(rate => currencyCodes.Contains(rate.TargetCurrency.Code));

                return rates;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError("GetExchangeRates: HTTP request failed for URL: {URL}", _url);
                return Enumerable.Empty<ExchangeRate>();
            }
            catch (XmlException ex)
            {
                _logger.LogError("GetExchangeRates: XML parsing failed {XmlErrorMessage}", ex.Message);
                return Enumerable.Empty<ExchangeRate>();
            }
            catch(TaskCanceledException ex)
            {
                _logger.LogError("GetExchangeRates: Request time out {TimeoutErrorMessage}", ex.Message);
                return Enumerable.Empty<ExchangeRate>();
            }
        }
    }
}
