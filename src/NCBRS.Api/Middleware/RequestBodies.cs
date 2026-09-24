namespace NCBRS.Middleware;

public static class RequestBodies
{
    /// <summary>
    /// The body exactly as it arrived, for device-signature verification.
    ///
    /// Read back from the buffered stream rather than re-serialised from the
    /// bound model: a re-serialisation is a second opinion about what the
    /// device sent, and the signature is over the first. Buffering is enabled
    /// only for requests carrying a signature header (Program.cs), so a request
    /// without one yields nothing here — which is what a verifier that then
    /// finds no signature should see.
    /// </summary>
    public static async Task<ReadOnlyMemory<byte>> ReadRawAsync(this HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.Body.CanSeek)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        request.Body.Position = 0;

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);

        return buffer.ToArray();
    }
}
