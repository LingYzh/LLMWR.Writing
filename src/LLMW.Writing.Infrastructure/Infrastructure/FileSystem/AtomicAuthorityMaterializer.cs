using System.Security.Cryptography;
using LLMW.Writing.Application.Authority;

namespace LLMW.Writing.Infrastructure.FileSystem;

public sealed class AtomicAuthorityMaterializer : IAuthorityMaterializer
{
    private const int BufferSize = 128 * 1024;
    private readonly string projectRoot;
    private readonly ImmutableBlobStore blobStore;

    public AtomicAuthorityMaterializer(string projectRoot, ImmutableBlobStore blobStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        this.projectRoot = Path.GetFullPath(projectRoot);
        this.blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
    }

    public void Materialize(
        string transactionId,
        IReadOnlyList<AuthorityMaterializationPlan> plans,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        ArgumentNullException.ThrowIfNull(plans);

        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var digest = ImmutableBlobStore.NormalizeDigest(plan.BlobDigest);
            var targetPath = ResolveProjectPath(plan.TargetRelativePath);
            var directory = Path.GetDirectoryName(targetPath)
                ?? throw new IOException("Materialization target directory could not be resolved.");
            Directory.CreateDirectory(directory);

            if (File.Exists(targetPath) && VerifyFile(targetPath, digest, cancellationToken))
            {
                continue;
            }

            var temporaryPath = Path.Combine(
                directory,
                $".tmp-{transactionId}-{RandomNumberGenerator.GetHexString(12).ToLowerInvariant()}");
            try
            {
                using (var source = blobStore.OpenRead(digest))
                using (var target = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.SequentialScan | FileOptions.WriteThrough))
                {
                    source.CopyTo(target, BufferSize);
                    target.Flush(flushToDisk: true);
                }

                if (!VerifyFile(temporaryPath, digest, cancellationToken))
                {
                    throw new BlobCorruptionException(digest, temporaryPath);
                }

                if (File.Exists(targetPath))
                {
                    File.Replace(temporaryPath, targetPath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(temporaryPath, targetPath, overwrite: false);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    public void Verify(
        string transactionId,
        IReadOnlyList<AuthorityMaterializationPlan> plans,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        ArgumentNullException.ThrowIfNull(plans);

        foreach (var plan in plans)
        {
            var digest = ImmutableBlobStore.NormalizeDigest(plan.BlobDigest);
            var targetPath = ResolveProjectPath(plan.TargetRelativePath);
            if (!File.Exists(targetPath) || !VerifyFile(targetPath, digest, cancellationToken))
            {
                throw new BlobCorruptionException(digest, targetPath);
            }
        }
    }

    private string ResolveProjectPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Materialization paths must be project-relative.", nameof(relativePath));
        }

        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relativePath));
        var rootWithSeparator = projectRoot.EndsWith(Path.DirectorySeparatorChar)
            ? projectRoot
            : projectRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Materialization path escapes the project root.");
        }

        return fullPath;
    }

    private static bool VerifyFile(string path, string expectedDigest, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        cancellationToken.ThrowIfCancellationRequested();
        return StringComparer.Ordinal.Equals(expectedDigest, digest);
    }
}
