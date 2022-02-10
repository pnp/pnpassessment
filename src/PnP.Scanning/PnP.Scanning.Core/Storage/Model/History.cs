using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

#nullable disable

namespace PnP.Scanning.Core.Storage
{
    [Index(new string[] { nameof(ScanId), nameof(Event), nameof(EventDate) }, IsUnique = true)]
    internal sealed class History
    {
        public Guid ScanId { get; set; }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        public string Event { get; set; }

        public DateTime EventDate { get; set; }

        public string Message { get; set; }
    }
}
