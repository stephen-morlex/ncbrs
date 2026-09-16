using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NCBRS.Models;

namespace NCBRS.Middleware;

/// <summary>
/// Builds the one error shape the API returns, so a client parses
/// validation failures, conflicts and not-founds the same way. These are
/// placed in the envelope's <c>data</c> by MetaEnvelopeFilter, which means
/// every rejection still carries the caller's transaction id.
/// </summary>
public static class ApiErrors
{
    public static ApiErrorResponse FromModelState(ModelStateDictionary modelState)
    {
        var errors = modelState
            .SelectMany(entry => entry.Value!.Errors.Select(error => new ApiError(
                CamelCase(entry.Key),
                // A binding failure (a string where a number belongs) has no
                // ErrorMessage, only an exception -- surface something a
                // human can act on rather than an empty string.
                string.IsNullOrWhiteSpace(error.ErrorMessage)
                    ? $"{CamelCase(entry.Key)} is not a valid value."
                    : error.ErrorMessage)))
            .ToList();

        return new ApiErrorResponse(
            StatusCodes.Status400BadRequest,
            "Validation failed.",
            errors);
    }

    public static ApiErrorResponse Single(int status, string title, string field, string message)
        => new(status, title, [new ApiError(field, message)]);

    public static ObjectResult Result(ApiErrorResponse error)
        => new(error) { StatusCode = error.Status };

    /// <summary>
    /// ModelState keys arrive in CLR casing ("Data.ChildFullName"); the JSON
    /// the client sent used camelCase, so point them at the field they
    /// actually typed.
    /// </summary>
    private static string CamelCase(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return key;
        }

        var segments = key.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length > 0 && char.IsUpper(segment[0]))
            {
                segments[i] = char.ToLowerInvariant(segment[0]) + segment[1..];
            }
        }

        return string.Join('.', segments);
    }
}
