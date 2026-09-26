using System.Text.Json.Serialization;

namespace Bingers.Api.DataContracts.Auth;

/// <summary>
/// Response of the <c>/me</c> endpoint.
/// </summary>
public class BingersMeResponse
{
    /// <summary>
    /// Gets or sets the account.
    /// </summary>
    [JsonPropertyName("user")]
    public BingersAccount User { get; set; }

    /// <summary>
    /// Gets or sets the public profile.
    /// </summary>
    [JsonPropertyName("profile")]
    public BingersProfile Profile { get; set; }
}

/// <summary>
/// A bingers.app public profile.
/// </summary>
#pragma warning disable SA1402 // File may only contain a single type
public class BingersProfile
{
    /// <summary>
    /// Gets or sets the handle.
    /// </summary>
    [JsonPropertyName("handle")]
    public string Handle { get; set; }

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the avatar url.
    /// </summary>
    [JsonPropertyName("avatarUrl")]
    public string AvatarUrl { get; set; }
}
#pragma warning restore SA1402
