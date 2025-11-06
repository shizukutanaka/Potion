using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

public interface ISecureCommunicator
{
    Task<SecureCommunicationResult> SendTelemetryAsync(string endpoint, object telemetryData, CancellationToken cancellationToken);
    Task<byte[]> EncryptLogDataAsync(string logData);
    Task<string> DecryptLogDataAsync(byte[] encryptedData);
}

public sealed record SecureCommunicationResult(
    bool Success,
    string? ErrorMessage,
    int? HttpStatusCode);

public sealed class SecureCommunicator : ISecureCommunicator, IDisposable
{
    private readonly ILogger<SecureCommunicator> _logger;
    private readonly HttpClient _httpClient;
    private readonly byte[] _encryptionKey;
    private bool _disposed;
    private const string EncryptionKeyFileName = "secure-telemetry.key";
    private const int IvLengthBytes = 16;

    public SecureCommunicator(
        ILogger<SecureCommunicator> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        handler.SslOptions.RemoteCertificateValidationCallback = ValidateServerCertificate;

        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestVersion = HttpVersion.Version20
        };

        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Otedama-Secure-Client", null));
        }

        _encryptionKey = LoadOrCreateEncryptionKey();
    }

    public async Task<SecureCommunicationResult> SendTelemetryAsync(string endpoint, object telemetryData, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint, nameof(endpoint));
        ArgumentNullException.ThrowIfNull(telemetryData, nameof(telemetryData));

        try
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                _logger.LogWarning("Invalid endpoint format: {Endpoint}", endpoint);
                return new SecureCommunicationResult(false, "Endpoint must be an absolute HTTPS URI.", null);
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                _logger.LogWarning("Endpoint contains user info which is not permitted: {Endpoint}", endpoint);
                return new SecureCommunicationResult(false, "Endpoint credentials are not allowed.", null);
            }

            if (!NetworkSecurityGuard.TryNormalizeHost(uri.Host, out var normalizedHost, out var isDnsName))
            {
                _logger.LogWarning("Endpoint host normalization failed: {Host}", uri.Host);
                return new SecureCommunicationResult(false, "Endpoint host is invalid.", null);
            }

            if (normalizedHost.Length == 0 || normalizedHost.Length > 253)
            {
                _logger.LogWarning("Endpoint host length invalid: {Host}", uri.Host);
                return new SecureCommunicationResult(false, "Endpoint host length is invalid.", null);
            }

            if (isDnsName && !NetworkSecurityGuard.HasValidDomainStructure(normalizedHost))
            {
                _logger.LogWarning("Endpoint domain structure invalid: {Host}", normalizedHost);
                return new SecureCommunicationResult(false, "Endpoint domain is invalid.", null);
            }

            if (NetworkSecurityGuard.IsHostRestricted(normalizedHost, isDnsName))
            {
                _logger.LogWarning("Endpoint host rejected by policy: {Host}", normalizedHost);
                return new SecureCommunicationResult(false, "Endpoint host is not permitted.", null);
            }

            if (!uri.IsDefaultPort)
            {
                var isDangerousPort = false;
                if (!NetworkSecurityGuard.IsPortNumberAllowed(uri.Port, out isDangerousPort))
                {
                    _logger.LogWarning("Endpoint port invalid: {Port}", uri.Port);
                    return new SecureCommunicationResult(false, "Endpoint port is invalid.", null);
                }

                if (isDangerousPort)
                {
                    _logger.LogWarning("Endpoint port rejected: {Port}", uri.Port);
                    return new SecureCommunicationResult(false, "Endpoint port is not permitted.", null);
                }
            }

            string decodedPathAndQuery;
            try
            {
                decodedPathAndQuery = Uri.UnescapeDataString(uri.PathAndQuery);
            }
            catch (Exception ex) when (ex is UriFormatException or ArgumentException)
            {
                _logger.LogWarning(ex, "Endpoint path decoding failed: {Endpoint}", endpoint);
                return new SecureCommunicationResult(false, "Endpoint path is invalid.", null);
            }

            const int maxPathAndQueryLength = 2048;
            if (decodedPathAndQuery.Length > maxPathAndQueryLength)
            {
                _logger.LogWarning("Endpoint path/query too long ({Length} characters)", decodedPathAndQuery.Length);
                return new SecureCommunicationResult(false, "Endpoint path is too long.", null);
            }

            if (decodedPathAndQuery.Any(ch => char.IsControl(ch) && ch != '\t'))
            {
                _logger.LogWarning("Endpoint path/query contains control characters");
                return new SecureCommunicationResult(false, "Endpoint path contains invalid characters.", null);
            }

            if (NetworkSecurityGuard.ContainsCrossSiteScriptingPattern(decodedPathAndQuery) || NetworkSecurityGuard.ContainsCrossSiteScriptingPattern(uri.Fragment))
            {
                _logger.LogWarning("Endpoint contains potential XSS pattern: {Endpoint}", endpoint);
                return new SecureCommunicationResult(false, "Endpoint path contains disallowed content.", null);
            }

            _logger.LogDebug("Sending telemetry to {Endpoint}", endpoint);

            // テレメトリデータをJSONにシリアライズ
            var jsonData = JsonSerializer.Serialize(telemetryData);

            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(jsonData, Encoding.UTF8, "application/json"),
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Telemetry sent successfully to {Endpoint}", endpoint);
                return new SecureCommunicationResult(true, null, (int)response.StatusCode);
            }
            else
            {
                var errorMessage = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to send telemetry to {Endpoint}: {StatusCode} - {ErrorMessage}",
                    endpoint, response.StatusCode, errorMessage);
                return new SecureCommunicationResult(false, string.IsNullOrWhiteSpace(errorMessage) ? response.ReasonPhrase : errorMessage, (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Telemetry send to {Endpoint} timed out", endpoint);
            return new SecureCommunicationResult(false, "Request timed out", null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error sending telemetry to {Endpoint}", endpoint);
            return new SecureCommunicationResult(false, "HTTP request failed", null);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "Timeout sending telemetry to {Endpoint}", endpoint);
            return new SecureCommunicationResult(false, "Request timeout", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error sending telemetry to {Endpoint}", endpoint);
            return new SecureCommunicationResult(false, "Internal error occurred", null);
        }
    }

    public async Task<byte[]> EncryptLogDataAsync(string logData)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(logData))
        {
            throw new ArgumentException("Log data cannot be null or empty", nameof(logData));
        }

        try
        {
            using var aes = CreateCipher();
            aes.GenerateIV();

            using var memoryStream = new MemoryStream();
            memoryStream.Write(aes.IV, 0, aes.IV.Length);

            using (var cryptoStream = new CryptoStream(memoryStream, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true))
            using (var writer = new StreamWriter(cryptoStream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(logData);
            }

            return memoryStream.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encrypt log data");
            throw;
        }
    }

    public async Task<string> DecryptLogDataAsync(byte[] encryptedData)
    {
        ThrowIfDisposed();

        if (encryptedData == null || encryptedData.Length == 0)
        {
            throw new ArgumentException("Encrypted data cannot be null or empty", nameof(encryptedData));
        }

        if (encryptedData.Length <= IvLengthBytes)
        {
            throw new ArgumentException("Encrypted payload is too small", nameof(encryptedData));
        }

        try
        {

            var iv = new byte[IvLengthBytes];
            Buffer.BlockCopy(encryptedData, 0, iv, 0, iv.Length);

            var cipherBytes = new byte[encryptedData.Length - iv.Length];
            Buffer.BlockCopy(encryptedData, iv.Length, cipherBytes, 0, cipherBytes.Length);

            using var aes = CreateCipher();
            aes.IV = iv;

            using var memoryStream = new MemoryStream(cipherBytes);
            using var cryptoStream = new CryptoStream(memoryStream, aes.CreateDecryptor(), CryptoStreamMode.Read);
            using var reader = new StreamReader(cryptoStream, Encoding.UTF8);

            return await reader.ReadToEndAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt log data");
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _httpClient.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private Aes CreateCipher()
    {
        var aes = Aes.Create();
        aes.KeySize = 256;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = _encryptionKey;
        return aes;
    }

    private byte[] LoadOrCreateEncryptionKey()
    {
        try
        {
            var keyPath = Path.Combine(ServicePaths.Certificates, EncryptionKeyFileName);

            Directory.CreateDirectory(ServicePaths.Certificates);

            if (File.Exists(keyPath))
            {
                var protectedKeyBytes = File.ReadAllBytes(keyPath);
                HardenKeyFilePermissions(keyPath);
                return ProtectedData.Unprotect(protectedKeyBytes, null, DataProtectionScope.LocalMachine);
            }

            var keyBytes = RandomNumberGenerator.GetBytes(32);
            var protectedBytes = ProtectedData.Protect(keyBytes, null, DataProtectionScope.LocalMachine);
            File.WriteAllBytes(keyPath, protectedBytes);
            HardenKeyFilePermissions(keyPath);

            return keyBytes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load or create encryption key");
            throw;
        }
    }

    private void HardenKeyFilePermissions(string keyPath)
    {
        try
        {
            var fileInfo = new FileInfo(keyPath);
            if (!fileInfo.Exists)
            {
                return;
            }

            var security = fileInfo.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var rules = security
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToList();

            var privilegedSids = new[]
            {
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null),
                new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null)
            };

            foreach (var rule in rules)
            {
                if (rule.IdentityReference is SecurityIdentifier sid && !privilegedSids.Any(p => p.Equals(sid)))
                {
                    security.RemoveAccessRule(rule);
                }
            }

            foreach (var sid in privilegedSids)
            {
                security.SetAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            }

            fileInfo.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enforce permissions on encryption key file {KeyPath}", keyPath);
        }
    }

    private bool ValidateServerCertificate(object? sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        if (certificate is null)
        {
            _logger.LogWarning("TLS certificate validation failed: certificate is null");
            return false;
        }

        var createdNewCertificate = false;
        X509Certificate2? cert2 = null;

        try
        {
            if (certificate is X509Certificate2 existing)
            {
                cert2 = existing;
            }
            else
            {
                cert2 = new X509Certificate2(certificate);
                createdNewCertificate = true;
            }

            if (cert2.NotBefore > DateTime.UtcNow || cert2.NotAfter < DateTime.UtcNow)
            {
                _logger.LogWarning(
                    "TLS certificate rejected due to invalid validity period for {Subject}. NotBefore={NotBefore}, NotAfter={NotAfter}",
                    cert2.Subject,
                    cert2.NotBefore,
                    cert2.NotAfter);
                return false;
            }

            if (sslPolicyErrors != SslPolicyErrors.None)
            {
                _logger.LogWarning(
                    "Initial TLS policy errors detected for {Subject}: {Errors}",
                    cert2.Subject,
                    sslPolicyErrors);
            }

            var signatureOid = cert2.SignatureAlgorithm.Value;
            var signatureName = string.IsNullOrWhiteSpace(cert2.SignatureAlgorithm.FriendlyName)
                ? signatureOid
                : cert2.SignatureAlgorithm.FriendlyName;

            var weakSignatureOids = new[]
            {
                "1.2.840.113549.1.1.5", // sha1RSA
                "1.2.840.113549.1.1.4", // md5RSA
                "1.2.840.113549.1.1.2", // md2RSA
                "1.3.14.3.2.29"         // sha1WithRSAEncryption (alternate OID)
            };

            if (weakSignatureOids.Contains(signatureOid, StringComparer.Ordinal))
            {
                _logger.LogWarning(
                    "TLS certificate rejected due to weak signature algorithm {SignatureAlgorithm} for {Subject}",
                    signatureName,
                    cert2.Subject);
                return false;
            }

            int? keySize = null;
            switch (cert2.PublicKey.Key)
            {
                case RSA rsa:
                    keySize = rsa.KeySize;
                    if (rsa.KeySize < 2048)
                    {
                        _logger.LogWarning(
                            "TLS certificate rejected due to weak RSA key size {KeySize} for {Subject}",
                            rsa.KeySize,
                            cert2.Subject);
                        return false;
                    }
                    break;
                case ECDsa ecdsa:
                    keySize = ecdsa.KeySize;
                    if (ecdsa.KeySize < 256)
                    {
                        _logger.LogWarning(
                            "TLS certificate rejected due to weak ECDSA key size {KeySize} for {Subject}",
                            ecdsa.KeySize,
                            cert2.Subject);
                        return false;
                    }
                    break;
            }

            var chainToUse = chain ?? new X509Chain();
            var disposeChain = chain is null;

            try
            {
                chainToUse.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                chainToUse.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
                chainToUse.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                chainToUse.ChainPolicy.VerificationTime = DateTime.UtcNow;

                var isChainValid = chainToUse.Build(cert2);
                if (!isChainValid)
                {
                    var chainDetails = chainToUse.ChainElements
                        .Cast<X509ChainElement>()
                        .Select(element => new
                        {
                            ElementSubject = element.Certificate.Subject,
                            ElementIssuer = element.Certificate.Issuer,
                            Status = string.Join(
                                "; ",
                                element.ChainElementStatus.Select(status =>
                                    $"{status.Status}: {status.StatusInformation.Trim()}"))
                        })
                        .ToArray();

                    _logger.LogWarning(
                        "TLS certificate chain validation failed for {Subject}. Details={@ChainDetails}",
                        cert2.Subject,
                        chainDetails);
                    return false;
                }
            }
            finally
            {
                if (disposeChain)
                {
                    chainToUse.Dispose();
                }
            }

            if (sslPolicyErrors != SslPolicyErrors.None)
            {
                _logger.LogWarning(
                    "TLS certificate rejected for {Subject} due to policy errors: {Errors}",
                    cert2.Subject,
                    sslPolicyErrors);
                return false;
            }

            _logger.LogDebug(
                "TLS certificate validated for {Subject}. Issuer={Issuer}, Signature={Signature}, KeySize={KeySize}",
                cert2.Subject,
                cert2.Issuer,
                signatureName,
                keySize);

            return true;
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "TLS certificate validation failed due to cryptographic error");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TLS certificate validation failed unexpectedly");
            return false;
        }
        finally
        {
            if (createdNewCertificate && cert2 is not null)
            {
                cert2.Dispose();
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SecureCommunicator));
        }
    }
}

