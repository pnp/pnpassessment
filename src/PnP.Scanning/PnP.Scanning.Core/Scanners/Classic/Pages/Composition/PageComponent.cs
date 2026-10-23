namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
    /// A constituent of a classic page that can be observed independently of every other one.
    /// </summary>
    /// <remarks>
    /// A SharePoint page is a composition, not a member of a mutually exclusive set of types: a web
    /// part page can carry a wiki field and a field control in its aspx at the same time. Each value
    /// here names one thing that is separately true or false of a page, so observing one never
    /// requires denying another.
    /// <para>
    /// Values are added as the analyzer that observes them is written. A speculative value would be
    /// a component nothing can ever report, which reads as an unexplained gap.
    /// </para>
    /// </remarks>
    internal enum PageComponent
    {
        /// <summary>
        /// The on-disk template the file was provisioned from, recorded by the server in
        /// <c>vti_setuppath</c> and surviving customization.
        /// </summary>
        ProvisioningTemplate,
    }
}
