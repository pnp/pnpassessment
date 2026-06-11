using System.Xml.Serialization;

namespace PnP.Scanning.Core.Scanners.WebPartMapping
{
    /// <summary>
    /// Loads the embedded <c>webpartmapping.xml</c> and answers whether a given classic web part
    /// type is "mappable" — i.e. has a known modern equivalent. This repo only <em>assesses</em>
    /// pages; it does not transform them, so only the mapping lookup is carried over from the legacy
    /// Modernization Scanner — the actual transform/function engine is intentionally left behind. We
    /// only need to decide <em>mappable vs unmapped</em>.
    /// </summary>
    public class WebPartMappingManager
    {
        // Fully qualified name of the embedded webpartmapping.xml resource. Folder separators map to
        // dots off the assembly's default namespace (PnP.Scanning.Core), mirroring how the Workflow
        // scanner references its embedded sp2013wfmodel.xml.
        private const string EmbeddedMappingResource = "PnP.Scanning.Core.Scanners.Classic.WebPartMapping.webpartmapping.xml";

        // Web parts whose community mappings are dropped when loading the embedded model: we are not
        // sure the community mapping will be used, matching the legacy behaviour where these were
        // commented out in the standard mapping file. (Providing a webpartmapping.xml on disk in the
        // old scanner re-enabled them; that on-disk override is not ported.)
        private const string ScriptEditorType = "Microsoft.SharePoint.WebPartPages.ScriptEditorWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string SimpleFormType = "Microsoft.SharePoint.WebPartPages.SimpleFormWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";

        // Case-insensitive index of the model's web parts by their (assembly-qualified) Type, first
        // occurrence winning to match the legacy FirstOrDefault lookup semantics.
        private readonly Dictionary<string, WebPart> webPartsByType;

        /// <summary>
        /// Creates a manager backed by the embedded <c>webpartmapping.xml</c>.
        /// </summary>
        public WebPartMappingManager()
        {
            Model = LoadMappingModel();

            webPartsByType = new Dictionary<string, WebPart>(StringComparer.InvariantCultureIgnoreCase);
            if (Model?.WebParts != null)
            {
                foreach (var webPart in Model.WebParts)
                {
                    if (!string.IsNullOrEmpty(webPart.Type) && !webPartsByType.ContainsKey(webPart.Type))
                    {
                        webPartsByType.Add(webPart.Type, webPart);
                    }
                }
            }
        }

        /// <summary>
        /// The deserialized <c>webpartmapping.xml</c> model.
        /// </summary>
        public WebPartMappingModel Model { get; }

        /// <summary>
        /// Is the given web part type known to the mapping file <em>and</em> backed by a modern
        /// mapping? This is the parity definition of "mappable" used by the legacy scanner when it
        /// computed a page's mapping percentage.
        /// </summary>
        /// <param name="webPartType">The assembly-qualified web part type.</param>
        public bool IsMappable(string webPartType)
        {
            if (string.IsNullOrEmpty(webPartType))
            {
                return false;
            }

            return webPartsByType.TryGetValue(webPartType, out var webPart) && webPart.Mappings != null;
        }

        /// <summary>
        /// Is the given web part type present in the mapping file at all (regardless of whether it
        /// has a modern mapping)? Mirrors the legacy <c>UniqueWebParts.csv</c> "InMappingFile" column.
        /// </summary>
        /// <param name="webPartType">The assembly-qualified web part type.</param>
        public bool InMappingFile(string webPartType)
        {
            if (string.IsNullOrEmpty(webPartType))
            {
                return false;
            }

            return webPartsByType.ContainsKey(webPartType);
        }

        /// <summary>
        /// Loads the web part mapping model from the embedded <c>webpartmapping.xml</c> resource.
        /// </summary>
        private static WebPartMappingModel LoadMappingModel()
        {
            XmlSerializer xmlMapping = new(typeof(WebPartMappingModel));

            using Stream stream = typeof(WebPartMappingManager).Assembly.GetManifestResourceStream(EmbeddedMappingResource);
            if (stream == null)
            {
                throw new InvalidOperationException($"Embedded web part mapping resource '{EmbeddedMappingResource}' was not found.");
            }

            var model = (WebPartMappingModel)xmlMapping.Deserialize(stream);

            // Drop web parts that have community mappings as we're not sure if that mapping will be
            // used. This aligns with the older model where the community mapping was commented out in
            // the standard mapping file.
            if (model?.WebParts != null)
            {
                foreach (var webPart in model.WebParts.Where(
                    p => p.Type != null &&
                         (p.Type.Equals(ScriptEditorType, StringComparison.InvariantCultureIgnoreCase) ||
                          p.Type.Equals(SimpleFormType, StringComparison.InvariantCultureIgnoreCase))))
                {
                    webPart.Mappings = null;
                }
            }

            return model;
        }
    }
}
