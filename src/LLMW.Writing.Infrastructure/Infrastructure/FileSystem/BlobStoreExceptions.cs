namespace LLMW.Writing.Infrastructure.FileSystem;

public class BlobStoreException : IOException
{
    public BlobStoreException(string message)
        : base(message)
    {
    }

    public BlobStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class BlobDigestMismatchException : BlobStoreException
{
    public BlobDigestMismatchException(string expectedDigest, string actualDigest)
        : base($"Blob digest mismatch. Expected '{expectedDigest}', observed '{actualDigest}'.")
    {
        ExpectedDigest = expectedDigest;
        ActualDigest = actualDigest;
    }

    public string ExpectedDigest { get; }

    public string ActualDigest { get; }
}

public sealed class BlobCorruptionException : BlobStoreException
{
    public BlobCorruptionException(string digest, string path)
        : base($"Existing blob '{digest}' is missing or corrupt at '{path}'.")
    {
        Digest = digest;
        Path = path;
    }

    public string Digest { get; }

    public string Path { get; }
}
