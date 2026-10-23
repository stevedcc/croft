using System.IO.Compression;

namespace ScreenshotGate.Compare;

/// <summary>Writes small RGBA PNGs (the gate's diff crops) without taking an image library
/// dependency — the PngPixelDecoder pattern, in reverse.</summary>
internal static class PngWriter
{
    /// <summary>Encodes 8-bit RGBA pixels (row-major) as a PNG.</summary>
    public static byte[] EncodeRgba(int width, int height, byte[] rgba)
    {
        var stride = width * 4;
        var raw = new byte[height * (1 + stride)];
        for (var row = 0; row < height; row++)
        {
            raw[row * (1 + stride)] = 0; // filter: none
            Buffer.BlockCopy(rgba, row * stride, raw, row * (1 + stride) + 1, stride);
        }

        using var idat = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionMode.Compress, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> ihdr = stackalloc byte[13];
        WriteInt(ihdr, 0, width);
        WriteInt(ihdr, 4, height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 6;    // colour type: RGBA
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", idat.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(MemoryStream png, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        WriteInt(header, 0, data.Length);
        System.Text.Encoding.ASCII.GetBytes(type, header[4..8]);
        png.Write(header);
        png.Write(data);

        var crcInput = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type, crcInput.AsSpan(0, 4));
        data.CopyTo(crcInput.AsSpan(4));
        Span<byte> crc = stackalloc byte[4];
        WriteInt(crc, 0, (int)Crc32(crcInput));
        png.Write(crc);
    }

    private static void WriteInt(Span<byte> target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
