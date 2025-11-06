using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Potion.Service.Infrastructure;

/// <summary>
/// データベースクエリ最適化サービス
/// クエリパフォーマンスの監視と最適化を自動的に実行
/// </summary>
public interface IDatabaseOptimizationService
{
    Task<DatabasePerformanceMetrics> GetPerformanceMetricsAsync();
    Task<IEnumerable<QueryPerformanceInfo>> GetSlowQueriesAsync();
    Task<DatabaseHealthReport> GetHealthReportAsync();
    Task<bool> OptimizeIndexesAsync();
    Task<bool> UpdateQueryStatisticsAsync();
    Task<bool> DefragmentIndexesAsync();
}

/// <summary>
/// データベースパフォーマンスメトリクス
/// </summary>
public class DatabasePerformanceMetrics
{
    public double AverageQueryTime { get; set; }
    public long TotalQueries { get; set; }
    public long SlowQueries { get; set; }
    public double CpuUsage { get; set; }
    public double MemoryUsage { get; set; }
    public long Deadlocks { get; set; }
    public long Timeouts { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// クエリパフォーマンス情報
/// </summary>
public class QueryPerformanceInfo
{
    public string QueryHash { get; set; } = string.Empty;
    public string QueryText { get; set; } = string.Empty;
    public double AverageExecutionTime { get; set; }
    public long ExecutionCount { get; set; }
    public double CpuTime { get; set; }
    public long LogicalReads { get; set; }
    public long PhysicalReads { get; set; }
    public DateTime FirstExecutionTime { get; set; }
    public DateTime LastExecutionTime { get; set; }
}

/// <summary>
/// データベースヘルスレポート
/// </summary>
public class DatabaseHealthReport
{
    public bool IsHealthy { get; set; }
    public string Status { get; set; } = string.Empty;
    public List<string> Issues { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// データベース最適化サービス実装
/// </summary>
public class DatabaseOptimizationService : IDatabaseOptimizationService
{
    private readonly ILogger<DatabaseOptimizationService> _logger;
    private readonly string _connectionString;

    public DatabaseOptimizationService(ILogger<DatabaseOptimizationService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_CONNECTION_STRING") ?? "Server=localhost;Database=PotionDB;Trusted_Connection=True;";
    }

    public async Task<DatabasePerformanceMetrics> GetPerformanceMetricsAsync()
    {
        try
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var metrics = new DatabasePerformanceMetrics();

            // クエリ統計を取得
            var queryStatsCmd = new SqlCommand(@"
                SELECT
                    AVG(total_elapsed_time / 1000.0) as AvgQueryTime,
                    COUNT(*) as TotalQueries,
                    SUM(CASE WHEN total_elapsed_time > 1000000 THEN 1 ELSE 0 END) as SlowQueries,
                    SUM(cpu_time) / 1000.0 as TotalCpuTime,
                    SUM(logical_reads) as TotalLogicalReads,
                    SUM(physical_reads) as TotalPhysicalReads
                FROM sys.dm_exec_query_stats
                CROSS APPLY sys.dm_exec_sql_text(sql_handle)", connection);

            using var reader = await queryStatsCmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                metrics.AverageQueryTime = reader.GetDouble(0);
                metrics.TotalQueries = reader.GetInt64(1);
                metrics.SlowQueries = reader.GetInt64(2);
            }

            // デッドロック情報を取得
            var deadlockCmd = new SqlCommand(@"
                SELECT cntr_value
                FROM sys.dm_os_performance_counters
                WHERE counter_name = 'Number of Deadlocks/sec' AND instance_name = '_Total'", connection);

            metrics.Deadlocks = (long)await deadlockCmd.ExecuteScalarAsync();

            return metrics;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting database performance metrics");
            return new DatabasePerformanceMetrics { Status = "Error" };
        }
    }

    public async Task<IEnumerable<QueryPerformanceInfo>> GetSlowQueriesAsync()
    {
        try
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var cmd = new SqlCommand(@"
                SELECT TOP 20
                    qs.query_hash,
                    SUBSTRING(st.text, (qs.statement_start_offset/2) + 1,
                        ((CASE statement_end_offset
                            WHEN -1 THEN DATALENGTH(st.text)
                            ELSE qs.statement_end_offset END
                        - qs.statement_start_offset)/2) + 1) AS query_text,
                    qs.execution_count,
                    (qs.total_elapsed_time / qs.execution_count) / 1000.0 as avg_execution_time,
                    qs.total_elapsed_time / 1000.0 as total_elapsed_time,
                    qs.total_cpu_time / 1000.0 as total_cpu_time,
                    qs.total_logical_reads,
                    qs.total_physical_reads,
                    qs.creation_time,
                    qs.last_execution_time
                FROM sys.dm_exec_query_stats qs
                CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
                WHERE qs.execution_count > 5
                ORDER BY (qs.total_elapsed_time / qs.execution_count) DESC", connection);

            var queries = new List<QueryPerformanceInfo>();
            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                queries.Add(new QueryPerformanceInfo
                {
                    QueryHash = reader.GetInt64(0).ToString(),
                    QueryText = reader.GetString(1),
                    ExecutionCount = reader.GetInt64(2),
                    AverageExecutionTime = reader.GetDouble(3),
                    CpuTime = reader.GetDouble(5),
                    LogicalReads = reader.GetInt64(6),
                    PhysicalReads = reader.GetInt64(7),
                    FirstExecutionTime = reader.GetDateTime(8),
                    LastExecutionTime = reader.GetDateTime(9)
                });
            }

            return queries;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting slow queries");
            return Enumerable.Empty<QueryPerformanceInfo>();
        }
    }

    public async Task<DatabaseHealthReport> GetHealthReportAsync()
    {
        var report = new DatabaseHealthReport();

        try
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            // インデックスの断片化をチェック
            var fragmentationCmd = new SqlCommand(@"
                SELECT
                    OBJECT_NAME(ips.object_id) as table_name,
                    i.name as index_name,
                    ips.avg_fragmentation_in_percent
                FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') ips
                INNER JOIN sys.indexes i ON ips.object_id = i.object_id AND ips.index_id = i.index_id
                WHERE ips.avg_fragmentation_in_percent > 30", connection);

            using var reader = await fragmentationCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var fragmentation = reader.GetDouble(2);
                if (fragmentation > 30)
                {
                    report.Issues.Add($"High fragmentation ({fragmentation:F1}%) detected in {reader.GetString(0)}.{reader.GetString(1)}");
                    report.Recommendations.Add($"Rebuild or reorganize index {reader.GetString(1)} in table {reader.GetString(0)}");
                }
            }

            // 未使用のインデックスをチェック
            var unusedIndexesCmd = new SqlCommand(@"
                SELECT
                    OBJECT_NAME(i.object_id) as table_name,
                    i.name as index_name,
                    us.user_seeks + us.user_scans + us.user_lookups as total_uses
                FROM sys.indexes i
                LEFT JOIN sys.dm_db_index_usage_stats us ON i.object_id = us.object_id AND i.index_id = us.index_id
                WHERE i.type_desc = 'NONCLUSTERED' AND us.user_seeks + us.user_scans + us.user_lookups < 10
                AND i.create_date < DATEADD(day, -30, GETDATE())", connection);

            var unusedCount = 0;
            using (var unusedReader = await unusedIndexesCmd.ExecuteReaderAsync())
            {
                while (await unusedReader.ReadAsync())
                {
                    unusedCount++;
                }
            }

            if (unusedCount > 0)
            {
                report.Issues.Add($"{unusedCount} unused indexes detected");
                report.Recommendations.Add("Consider dropping unused indexes to improve write performance");
            }

            // 欠落しているインデックスをチェック
            var missingIndexesCmd = new SqlCommand(@"
                SELECT TOP 10
                    DB_NAME(mid.database_id) as database_name,
                    OBJECT_NAME(mid.object_id, mid.database_id) as table_name,
                    mid.equality_columns,
                    mid.inequality_columns,
                    mid.included_columns,
                    mig.index_group_handle,
                    mig.index_handle,
                    mig.avg_user_impact
                FROM sys.dm_db_missing_index_details mid
                INNER JOIN sys.dm_db_missing_index_groups mig ON mid.index_handle = mig.index_handle
                ORDER BY mig.avg_user_impact DESC", connection);

            using (var missingReader = await missingIndexesCmd.ExecuteReaderAsync())
            {
                while (await missingReader.ReadAsync())
                {
                    report.Recommendations.Add($"Consider creating index on {missingReader.GetString(1)} with columns: {missingReader.GetString(2)}");
                }
            }

            report.IsHealthy = !report.Issues.Any();
            report.Status = report.IsHealthy ? "Healthy" : "Needs Attention";

            return report;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating database health report");
            report.IsHealthy = false;
            report.Status = "Error";
            report.Issues.Add($"Error generating health report: {ex.Message}");
            return report;
        }
    }

    public async Task<bool> OptimizeIndexesAsync()
    {
        try
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            // 断片化率が高いインデックスを再構築
            var rebuildCmd = new SqlCommand(@"
                DECLARE @table_name NVARCHAR(255), @index_name NVARCHAR(255), @fragmentation FLOAT;

                DECLARE index_cursor CURSOR FOR
                SELECT
                    OBJECT_NAME(ips.object_id),
                    i.name,
                    ips.avg_fragmentation_in_percent
                FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') ips
                INNER JOIN sys.indexes i ON ips.object_id = i.object_id AND ips.index_id = i.index_id
                WHERE ips.avg_fragmentation_in_percent > 30;

                OPEN index_cursor;
                FETCH NEXT FROM index_cursor INTO @table_name, @index_name, @fragmentation;

                WHILE @@FETCH_STATUS = 0
                BEGIN
                    DECLARE @sql NVARCHAR(MAX) = 'ALTER INDEX [' + @index_name + '] ON [' + @table_name + '] REBUILD;';
                    EXEC sp_executesql @sql;

                    FETCH NEXT FROM index_cursor INTO @table_name, @index_name, @fragmentation;
                END;

                CLOSE index_cursor;
                DEALLOCATE index_cursor;", connection);

            await rebuildCmd.ExecuteNonQueryAsync();

            _logger.LogInformation("Index optimization completed");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error optimizing indexes");
            return false;
        }
    }

    public async Task<bool> UpdateQueryStatisticsAsync()
    {
        try
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var cmd = new SqlCommand("EXEC sp_updatestats;", connection);
            await cmd.ExecuteNonQueryAsync();

            _logger.LogInformation("Query statistics updated");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating query statistics");
            return false;
        }
    }

    public async Task<bool> DefragmentIndexesAsync()
    {
        try
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            // 中程度の断片化（10-30%）のインデックスを再構成
            var reorganizeCmd = new SqlCommand(@"
                DECLARE @table_name NVARCHAR(255), @index_name NVARCHAR(255), @fragmentation FLOAT;

                DECLARE index_cursor CURSOR FOR
                SELECT
                    OBJECT_NAME(ips.object_id),
                    i.name,
                    ips.avg_fragmentation_in_percent
                FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') ips
                INNER JOIN sys.indexes i ON ips.object_id = i.object_id AND ips.index_id = i.index_id
                WHERE ips.avg_fragmentation_in_percent BETWEEN 10 AND 30;

                OPEN index_cursor;
                FETCH NEXT FROM index_cursor INTO @table_name, @index_name, @fragmentation;

                WHILE @@FETCH_STATUS = 0
                BEGIN
                    DECLARE @sql NVARCHAR(MAX) = 'ALTER INDEX [' + @index_name + '] ON [' + @table_name + '] REORGANIZE;';
                    EXEC sp_executesql @sql;

                    FETCH NEXT FROM index_cursor INTO @table_name, @index_name, @fragmentation;
                END;

                CLOSE index_cursor;
                DEALLOCATE index_cursor;", connection);

            await reorganizeCmd.ExecuteNonQueryAsync();

            _logger.LogInformation("Index defragmentation completed");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error defragmenting indexes");
            return false;
        }
    }
}
