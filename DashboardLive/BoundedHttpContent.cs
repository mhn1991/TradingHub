using System.Buffers;

namespace Dashboard.Live;

internal static class BoundedHttpContent
{
    public static async Task<byte[]> ReadAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maximumBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        long? contentLength = content.Headers.ContentLength;
        if (contentLength > maximumBytes)
        {
            throw TooLarge(maximumBytes);
        }

        int initialCapacity = contentLength is > 0
            ? (int)contentLength.Value
            : Math.Min(maximumBytes, 16 * 1024);
        using var result = new MemoryStream(initialCapacity);
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(maximumBytes, 64 * 1024));
        try
        {
            while (true)
            {
                int remaining = maximumBytes - checked((int)result.Length);
                int readLength = Math.Min(buffer.Length, remaining + 1);
                int read = await stream.ReadAsync(
                    buffer.AsMemory(0, readLength),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return result.ToArray();
                }

                if (read > remaining)
                {
                    throw TooLarge(maximumBytes);
                }

                result.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static InvalidDataException TooLarge(int maximumBytes) =>
        new($"The HTTP response exceeded {maximumBytes} bytes.");
}
