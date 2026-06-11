﻿using System.Text.Json;

namespace PnP.Scanning.Core.Scanners.WebPartMapping
{
    /// <summary>
    /// Entity to describe a web part on a wiki or webpart page.
    /// Ported from the SharePoint Modernization Scanner
    /// (SharePointPnP.Modernization.Framework\Entities\WebPartEntity.cs). The transform/function
    /// engine is intentionally left behind; this is the plain inventory record consumed by the
    /// mapping lookup (<see cref="WebPartMappingManager"/>) and persisted to ClassicPageWebPart.
    /// </summary>
    public class WebPartEntity
    {
        /// <summary>
        /// Default constructor
        /// </summary>
        public WebPartEntity()
        {
            this.Properties = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
        }

        /// <summary>
        /// Web part type
        /// </summary>
        public string Type { get; set; }

        /// <summary>
        /// Web part id
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Web part server control id
        /// </summary>
        public string ServerControlId { get; set; }

        /// <summary>
        /// Web part zone id
        /// </summary>
        public string ZoneId { get; set; }

        /// <summary>
        /// Is this a visible web part or not
        /// </summary>
        public bool Hidden { get; set; }

        /// <summary>
        /// Is this web part closed or not
        /// </summary>
        public bool IsClosed { get; set; }

        /// <summary>
        /// Title of the web part
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Web part position: row
        /// </summary>
        public int Row { get; set; }

        /// <summary>
        /// Web part position: column
        /// </summary>
        public int Column { get; set; }

        /// <summary>
        ///  Web part position: order
        /// </summary>
        public int Order { get; set; }

        /// <summary>
        /// Web part position: zone index
        /// </summary>
        public uint ZoneIndex { get; set; }

        /// <summary>
        /// Dictionary with web part properties
        /// </summary>
        public Dictionary<string, string> Properties { get; set; }

        /// <summary>
        /// Returns the shortened web part type name (the class name, dropping the assembly qualifier).
        /// </summary>
        /// <returns>Shortened web part type name</returns>
        public string TypeShort()
        {
            return GetTypeShort(Type);
        }

        /// <summary>
        /// Returns this instance as Json
        /// </summary>
        /// <returns>Json serialized string of this web part instance</returns>
        public string Json()
        {
            return JsonSerializer.Serialize(this);
        }

        /// <summary>
        /// Returns the part of a type string before the first comma (the type name without the
        /// assembly qualification). Ported from the legacy StringExtensions.GetTypeShort.
        /// </summary>
        internal static string GetTypeShort(string typeValue)
        {
            if (string.IsNullOrEmpty(typeValue))
            {
                return typeValue;
            }

            string name = typeValue;
            var typeSplit = typeValue.Split(new string[] { "," }, StringSplitOptions.RemoveEmptyEntries);
            if (typeSplit.Length > 0)
            {
                name = typeSplit[0];
            }

            return $"{name}";
        }
    }
}
