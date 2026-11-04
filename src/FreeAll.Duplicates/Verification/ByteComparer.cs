using FreeAll.Duplicates.Files;

namespace FreeAll.Duplicates.Verification;

public enum ComparisonOutcome
{
    Equal,
    Different,

    /// <summary>The first file couldn't be read to the end.</summary>
    FirstFailed,

    /// <summary>The second file couldn't be read to the end.</summary>
    SecondFailed,
}

/// <param name="Outcome">What the comparison showed.</param>
/// <param name="Failure">Why a file couldn't be read, when one couldn't.</param>
public readonly record struct ComparisonResult(ComparisonOutcome Outcome, FileOpenStatus Failure = FileOpenStatus.Opened);

/// <summary>
/// The proof behind every duplicate claim (F-027): two files are the same only when each of their bytes
/// has been compared. A matching hash only says the comparison is worth doing.
/// </summary>
public static class ByteComparer
{
    /// <summary>Streams both files through the buffers and stops at the first difference.</summary>
    /// <param name="first">One file.</param>
    /// <param name="second">The other.</param>
    /// <param name="size">The size both should have.</param>
    /// <param name="firstBuffer">Working memory for the first file.</param>
    /// <param name="secondBuffer">Working memory for the second file; the same length.</param>
    /// <param name="onCompared">Called with the bytes read from both files after each step.</param>
    /// <param name="cancellationToken">Checked between reads.</param>
    public static ComparisonResult Compare(IOpenFile first, IOpenFile second, long size, byte[] firstBuffer, byte[] secondBuffer, Action<long>? onCompared, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(firstBuffer);
        ArgumentNullException.ThrowIfNull(secondBuffer);

        long offset = 0;
        while (offset < size)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wanted = (int)Math.Min(firstBuffer.Length, size - offset);
            if (Fill(first, firstBuffer.AsSpan(0, wanted), offset) is { } firstFailure)
            {
                return new ComparisonResult(ComparisonOutcome.FirstFailed, firstFailure);
            }

            if (Fill(second, secondBuffer.AsSpan(0, wanted), offset) is { } secondFailure)
            {
                return new ComparisonResult(ComparisonOutcome.SecondFailed, secondFailure);
            }

            onCompared?.Invoke(2L * wanted);
            if (!firstBuffer.AsSpan(0, wanted).SequenceEqual(secondBuffer.AsSpan(0, wanted)))
            {
                return new ComparisonResult(ComparisonOutcome.Different);
            }

            offset += wanted;
        }

        return new ComparisonResult(ComparisonOutcome.Equal);
    }

    /// <summary>Reads exactly the buffer's length. A read can return less than asked; only 0 means the end.</summary>
    /// <returns>Null when filled; otherwise why it couldn't be.</returns>
    private static FileOpenStatus? Fill(IOpenFile file, Span<byte> buffer, long offset)
    {
        try
        {
            var filled = 0;
            while (filled < buffer.Length)
            {
                var read = file.Read(buffer[filled..], offset + filled);
                if (read == 0)
                {
                    return FileOpenStatus.Missing;
                }

                filled += read;
            }

            return null;
        }
        catch (FileReadException ex)
        {
            return ex.Status;
        }
        catch (IOException)
        {
            return FileOpenStatus.Failed;
        }
        catch (UnauthorizedAccessException)
        {
            return FileOpenStatus.AccessDenied;
        }
    }
}
