using LLMW.Writing.Application.Editor;
using LLMW.Writing.Application.Reconcile;
using LLMW.Writing.Contracts.Editor;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Infrastructure.FileSystem;

public sealed class DraftFileStore : IDraftFileStore
{
    private const int BufferSize = 128 * 1024;
    private readonly ProjectPathResolver resolver;
    private readonly ISelfWriteTracker? selfWriteTracker;

    public DraftFileStore(ProjectPathResolver resolver, ISelfWriteTracker? selfWriteTracker = null)
    {
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        this.selfWriteTracker = selfWriteTracker;
    }

    public EditorResult<string> ResolveLeaseKey(string relativePath)
    {
        var physical = TryPhysical(relativePath);
        return physical.Succeeded
            ? EditorResult<string>.Ok(physical.Value!)
            : EditorResult<string>.Fail(physical.ErrorCode!);
    }

    public EditorResult<DraftFileSnapshot> Read(string relativePath)
    {
        var physical = TryPhysical(relativePath);
        if (!physical.Succeeded)
        {
            return EditorResult<DraftFileSnapshot>.Fail(physical.ErrorCode!);
        }

        return ReadPhysical(relativePath, physical.Value!);
    }

    public EditorResult<DraftFileSnapshot> ReadFromLeaseKey(string leaseKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseKey);
        try
        {
            var relative = resolver.FromFullPath(leaseKey);
            return Read(relative);
        }
        catch (UnauthorizedAccessException)
        {
            return EditorResult<DraftFileSnapshot>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
        }
    }

    public string DigestOf(ReadOnlySpan<byte> bytes) => ContentDigest.Sha256Hex(bytes);

    public EditorResult<DraftFileSnapshot> AtomicReplace(
        string relativePath,
        string expectedDigest,
        byte[] utf8NoBomLf,
        IEditorSaveFaultInjector faults)
    {
        ArgumentNullException.ThrowIfNull(utf8NoBomLf);
        ArgumentNullException.ThrowIfNull(faults);
        if (utf8NoBomLf.Length > EditorTransportLimits.MaximumDocumentUtf8Bytes)
        {
            return EditorResult<DraftFileSnapshot>.Fail(IpcErrorCodes.EditorDocumentTooLarge);
        }

        var physical = TryPhysical(relativePath);
        if (!physical.Succeeded)
        {
            return EditorResult<DraftFileSnapshot>.Fail(physical.ErrorCode!);
        }

        var targetPath = physical.Value!;
        var current = ReadPhysical(relativePath, targetPath);
        if (!current.Succeeded)
        {
            return current;
        }

        if (!StringComparer.Ordinal.Equals(current.Value!.Digest, ContentDigest.Normalize(expectedDigest)))
        {
            return EditorResult<DraftFileSnapshot>.Fail(IpcErrorCodes.EditorStaleBase);
        }

        var newDigest = ContentDigest.Sha256Hex(utf8NoBomLf);
        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new IOException("Draft target directory could not be resolved.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            ".tmp-editor-" + Guid.NewGuid().ToString("N"));
        using var selfWrite = selfWriteTracker?.BeginOperation(
            [new SelfWriteExpectation(relativePath, newDigest)]);
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.WriteThrough))
            {
                stream.Write(utf8NoBomLf);
                stream.Flush(flushToDisk: true);
            }

            faults.ThrowIf(EditorSaveFaultPoint.AfterTempFileWrite);
            faults.ThrowIf(EditorSaveFaultPoint.AfterFlush);
            if (!StringComparer.Ordinal.Equals(ContentDigest.Sha256Hex(File.ReadAllBytes(temporaryPath)), newDigest))
            {
                return EditorResult<DraftFileSnapshot>.Fail(IpcErrorCodes.EditorUploadHashMismatch);
            }

            var recheck = ReadPhysical(relativePath, targetPath);
            if (!recheck.Succeeded
                || !StringComparer.Ordinal.Equals(recheck.Value!.Digest, ContentDigest.Normalize(expectedDigest)))
            {
                return EditorResult<DraftFileSnapshot>.Fail(IpcErrorCodes.EditorStaleBase);
            }

            faults.ThrowIf(EditorSaveFaultPoint.BeforeAtomicReplace);
            File.Replace(temporaryPath, targetPath, destinationBackupFileName: null);
            faults.ThrowIf(EditorSaveFaultPoint.AfterAtomicReplace);
            return ReadPhysical(relativePath, targetPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private EditorResult<string> TryPhysical(string relativePath)
    {
        if (!DraftDocumentResolver.IsDraftWorkspacePath(relativePath)
            || DraftDocumentResolver.IsManuscriptPath(relativePath))
        {
            return EditorResult<string>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
        }

        try
        {
            var normalized = resolver.NormalizeRelativePath(relativePath);
            if (!DraftDocumentResolver.IsDraftWorkspacePath(normalized))
            {
                return EditorResult<string>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
            }

            return EditorResult<string>.Ok(resolver.Resolve(normalized));
        }
        catch (UnauthorizedAccessException)
        {
            return EditorResult<string>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
        }
    }

    private static EditorResult<DraftFileSnapshot> ReadPhysical(string relativePath, string physicalPath)
    {
        if (!File.Exists(physicalPath))
        {
            return EditorResult<DraftFileSnapshot>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
        }

        try
        {
            if ((File.GetAttributes(physicalPath) & FileAttributes.ReparsePoint) != 0)
            {
                return EditorResult<DraftFileSnapshot>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
            }

            var bytes = File.ReadAllBytes(physicalPath);
            if (bytes.LongLength > EditorTransportLimits.MaximumDocumentUtf8Bytes)
            {
                return EditorResult<DraftFileSnapshot>.Fail(IpcErrorCodes.EditorDocumentTooLarge);
            }

            var digest = ContentDigest.Sha256Hex(bytes);
            return EditorResult<DraftFileSnapshot>.Ok(new DraftFileSnapshot(
                relativePath,
                physicalPath,
                bytes,
                digest,
                bytes.Length));
        }
        catch (UnauthorizedAccessException)
        {
            return EditorResult<DraftFileSnapshot>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
        }
        catch (IOException)
        {
            return EditorResult<DraftFileSnapshot>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
        }
    }
}
