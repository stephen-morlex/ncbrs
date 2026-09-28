namespace NCBRS.Client.Storage;

/// <summary>
/// Where the store's key comes from: the platform's key store (Android
/// Keystore, via MAUI SecureStorage), which keeps it off the file system the
/// store sits on.
///
/// The one rule here is when a key may be <em>created</em>: only when there is
/// no store yet. A platform key store can lose its entry — a cleared credential
/// store, a restore — while the file survives. Minting a new key then would
/// look like success: the app would start, find the store unreadable, and the
/// first save under the new key would bury births nobody has synced. So a
/// missing key beside an existing store is refused, and the tablet goes to the
/// district rather than on.
/// </summary>
public static class StateKey
{
    public static async Task<byte[]> ResolveAsync(
        bool storeExists,
        Func<Task<string?>> readKey,
        Func<string, Task> writeKey)
    {
        if (await readKey() is { Length: > 0 } saved)
        {
            return Convert.FromBase64String(saved);
        }

        if (storeExists)
        {
            throw new StateFileUnreadableException(
                "The device store exists but its key is gone from the platform key store. "
                + "Do not reset the app: the store may hold births that have not been synced.");
        }

        var key = EncryptedStateFile.NewKey();
        await writeKey(Convert.ToBase64String(key));
        return key;
    }
}
