namespace LLMW.Writing.Infrastructure.Authority;

public class AuthorityTransactionException : InvalidOperationException
{
    public AuthorityTransactionException(string message)
        : base(message)
    {
    }

    public AuthorityTransactionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class AuthorityRecoveryRequiredException : AuthorityTransactionException
{
    public AuthorityRecoveryRequiredException(string transactionId)
        : base($"Authority transaction '{transactionId}' requires explicit recovery.")
    {
    }
}
