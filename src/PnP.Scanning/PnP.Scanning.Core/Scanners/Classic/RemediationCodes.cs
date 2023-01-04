namespace PnP.Scanning.Core.Scanners
{
    internal enum RemediationCodes
    {
        /// <summary>
        /// WebPart page
        /// </summary>
        CP1,
        /// <summary>
        /// Wiki page
        /// </summary>
        CP2,
        /// <summary>
        /// Publishing page
        /// </summary>
        CP3,
        /// <summary>
        /// Blog page
        /// </summary>
        CP4,
        /// <summary>
        /// ASPX page
        /// </summary>
        CP5,

        /// <summary>
        /// Lists forced in classic via a setting 
        /// </summary>
        CL1,
        /// <summary>
        /// Lists forced in classic via incompatible list customization 
        /// </summary>
        CL2,
        /// <summary>
        /// Lists forced in classic via updated page hosting the list (e.g. added web part on the page) or because it's unghosted
        /// </summary>
        CL3,
        /// <summary>
        /// Lists for which there’s no modern alternative like Task and Calendar list 
        /// </summary>
        CL4,
        /// <summary>
        /// List uses incompatible field type
        /// </summary>
        CL5,
        /// <summary>
        /// List uses incompatible view (gantt / calendar) 
        /// </summary>
        CL6,

        /// <summary>
        /// Workflow 2013
        /// </summary>
        WF1,

        /// <summary>
        /// InfoPath 2013 List forms 
        /// </summary>
        IF1,
        /// <summary>
        /// InfoPath 2013 Form libraries 
        /// </summary>
        IF2,

        /// <summary>
        /// Blog site
        /// </summary>
        CS1,
        /// <summary>
        /// Publishing portal
        /// </summary>
        CS2,

        /// <summary>
        /// SharePoint hosted AddIn 
        /// </summary>
        CE1,
        /// <summary>
        /// Provider hosted AddIn
        /// </summary>
        CE2,
        /// <summary>
        /// Custom master pages used
        /// </summary>
        CE3,
        /// <summary>
        /// Custom CSS used
        /// </summary>
        CE4,
        /// <summary>
        /// Custom themes
        /// </summary>
        CE5,
        /// <summary>
        /// Ribbon extension for lists via User Custom Actions
        /// </summary>
        CE6,
        /// <summary>
        /// Not used for the moment
        /// </summary>
        CE7,
        /// <summary>
        /// Embedding script via User Custom Actions 
        /// </summary>
        CE8,
        /// <summary>
        /// Other ribbon/UI extensions via User Custom Actions
        /// </summary>
        CE9,

    }
}
