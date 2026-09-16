using FluentValidation;
using Microsoft.AspNetCore.Mvc.Filters;
using NCBRS.Models;

namespace NCBRS.Middleware;

/// <summary>
/// Runs the FluentValidation validator registered for each bound argument
/// and turns any failures into the API's standard error shape.
///
/// Written by hand rather than using FluentValidation.AspNetCore: that
/// package's automatic validation is deprecated by its maintainer and is
/// still pinned to the 11.x line, so wiring it here would have meant
/// holding the core library back a major version.
///
/// Ordered ahead of the idempotency claim, so a payload that never had a
/// chance of succeeding is rejected without opening a transaction or
/// consuming a transaction id.
/// </summary>
public class FluentValidationFilter(IServiceProvider services) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var errors = new List<ApiError>();

        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null)
            {
                continue;
            }

            // The envelope is unwrapped rather than validated as a whole:
            // a validator per ApiRequest<T> would need registering for every
            // new endpoint, and a forgotten one fails open.
            if (argument is IHasRequestMeta envelope)
            {
                await ValidateEnvelopeAsync(envelope, errors, context.HttpContext.RequestAborted);
            }
            else
            {
                errors.AddRange(await ValidateAsync(argument, prefix: null, context.HttpContext.RequestAborted));
            }
        }

        if (errors.Count > 0)
        {
            context.Result = ApiErrors.Result(new ApiErrorResponse(
                StatusCodes.Status400BadRequest, "Validation failed.", errors));
            return;
        }

        await next();
    }

    private async Task ValidateEnvelopeAsync(
        IHasRequestMeta envelope,
        List<ApiError> errors,
        CancellationToken cancellationToken)
    {
        if (envelope.Meta is not null)
        {
            errors.AddRange(await ValidateAsync(envelope.Meta, prefix: "meta", cancellationToken));
        }

        var data = GetData(envelope);

        if (data is null)
        {
            errors.Add(new ApiError("data", "data is required."));
            return;
        }

        errors.AddRange(await ValidateAsync(data, prefix: "data", cancellationToken));
    }

    private async Task<IEnumerable<ApiError>> ValidateAsync(
        object model,
        string? prefix,
        CancellationToken cancellationToken)
    {
        if (services.GetService(typeof(IValidator<>).MakeGenericType(model.GetType())) is not IValidator validator)
        {
            return [];
        }

        var result = await validator.ValidateAsync(new ValidationContext<object>(model), cancellationToken);

        return result.Errors.Select(failure =>
            new ApiError(FieldName(prefix, failure.PropertyName), failure.ErrorMessage));
    }

    private static object? GetData(IHasRequestMeta envelope)
        => envelope.GetType().GetProperty(nameof(ApiRequest<object>.Data))?.GetValue(envelope);

    /// <summary>
    /// FluentValidation reports paths in CLR casing ("ChildFullName"). The
    /// client sent camelCase JSON, so point them at the field they typed --
    /// "data.childFullName", not "ChildFullName".
    /// </summary>
    private static string FieldName(string? prefix, string propertyPath)
    {
        // A rule written against the whole object (RuleFor(x => x)) reports an
        // empty path. It is about "data" itself, so it points there rather
        // than at "data." or a doubled "data.data".
        if (string.IsNullOrEmpty(propertyPath))
        {
            return prefix ?? "data";
        }

        var segments = propertyPath.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length > 0 && char.IsUpper(segment[0]))
            {
                segments[i] = char.ToLowerInvariant(segment[0]) + segment[1..];
            }
        }

        var path = string.Join('.', segments);

        return prefix is null ? path : $"{prefix}.{path}";
    }
}
