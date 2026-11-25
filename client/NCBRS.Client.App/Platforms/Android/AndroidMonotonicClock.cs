using Android.OS;
using NCBRS.Client.Time;
using AndroidSettings = Android.Provider.Settings;

namespace NCBRS.Client.App;

/// <summary>
/// Android's elapsed real time: time since boot, deep sleep included, which the
/// date setting cannot change. A boot is named by the system's boot count, so a
/// reading taken before the app was closed still compares with one taken after,
/// as long as the tablet was not restarted in between.
/// </summary>
public sealed class AndroidMonotonicClock : IMonotonicClock
{
    // Read once: the count cannot change while this process lives. If it cannot
    // be read, every app start is its own "boot", which is the conservative
    // answer (TrustedClock and OfflinePinLock then restart rather than carry on).
    private readonly string _bootId = BootId();

    public MonotonicReading Read() => new(_bootId, TimeSpan.FromMilliseconds(SystemClock.ElapsedRealtime()));

    private static string BootId()
    {
        var count = AndroidSettings.Global.GetInt(
            Android.App.Application.Context.ContentResolver, AndroidSettings.Global.BootCount, -1);
        return count >= 0 ? $"android-boot-{count}" : "process-" + Guid.NewGuid().ToString("N");
    }
}
