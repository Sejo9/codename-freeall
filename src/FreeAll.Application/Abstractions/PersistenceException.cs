namespace FreeAll.Application.Abstractions;

/// <summary>
/// A database read or write could not be completed, or the database is unavailable.
/// Callers must treat the operation as not performed.
/// </summary>
public sealed class PersistenceException : Exception
{
    public PersistenceException()
    {
    }

    public PersistenceException(string message)
        : base(message)
    {
    }

    public PersistenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
