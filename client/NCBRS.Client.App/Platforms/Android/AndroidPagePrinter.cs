using Android.Content;
using Android.Print;
using Android.Webkit;
using WebView = Android.Webkit.WebView;
using NCBRS.Client.App.Services;
using NCBRS.Client.Printing;

namespace NCBRS.Client.App;

/// <summary>
/// Prints through Android's own print system: the document as a page in an
/// off-screen web view, handed to the print dialog, where the user picks a
/// printer the tablet can reach (Mopria or a vendor plugin) or saves a PDF.
/// No printer-specific code, which is why it suits a facility with an
/// ordinary office printer.
/// </summary>
public sealed class AndroidPagePrinter : IDocumentPrinter
{
    // The print job reads from the view after this call returns, so it is
    // kept alive until the next document replaces it.
    private static WebView? _printing;

    public Task<string?> PrintAsync(PrintedDocument document)
        => MainThread.InvokeOnMainThreadAsync<string?>(() =>
        {
            var activity = Platform.CurrentActivity;
            if (activity is null)
            {
                return "The app is not in the foreground.";
            }

            var html = PrintedDocumentHtml.Render(document, QrModules.For(document.QrText));
            var view = new WebView(activity);
            view.Settings.JavaScriptEnabled = false;
            view.SetWebViewClient(new PrintWhenLoaded(activity, document.Title));
            view.LoadDataWithBaseURL(null, html, "text/html", "utf-8", null);
            _printing = view;
            return null;
        });

    private sealed class PrintWhenLoaded(Context context, string jobName) : WebViewClient
    {
        private bool _sent;

        public override void OnPageFinished(WebView? view, string? url)
        {
            if (_sent || view is null)
            {
                return;
            }

            _sent = true;
            var printer = (PrintManager)context.GetSystemService(Context.PrintService)!;
            // A4 by default: ISO paper, not the dialog's US Letter. The user
            // can still pick any size the printer offers.
            var attributes = new PrintAttributes.Builder().SetMediaSize(PrintAttributes.MediaSize.IsoA4!).Build();
            printer.Print(jobName, view.CreatePrintDocumentAdapter(jobName), attributes);
        }
    }
}
