using FluentValidation;
using NCBRS.Models;

namespace NCBRS.Validation;

/// <summary>
/// Bounds on a problem report. A report is short text and counts: a tablet
/// sending more than this is not reporting a problem, and the queue several
/// people read must not become a place to store anything else.
/// </summary>
public class ReportProblemRequestValidator : AbstractValidator<ReportProblemRequest>
{
    public const int MaxDescription = 1000;
    public const int MaxProblems = 5;
    public const int MaxProblemLength = 300;
    public const int MaxShortText = 64;

    public ReportProblemRequestValidator()
    {
        RuleFor(request => request.ReportId).NotEmpty();
        RuleFor(request => request.DeviceId).NotEmpty();
        RuleFor(request => request.FacilityId).NotEmpty();
        RuleFor(request => request.WrittenAtUtc).NotEmpty();
        RuleFor(request => request.Description).MaximumLength(MaxDescription);

        RuleFor(request => request.Diagnostics).NotNull();
        RuleFor(request => request.Diagnostics.AppVersion).MaximumLength(MaxShortText);
        RuleFor(request => request.Diagnostics.Platform).MaximumLength(MaxShortText);
        RuleFor(request => request.Diagnostics.Language).MaximumLength(MaxShortText);
        RuleFor(request => request.Diagnostics.ClockVerdict).MaximumLength(MaxShortText);
        RuleFor(request => request.Diagnostics.LastSyncProblems)
            .Must(problems => problems.Count <= MaxProblems)
            .WithMessage($"At most {MaxProblems} sync problems.");
        RuleForEach(request => request.Diagnostics.LastSyncProblems).MaximumLength(MaxProblemLength);
    }
}

public class ResolveProblemReportRequestValidator : AbstractValidator<ResolveProblemReportRequest>
{
    public const int MaxNote = 500;

    public ResolveProblemReportRequestValidator()
    {
        RuleFor(request => request.Note).MaximumLength(MaxNote);
    }
}
