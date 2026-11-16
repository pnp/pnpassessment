using PnP.Scanning.Process.Commands;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

/// <summary>
/// Verifies the wording of stage, progress and issues in the list and status tables.
/// </summary>
public sealed class AssessmentProgressTextTests
{
    /// <summary>
    /// Verifies that counted progress shows done, total, unit and a rounded-down percentage, and that an unknown total shows the count alone.
    /// </summary>
    [Theory]
    [InlineData(3, 12, "webs", "3 / 12 webs (25%)")]
    [InlineData(0, 12, "webs", "0 / 12 webs (0%)")]
    [InlineData(12, 12, "webs", "12 / 12 webs (100%)")]
    [InlineData(2, 3, "items", "2 / 3 items (66%)")]
    [InlineData(7, 0, "site collections", "7 site collections")]
    [InlineData(0, 0, "webs", "-")]
    [InlineData(0, 0, "", "-")]
    public void Progress_shows_count_total_unit_and_percentage(int completed, int total, string unit, string expected) =>
        Assert.Equal(expected, AssessmentProgressText.Progress(completed, total, unit));

    /// <summary>
    /// Verifies that a reported stage is shown as is and that a status-only assessment gets a stage from its status.
    /// </summary>
    [Theory]
    [InlineData("Reading sites", "Running", "Reading sites")]
    [InlineData("Completed", "Finished", "Completed")]
    [InlineData("", "Finished", "Completed")]
    [InlineData("", "Running", "Assessing sites")]
    [InlineData("", "Paused", "-")]
    public void Stage_shows_the_reported_stage_or_names_it_from_the_status(string stage, string status, string expected) =>
        Assert.Equal(expected, AssessmentProgressText.Stage(stage, status));

    /// <summary>
    /// Verifies that issues name incomplete reads and unassessed items, and add the stop reason only for a terminated assessment.
    /// </summary>
    [Theory]
    [InlineData("Running", 0, 0, "", "-")]
    [InlineData("Running", 1, 0, "", "1 read incomplete")]
    [InlineData("Finished", 3, 1, "Failed: last record", "3 reads incomplete\n1 item not assessed")]
    [InlineData("Finished", 0, 2, "", "2 items not assessed")]
    [InlineData("Terminated", 0, 0, "Execution was canceled by stop.", "Execution was canceled by stop.")]
    [InlineData("Terminated", 2, 0, "System.InvalidOperationException: boom\n   at Caller()", "2 reads incomplete\nSystem.InvalidOperationException: boom")]
    public void Issues_name_what_needs_attention(
        string status,
        int readIssues,
        int analysisIssues,
        string errorSummary,
        string expected) =>
        Assert.Equal(expected, AssessmentProgressText.Issues(status, readIssues, analysisIssues, errorSummary));

    /// <summary>
    /// Verifies that a long stop reason is cut to one hundred characters so the table stays readable.
    /// </summary>
    [Fact]
    public void Issues_shorten_a_long_stop_reason()
    {
        string text = AssessmentProgressText.Issues("Terminated", 0, 0, new string('x', 250));

        Assert.Equal(new string('x', 100) + "...", text);
    }
}