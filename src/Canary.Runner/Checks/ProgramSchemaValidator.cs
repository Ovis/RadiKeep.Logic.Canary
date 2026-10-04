using System.Text;
using RadiCorder.Logics.RdbContext;
using RadiCorder.Logics.Models.NhkRadiru.JsonEntity;

namespace Canary.Runner;

/// <summary>
/// 録音に必要な必須項目と任意項目の欠落を判定する。
/// </summary>
internal static class ProgramSchemaValidator
{
    internal static ProgramSchemaValidationResult ValidateRadikoPrograms(
        IReadOnlyList<RadikoProgram> programs)
    {
        var requiredIssues = new List<ProgramSchemaIssue>();
        var optionalMissing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seenProgramIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < programs.Count; i++)
        {
            var p = programs[i];
            var idForLog = string.IsNullOrWhiteSpace(p.ProgramId) ? $"index:{i}" : p.ProgramId;

            if (string.IsNullOrWhiteSpace(p.ProgramId))
            {
                requiredIssues.Add(new ProgramSchemaIssue(idForLog, "ProgramId", "missing"));
            }
            else if (!seenProgramIds.Add(p.ProgramId))
            {
                requiredIssues.Add(new ProgramSchemaIssue(idForLog, "ProgramId", "duplicate"));
            }

            if (string.IsNullOrWhiteSpace(p.StationId))
            {
                requiredIssues.Add(new ProgramSchemaIssue(idForLog, "StationId", "missing"));
            }

            if (string.IsNullOrWhiteSpace(p.Title))
            {
                requiredIssues.Add(new ProgramSchemaIssue(idForLog, "Title", "missing"));
            }

            if (p.StartTime == default)
            {
                requiredIssues.Add(new ProgramSchemaIssue(idForLog, "StartTime", "missing_or_invalid"));
            }

            if (p.EndTime == default)
            {
                requiredIssues.Add(new ProgramSchemaIssue(idForLog, "EndTime", "missing_or_invalid"));
            }

            if (p.StartTime != default && p.EndTime != default && p.EndTime <= p.StartTime)
            {
                requiredIssues.Add(new ProgramSchemaIssue(idForLog, "Duration", "end_before_or_equal_start"));
            }

            CountOptionalIfMissing(optionalMissing, "Performer", p.Performer);
            CountOptionalIfMissing(optionalMissing, "Description", p.Description);
            CountOptionalIfMissing(optionalMissing, "ProgramUrl", p.ProgramUrl);
            CountOptionalIfMissing(optionalMissing, "ImageUrl", p.ImageUrl);
        }

        return new ProgramSchemaValidationResult(requiredIssues, optionalMissing);
    }

    internal static ProgramSchemaValidationResult ValidateRadiruPrograms(
        IReadOnlyList<RadiruProgramJsonEntity> programs)
    {
        var requiredIssues = new List<ProgramSchemaIssue>();
        var optionalMissing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seenProgramIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < programs.Count; i++)
        {
            var p = programs[i];
            var idForLog = string.IsNullOrWhiteSpace(p.Id) ? $"index:{i}" : p.Id;

            if (string.IsNullOrWhiteSpace(p.Id))
            {
                requiredIssues.Add(new ProgramSchemaIssue(idForLog, "Id", "missing"));
            }
            else if (!seenProgramIds.Add(p.Id))
            {
                requiredIssues.Add(new ProgramSchemaIssue(idForLog, "Id", "duplicate"));
            }

            if (string.IsNullOrWhiteSpace(p.Name))
            {
                requiredIssues.Add(new ProgramSchemaIssue(idForLog, "Name", "missing"));
            }

            if (p.StartDate == default)
            {
                requiredIssues.Add(new ProgramSchemaIssue(idForLog, "StartDate", "missing_or_invalid"));
            }

            if (p.EndDate == default)
            {
                requiredIssues.Add(new ProgramSchemaIssue(idForLog, "EndDate", "missing_or_invalid"));
            }

            if (p.StartDate != default && p.EndDate != default && p.EndDate <= p.StartDate)
            {
                requiredIssues.Add(new ProgramSchemaIssue(idForLog, "Duration", "end_before_or_equal_start"));
            }

            CountOptionalIfMissing(optionalMissing, "Description", p.Description);
            CountOptionalIfMissing(optionalMissing, "Url", p.Url);
            CountOptionalIfMissing(optionalMissing, "IdentifierGroup.ServiceId", p.IdentifierGroup.ServiceId);
            CountOptionalIfMissing(optionalMissing, "IdentifierGroup.AreaId", p.IdentifierGroup.AreaId);
            CountOptionalIfMissing(optionalMissing, "IdentifierGroup.RadioEpisodeName", p.IdentifierGroup.RadioEpisodeName);
            CountOptionalIfMissing(optionalMissing, "About.Url", p.About.Url);
            CountOptionalIfMissing(optionalMissing, "About.PartOfSeries.Logo.Medium.Url", p.About.PartOfSeries.Logo.Medium.Url);
        }

        return new ProgramSchemaValidationResult(requiredIssues, optionalMissing);
    }

    internal static void AppendValidationLog(StringBuilder log, ProgramSchemaValidationResult validation, int totalCount)
    {
        log.AppendLine($"required_issue_count={validation.RequiredIssues.Count}");

        foreach (var issue in validation.RequiredIssues.Take(20))
        {
            log.AppendLine($"required_issue program={issue.ProgramId} field={issue.Field} reason={issue.Reason}");
        }

        foreach (var missing in validation.OptionalMissingCounts.OrderBy(x => x.Key))
        {
            var ratio = totalCount == 0 ? 0 : (double)missing.Value / totalCount;
            log.AppendLine($"optional_missing field={missing.Key} count={missing.Value} ratio={ratio:F3}");
        }
    }

    internal static void CountOptionalIfMissing(Dictionary<string, int> counts, string fieldName, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        counts.TryGetValue(fieldName, out var current);
        counts[fieldName] = current + 1;
    }
}
