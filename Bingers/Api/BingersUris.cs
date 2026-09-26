namespace Bingers.Api;

/// <summary>
/// The bingers.app URI class.
/// </summary>
public static class BingersUris
{
    /// <summary>
    /// The web site base URL, also sent as request origin.
    /// </summary>
    public const string WebBaseUrl = "https://bingers.app";

    /// <summary>
    /// The page where users request a magic sign-in link by email.
    /// </summary>
    public const string MobileSignIn = WebBaseUrl + "/mobile-signin";

    /// <summary>
    /// The API base URL.
    /// </summary>
    public const string ApiBaseUrl = "https://api.bingers.app";

    /// <summary>
    /// The catalog base URL.
    /// </summary>
    public const string CatalogBaseUrl = "https://catalog.bingers.app";

    /// <summary>
    /// The magic link verification URI.
    /// </summary>
    public const string VerifyMagicLink = ApiBaseUrl + "/auth/magic-link/verify";

    /// <summary>
    /// The session URI.
    /// </summary>
    public const string GetSession = ApiBaseUrl + "/auth/get-session";

    /// <summary>
    /// The profile URI.
    /// </summary>
    public const string Me = ApiBaseUrl + "/me";

    /// <summary>
    /// The title search URI.
    /// </summary>
    public const string SearchTitles = ApiBaseUrl + "/search/titles";

    /// <summary>
    /// The sync pull URI.
    /// </summary>
    public const string SyncPull = ApiBaseUrl + "/sync/pull";

    /// <summary>
    /// The sync push URI.
    /// </summary>
    public const string SyncPush = ApiBaseUrl + "/sync/push";
}
