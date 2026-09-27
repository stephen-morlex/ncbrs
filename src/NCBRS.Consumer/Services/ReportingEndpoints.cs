using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace NCBRS.Consumer.Services;

/// <summary>
/// The county-scoped reporting endpoints, as methods rather than inline
/// lambdas in <c>Program.cs</c>, so the scoping is tested where it is applied.
/// Each resolves the caller's county first (<see cref="ReportingScope"/>). A
/// test of the rule alone would stay green if an endpoint simply stopped
/// calling it.
///
/// Mapped as method groups: minimal APIs bind a method's parameters by name
/// exactly as a lambda's, so the contract is unchanged.
/// </summary>
public static class ReportingEndpoints
{
    public static async Task<IResult> SummaryAsync(
        DateTime? from,
        DateTime? to,
        string? districtId,
        ClaimsPrincipal user,
        DashboardQueryService dashboard,
        CancellationToken cancellationToken)
    {
        var scope = ReportingScope.Resolve(user, districtId);
        if (scope.AsRefusal() is { } refusal)
        {
            return refusal;
        }

        var (fromUtc, toUtc) = Range(from, to);

        return toUtc <= fromUtc
            ? Results.BadRequest(new ApiError("'to' must be after 'from'."))
            : Results.Ok(await dashboard.SummaryAsync(fromUtc, toUtc, scope.CountyCode, cancellationToken));
    }

    public static async Task<IResult> TrendsAsync(
        DateTime? from,
        DateTime? to,
        string? districtId,
        ClaimsPrincipal user,
        DashboardQueryService dashboard,
        CancellationToken cancellationToken)
    {
        var scope = ReportingScope.Resolve(user, districtId);
        if (scope.AsRefusal() is { } refusal)
        {
            return refusal;
        }

        var (fromUtc, toUtc) = Range(from, to);

        return toUtc <= fromUtc
            ? Results.BadRequest(new ApiError("'to' must be after 'from'."))
            : Results.Ok(await dashboard.TrendsAsync(fromUtc, toUtc, scope.CountyCode, cancellationToken));
    }

    /// <summary>
    /// The comparison across counties. A district officer gets their own row:
    /// the others are other counties' figures, however the request is phrased.
    /// </summary>
    public static async Task<IResult> CountiesAsync(
        DateTime? from,
        DateTime? to,
        ClaimsPrincipal user,
        DashboardQueryService dashboard,
        CancellationToken cancellationToken)
    {
        var scope = ReportingScope.Resolve(user, requestedCountyCode: null);
        if (scope.AsRefusal() is { } refusal)
        {
            return refusal;
        }

        var (fromUtc, toUtc) = Range(from, to);
        if (toUtc <= fromUtc)
        {
            return Results.BadRequest(new ApiError("'to' must be after 'from'."));
        }

        var counties = await dashboard.CountiesAsync(fromUtc, toUtc, cancellationToken);

        return Results.Ok(scope.CountyCode is { } own
            ? counties.Where(county => string.Equals(county.CountyCode, own, StringComparison.OrdinalIgnoreCase)).ToList()
            : counties);
    }

    public static async Task<IResult> SilentDevicesAsync(
        int? silentForDays,
        string? districtId,
        ClaimsPrincipal user,
        DashboardQueryService dashboard,
        CancellationToken cancellationToken)
    {
        var scope = ReportingScope.Resolve(user, districtId);
        if (scope.AsRefusal() is { } refusal)
        {
            return refusal;
        }

        var days = silentForDays ?? 7;

        return days < 1
            ? Results.BadRequest(new ApiError("'silentForDays' must be at least 1."))
            : Results.Ok(await dashboard.SilentDevicesAsync(days, scope.CountyCode, cancellationToken));
    }

    /// <summary>
    /// Defaults to the last full year of births. An unbounded default would scan
    /// the whole projection to answer a casual page load.
    /// </summary>
    public static (DateTime FromUtc, DateTime ToUtc) Range(DateTime? from, DateTime? to)
    {
        var toUtc = AsUtc(to ?? DateTime.UtcNow.Date.AddDays(1));

        return (AsUtc(from ?? toUtc.AddYears(-1)), toUtc);
    }

    // "?from=2026-09-01" parses with no kind, and ToUniversalTime would read
    // that as local time and shift it by the server's offset -- so a birth just
    // after midnight would fall outside a query for its own month, differently
    // depending on where the server happens to run. A date on the wire is UTC.
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
