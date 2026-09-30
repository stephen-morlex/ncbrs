using System.Net;
using System.Text;

namespace NCBRS.Client.Printing;

/// <summary>
/// A <see cref="PrintedDocument"/> as a page for Android's print system: the
/// tablet hands this to a print view and the user picks a printer, or saves a
/// PDF. Here rather than in the shell so it is tested: every value on the page
/// is typed by someone, and a name is text, never markup.
///
/// The QR code comes in as its modules (the shell's QR encoder) and is drawn
/// as a single SVG path, so it prints sharp at any size with no image file.
/// </summary>
public static class PrintedDocumentHtml
{
    public static string Render(PrintedDocument document, bool[,] qrModules)
    {
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"").Append(document.RightToLeft ? "ar" : "en")
            .Append("\" dir=\"").Append(document.RightToLeft ? "rtl" : "ltr").Append("\"><head><meta charset=\"utf-8\">")
            .Append("<style>")
            // No page size: the page follows whatever paper is chosen in the
            // print dialog, which defaults to A4 (ISO, as South Sudan uses).
            .Append("@page{margin:12mm}")
            .Append("body{font-family:sans-serif;color:#000;margin:0}")
            .Append(".heading{font-size:11pt;text-align:center;margin:0 0 4mm}")
            .Append("h1{font-size:18pt;text-align:center;margin:0 0 6mm}")
            .Append(".provisional{border:2px solid #000;padding:2mm;text-align:center;font-weight:bold;margin:0 0 4mm}")
            .Append("table{width:100%;border-collapse:collapse;font-size:12pt}")
            .Append("th{text-align:start;font-weight:normal;padding:1.5mm 3mm 1.5mm 0;width:40%;vertical-align:top}")
            .Append("td{font-weight:bold;padding:1.5mm 0;vertical-align:top}")
            .Append(".qr{text-align:center;margin:6mm 0 3mm}")
            .Append(".qr svg{width:42mm;height:42mm}")
            .Append(".notice{font-size:10pt;text-align:center;margin:0}")
            .Append("</style></head><body>");

        html.Append("<p class=\"heading\">").Append(Text(document.Heading)).Append("</p>");
        html.Append("<h1>").Append(Text(document.Title)).Append("</h1>");

        if (document.Provisional)
        {
            html.Append("<p class=\"provisional\">").Append(Text(document.Title)).Append("</p>");
        }

        html.Append("<table>");
        foreach (var line in document.Lines)
        {
            html.Append("<tr><th>").Append(Text(line.Label)).Append("</th><td><bdi>")
                .Append(Text(line.Value)).Append("</bdi></td></tr>");
        }

        html.Append("</table>");
        html.Append("<div class=\"qr\">").Append(Svg(qrModules)).Append("</div>");
        html.Append("<p class=\"notice\">").Append(Text(document.Notice)).Append("</p>");
        html.Append("</body></html>");
        return html.ToString();
    }

    /// <summary>The modules as one path, with a four-module quiet zone, which scanners need to find the code.</summary>
    public static string Svg(bool[,] modules)
    {
        const int quiet = 4;
        var width = modules.GetLength(0);
        var height = modules.GetLength(1);
        var path = new StringBuilder();

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (modules[x, y])
                {
                    path.Append('M').Append(x + quiet).Append(' ').Append(y + quiet).Append("h1v1h-1z");
                }
            }
        }

        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width + 2 * quiet} {height + 2 * quiet}\" "
               + "shape-rendering=\"crispEdges\"><rect width=\"100%\" height=\"100%\" fill=\"#fff\"/>"
               + $"<path d=\"{path}\" fill=\"#000\"/></svg>";
    }

    private static string Text(string value) => WebUtility.HtmlEncode(value);
}
