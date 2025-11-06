using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// 検索機能の強化サービス
/// 全文検索とフィルタリング機能の改善を実装
/// </summary>
public interface IAdvancedSearchService
{
    Task<SearchResult> SearchAsync(string query, SearchOptions options);
    Task<SearchResult> SearchWithFiltersAsync(string query, Dictionary<string, object> filters, SearchOptions options);
    Task<AutoCompleteResult> GetAutoCompleteSuggestionsAsync(string query, int maxSuggestions = 10);
    Task<SearchIndex> BuildSearchIndexAsync(IEnumerable<SearchDocument> documents);
    Task<bool> UpdateSearchIndexAsync(string documentId, SearchDocument document);
    Task<bool> RemoveFromSearchIndexAsync(string documentId);
    Task<SearchAnalytics> GetSearchAnalyticsAsync(DateTime? startDate = null, DateTime? endDate = null);
    Task<IEnumerable<string>> GetPopularSearchTermsAsync(int limit = 20);
}

/// <summary>
/// 検索オプション
/// </summary>
public class SearchOptions
{
    public SearchType Type { get; set; } = SearchType.FullText;
    public int PageSize { get; set; } = 20;
    public int PageNumber { get; set; } = 1;
    public SortOrder SortBy { get; set; } = SortOrder.Relevance;
    public bool IncludeHighlights { get; set; } = true;
    public Dictionary<string, object> AdditionalOptions { get; set; } = new();
}

/// <summary>
/// 検索タイプ
/// </summary>
public enum SearchType
{
    FullText,
    Fuzzy,
    Phrase,
    Boolean,
    Wildcard
}

/// <summary>
/// ソート順序
/// </summary>
public enum SortOrder
{
    Relevance,
    Date,
    Title,
    Popularity
}

/// <summary>
/// 検索結果
/// </summary>
public class SearchResult
{
    public List<SearchResultItem> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
    public TimeSpan SearchTime { get; set; }
    public List<string> Suggestions { get; set; } = new();
    public Dictionary<string, int> FacetCounts { get; set; } = new();
}

/// <summary>
/// 検索結果項目
/// </summary>
public class SearchResultItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public double RelevanceScore { get; set; }
    public DateTime LastModified { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
    public List<string> Highlights { get; set; } = new();
    public string Snippet { get; set; } = string.Empty;
}

/// <summary>
/// オートコンプリート結果
/// </summary>
public class AutoCompleteResult
{
    public List<string> Suggestions { get; set; } = new();
    public List<string> Categories { get; set; } = new();
    public Dictionary<string, int> Popularity { get; set; } = new();
}

