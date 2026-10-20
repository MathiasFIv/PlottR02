using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlottR0._2.Repository.Configuration;

namespace PlottR0._2.Repository.Storage;

public sealed class S3ObjectStore : IObjectStore, IDisposable
{
    private readonly ObjectStorageOptions _options;
    private readonly ILogger<S3ObjectStore> _logger;
    private readonly AmazonS3Client _client;
    private bool _disposed;

    public S3ObjectStore(IOptions<ObjectStorageOptions> options, ILogger<S3ObjectStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _logger = logger;

        var config = new AmazonS3Config
        {
            ServiceURL = _options.Endpoint,
            ForcePathStyle = _options.ForcePathStyle,
            AuthenticationRegion = _options.Region,
            MaxErrorRetry = 5,
            Timeout = TimeSpan.FromSeconds(100),
        };

        var credentials = new BasicAWSCredentials(_options.AccessKeyId, _options.SecretAccessKey);
        _client = new AmazonS3Client(credentials, config);
    }

    public async Task PutAsync(string key, Stream content, string? contentType, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);

        var request = new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            InputStream = content,
            AutoCloseStream = false,
        };

        if (!string.IsNullOrEmpty(contentType))
            request.ContentType = contentType;

        await _client.PutObjectAsync(request, ct).ConfigureAwait(false);
        _logger.LogDebug("Uploaded object '{Key}' to bucket '{Bucket}'.", key, _options.Bucket);
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            await _client.GetObjectMetadataAsync(_options.Bucket, key, ct).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return false;
        }
    }

    public async Task GetToFileAsync(string key, string destinationPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var parent = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        var tempPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            try
            {
                using var response = await _client
                    .GetObjectAsync(new GetObjectRequest { BucketName = _options.Bucket, Key = key }, ct)
                    .ConfigureAwait(false);

                using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.ResponseStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
                }
            }
            catch (AmazonS3Exception ex) when (IsNotFound(ex))
            {
                throw new FileNotFoundException($"Object '{key}' was not found in bucket '{_options.Bucket}'.", ex);
            }

            File.Move(tempPath, destinationPath, overwrite: true);
            _logger.LogDebug("Downloaded object '{Key}' to '{Destination}'.", key, destinationPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                }
            }
        }
    }

    public async Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct = default)
    {
        var keys = new List<string>();
        string? continuationToken = null;

        do
        {
            var request = new ListObjectsV2Request
            {
                BucketName = _options.Bucket,
                Prefix = prefix,
                ContinuationToken = continuationToken,
            };

            var response = await _client.ListObjectsV2Async(request, ct).ConfigureAwait(false);

            if (response.S3Objects is not null)
            {
                foreach (var s3Object in response.S3Objects)
                {
                    if (!string.IsNullOrEmpty(s3Object.Key))
                        keys.Add(s3Object.Key);
                }
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuationToken is not null);

        return keys;
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await _client.DeleteObjectAsync(_options.Bucket, key, ct).ConfigureAwait(false);
        _logger.LogDebug("Deleted object '{Key}' from bucket '{Bucket}'.", key, _options.Bucket);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _client.Dispose();
    }

    private static bool IsNotFound(AmazonS3Exception ex)
        => ex.StatusCode == HttpStatusCode.NotFound
        || string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ex.ErrorCode, "NotFound", StringComparison.OrdinalIgnoreCase);
}
