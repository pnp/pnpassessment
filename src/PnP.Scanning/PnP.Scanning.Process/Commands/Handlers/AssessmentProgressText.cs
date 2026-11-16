using System.Globalization;

namespace PnP.Scanning.Process.Commands
{
    /// <summary>
    /// Words the stage, progress and issues of an assessment for the list and status tables, in terms a person
    /// waiting for the assessment recognizes.
    /// </summary>
    internal static class AssessmentProgressText
    {
        private const int SummaryLength = 100;

        /// <summary>
        /// Returns the reported stage, or names the stage of an assessment that reports only its status.
        /// </summary>
        internal static string Stage(string stage, string status)
        {
            if (!string.IsNullOrWhiteSpace(stage))
            {
                return stage;
            }

            return status switch
            {
                "Finished" => "Completed",
                "Running" => "Assessing sites",
                _ => "-",
            };
        }

        /// <summary>
        /// Shows counted progress as "done / total unit (percent)", a count alone while the total is unknown,
        /// and a dash for a step that has no count.
        /// </summary>
        internal static string Progress(int completed, int total, string unit)
        {
            string suffix = string.IsNullOrWhiteSpace(unit) ? "" : " " + unit;
            if (total > 0)
            {
                int percent = (int)Math.Floor(Math.Min(completed, total) * 100d / total);
                return $"{Number(completed)} / {Number(total)}{suffix} ({percent}%)";
            }

            return completed > 0 ? $"{Number(completed)}{suffix}" : "-";
        }

        /// <summary>
        /// Lists what needs attention: reads that stayed incomplete, items that stayed unassessed and, for a
        /// terminated assessment, the first line of why it stopped. A dash means nothing needs attention.
        /// </summary>
        internal static string Issues(string status, int readIssues, int analysisIssues, string errorSummary)
        {
            List<string> lines = new();
            if (readIssues > 0)
            {
                lines.Add(readIssues == 1 ? "1 read incomplete" : $"{Number(readIssues)} reads incomplete");
            }

            if (analysisIssues > 0)
            {
                lines.Add(analysisIssues == 1 ? "1 item not assessed" : $"{Number(analysisIssues)} items not assessed");
            }

            if (status == "Terminated" && !string.IsNullOrWhiteSpace(errorSummary))
            {
                string reason = errorSummary.Split('\n')[0].Trim();
                lines.Add(reason.Length > SummaryLength ? reason.Substring(0, SummaryLength) + "..." : reason);
            }

            return lines.Count == 0 ? "-" : string.Join("\n", lines);
        }

        private static string Number(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
    }
}