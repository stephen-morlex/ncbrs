using System.Security.Cryptography;
using System.Text.Json;
using NCBRS.Client.Localization;
using NCBRS.Client.Network;

namespace NCBRS.Client.Storage;

/// <summary>
/// The device store could not be read: the key is not the one it was written
/// with, the file was altered, or it is not a store at all.
///
/// Deliberately an exception and never an empty result. A store read as "no
/// state" makes the app start as a fresh device — a new key, a new enrolment —
/// and its first save overwrites births that were never synced. The shell must
/// stop and say so, and must not save over the file.
/// </summary>
public sealed class StateFileUnreadableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// WS-B2: the tablet's state at rest, encrypted with AES-256-GCM under a key
/// the platform keeps (Android Keystore, via MAUI SecureStorage) — never
/// beside the file.
///
/// The format is one version byte, a 12-byte nonce, the 16-byte tag and the
/// ciphertext of <see cref="DeviceState"/> as JSON. The tag covers the version
/// byte too, so a file cannot be relabelled to be read another way. Every save
/// draws a fresh nonce: GCM under a repeated nonce and key gives away the key
/// stream.
///
/// <b>A save replaces the file whole or not at all.</b> It writes a temporary
/// file, flushes it to the disk, then renames it over the old one, which both
/// Android and Windows do atomically on one volume. A tablet whose battery dies
/// mid-save keeps the previous state, never half of each — and the previous
/// state is always consistent, because the core saves after every act.
///
/// Saves are serialised: a connectivity window's persist callback and a
/// registration may both save at once.
/// </summary>
public sealed class EncryptedStateFile
{
    public const int KeySize = 32;

    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 1 + NonceSize + TagSize;

    private static readonly byte[] Context = "ncbrs-device-state"u8.ToArray();

    private readonly string _path;
    private readonly string _temporaryPath;
    private readonly byte[] _key;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EncryptedStateFile(string path, byte[] key)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"The state key must be {KeySize} bytes.", nameof(key));
        }

        _path = path;
        _temporaryPath = path + ".tmp";
        _key = [.. key];
    }

    /// <summary>A new random key, for the platform key store to keep.</summary>
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeySize);

    /// <summary>Whether a store has ever been written here.</summary>
    public bool Exists => File.Exists(_path);

    /// <summary>
    /// The saved state, or null if nothing was ever saved. Throws
    /// <see cref="StateFileUnreadableException"/> if a file is there and cannot
    /// be read — never null for that.
    /// </summary>
    public async Task<DeviceState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // A temporary file left by a save that never reached its rename is
            // an unfinished write, not state. The file it would have replaced
            // is still whole.
            if (!File.Exists(_path))
            {
                return null;
            }

            var sealedBytes = await File.ReadAllBytesAsync(_path, cancellationToken);
            if (sealedBytes.Length < HeaderSize || sealedBytes[0] != FormatVersion)
            {
                throw new StateFileUnreadableException(
                    Strings.Store_UnknownFormat);
            }

            var nonce = sealedBytes.AsSpan(1, NonceSize);
            var tag = sealedBytes.AsSpan(1 + NonceSize, TagSize);
            var ciphertext = sealedBytes.AsSpan(HeaderSize);
            var plaintext = new byte[ciphertext.Length];

            try
            {
                using var aes = new AesGcm(_key, TagSize);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData());
            }
            catch (AuthenticationTagMismatchException exception)
            {
                throw new StateFileUnreadableException(
                    Strings.Store_NotDecrypted, exception);
            }

            try
            {
                return JsonSerializer.Deserialize<DeviceState>(plaintext, ClientJson.Options)
                       ?? throw new StateFileUnreadableException(Strings.Store_Empty);
            }
            catch (JsonException exception)
            {
                throw new StateFileUnreadableException(Strings.Store_NotParsed, exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Replace the saved state with <paramref name="state"/>, atomically.</summary>
    public async Task SaveAsync(DeviceState state, CancellationToken cancellationToken = default)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(state, ClientJson.Options);
        var sealedBytes = new byte[HeaderSize + plaintext.Length];
        sealedBytes[0] = FormatVersion;

        var nonce = sealedBytes.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Encrypt(
                nonce,
                plaintext,
                sealedBytes.AsSpan(HeaderSize),
                sealedBytes.AsSpan(1 + NonceSize, TagSize),
                AssociatedData());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);

            await using (var file = new FileStream(
                             _temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             bufferSize: 4096, FileOptions.WriteThrough))
            {
                await file.WriteAsync(sealedBytes, cancellationToken);
                file.Flush(flushToDisk: true);
            }

            File.Move(_temporaryPath, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static byte[] AssociatedData() => [.. Context, FormatVersion];
}
