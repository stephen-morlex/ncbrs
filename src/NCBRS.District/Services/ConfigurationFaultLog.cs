namespace NCBRS.District.Services;

/// <summary>
/// Logs a configuration fault — the node's credentials refused, its token not
/// accepted — as an error, at most once a minute.
///
/// These are the failures that look like an outage from the outside: batches
/// are held and the queue grows, exactly as when the link is down. Unlike an
/// outage they never fix themselves, so they have to be said out loud. Once a
/// minute, because every batch on every poll hits the same fault.
///
/// A singleton rather than state on <see cref="CentralApiClient"/>, which is a
/// typed HTTP client and created afresh for each poll.
/// </summary>
public sealed class ConfigurationFaultLog
{
    private long _lastReportedTicks;

    public void Report(ILogger logger, string fault)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastReportedTicks);
        if (now - last < TimeSpan.TicksPerMinute
            || Interlocked.CompareExchange(ref _lastReportedTicks, now, last) != last)
        {
            return;
        }

        logger.LogError("District node configuration fault: {Fault}", fault);
    }
}
