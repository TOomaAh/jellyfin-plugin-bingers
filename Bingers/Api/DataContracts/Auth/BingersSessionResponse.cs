using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bingers.Api.DataContracts.Auth;

/// <summary>
/// Response of the better-auth <c>/auth/get-session</c> endpoint.
/// </summary>
public class BingersSessionResponse
{
    /// <summary>
    /// Gets or sets the session.
    /// </summary>
    [JsonPropertyName("session")]
    public BingersSession Session { get; set; }

    /// <summary>
    /// Gets or sets the user.
    /// </summary>
    [JsonPropertyName("user")]
    public BingersAccount User { get; set; }
}

/// <summary>
/// A bingers.app session.
/// </summary>
#pragma warning disable SA1402 // File may only contain a single type
public class BingersSession
{
    /// <summary>
    /// Gets or sets the session id.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; }

    /// <summary>
    /// Gets or sets the session expiration (ISO date string or unix timestamp).
    /// </summary>
    [JsonPropertyName("expiresAt")]
    public JsonElement ExpiresAt { get; set; }
}

/// <summary>
/// A bingers.app account.
/// </summary>
public class BingersAccount
{
    /// <summary>
    /// Gets or sets the account id.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; }

    /// <summary>
    /// Gets or sets the email.
    /// </summary>
    [JsonPropertyName("email")]
    public string Email { get; set; }

    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; }

    /// <summary>
    /// Gets or sets the user name.
    /// </summary>
    [JsonPropertyName("username")]
    public string Username { get; set; }

    /// <summary>
    /// Gets or sets the avatar url.
    /// </summary>
    [JsonPropertyName("image")]
    public string Image { get; set; }
}
#pragma warning restore SA1402
