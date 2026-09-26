namespace Bingers.Api;

/// <summary>
/// Body of the link request.
/// </summary>
public class BingersLinkRequest
{
    /// <summary>
    /// Gets or sets the magic link received by email (or its token).
    /// </summary>
    public string MagicLink { get; set; }
}