/// <summary>
/// 検索ドキュメント
/// </summary>
public class SearchDocument
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public Dictionary<string, string> Metadata { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 検索インデックス
/// </summary>
public class SearchIndex
{
    public Dictionary<string, SearchDocument> Documents { get; set; } = new();
    public Dictionary<string, HashSet<string>> InvertedIndex { get; set; } = new();
    public Dictionary<string, int> TermFrequency { get; set; } = new();
    public int TotalDocuments { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 検索分析情報
/// </summary>
public class SearchAnalytics
{
    public int TotalSearches { get; set; }
    public int UniqueUsers { get; set; }
    public double AverageResultsPerSearch { get; set; }
    public double ClickThroughRate { get; set; }
    public List<SearchTermAnalytics> TopSearchTerms { get; set; } = new();
    public Dictionary<string, int> SearchesByHour { get; set; } = new();
    public Dictionary<string, int> SearchesByDay { get; set; } = new();
}

/// <summary>
/// 検索語句分析情報
/// </summary>
public class SearchTermAnalytics
{
    public string Term { get; set; } = string.Empty;
    public int SearchCount { get; set; }
    public double AverageResults { get; set; }
    public double ClickThroughRate { get; set; }
}

/// <summary>
/// 高度な検索サービス実装
/// </summary>
public class AdvancedSearchService : IAdvancedSearchService
{
    private readonly ILogger<AdvancedSearchService> _logger;
    private SearchIndex _searchIndex = new();
    private readonly List<SearchAnalytics> _searchHistory = new();

    public AdvancedSearchService(ILogger<AdvancedSearchService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SearchResult> SearchAsync(string query, SearchOptions options)
    {
        return await SearchWithFiltersAsync(query, new Dictionary<string, object>(), options);
    }

    public async Task<SearchResult> SearchWithFiltersAsync(string query, Dictionary<string, object> filters, SearchOptions options)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new SearchResult
        {
            PageNumber = options.PageNumber,
            PageSize = options.PageSize
        };

        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                result.Items = new List<SearchResultItem>();
                result.TotalCount = 0;
                return result;
            }

            // クエリの前処理
            var processedQuery = PreprocessQuery(query);

            // 検索実行
            var matchingDocuments = PerformSearch(processedQuery, filters);

            // ソート
            matchingDocuments = ApplySorting(matchingDocuments, options.SortBy);

            // ページネーション
            result.TotalCount = matchingDocuments.Count;
            result.TotalPages = (int)Math.Ceiling((double)result.TotalCount / options.PageSize);

            var pagedDocuments = matchingDocuments
                .Skip((options.PageNumber - 1) * options.PageSize)
                .Take(options.PageSize)
                .ToList();

            // 結果項目の作成
            foreach (var doc in pagedDocuments)
            {
                var item = new SearchResultItem
                {
                    Id = doc.Id,
                    Title = doc.Title,
                    Content = doc.Content,
                    Url = GenerateUrl(doc),
                    RelevanceScore = CalculateRelevanceScore(doc, processedQuery),
                    LastModified = doc.LastModified,
                    Metadata = doc.Metadata
                };

                if (options.IncludeHighlights)
                {
                    item.Highlights = GenerateHighlights(doc.Content, processedQuery);
                    item.Snippet = GenerateSnippet(doc.Content, processedQuery);
                }

                result.Items.Add(item);
            }

            // 検索候補の生成
            if (!result.Items.Any())
            {
                result.Suggestions = GenerateSearchSuggestions(query);
            }

            // ファセットカウントの生成
            result.FacetCounts = GenerateFacetCounts(matchingDocuments);

            stopwatch.Stop();
            result.SearchTime = stopwatch.Elapsed;

            // 検索履歴の記録
            RecordSearch(query, result.TotalCount, result.SearchTime);

            _logger.LogInformation("Search completed: '{Query}' returned {ResultCount} results in {SearchTime}ms",
                query, result.TotalCount, result.SearchTime.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            result.SearchTime = stopwatch.Elapsed;

            _logger.LogError(ex, "Error during search for query: {Query}", query);

            result.Items = new List<SearchResultItem>();
            result.TotalCount = 0;
            result.Suggestions = new List<string> { "Try a different search term", "Check your spelling", "Use fewer keywords" };

            return result;
        }
    }

    public async Task<AutoCompleteResult> GetAutoCompleteSuggestionsAsync(string query, int maxSuggestions = 10)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            {
                return new AutoCompleteResult();
            }

            var suggestions = new List<string>();
            var categories = new List<string>();
            var popularity = new Dictionary<string, int>();

            // タイトルからの候補
            var titleMatches = _searchIndex.Documents.Values
                .Where(doc => doc.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(doc => doc.Title)
                .Take(maxSuggestions / 2);

            suggestions.AddRange(titleMatches);

            // コンテンツからの候補
            var contentMatches = _searchIndex.Documents.Values
                .Where(doc => doc.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(doc => ExtractRelevantPhrase(doc.Content, query))
                .Where(phrase => !string.IsNullOrEmpty(phrase))
                .Take(maxSuggestions / 2);

            suggestions.AddRange(contentMatches);

            // カテゴリからの候補
            categories = _searchIndex.Documents.Values
                .Where(doc => doc.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(doc => doc.Category)
                .Distinct()
                .Take(maxSuggestions / 3)
                .ToList();

            // 人気検索語句からの候補（実際の実装では履歴から取得）
            var popularTerms = await GetPopularSearchTermsAsync(maxSuggestions);
            suggestions.AddRange(popularTerms.Where(term => term.Contains(query, StringComparison.OrdinalIgnoreCase)));

            return new AutoCompleteResult
            {
                Suggestions = suggestions.Distinct().Take(maxSuggestions).ToList(),
                Categories = categories,
                Popularity = popularity
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating autocomplete suggestions for query: {Query}", query);
            return new AutoCompleteResult();
        }
    }

    public async Task<SearchIndex> BuildSearchIndexAsync(IEnumerable<SearchDocument> documents)
    {
        try
        {
            _logger.LogInformation("Building search index with {DocumentCount} documents", documents.Count());

            _searchIndex = new SearchIndex();
            var invertedIndex = new Dictionary<string, HashSet<string>>();
            var termFrequency = new Dictionary<string, int>();

            foreach (var doc in documents)
            {
                _searchIndex.Documents[doc.Id] = doc;

                // ドキュメントのテキストをトークン化
                var tokens = TokenizeDocument(doc);

                foreach (var token in tokens)
                {
                    // 転置インデックスの構築
                    if (!invertedIndex.TryGetValue(token, out var docIds))
                    {
                        docIds = new HashSet<string>();
                        invertedIndex[token] = docIds;
                    }
                    docIds.Add(doc.Id);

                    // 語句頻度のカウント
                    var key = $"{token}:{doc.Id}";
                    termFrequency[key] = termFrequency.GetValueOrDefault(key, 0) + 1;
                }
            }

            _searchIndex.InvertedIndex = invertedIndex;
            _searchIndex.TermFrequency = termFrequency;
            _searchIndex.TotalDocuments = documents.Count();
            _searchIndex.LastUpdated = DateTime.UtcNow;

            _logger.LogInformation("Search index built successfully with {TermCount} unique terms",
                invertedIndex.Count);

            return _searchIndex;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building search index");
            throw new InvalidOperationException("Failed to build search index", ex);
        }
    }

    public async Task<bool> UpdateSearchIndexAsync(string documentId, SearchDocument document)
    {
        try
        {
            if (_searchIndex.Documents.ContainsKey(documentId))
            {
                // 既存ドキュメントの削除
                var oldDocument = _searchIndex.Documents[documentId];
                var oldTokens = TokenizeDocument(oldDocument);

                foreach (var token in oldTokens)
                {
                    if (_searchIndex.InvertedIndex.TryGetValue(token, out var docIds))
                    {
                        docIds.Remove(documentId);

                        // 空のドキュメントセットをクリーンアップ
                        if (docIds.Count == 0)
                        {
                            _searchIndex.InvertedIndex.Remove(token);
                        }
                    }
                }
            }

            // 新しいドキュメントの追加
            _searchIndex.Documents[documentId] = document;
            var newTokens = TokenizeDocument(document);

            foreach (var token in newTokens)
            {
                if (!_searchIndex.InvertedIndex.TryGetValue(token, out var docIds))
                {
                    docIds = new HashSet<string>();
                    _searchIndex.InvertedIndex[token] = docIds;
                }
                docIds.Add(documentId);
            }

            _searchIndex.LastUpdated = DateTime.UtcNow;

            _logger.LogInformation("Search index updated for document: {DocumentId}", documentId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating search index for document: {DocumentId}", documentId);
            return false;
        }
    }

    public async Task<bool> RemoveFromSearchIndexAsync(string documentId)
    {
        try
        {
            if (!_searchIndex.Documents.TryGetValue(documentId, out var document))
            {
                return false;
            }

            // ドキュメントの削除
            _searchIndex.Documents.Remove(documentId);

            // 転置インデックスからの削除
            var tokens = TokenizeDocument(document);
            foreach (var token in tokens)
            {
                if (_searchIndex.InvertedIndex.TryGetValue(token, out var docIds))
                {
                    docIds.Remove(documentId);

                    if (docIds.Count == 0)
                    {
                        _searchIndex.InvertedIndex.Remove(token);
                    }
                }
            }

            _searchIndex.LastUpdated = DateTime.UtcNow;

            _logger.LogInformation("Document removed from search index: {DocumentId}", documentId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing document from search index: {DocumentId}", documentId);
            return false;
        }
    }

    public async Task<SearchAnalytics> GetSearchAnalyticsAsync(DateTime? startDate = null, DateTime? endDate = null)
    {
        var analytics = new SearchAnalytics();

        try
        {
            var filteredHistory = _searchHistory.AsEnumerable();

            if (startDate.HasValue)
            {
                filteredHistory = filteredHistory.Where(h => h.Timestamp >= startDate.Value);
            }

            if (endDate.HasValue)
            {
                filteredHistory = filteredHistory.Where(h => h.Timestamp <= endDate.Value);
            }

            analytics.TotalSearches = filteredHistory.Count();

            if (analytics.TotalSearches > 0)
            {
                analytics.AverageResultsPerSearch = filteredHistory.Average(h => h.ResultCount);
                analytics.ClickThroughRate = filteredHistory.Count(h => h.WasClicked) / (double)analytics.TotalSearches;

                // 時間帯別検索数
                analytics.SearchesByHour = filteredHistory
                    .GroupBy(h => h.Timestamp.Hour)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count());

                // 日別検索数
                analytics.SearchesByDay = filteredHistory
                    .GroupBy(h => h.Timestamp.Date)
                    .ToDictionary(g => g.Key.ToString("yyyy-MM-dd"), g => g.Count());

                // 人気検索語句
                analytics.TopSearchTerms = filteredHistory
                    .GroupBy(h => h.Query)
                    .Select(g => new SearchTermAnalytics
                    {
                        Term = g.Key,
                        SearchCount = g.Count(),
                        AverageResults = g.Average(h => h.ResultCount)
                    })
                    .OrderByDescending(t => t.SearchCount)
                    .Take(10)
                    .ToList();
            }

            return analytics;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating search analytics");
            return analytics;
        }
    }

    public async Task<IEnumerable<string>> GetPopularSearchTermsAsync(int limit = 20)
    {
        return _searchHistory
            .GroupBy(h => h.Query)
            .OrderByDescending(g => g.Count())
            .Take(limit)
            .Select(g => g.Key)
            .ToList();
    }

    private string PreprocessQuery(string query)
    {
        // クエリの前処理（正規化、ステミングなど）
        var processed = query.ToLowerInvariant().Trim();

        // 特殊文字の除去
        processed = Regex.Replace(processed, @"[^\w\s]", " ");

        // 複数のスペースを単一スペースに
        processed = Regex.Replace(processed, @"\s+", " ");

        return processed;
    }

    private List<SearchDocument> PerformSearch(string query, Dictionary<string, object> filters)
    {
        var matchingDocuments = new List<SearchDocument>();
        var queryTerms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (!queryTerms.Any())
        {
            return matchingDocuments;
        }

        // 各クエリ語句でドキュメントを検索
        foreach (var term in queryTerms)
        {
            if (_searchIndex.InvertedIndex.TryGetValue(term, out var docIds))
            {
                foreach (var docId in docIds)
                {
                    if (_searchIndex.Documents.TryGetValue(docId, out var doc))
                    {
                        // フィルターの適用
                        if (ApplyFilters(doc, filters))
                        {
                            if (!matchingDocuments.Any(d => d.Id == doc.Id))
                            {
                                matchingDocuments.Add(doc);
                            }
                        }
                    }
                }
            }
        }

        return matchingDocuments;
    }

    private bool ApplyFilters(SearchDocument document, Dictionary<string, object> filters)
    {
        foreach (var filter in filters)
        {
            switch (filter.Key)
            {
                case "category":
                    if (!document.Category.Equals(filter.Value.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                    break;
                case "createdAfter":
                    if (document.CreatedAt < (DateTime)filter.Value)
                    {
                        return false;
                    }
                    break;
                case "createdBefore":
                    if (document.CreatedAt > (DateTime)filter.Value)
                    {
                        return false;
                    }
                    break;
                case "metadata":
                    var metadataFilters = (Dictionary<string, string>)filter.Value;
                    foreach (var metadataFilter in metadataFilters)
                    {
                        if (!document.Metadata.TryGetValue(metadataFilter.Key, out var value) ||
                            !value.Equals(metadataFilter.Value, StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }
                    }
                    break;
            }
        }

        return true;
    }

    private List<SearchDocument> ApplySorting(List<SearchDocument> documents, SortOrder sortBy)
    {
        return sortBy switch
        {
            SortOrder.Relevance => documents.OrderByDescending(d => CalculateRelevanceScore(d, "")).ToList(),
            SortOrder.Date => documents.OrderByDescending(d => d.LastModified).ToList(),
            SortOrder.Title => documents.OrderBy(d => d.Title).ToList(),
            SortOrder.Popularity => documents.OrderByDescending(d => d.Metadata.GetValueOrDefault("popularity", "0")).ToList(),
            _ => documents
        };
    }

    private double CalculateRelevanceScore(SearchDocument document, string query)
    {
        // 簡易的な関連性スコア計算（実際の実装ではより高度なアルゴリズムを使用）
        var score = 0.0;

        // タイトルマッチのボーナス
        if (document.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 10.0;
        }

        // コンテンツマッチのスコア
        var contentMatches = Regex.Matches(document.Content, Regex.Escape(query), RegexOptions.IgnoreCase).Count;
        score += contentMatches * 2.0;

        // 新しいドキュメントのボーナス
        var daysSinceCreated = (DateTime.UtcNow - document.CreatedAt).TotalDays;
        if (daysSinceCreated < 30)
        {
            score += Math.Max(0, 5 - daysSinceCreated / 6); // 30日以内のドキュメントにボーナス
        }

        return score;
    }

    private string GenerateUrl(SearchDocument document)
    {
        // ドキュメントのURLを生成（実際の実装では適切なURL生成ロジックを使用）
        return $"/search/{document.Id}";
    }

    private List<string> GenerateHighlights(string content, string query)
    {
        var highlights = new List<string>();

        // クエリ語句を含むスニペットを抽出
        var queryTerms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var term in queryTerms)
        {
            var regex = new Regex($@"(.{{0,50}}){Regex.Escape(term)}(.{{0,50}})", RegexOptions.IgnoreCase);
            var matches = regex.Matches(content);

            foreach (Match match in matches.Take(3)) // 最大3つのハイライト
            {
                var highlight = match.Value.Trim();
                if (highlight.Length > 100)
                {
                    highlight = highlight.Substring(0, 97) + "...";
                }

                highlights.Add(highlight);
            }
        }

        return highlights.Distinct().ToList();
    }

    private string GenerateSnippet(string content, string query)
    {
        // クエリ語句を含むスニペットを生成
        var queryTerms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var term in queryTerms)
        {
            var index = content.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var start = Math.Max(0, index - 50);
                var end = Math.Min(content.Length, index + term.Length + 100);

                var snippet = content.Substring(start, end - start);

                // 前後の文の境界で調整
                var sentenceEnd = snippet.LastIndexOf('.');
                if (sentenceEnd > 0)
                {
                    snippet = snippet.Substring(0, sentenceEnd + 1);
                }

                return snippet.Trim();
            }
        }

        // クエリ語句が見つからない場合は最初の部分を返す
        return content.Length > 200 ? content.Substring(0, 197) + "..." : content;
    }

    private List<string> GenerateSearchSuggestions(string query)
    {
        var suggestions = new List<string>();

        // スペル修正の候補
        suggestions.AddRange(GenerateSpellCorrectionSuggestions(query));

        // 関連語句の候補
        suggestions.AddRange(GenerateRelatedTermSuggestions(query));

        // カテゴリベースの候補
        suggestions.AddRange(GenerateCategoryBasedSuggestions(query));

        return suggestions.Take(5).ToList();
    }

    private List<string> GenerateSpellCorrectionSuggestions(string query)
    {
        // 簡易的なスペル修正（実際の実装ではより高度なアルゴリズムを使用）
        var suggestions = new List<string>();

        // 一般的なタイポの修正
        var commonTypos = new Dictionary<string, string>
        {
            ["teh"] = "the",
            ["recieve"] = "receive",
            ["seperate"] = "separate",
            ["occured"] = "occurred"
        };

        foreach (var typo in commonTypos)
        {
            if (query.Contains(typo.Key))
            {
                suggestions.Add(query.Replace(typo.Key, typo.Value));
            }
        }

        return suggestions;
    }

    private List<string> GenerateRelatedTermSuggestions(string query)
    {
        // 関連語句の候補（実際の実装ではシソーラスや関連性データを使用）
        var relatedTerms = new Dictionary<string, List<string>>
        {
            ["search"] = new List<string> { "find", "lookup", "query" },
            ["document"] = new List<string> { "file", "page", "article" },
            ["user"] = new List<string> { "account", "profile", "person" }
        };

        var suggestions = new List<string>();

        foreach (var term in query.Split(' '))
        {
            if (relatedTerms.TryGetValue(term.ToLowerInvariant(), out var related))
            {
                suggestions.AddRange(related);
            }
        }

        return suggestions;
    }

    private List<string> GenerateCategoryBasedSuggestions(string query)
    {
        // カテゴリベースの候補
        return _searchIndex.Documents.Values
            .Where(doc => doc.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                         query.Contains(doc.Category, StringComparison.OrdinalIgnoreCase))
            .Select(doc => $"in {doc.Category}")
            .Distinct()
            .Take(3)
            .ToList();
    }

    private Dictionary<string, int> GenerateFacetCounts(List<SearchDocument> documents)
    {
        return documents
            .GroupBy(doc => doc.Category)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    private List<string> TokenizeDocument(SearchDocument document)
    {
        // ドキュメントのテキストをトークン化
        var text = $"{document.Title} {document.Content}".ToLowerInvariant();

        // 特殊文字の除去と単語分割
        var tokens = Regex.Split(text, @"\W+")
            .Where(token => !string.IsNullOrWhiteSpace(token) && token.Length > 2)
            .Distinct()
            .ToList();

        return tokens;
    }

    private string ExtractRelevantPhrase(string content, string query)
    {
        var index = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var start = Math.Max(0, index - 30);
            var end = Math.Min(content.Length, index + query.Length + 50);

            return content.Substring(start, end - start).Trim();
        }

        return string.Empty;
    }

    private void RecordSearch(string query, int resultCount, TimeSpan searchTime)
    {
        var searchRecord = new SearchAnalytics
        {
            TotalSearches = 1,
            Timestamp = DateTime.UtcNow,
            Query = query,
            ResultCount = resultCount,
            SearchTime = searchTime
        };

        _searchHistory.Add(searchRecord);

        // 古い履歴をクリーンアップ（最大1000件保持）
        if (_searchHistory.Count > 1000)
        {
            _searchHistory.RemoveRange(0, _searchHistory.Count - 1000);
        }
    }

    private class SearchAnalytics
    {
        public DateTime Timestamp { get; set; }
        public string Query { get; set; } = string.Empty;
        public int ResultCount { get; set; }
        public TimeSpan SearchTime { get; set; }
        public bool WasClicked { get; set; }
    }
}
