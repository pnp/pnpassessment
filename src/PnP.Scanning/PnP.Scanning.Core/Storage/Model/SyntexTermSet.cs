using Microsoft.EntityFrameworkCore;

namespace PnP.Scanning.Core.Storage
{
    [Index(new string[] { nameof(ScanId), nameof(TermSetId) }, IsUnique = true)]
    internal sealed class SyntexTermSet
    {
        public Guid ScanId { get; set; }

        public Guid TermSetId { get; set; }

        public string Title { get; set; }

        public string Description { get; set; }
    }
}
