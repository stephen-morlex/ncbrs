using NCBRS.Client.Printing;
using ZXing;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace NCBRS.Client.App.Services;

/// <summary>
/// A printer the tablet can send a document to. Android's print system is
/// one (an A4/A5 page, or a PDF); a Bluetooth thermal printer is the other.
/// </summary>
public interface IDocumentPrinter
{
    /// <summary>Hands the document over; null when sent, else what went wrong.</summary>
    Task<string?> PrintAsync(PrintedDocument document);
}

public static class QrModules
{
    /// <summary>
    /// The QR code's modules, with no margin: each printer adds its own quiet
    /// zone. Error correction M, the common choice for printed codes that
    /// may be creased or smudged.
    /// </summary>
    public static bool[,] For(string text)
    {
        var matrix = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0, new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.MARGIN] = 0,
            [EncodeHintType.ERROR_CORRECTION] = ErrorCorrectionLevel.M,
            [EncodeHintType.CHARACTER_SET] = "UTF-8",
        });

        var modules = new bool[matrix.Width, matrix.Height];
        for (var y = 0; y < matrix.Height; y++)
        {
            for (var x = 0; x < matrix.Width; x++)
            {
                modules[x, y] = matrix[x, y];
            }
        }

        return modules;
    }
}