// 拡張機能：セキュアなログファイル管理
public interface ISecureLogManager
{
    Task<string> WriteSecureLogAsync(string logEntry, string category);
    Task<string?> ReadSecureLogAsync(string logId);
    Task RotateLogsAsync();
}

public sealed class SecureLogManager : ISecureLogManager
{
    private readonly ILogger<SecureLogManager> _logger;
    private readonly ISecureCommunicator _secureCommunicator;
    private readonly string _logDirectory;

    public SecureLogManager(
        ILogger<SecureLogManager> logger,
        ISecureCommunicator secureCommunicator)
    {
        _logger = logger;
        _secureCommunicator = secureCommunicator;
        _logDirectory = Path.Combine(ServicePaths.Logs, "secure");
        Directory.CreateDirectory(_logDirectory);
    }

    public async Task<string> WriteSecureLogAsync(string logEntry, string category)
    {
        try
        {
            var logId = Guid.NewGuid().ToString();
            var timestamp = DateTimeOffset.UtcNow;

            var logRecord = new
            {
                LogId = logId,
                Timestamp = timestamp,
                Category = category,
                Entry = logEntry,
                MachineName = Environment.MachineName
            };

            var jsonData = JsonSerializer.Serialize(logRecord);

            // ログを暗号化
            var encryptedData = await _secureCommunicator.EncryptLogDataAsync(jsonData);

            var logPath = Path.Combine(_logDirectory, $"{logId}.encrypted");
            await File.WriteAllBytesAsync(logPath, encryptedData);

            _logger.LogDebug("Secure log written: {LogId}", logId);
            return logId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write secure log");
            throw;
        }
    }

    public async Task<string?> ReadSecureLogAsync(string logId)
    {
        try
        {
            var logPath = Path.Combine(_logDirectory, $"{logId}.encrypted");

            if (!File.Exists(logPath))
            {
                return null;
            }

            var encryptedData = await File.ReadAllBytesAsync(logPath);
            return await _secureCommunicator.DecryptLogDataAsync(encryptedData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read secure log: {LogId}", logId);
            return null;
        }
    }

    public async Task RotateLogsAsync()
    {
        try
        {
            var cutoffDate = DateTimeOffset.UtcNow.AddDays(-30); // 30日より古いログを削除

            var logFiles = Directory.GetFiles(_logDirectory, "*.encrypted");

            foreach (var logFile in logFiles)
            {
                try
                {
                    var fileInfo = new FileInfo(logFile);
                    if (fileInfo.CreationTimeUtc < cutoffDate.UtcDateTime)
                    {
                        File.Delete(logFile);
                        _logger.LogDebug("Rotated secure log: {LogFile}", logFile);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to rotate log file: {LogFile}", logFile);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rotate secure logs");
        }
    }
}
