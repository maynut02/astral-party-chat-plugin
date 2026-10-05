using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AstralPartyChatPlugin;

internal static class RemotePayload
{
    public const int MaxCharactersBytes = 64 * 1024;
    public const int MaxRoomBytes = 16 * 1024;
    public const int MaxPortraitBytes = 4 * 1024 * 1024;
    public const int MaxPortraitDimension = 1024;
    public const long MaxPortraitPixels = 1024 * 1024;

    // Callers use ResponseHeadersRead and keep their deadline alive through this read.
    public static async Task<byte[]> ReadBytesAsync(HttpContent content, int maximumBytes, CancellationToken token)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (content.Headers.ContentLength is { } length && (length < 0 || length > maximumBytes))
            throw new InvalidDataException("Remote response exceeds the size limit.");
        using var source = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(8192, maximumBytes)];
        while (true)
        {
            var count = (int)Math.Min(buffer.Length, maximumBytes - output.Length + 1);
            var read = await source.ReadAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("Remote response exceeds the size limit.");
            output.Write(buffer, 0, read);
        }
    }

    // Reject excessive dimensions before Unity's native decoder allocates a texture.
    public static bool IsSafePortraitPng(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length < 33 || bytes.Length > MaxPortraitBytes
            || !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(8, 4)) != 13
            || !bytes.Slice(12, 4).SequenceEqual(new byte[] { 73, 72, 68, 82 }))
            return false;
        var pngWidth = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4));
        var pngHeight = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4));
        if (pngWidth == 0 || pngHeight == 0
            || pngWidth > MaxPortraitDimension || pngHeight > MaxPortraitDimension
            || (long)pngWidth * pngHeight > MaxPortraitPixels)
            return false;
        width = (int)pngWidth;
        height = (int)pngHeight;
        return true;
    }
}
