using Android.Graphics;
using Android.Text;
using Color = Android.Graphics.Color;
using Layout = Android.Text.Layout;
using Paint = Android.Graphics.Paint;
using NCBRS.Client.App.Services;
using NCBRS.Client.Printing;

namespace NCBRS.Client.App;

/// <summary>
/// Draws a <see cref="PrintedDocument"/> the width of a thermal printer's
/// paper, then reduces it to black and white dots for <see cref="EscPos"/>.
///
/// Android lays out the text, so Arabic is shaped and runs right to left
/// exactly as it does on screen; the printer never sees a character. Labels
/// and values are stacked rather than side by side, because 48 mm of paper
/// has no room for two columns.
/// </summary>
public static class ThermalRenderer
{
    private const int Margin = 8;
    private const int Gap = 10;

    public static Bitmap Draw(PrintedDocument document, int widthDots)
    {
        var textWidth = widthDots - 2 * Margin;
        var blocks = new List<(Func<Canvas, int, int> Draw, int Height)>();

        void Text(string text, float size, bool bold, bool centred, int gapAfter = Gap)
        {
            var paint = new TextPaint(PaintFlags.AntiAlias) { Color = Color.Black, TextSize = size };
            paint.SetTypeface(Typeface.Create(Typeface.Default, bold ? TypefaceStyle.Bold : TypefaceStyle.Normal));
            var layout = StaticLayout.Builder.Obtain(text, 0, text.Length, paint, textWidth)
                .SetAlignment(centred ? Layout.Alignment.AlignCenter! : Layout.Alignment.AlignNormal!)
                .SetTextDirection(document.RightToLeft ? TextDirectionHeuristics.Rtl! : TextDirectionHeuristics.Ltr!)
                .Build();
            Func<Canvas, int, int> draw = (canvas, top) =>
            {
                canvas.Save();
                canvas.Translate(Margin, top);
                layout.Draw(canvas);
                canvas.Restore();
                return layout.Height;
            };
            blocks.Add((draw, layout.Height + gapAfter));
        }

        Text(document.Heading, 20, bold: false, centred: true);
        Text(document.Title, 30, bold: true, centred: true, gapAfter: 16);

        foreach (var line in document.Lines)
        {
            Text(line.Label, 20, bold: false, centred: false, gapAfter: 2);
            Text(line.Value, 26, bold: true, centred: false);
        }

        // The QR code as large as the paper allows, whole dots per module,
        // with the four-module quiet zone a scanner needs to find it.
        var modules = QrModules.For(document.QrText);
        var count = modules.GetLength(0);
        var module = Math.Max(2, Math.Min(8, (int)(widthDots * 0.8) / (count + 8)));
        var qrSide = (count + 8) * module;
        Func<Canvas, int, int> drawQr = (canvas, top) =>
        {
            var paint = new Paint { Color = Color.Black };
            var left = (widthDots - qrSide) / 2 + 4 * module;
            for (var y = 0; y < count; y++)
            {
                for (var x = 0; x < count; x++)
                {
                    if (modules[x, y])
                    {
                        canvas.DrawRect(left + x * module, top + (4 + y) * module, left + (x + 1) * module, top + (5 + y) * module, paint);
                    }
                }
            }

            return qrSide;
        };
        blocks.Add((drawQr, qrSide + Gap));

        Text(document.Notice, 20, bold: false, centred: true, gapAfter: 0);

        var height = Margin + blocks.Sum(block => block.Height) + Margin;
        var bitmap = Bitmap.CreateBitmap(widthDots, height, Bitmap.Config.Argb8888!)!;
        using var page = new Canvas(bitmap);
        page.DrawColor(Color.White);

        // A provisional slip is framed, so it cannot be mistaken for an ordinary one.
        if (document.Provisional)
        {
            var frame = new Paint { Color = Color.Black, StrokeWidth = 4 };
            frame.SetStyle(Paint.Style.Stroke);
            page.DrawRect(2, 2, widthDots - 2, height - 2, frame);
        }

        var top = Margin;
        foreach (var (draw, blockHeight) in blocks)
        {
            draw(page, top);
            top += blockHeight;
        }

        return bitmap;
    }

    /// <summary>Black where the drawn page is darker than mid grey: thermal paper has no greys.</summary>
    public static MonochromeImage Dots(Bitmap bitmap)
    {
        var pixels = new int[bitmap.Width * bitmap.Height];
        bitmap.GetPixels(pixels, 0, bitmap.Width, 0, 0, bitmap.Width, bitmap.Height);
        var black = new bool[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var colour = pixels[i];
            var luminance = (299 * ((colour >> 16) & 0xFF) + 587 * ((colour >> 8) & 0xFF) + 114 * (colour & 0xFF)) / 1000;
            black[i] = luminance < 128;
        }

        return new MonochromeImage(bitmap.Width, bitmap.Height, black);
    }
}
