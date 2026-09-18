namespace PnP.Scanning.Core.Services
{
    // A restart queue is only the unprocessed subset, not a new observation of
    // the site collection's complete Web authority.
    internal sealed record WebEnumerationResult(List<EnumeratedWeb> Webs, bool IsCheckpointReplay = false);

    internal sealed class EnumeratedWeb
    {
        internal string WebUrl { get; set; }

        internal string WebTemplate { get; set; }

    }
}
