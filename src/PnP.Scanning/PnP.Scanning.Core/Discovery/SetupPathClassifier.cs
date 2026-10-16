namespace PnP.Scanning.Core.Discovery;

/// <summary>
/// Page family a ghosted ASPX file was provisioned from, derived from its <c>vti_setuppath</c> value.
/// </summary>
internal enum SetupPathPageFamily
{
    /// <summary>No setup path: the file is unghosted, so it is user authored or user uploaded.</summary>
    Unghosted,
    /// <summary>A list view. Every AllItems.aspx and custom view ghosts from a single viewpage.aspx.</summary>
    ListView,
    /// <summary>A list form. DispForm/EditForm/NewForm all ghost from a single form.aspx.</summary>
    ListForm,
    WikiPage,
    WebPartPage,
    BlogPost,
    /// <summary>A setup path that matches no known template: a custom site definition or third-party provisioning.</summary>
    Unknown,
}

/// <summary>
/// Classifies a file's <c>vti_setuppath</c> value into a page family. The setup path records which on-disk
/// template a ghosted file was provisioned from and survives customization, so it is the only client-visible
/// signal that separates a system list form or view from a user content page.
/// </summary>
/// <remarks>
/// The web part page templates match the set the server itself treats as convertible web part page layouts
/// (<c>ClassicPageLayout.SupportedWebPartPageLayouts</c>): the eight standard <c>spstd</c> layouts, the basic
/// <c>bpstd</c> layout, and the four site-definition home pages.
/// </remarks>
internal static class SetupPathClassifier
{
    // Suffix matching mirrors the server classifier: real values carry a provisioning prefix such as
    // 'DocumentTemplates\' or '1033\STS\doctemp\smartpgs\', so they never equal a bare file name.
    private static readonly (string Suffix, SetupPathPageFamily Family)[] KnownTemplates =
    {
        (@"pages\viewpage.aspx", SetupPathPageFamily.ListView),
        (@"pages\form.aspx", SetupPathPageFamily.ListForm),
        (@"wkpstd.aspx", SetupPathPageFamily.WikiPage),
        // The standard web part page templates, keyed by bare file name as the server keys them.
        (@"spstd1.aspx", SetupPathPageFamily.WebPartPage),
        (@"spstd2.aspx", SetupPathPageFamily.WebPartPage),
        (@"spstd3.aspx", SetupPathPageFamily.WebPartPage),
        (@"spstd4.aspx", SetupPathPageFamily.WebPartPage),
        (@"spstd5.aspx", SetupPathPageFamily.WebPartPage),
        (@"spstd6.aspx", SetupPathPageFamily.WebPartPage),
        (@"spstd7.aspx", SetupPathPageFamily.WebPartPage),
        (@"spstd8.aspx", SetupPathPageFamily.WebPartPage),
        (@"bpstd.aspx", SetupPathPageFamily.WebPartPage),
        // The site-definition home pages, keyed by their full template path as the server keys them.
        // A web's root default.aspx ghosts from one of these, so without them the entry page of every
        // team site, document workspace and project site falls through as an unrecognised template.
        (@"sitetemplates\sts\default.aspx", SetupPathPageFamily.WebPartPage),
        (@"sitetemplates\sts\defaultdws.aspx", SetupPathPageFamily.WebPartPage),
        (@"sitetemplates\projectsite\default.aspx", SetupPathPageFamily.WebPartPage),
        (@"features\bdr\default.aspx", SetupPathPageFamily.WebPartPage),
        (@"Features\BlogContent\Post.aspx", SetupPathPageFamily.BlogPost),
    };

    internal static SetupPathPageFamily Classify(string setupPath)
    {
        if (string.IsNullOrWhiteSpace(setupPath)) return SetupPathPageFamily.Unghosted;
        var trimmed = setupPath.Trim();
        foreach (var (suffix, family) in KnownTemplates)
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return family;
        return SetupPathPageFamily.Unknown;
    }
}
