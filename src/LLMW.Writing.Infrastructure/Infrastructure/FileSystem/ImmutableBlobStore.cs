using System.Buffers;
using System.Security.Cryptography;
using LLMW.Writing.Application.Authority;

namespace LLMW.Writing.Infrastructure.FileSystem;

public sealed class ImmutableBlobStore : IImmutableBlobStore
{
    private const int BufferSize = 128 * 1024;
    private const string TemporaryPrefix = ".tmp-";
    private readonly string objectsRoot;

    public ImmutableBlobStore(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        objectsRoot = Path.Combine(Path.GetFullPath(projectRoot), ".llmw", "objects");
        Directory.CreateDirectory(objectsRoot);
    }

    public string ObjectsRoot => objectsRoot;

    public BlobStageResult Stage(
        Stream source,
        string? expectedDigest = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The blob source stream must be readable.", nameof(source));
        }

        var normalizedExpected = expectedDigest is null ? null : NormalizeDigest(expectedDigest);
        var ingestPath = Path.Combine(objectsRoot, TemporaryPrefix + RandomNumberGenerator.GetHexString(24).ToLowerInvariant());
        string? publishTemporaryPath = null;

        try
        {
            var (actualDigest, length) = WriteAndHash(source, ingestPath, cancellationToken);
            if (normalizedExpected is not null &&
                !StringComparer.Ordinal.Equals(normalizedExpected, actualDigest))
            {
                throw new BlobDigestMismatchException(normalizedExpected, actualDigest);
            }

            var targetPath = GetPath(actualDigest);
            var targetDirectory = Path.GetDirectoryName(targetPath)
                ?? throw new BlobStoreException("Blob target directory could not be resolved.");
            Directory.CreateDirectory(targetDirectory);

            if (File.Exists(targetPath))
            {
                EnsureExistingBlobIsValid(actualDigest, targetPath, cancellationToken);
                return new BlobStageResult(actualDigest, targetPath, length, Deduplicated: true);
            }

            publishTemporaryPath = Path.Combine(
                targetDirectory,
                TemporaryPrefix + RandomNumberGenerator.GetHexString(24).ToLowerInvariant());
            File.Move(ingestPath, publishTemporaryPath);
            EnsureFileDigest(publishTemporaryPath, actualDigest, cancellationToken);

            try
            {
                File.Move(publishTemporaryPath, targetPath, overwrite: false);
                publishTemporaryPath = null;
                return new BlobStageResult(actualDigest, targetPath, length, Deduplicated: false);
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                EnsureExistingBlobIsValid(actualDigest, targetPath, cancellationToken);
                return new BlobStageResult(actualDigest, targetPath, length, Deduplicated: true);
            }
        }
        finally
        {
            TryDeleteTemporary(ingestPath);
            if (publishTemporaryPath is not null)
            {
                TryDeleteTemporary(publishTemporaryPath);
            }
        }
    }

    public Stream OpenRead(string digest)
    {
        var normalized = NormalizeDigest(digest);
        var path = GetPath(normalized);
        if (!File.Exists(path))
        {
            throw new BlobCorruptionException(normalized, path);
        }

        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan);
    }

    public bool Verify(string digest, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeDigest(digest);
        var path = GetPath(normalized);
        return File.Exists(path) && StringComparer.Ordinal.Equals(HashFile(path, cancellationToken), normalized);
    }

    public string GetPath(string digest)
    {
        var normalized = NormalizeDigest(digest);
        return Path.Combine(objectsRoot, normalized[..2], normalized[2..]);
    }

    public int CleanupTemporaryFiles(TimeSpan minimumAge)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumAge, TimeSpan.Zero);

        if (!Directory.Exists(objectsRoot))
        {
            return 0;
        }

        var threshold = DateTime.UtcNow - minimumAge;
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(objectsRoot, TemporaryPrefix + "*", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) <= threshold)
                {
                    File.Delete(path);
                    removed++;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    public static string NormalizeDigest(string digest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        if (digest.Length != 64 || digest.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A blob digest must be exactly 64 hexadecimal characters.", nameof(digest));
        }

        return digest.ToLowerInvariant();
    }

    private static (string Digest, long Length) WriteAndHash(
        Stream source,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.WriteThrough);
            long length = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                output.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
                length += read;
            }

            output.Flush(flushToDisk: true);
            return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string HashFile(string path, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void EnsureExistingBlobIsValid(
        string digest,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            EnsureFileDigest(path, digest, cancellationToken);
        }
        catch (BlobDigestMismatchException)
        {
            throw new BlobCorruptionException(digest, path);
        }
        catch (FileNotFoundException)
        {
            throw new BlobCorruptionException(digest, path);
        }
    }

    private static void EnsureFileDigest(string path, string expectedDigest, CancellationToken cancellationToken)
    {
        var observed = HashFile(path, cancellationToken);
        if (!StringComparer.Ordinal.Equals(expectedDigest, observed))
        {
            throw new BlobDigestMismatchException(expectedDigest, observed);
        }
    }

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
