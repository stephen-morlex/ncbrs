using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using NCBRS.Data;
using NCBRS.Services;

namespace NCBRS.Tests;

/// <summary>
/// Builds the authenticated context a controller now needs: a principal
/// carrying a Keycloak subject and realm roles, plus the
/// CurrentRegistrarService that resolves it to a Registrar.
/// </summary>
public static class AuthTestContext
{
    /// <summary>
    /// Matches the subject seeded on the test registrar. Arbitrary, but must
    /// agree between the principal and the Registrar row or the caller
    /// resolves to nobody.
    /// </summary>
    public const string DefaultSubject = "11111111-1111-4111-8111-111111111111";

    public static DefaultHttpContext HttpContextFor(
        string subject = DefaultSubject,
        params string[] roles)
        => HttpContextFor(subject, client: null, roles);

    /// <summary>
    /// As above, with the token's authorised party (`azp`): the OIDC client
    /// the identity provider issued it to, which decides the registration
    /// channel.
    /// </summary>
    public static DefaultHttpContext HttpContextFor(string subject, string? client, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, subject)
        };

        if (client is not null)
        {
            claims.Add(new Claim("azp", client));
        }

        claims.AddRange((roles.Length > 0 ? roles : [NcbrsRoles.FacilityRegistrar])
            .Select(role => new Claim(ClaimTypes.Role, role)));

        var identity = new ClaimsIdentity(claims, authenticationType: "Test",
            nameType: ClaimTypes.Name, roleType: ClaimTypes.Role);

        return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    public static CurrentRegistrarService RegistrarService(NcbrsDbContext db, HttpContext http)
        => new(db, new HttpContextAccessor { HttpContext = http }, new CountyLookup(db));
}
