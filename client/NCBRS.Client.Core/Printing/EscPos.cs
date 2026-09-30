namespace NCBRS.Client.Printing;

/// <summary>
/// A 1-bit image, row by row from the top: true is a black dot. The shell
/// renders a <see cref="PrintedDocument"/> into one at the printer's width
/// (384 dots for 58 mm paper, 576 for 80 mm).
/// </summary>
public sealed class MonochromeImage
{
    public MonochromeImage(int width, int height, bool[] black)
    {
        if (width <= 0 || height <= 0 || black.Length != width * height)
        {
            throw new ArgumentException("The pixels do not match the size given.", nameof(black));
        }

        Width = width;
        Height = height;
        Black = black;
    }

    public int Width { get; }
    public int Height { get; }
    public bool[] Black { get; }
}

/// <summary>
/// The bytes a portable ESC/POS thermal printer takes.
///
/// <b>Everything is sent as a raster image, never as printer text.</b> The
/// cheap Bluetooth printers a village post can afford carry Latin fonts only,
/// and none of them shapes Arabic. Rendered by the tablet and sent as dots, the
/// slip says exactly what the screen says, in either language, with its QR
/// code, on any printer that speaks ESC/POS.
/// </summary>
public static class EscPos
{
    /// <summary>
    /// Rows per raster command. The command allows more, but small printers
    /// have small buffers, and a tall image in one command is where the cheap
    /// ones drop lines.
    /// </summary>
    public const int BandRows = 255;

    /// <summary>ESC @: reset the printer to its defaults.</summary>
    public static readonly byte[] Initialise = [0x1B, 0x40];

    /// <summary>ESC d n: feed n lines, so the end of the slip clears the tear bar.</summary>
    public static byte[] Feed(byte lines) => [0x1B, 0x64, lines];

    /// <summary>GS V 66 0: feed to the cutter and cut, on printers that have one. Others ignore it.</summary>
    public static readonly byte[] Cut = [0x1D, 0x56, 0x42, 0x00];

    /// <summary>
    /// GS v 0, in bands: each row packed eight dots to a byte, the leftmost dot
    /// in the highest bit, padded to a whole byte on the right.
    /// </summary>
    public static byte[] Raster(MonochromeImage image)
    {
        var bytesPerRow = (image.Width + 7) / 8;
        using var output = new MemoryStream();

        for (var top = 0; top < image.Height; top += BandRows)
        {
            var rows = Math.Min(BandRows, image.Height - top);
            output.Write([0x1D, 0x76, 0x30, 0x00,
                (byte)(bytesPerRow & 0xFF), (byte)(bytesPerRow >> 8),
                (byte)(rows & 0xFF), (byte)(rows >> 8)]);

            var band = new byte[bytesPerRow * rows];
            for (var y = 0; y < rows; y++)
            {
                for (var x = 0; x < image.Width; x++)
                {
                    if (image.Black[(top + y) * image.Width + x])
                    {
                        band[y * bytesPerRow + x / 8] |= (byte)(0x80 >> (x % 8));
                    }
                }
            }

            output.Write(band);
        }

        return output.ToArray();
    }

    /// <summary>A whole print job: reset, the image, a feed past the tear bar, and a cut.</summary>
    public static byte[] Job(MonochromeImage image, byte feedLines = 4)
        => [.. Initialise, .. Raster(image), .. Feed(feedLines), .. Cut];
}
