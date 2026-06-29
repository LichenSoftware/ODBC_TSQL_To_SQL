using System.Text;
using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems;

/// <summary>
/// Writes work items to a human-readable Markdown file organized by priority groups.
/// </summary>
public sealed class WorkItemMarkdownWriter : IWorkItemMarkdownWriter
{
    private static readonly string[] PriorityOrder = ["Critical", "High", "Medium", "Low"];

    /// <inheritdoc />
    public async Task<WriteResult> WriteAsync(
        WorkItemResult result,
        string outputPath,
        CancellationToken ct)
    {
        try
        {
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var markdown = GenerateMarkdown(result);
            await File.WriteAllTextAsync(outputPath, markdown, Encoding.UTF8, ct);

            return new WriteResult { Succeeded = true };
        }
        catch (Exception ex)
        {
            return new WriteResult
            {
                Succeeded = false,
                ErrorMessage = $"Failed to write Markdown file '{outputPath}': {ex.Message}"
            };
        }
    }

    internal string GenerateMarkdown(WorkItemResult result)
    {
        var sb = new StringBuilder();

        AppendSummarySection(sb, result);
        AppendRiskDistribution(sb, result.WorkItems);
        AppendTableOfContents(sb, result.WorkItems);
        AppendWorkItemsByPriority(sb, result.WorkItems);

        return sb.ToString();
    }

    private static void AppendSummarySection(StringBuilder sb, WorkItemResult result)
    {
        sb.AppendLine("# Migration Work Items Report");
        sb.AppendLine();
        sb.AppendLine($"**Generated:** {result.Metadata.GeneratedAt:O}");
        sb.AppendLine($"**Source:** {result.Metadata.SourceAssessmentPath ?? "in-memory"}");
        sb.AppendLine($"**Total Work Items:** {result.Metadata.TotalWorkItemCount}");
        sb.AppendLine($"**Estimated Effort:** {FormatEffort(result.Metadata.TotalEstimatedEffort)}");
        sb.AppendLine();
    }

    private static void AppendRiskDistribution(StringBuilder sb, IReadOnlyList<WorkItem> workItems)
    {
        sb.AppendLine("## Risk Distribution");
        sb.AppendLine();
        sb.AppendLine("| Priority | Count |");
        sb.AppendLine("|----------|-------|");

        foreach (var priority in PriorityOrder)
        {
            var count = workItems.Count(wi => wi.Priority == priority);
            if (count > 0)
            {
                sb.AppendLine($"| {priority} | {count} |");
            }
        }

        sb.AppendLine();
    }

    private static void AppendTableOfContents(StringBuilder sb, IReadOnlyList<WorkItem> workItems)
    {
        var presentPriorities = PriorityOrder
            .Where(p => workItems.Any(wi => wi.Priority == p))
            .ToList();

        if (presentPriorities.Count == 0)
            return;

        sb.AppendLine("## Table of Contents");
        sb.AppendLine();

        foreach (var priority in presentPriorities)
        {
            var anchor = $"{priority.ToLowerInvariant()}-priority";
            sb.AppendLine($"- [{priority} Priority](#{anchor})");
        }

        sb.AppendLine();
    }

    private static void AppendWorkItemsByPriority(StringBuilder sb, IReadOnlyList<WorkItem> workItems)
    {
        foreach (var priority in PriorityOrder)
        {
            var group = workItems
                .Where(wi => wi.Priority == priority)
                .OrderByDescending(wi => wi.PriorityScore)
                .ToList();

            if (group.Count == 0)
                continue;

            sb.AppendLine($"## {priority} Priority");
            sb.AppendLine();

            foreach (var item in group)
            {
                AppendWorkItem(sb, item);
            }
        }
    }

    private static void AppendWorkItem(StringBuilder sb, WorkItem item)
    {
        // Title heading
        sb.AppendLine($"### {item.Id}: {item.Title}");
        sb.AppendLine();

        // Description
        sb.AppendLine($"**Description:** {item.Description}");
        sb.AppendLine();

        // SQL Server Pattern
        sb.AppendLine("**SQL Server Pattern:**");
        sb.AppendLine("```sql");
        sb.AppendLine(item.SqlServerPattern);
        sb.AppendLine("```");
        sb.AppendLine();

        // PostgreSQL Equivalent
        sb.AppendLine("**PostgreSQL Equivalent:**");
        sb.AppendLine("```sql");
        sb.AppendLine(item.PostgresEquivalent);
        sb.AppendLine("```");
        sb.AppendLine();

        // Affected Objects
        sb.AppendLine("**Affected Objects:**");
        foreach (var obj in item.AffectedObjects)
        {
            sb.AppendLine($"- {obj.Name} ({obj.Type}) — {obj.StatementCount} {(obj.StatementCount == 1 ? "statement" : "statements")}");
        }
        sb.AppendLine();

        // Acceptance Criteria
        sb.AppendLine("**Acceptance Criteria:**");
        for (var i = 0; i < item.AcceptanceCriteria.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {item.AcceptanceCriteria[i]}");
        }
        sb.AppendLine();
    }

    private static string FormatEffort(HourRange effort)
    {
        var minFormatted = FormatHours(effort.MinHours);
        var maxFormatted = FormatHours(effort.MaxHours);
        return $"{minFormatted}-{maxFormatted} hours";
    }

    private static string FormatHours(double hours)
    {
        if (hours == Math.Floor(hours))
            return ((int)hours).ToString();

        return hours.ToString("0.##");
    }
}
