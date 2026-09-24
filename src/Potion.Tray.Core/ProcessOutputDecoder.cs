using System.Text;

namespace Potion.Tray.Core;

public static class ProcessOutputDecoder
{
    public const int MaxBytes = 1 << 20;

    public static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var retained = new byte[MaxBytes];
        var retainedCount = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0)
            {
                break;
            }

            var total = retainedCount + read;
            if (total <= MaxBytes)
            {
                Buffer.BlockCopy(buffer, 0, retained, retainedCount, read);
                retainedCount = total;
                continue;
            }

            var drop = total - MaxBytes;
            if ((drop & 1) != 0)
            {
                drop++;
            }

            var retainedFromPrevious = Math.Max(0, retainedCount - drop);
            if (retainedFromPrevious > 0)
            {
                Buffer.BlockCopy(retained, drop, retained, 0, retainedFromPrevious);
            }

            var bufferOffset = Math.Max(0, drop - retainedCount);
            var retainedFromBuffer = read - bufferOffset;
            if (retainedFromBuffer > 0)
            {
                Buffer.BlockCopy(buffer, bufferOffset, retained, retainedFromPrevious, retainedFromBuffer);
            }

            retainedCount = retainedFromPrevious + retainedFromBuffer;
        }

        return retained.AsSpan(0, retainedCount).ToArray();
    }

    public static string Decode(byte[]? bytes, Encoding fallback)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return string.Empty;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length < 4 || bytes.Length % 2 != 0)
        {
            return fallback.GetString(bytes);
        }

        var windowLength = Math.Min(bytes.Length, 512);
        windowLength -= windowLength % 2;
        var units = windowLength / 2;
        var zeroOdd = 0;
        var zeroEven = 0;
        for (var index = 0; index < windowLength; index += 2)
        {
            if (bytes[index] == 0)
            {
                zeroEven++;
            }

            if (bytes[index + 1] == 0)
            {
                zeroOdd++;
            }
        }

        if (zeroOdd > 0 && zeroEven == 0 && zeroOdd * 20 >= units)
        {
            return Encoding.Unicode.GetString(bytes);
        }

        if (zeroEven > 0 && zeroOdd == 0 && zeroEven * 20 >= units)
        {
            return Encoding.BigEndianUnicode.GetString(bytes);
        }

        return fallback.GetString(bytes);
    }
}
