using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// ファイルアップロードセキュリティの強化サービス
/// ファイルタイプとサイズ制限の強化を実装
/// </summary>
public interface IFileUploadSecurityService
{
    Task<FileUploadResult> ValidateFileAsync(IFormFile file, FileUploadOptions options);
    Task<FileUploadResult> ValidateFilesAsync(IEnumerable<IFormFile> files, FileUploadOptions options);
    Task<string> GenerateSecureFileName(string originalFileName, string userId);
    Task<bool> ScanForMalwareAsync(string filePath);
    Task<FileSecurityReport> GetSecurityReportAsync();
    Task<IEnumerable<string>> GetAllowedExtensionsAsync();
}

/// <summary>
/// ファイルアップロードオプション
/// </summary>
public class FileUploadOptions
{
    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024; // 10MB
    public long MaxTotalSizeBytes { get; set; } = 100 * 1024 * 1024; // 100MB
    public int MaxFileCount { get; set; } = 10;
    public HashSet<string> AllowedExtensions { get; set; } = new();
    public HashSet<string> BlockedExtensions { get; set; } = new();
    public bool RequireVirusScan { get; set; } = true;
    public bool AllowOverwrite { get; set; } = false;
    public string UploadDirectory { get; set; } = "uploads";
    public bool GenerateSecureFileName { get; set; } = true;
}

/// <summary>
/// ファイルアップロード結果
/// </summary>
public class FileUploadResult
{
    public bool IsValid { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string SecureFileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public FileSecurityStatus SecurityStatus { get; set; } = FileSecurityStatus.Unknown;
}

/// <summary>
/// ファイルセキュリティステータス
/// </summary>
public enum FileSecurityStatus
{
    Unknown,
    Safe,
    Suspicious,
    Dangerous,
    Blocked
}

/// <summary>
/// ファイルセキュリティレポート
/// </summary>
public class FileSecurityReport
{
    public int TotalFilesScanned { get; set; }
    public int SafeFiles { get; set; }
    public int SuspiciousFiles { get; set; }
    public int DangerousFiles { get; set; }
    public int BlockedFiles { get; set; }
    public DateTime LastScanTime { get; set; } = DateTime.UtcNow;
    public List<FileThreat> Threats { get; set; } = new();
}

/// <summary>
/// ファイル脅威情報
/// </summary>
public class FileThreat
{
    public string FileName { get; set; } = string.Empty;
    public string ThreatType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// ファイルアップロードセキュリティサービス実装
/// </summary>
public class FileUploadSecurityService : IFileUploadSecurityService
{
    private readonly ILogger<FileUploadSecurityService> _logger;
    private readonly string _uploadRootPath;

    // 危険なファイル拡張子
    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // 実行可能ファイル
        ".exe", ".bat", ".cmd", ".com", ".scr", ".pif", ".jar", ".app",
        // スクリプトファイル
        ".js", ".vbs", ".ps1", ".py", ".pl", ".rb", ".sh",
        // システムファイル
        ".sys", ".dll", ".ocx", ".drv", ".cpl", ".msc",
        // ドキュメントファイル（マクロ付き）
        ".docm", ".xlsm", ".pptm", ".doc", ".xls", ".ppt",
        // 圧縮ファイル（潜在的に危険）
        ".zip", ".rar", ".7z", ".tar", ".gz",
        // その他の危険なファイル
        ".reg", ".msi", ".msp", ".lnk", ".url"
    };

