using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Localization;
using System.Globalization;
using System.Collections.Concurrent;

namespace Potion.Service.Infrastructure
{
    public class InternationalizationService
    {
        private readonly IStringLocalizer<InternationalizationService> _localizer;
        private readonly IMemoryCache _cache;
        private readonly ConcurrentDictionary<string, string> _translationCache = new();

        public InternationalizationService(
            IStringLocalizer<InternationalizationService> localizer,
            IMemoryCache cache)
        {
            _localizer = localizer;
            _cache = cache;
        }

        public string GetLocalizedString(string key)
        {
            var culture = CultureInfo.CurrentUICulture.Name;
            var cacheKey = $"{culture}:{key}";

            // Try to get from concurrent cache first
            if (_translationCache.TryGetValue(cacheKey, out var cachedValue))
            {
                return cachedValue;
            }

            // Try to get from memory cache
            if (_cache.TryGetValue(cacheKey, out cachedValue))
            {
                _translationCache[cacheKey] = cachedValue;
                return cachedValue;
            }

            // Get from localizer and cache the result
            var localizedString = _localizer[key];
            var result = localizedString.ResourceNotFound ? key : localizedString.Value;

            // Cache for 1 hour with sliding expiration
            var cacheOptions = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1),
                SlidingExpiration = TimeSpan.FromMinutes(30)
            };

            _cache.Set(cacheKey, result, cacheOptions);
            _translationCache[cacheKey] = result;

            return result;
        }

        public void SetCulture(string culture)
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            CultureInfo.CurrentUICulture = new CultureInfo(culture);
        }
    }
}