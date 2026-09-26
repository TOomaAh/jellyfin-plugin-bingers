using System;
using System.Net.Http;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Bingers.Helpers;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Bingers.Api;

/// <summary>
/// The bingers.app controller class.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("[controller]")]
[Produces(MediaTypeNames.Application.Json)]
public class BingersController : ControllerBase
{
    private readonly BingersApi _bingersApi;
    private readonly ILogger<BingersController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BingersController"/> class.
    /// </summary>
    /// <param name="bingersApi">The <see cref="BingersApi"/>.</param>
    /// <param name="logger">The <see cref="ILogger{BingersController}"/>.</param>
    public BingersController(BingersApi bingersApi, ILogger<BingersController> logger)
    {
        _bingersApi = bingersApi;
        _logger = logger;
    }

    /// <summary>
    /// Links a Jellyfin user to a bingers.app account with the magic link received by email.
    /// </summary>
    /// <param name="userGuid">The GUID of the Jellyfin user.</param>
    /// <param name="request">The magic link.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <response code="200">Account linked.</response>
    /// <response code="400">Invalid or expired magic link.</response>
    /// <returns>The link status.</returns>
    [HttpPost("Users/{userGuid}/Link")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BingersLinkStatus>> Link([FromRoute] Guid userGuid, [FromBody] BingersLinkRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var bingersUser = UserHelper.GetBingersUser(userGuid);
        if (bingersUser == null)
        {
            Plugin.Instance.PluginConfiguration.AddUser(userGuid);
            Plugin.Instance.SaveConfiguration();
            bingersUser = UserHelper.GetBingersUser(userGuid);
        }

        try
        {
            await _bingersApi.LinkAsync(bingersUser, request.MagicLink, cancellationToken).ConfigureAwait(false);
        }
        catch (BingersApiException ex)
        {
            _logger.LogWarning("Failed to link Bingers account for user {UserId}: {Message}", userGuid, ex.Message);
            var status = (int)ex.StatusCode is >= 400 and < 600 ? (int)ex.StatusCode : StatusCodes.Status400BadRequest;
            return StatusCode(status, new { error = ex.Message, code = ex.Code });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to reach bingers.app");
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Failed to reach bingers.app" });
        }

        return GetStatus(userGuid);
    }

    /// <summary>
    /// Unlinks the bingers.app account of a Jellyfin user.
    /// </summary>
    /// <param name="userGuid">The GUID of the Jellyfin user.</param>
    /// <response code="200">Account unlinked.</response>
    /// <returns>The link status.</returns>
    [HttpPost("Users/{userGuid}/Unlink")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<BingersLinkStatus> Unlink([FromRoute] Guid userGuid)
    {
        var bingersUser = UserHelper.GetBingersUser(userGuid);
        if (bingersUser != null)
        {
            BingersApi.Unlink(bingersUser);
        }

        return GetStatus(userGuid);
    }

    /// <summary>
    /// Validates the stored bingers.app session of a Jellyfin user.
    /// </summary>
    /// <param name="userGuid">The GUID of the Jellyfin user.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <response code="200">Status returned.</response>
    /// <returns>The link status.</returns>
    [HttpGet("Users/{userGuid}/Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<BingersLinkStatus>> Status([FromRoute] Guid userGuid, CancellationToken cancellationToken)
    {
        var bingersUser = UserHelper.GetBingersUser(userGuid);
        if (bingersUser != null)
        {
            await _bingersApi.ValidateSessionAsync(bingersUser, cancellationToken).ConfigureAwait(false);
        }

        return GetStatus(userGuid);
    }

    private static BingersLinkStatus GetStatus(Guid userGuid)
    {
        var bingersUser = UserHelper.GetBingersUser(userGuid);
        return new BingersLinkStatus
        {
            IsLinked = bingersUser?.IsLinked() == true,
            NeedsReauthorization = bingersUser?.NeedsReauthorization == true,
            Username = bingersUser?.Username,
            Email = bingersUser?.Email
        };
    }
}