    // 許可されたMIMEタイプ
    private static readonly Dictionary<string, HashSet<string>> AllowedMimeTypes = new()
    {
        [".jpg"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/jpeg" },
        [".jpeg"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/jpeg" },
        [".png"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png" },
        [".gif"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/gif" },
        [".webp"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/webp" },
        [".pdf"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/pdf" },
        [".txt"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "text/plain" },
        [".csv"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "text/csv", "application/csv" },
        [".json"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/json" },
        [".xml"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/xml", "text/xml" }
    };

    // 危険なファイルシグネチャ（マジックバイト）
    private static readonly Dictionary<string, byte[]> DangerousSignatures = new()
    {
        [".exe"] = new byte[] { 0x4D, 0x5A }, // MZ header
        [".dll"] = new byte[] { 0x4D, 0x5A },
        [".bat"] = new byte[] { 0x40, 0x45, 0x43, 0x48, 0x4F }, // @ECHO
        [".cmd"] = new byte[] { 0x40, 0x45, 0x43, 0x48, 0x4F },
        [".scr"] = new byte[] { 0x4D, 0x5A },
        [".pif"] = new byte[] { 0x4D, 0x5A },
        [".com"] = new byte[] { 0x4D, 0x5A }
    };

    public FileUploadSecurityService(ILogger<FileUploadSecurityService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _uploadRootPath = Path.Combine(ServicePaths.BaseDirectory, "uploads");
        Directory.CreateDirectory(_uploadRootPath);
    }

    public async Task<FileUploadResult> ValidateFileAsync(IFormFile file, FileUploadOptions options)
    {
        var result = new FileUploadResult
        {
            FileName = file.FileName,
            FileSize = file.Length,
            ContentType = file.ContentType
        };

        try
        {
            // 基本的な検証
            var basicValidation = await ValidateBasicFilePropertiesAsync(file, options);
            if (!basicValidation.IsValid)
            {
                result.IsValid = false;
                result.Errors.AddRange(basicValidation.Errors);
                return result;
            }

            // 拡張子の検証
            var extensionValidation = ValidateFileExtension(file.FileName, options);
            if (!extensionValidation.IsValid)
            {
                result.IsValid = false;
                result.Errors.AddRange(extensionValidation.Errors);
                result.SecurityStatus = FileSecurityStatus.Dangerous;
                return result;
            }

            // MIMEタイプの検証
            var mimeValidation = ValidateMimeType(file.FileName, file.ContentType);
            if (!mimeValidation.IsValid)
            {
                result.IsValid = false;
                result.Errors.AddRange(mimeValidation.Errors);
                result.SecurityStatus = FileSecurityStatus.Suspicious;
            }

            // ファイルシグネチャの検証（マジックバイトチェック）
            var signatureValidation = await ValidateFileSignatureAsync(file);
            if (!signatureValidation.IsValid)
            {
                result.IsValid = false;
                result.Errors.AddRange(signatureValidation.Errors);
                result.SecurityStatus = FileSecurityStatus.Dangerous;
                return result;
            }

            // ウイルススキャン（オプション）
            if (options.RequireVirusScan)
            {
                var virusScanResult = await ScanForMalwareAsync(file.FileName);
                if (!virusScanResult)
                {
                    result.IsValid = false;
                    result.Errors.Add("File failed virus scan");
                    result.SecurityStatus = FileSecurityStatus.Dangerous;
                    return result;
                }
            }

            // セキュアなファイル名を生成
            result.SecureFileName = options.GenerateSecureFileName
                ? await GenerateSecureFileName(file.FileName, "system")
                : Path.GetFileNameWithoutExtension(file.FileName) + Path.GetExtension(file.FileName);

            result.IsValid = true;
            result.SecurityStatus = FileSecurityStatus.Safe;

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating file {FileName}", file.FileName);
            result.IsValid = false;
            result.Errors.Add($"Validation error: {ex.Message}");
            result.SecurityStatus = FileSecurityStatus.Unknown;
            return result;
        }
    }

    public async Task<FileUploadResult> ValidateFilesAsync(IEnumerable<IFormFile> files, FileUploadOptions options)
    {
        var result = new FileUploadResult();
        var totalSize = 0L;
        var fileResults = new List<FileUploadResult>();

        foreach (var file in files)
        {
            var fileResult = await ValidateFileAsync(file, options);
            fileResults.Add(fileResult);

            if (fileResult.IsValid)
            {
                totalSize += fileResult.FileSize;
            }
        }

        // 合計サイズチェック
        if (totalSize > options.MaxTotalSizeBytes)
        {
            result.IsValid = false;
            result.Errors.Add($"Total upload size ({totalSize} bytes) exceeds maximum allowed ({options.MaxTotalSizeBytes} bytes)");
        }

        // ファイル数のチェック
        if (files.Count() > options.MaxFileCount)
        {
            result.IsValid = false;
            result.Errors.Add($"File count ({files.Count()}) exceeds maximum allowed ({options.MaxFileCount})");
        }

        // 全体的な結果をまとめる
        result.IsValid = result.IsValid && fileResults.All(f => f.IsValid);
        result.Errors.AddRange(fileResults.Where(f => !f.IsValid).SelectMany(f => f.Errors));
        result.Warnings.AddRange(fileResults.SelectMany(f => f.Warnings));

        return result;
    }

    public async Task<string> GenerateSecureFileName(string originalFileName, string userId)
    {
        try
        {
            var extension = Path.GetExtension(originalFileName);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var randomSuffix = GenerateRandomString(8);

            // ユーザーIDとタイムスタンプをハッシュ化してセキュアなファイル名を生成
            var combined = $"{userId}_{timestamp}_{randomSuffix}";
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
            var hashString = Convert.ToHexString(hashBytes).Substring(0, 16);

            var secureFileName = $"{hashString}{extension}";

            _logger.LogDebug("Generated secure file name for {OriginalName}: {SecureName}", originalFileName, secureFileName);

            return secureFileName;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating secure file name for {OriginalName}", originalFileName);
            throw new InvalidOperationException("Failed to generate secure file name", ex);
        }
    }

    public async Task<bool> ScanForMalwareAsync(string filePath)
    {
        try
        {
            // 実際の実装ではウイルス対策ソフトウェアと連携
            // ここでは簡易的なチェックとしてファイルサイズと拡張子を検証

            var fileInfo = new FileInfo(filePath);

            // 異常なサイズのチェック（DoS対策）
            if (fileInfo.Length > 100 * 1024 * 1024) // 100MB以上
            {
                _logger.LogWarning("Suspicious file size detected: {FileSize} bytes for {FilePath}", fileInfo.Length, filePath);
                return false;
            }

            // ファイルシグネチャのチェック（実際の実装ではより詳細なチェック）
            using var stream = File.OpenRead(filePath);
            var signature = new byte[8];
            await stream.ReadAsync(signature, 0, signature.Length);

            // 危険なシグネチャのチェック
            foreach (var dangerousSignature in DangerousSignatures)
            {
                if (signature.Take(dangerousSignature.Value.Length).SequenceEqual(dangerousSignature.Value))
                {
                    _logger.LogWarning("Dangerous file signature detected in {FilePath}", filePath);
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning file for malware: {FilePath}", filePath);
            return false;
        }
    }

    public async Task<FileSecurityReport> GetSecurityReportAsync()
    {
        var report = new FileSecurityReport();

        try
        {
            var uploadDirectories = Directory.GetDirectories(_uploadRootPath, "*", SearchOption.AllDirectories);

            foreach (var directory in uploadDirectories)
            {
                var files = Directory.GetFiles(directory, "*.*", SearchOption.TopDirectoryOnly);

                foreach (var file in files)
                {
                    report.TotalFilesScanned++;

                    var fileName = Path.GetFileName(file);
                    var extension = Path.GetExtension(file).ToLowerInvariant();

                    // 拡張子ベースの分類
                    if (DangerousExtensions.Contains(extension))
                    {
                        report.DangerousFiles++;
                        report.Threats.Add(new FileThreat
                        {
                            FileName = fileName,
                            ThreatType = "DangerousExtension",
                            Description = $"File has dangerous extension: {extension}"
                        });
                    }
                    else
                    {
                        report.SafeFiles++;
                    }
                }
            }

            report.LastScanTime = DateTime.UtcNow;

            return report;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating file security report");
            return new FileSecurityReport { Threats = { new FileThreat { Description = $"Error generating report: {ex.Message}" } } };
        }
    }

    public async Task<IEnumerable<string>> GetAllowedExtensionsAsync()
    {
        return AllowedMimeTypes.Keys;
    }

    private async Task<FileUploadResult> ValidateBasicFilePropertiesAsync(IFormFile file, FileUploadOptions options)
    {
        var result = new FileUploadResult { FileName = file.FileName };

        // ファイルサイズチェック
        if (file.Length > options.MaxFileSizeBytes)
        {
            result.Errors.Add($"File size ({file.Length} bytes) exceeds maximum allowed ({options.MaxFileSizeBytes} bytes)");
            result.IsValid = false;
        }

        if (file.Length == 0)
        {
            result.Errors.Add("File is empty");
            result.IsValid = false;
        }

        // ファイル名のチェック
        if (string.IsNullOrWhiteSpace(file.FileName))
        {
            result.Errors.Add("File name is required");
            result.IsValid = false;
        }

        // 危険なファイル名のチェック
        if (ContainsDangerousFileNamePatterns(file.FileName))
        {
            result.Errors.Add("File name contains potentially dangerous patterns");
            result.IsValid = false;
        }

        return result;
    }

    private FileUploadResult ValidateFileExtension(string fileName, FileUploadOptions options)
    {
        var result = new FileUploadResult { FileName = fileName };
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        // ブロックされた拡張子のチェック
        if (options.BlockedExtensions.Contains(extension))
        {
            result.Errors.Add($"File extension '{extension}' is not allowed");
            result.IsValid = false;
            return result;
        }

        // 許可された拡張子のチェック（指定されている場合）
        if (options.AllowedExtensions.Any() && !options.AllowedExtensions.Contains(extension))
        {
            result.Errors.Add($"File extension '{extension}' is not in the allowed list");
            result.IsValid = false;
            return result;
        }

        // 危険な拡張子のチェック
        if (DangerousExtensions.Contains(extension))
        {
            result.Warnings.Add($"File extension '{extension}' is potentially dangerous");
            result.SecurityStatus = FileSecurityStatus.Suspicious;
        }

        return result;
    }

    private FileUploadResult ValidateMimeType(string fileName, string contentType)
    {
        var result = new FileUploadResult { FileName = fileName };
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (AllowedMimeTypes.TryGetValue(extension, out var allowedTypes))
        {
            if (!allowedTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            {
                result.Errors.Add($"MIME type '{contentType}' is not allowed for extension '{extension}'");
                result.IsValid = false;
                result.SecurityStatus = FileSecurityStatus.Suspicious;
            }
        }

        // 危険なMIMEタイプのチェック
        var dangerousMimeTypes = new[]
        {
            "application/x-executable",
            "application/x-msdownload",
            "application/x-msdos-program",
            "application/x-script",
            "text/javascript",
            "application/javascript"
        };

        if (dangerousMimeTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
        {
            result.Errors.Add($"MIME type '{contentType}' is not allowed");
            result.IsValid = false;
            result.SecurityStatus = FileSecurityStatus.Dangerous;
        }

        return result;
    }

    private async Task<FileUploadResult> ValidateFileSignatureAsync(IFormFile file)
    {
        var result = new FileUploadResult { FileName = file.FileName };

        try
        {
            using var stream = file.OpenReadStream();
            var signature = new byte[8];
            var bytesRead = await stream.ReadAsync(signature, 0, signature.Length);

            if (bytesRead >= 2)
            {
                // 危険なシグネチャのチェック
                foreach (var dangerousSignature in DangerousSignatures)
                {
                    if (signature.Take(dangerousSignature.Value.Length).SequenceEqual(dangerousSignature.Value))
                    {
                        result.Errors.Add($"File signature indicates executable content: {dangerousSignature.Key}");
                        result.SecurityStatus = FileSecurityStatus.Dangerous;
                        result.IsValid = false;
                        return result;
                    }
                }

                // 画像ファイルの追加チェック
                if (IsImageFile(file.FileName))
                {
                    var imageValidation = await ValidateImageContentAsync(signature, file);
                    if (!imageValidation.IsValid)
                    {
                        result.Errors.AddRange(imageValidation.Errors);
                        result.IsValid = false;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"File signature validation error: {ex.Message}");
            result.IsValid = false;
        }

        return result;
    }

    private async Task<FileUploadResult> ValidateImageContentAsync(byte[] signature, IFormFile file)
    {
        var result = new FileUploadResult { FileName = file.FileName };

        // JPEGのチェック
        if (signature[0] == 0xFF && signature[1] == 0xD8)
        {
            // JPEGファイルの追加検証（メタデータチェックなど）
            result.Warnings.Add("JPEG file detected - ensure no embedded malicious content");
        }

        // PNGのチェック
        if (signature[0] == 0x89 && signature[1] == 0x50)
        {
            result.Warnings.Add("PNG file detected - ensure no malicious chunks");
        }

        return result;
    }

    private bool IsImageFile(string fileName)
    {
        var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return imageExtensions.Contains(extension);
    }

    private bool ContainsDangerousFileNamePatterns(string fileName)
    {
        var dangerousPatterns = new[]
        {
            @"<script",
            @"javascript:",
            @"vbscript:",
            @"data:",
            @"..",
            @"\.\.",
            @"%2e%2e",
            @"%252e%252e"
        };

        return dangerousPatterns.Any(pattern =>
            Regex.IsMatch(fileName, pattern, RegexOptions.IgnoreCase));
    }

    private string GenerateRandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var random = new byte[length];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(random);
        }

        return new string(random.Select(b => chars[b % chars.Length]).ToArray());
    }

    /// <summary>
/// ファイルセキュリティヘルパー
/// </summary>
    public static class FileSecurityHelpers
    {
        public static bool IsAllowedFileType(string fileName, FileUploadOptions options)
        {
            var extension = Path.GetExtension(fileName).ToLowerInvariant();

            if (options.BlockedExtensions.Contains(extension))
            {
                return false;
            }

            if (options.AllowedExtensions.Any() && !options.AllowedExtensions.Contains(extension))
            {
                return false;
            }

            return true;
        }

        public static string SanitizeFileName(string fileName)
        {
            // 危険な文字を除去または置換
            var sanitized = Regex.Replace(fileName, @"[<>'""/\\|?*\x00-\x1f]", "_");

            // ファイル名の長さを制限
            if (sanitized.Length > 100)
            {
                var extension = Path.GetExtension(sanitized);
                sanitized = sanitized.Substring(0, 95 - extension.Length) + extension;
            }

            return sanitized;
        }

        public static async Task<bool> IsFileContentSafeAsync(IFormFile file)
        {
            // 簡易的なコンテンツチェック
            using var stream = file.OpenReadStream();
            var buffer = new byte[1024];
            var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);

            var content = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            // スクリプトタグのチェック
            return !Regex.IsMatch(content, @"<script[^>]*>", RegexOptions.IgnoreCase);
        }
    }
}
