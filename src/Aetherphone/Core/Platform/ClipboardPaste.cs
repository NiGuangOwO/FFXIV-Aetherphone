using System.Runtime.InteropServices;
using Aetherphone.Core.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Aetherphone.Core.Platform;

internal static class ClipboardPaste
{
    private const uint CfDib = 8;
    private const uint CfDibV5 = 17;

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    public static bool HasImage()
    {
        return IsClipboardFormatAvailable(CfDib) || IsClipboardFormatAvailable(CfDibV5);
    }

    public static byte[]? ReadImagePng(int maxDimension)
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return null;
        }

        try
        {
            var format = IsClipboardFormatAvailable(CfDib) ? CfDib
                : IsClipboardFormatAvailable(CfDibV5) ? CfDibV5
                : 0;
            if (format == 0)
            {
                return null;
            }

            var handle = GetClipboardData(format);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var size = (long)GlobalSize(handle);
            if (size <= 0 || size > int.MaxValue)
            {
                return null;
            }

            var locked = GlobalLock(handle);
            if (locked == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var dib = new byte[(int)size];
                Marshal.Copy(locked, dib, 0, dib.Length);
                var bmp = DibToBmp(dib);
                if (bmp is null)
                {
                    return null;
                }

                using var input = new MemoryStream(bmp);
                using var image = Image.Load<Rgba32>(ImageProcessor.SingleFrame, input);
                if (maxDimension > 0 && (image.Width > maxDimension || image.Height > maxDimension))
                {
                    var factor = MathF.Min((float)maxDimension / image.Width, (float)maxDimension / image.Height);
                    var width = Math.Max(1, (int)MathF.Round(image.Width * factor));
                    var height = Math.Max(1, (int)MathF.Round(image.Height * factor));
                    image.Mutate(context => context.Resize(width, height));
                }

                using var output = new MemoryStream();
                image.SaveAsPng(output);
                return output.ToArray();
            }
            finally
            {
                GlobalUnlock(locked);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static byte[]? DibToBmp(byte[] dib)
    {
        if (dib.Length < 40)
        {
            return null;
        }

        var headerSize = BitConverter.ToInt32(dib, 0);
        var payload = headerSize switch
        {
            40 => dib,
            124 => ConvertV5Header(dib),
            _ => null,
        };
        if (payload is null)
        {
            return null;
        }

        var bitCount = BitConverter.ToInt16(payload, 14);
        var colorUsed = BitConverter.ToInt32(payload, 32);
        var paletteSize = colorUsed > 0
            ? colorUsed * 4
            : bitCount <= 8 ? (1 << bitCount) * 4 : 0;
        var offBits = 14 + 40 + paletteSize;
        if (offBits > 14 + payload.Length)
        {
            offBits = 14 + payload.Length;
        }

        var bmp = new byte[14 + payload.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        WriteInt32(bmp, 2, bmp.Length);
        WriteInt32(bmp, 10, offBits);
        Buffer.BlockCopy(payload, 0, bmp, 14, payload.Length);
        return bmp;
    }

    private static byte[]? ConvertV5Header(byte[] dib)
    {
        if (dib.Length < 124)
        {
            return null;
        }

        var header = new byte[40];
        WriteInt32(header, 0, 40);
        WriteInt32(header, 4, BitConverter.ToInt32(dib, 4));
        WriteInt32(header, 8, BitConverter.ToInt32(dib, 8));
        WriteInt16(header, 12, BitConverter.ToInt16(dib, 12));
        WriteInt16(header, 14, BitConverter.ToInt16(dib, 14));
        WriteInt32(header, 16, BitConverter.ToInt32(dib, 16));
        var payload = new byte[40 + dib.Length - 124];
        Buffer.BlockCopy(header, 0, payload, 0, 40);
        Buffer.BlockCopy(dib, 124, payload, 40, dib.Length - 124);
        return payload;
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteInt16(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }
}
