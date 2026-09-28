using NCBRS.Client.Storage;

namespace NCBRS.Client.App.Services;

/// <summary>
/// Opens the tablet's encrypted store (B2): the file in the app's private data
/// directory, its key in the platform key store (Android Keystore, through
/// SecureStorage). Key and file never sit side by side.
///
/// Everything with a rule in it — the encryption, the atomic save, when a key
/// may be created — is in <c>NCBRS.Client.Core.Storage</c> and tested there;
/// this only supplies the platform's places.
/// </summary>
public static class DeviceStorage
{
    private const string KeyName = "ncbrs-device-state-key";

    public static string StatePath => Path.Combine(FileSystem.AppDataDirectory, "device.state");

    public static async Task<EncryptedStateFile> OpenAsync()
    {
        var key = await StateKey.ResolveAsync(
            File.Exists(StatePath),
            () => SecureStorage.Default.GetAsync(KeyName),
            value => SecureStorage.Default.SetAsync(KeyName, value));

        return new EncryptedStateFile(StatePath, key);
    }
}
