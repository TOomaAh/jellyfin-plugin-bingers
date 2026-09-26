using System;
using System.Threading;
using System.Threading.Tasks;
using Bingers.Api;
using Bingers.Helpers;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bingers;

/// <summary>
/// All communication between the server and the plugins server instance should occur in this class.
/// </summary>
public class ServerMediator : IHostedService, IDisposable
{
    private readonly ILogger<ServerMediator> _logger;
    private readonly ISessionManager _sessionManager;
    private readonly IUserDataManager _userDataManager;
    private readonly UserDataManagerEventsHelper _userDataManagerEventsHelper;
    private readonly BingersApi _bingersApi;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerMediator"/> class.
    /// </summary>
    /// <param name="sessionManager">The <see cref="ISessionManager"/>.</param>
    /// <param name="userDataManager">The <see cref="IUserDataManager"/>.</param>
    /// <param name="loggerFactory">The <see cref="ILoggerFactory"/>.</param>
    /// <param name="bingersApi">The <see cref="BingersApi"/>.</param>
    public ServerMediator(
        ISessionManager sessionManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        BingersApi bingersApi)
    {
        _sessionManager = sessionManager;
        _userDataManager = userDataManager;
        _bingersApi = bingersApi;

        _logger = loggerFactory.CreateLogger<ServerMediator>();
        _userDataManagerEventsHelper = new UserDataManagerEventsHelper(loggerFactory.CreateLogger<UserDataManagerEventsHelper>(), _bingersApi);
    }

    /// <summary>
    /// User data was saved.
    /// Let bingers.app know that the user manually marked an item as played.
    /// </summary>
    /// <param name="sender">The sending entity.</param>
    /// <param name="userDataSaveEventArgs">The <see cref="UserDataSaveEventArgs"/>.</param>
    private void OnUserDataSaved(object sender, UserDataSaveEventArgs userDataSaveEventArgs)
    {
        // Ignore change events for any reason other than manually toggling played.
        // Played to completion items are handled by the playback stopped event.
        if (userDataSaveEventArgs.SaveReason != UserDataSaveReason.TogglePlayed
            || userDataSaveEventArgs.Item == null
            || userDataSaveEventArgs.UserData?.Played != true)
        {
            return;
        }

        var bingersUser = UserHelper.GetBingersUser(userDataSaveEventArgs.UserId, true);
        if (bingersUser == null)
        {
            return;
        }

        var item = userDataSaveEventArgs.Item;
        if (!bingersUser.PostSetWatched)
        {
            _logger.LogVerbose(bingersUser.ExtraLogging, "{Item} was marked played manually; not sent to Bingers (option disabled)", item.Name);
            return;
        }

        var reason = UserHelper.GetSyncBlockReason(item, bingersUser);
        if (reason != null)
        {
            _logger.LogVerbose(bingersUser.ExtraLogging, "{Item} was marked played manually; not sent to Bingers: {Reason}", item.Name, reason);
            return;
        }

        _logger.LogVerbose(bingersUser.ExtraLogging, "{Item} was marked played manually; queued for Bingers", item.Name);
        _userDataManagerEventsHelper.QueueWatched(item, bingersUser);
    }

    /// <summary>
    /// Media playback has stopped.
    /// Depending on playback progress, let bingers.app know the user has completed watching the item.
    /// </summary>
    /// <param name="sender">The sending entity.</param>
    /// <param name="playbackStoppedEventArgs">The <see cref="PlaybackStopEventArgs"/>.</param>
    private async void KernelPlaybackStopped(object sender, PlaybackStopEventArgs playbackStoppedEventArgs)
    {
        if (playbackStoppedEventArgs.Users == null || playbackStoppedEventArgs.Users.Count == 0 || playbackStoppedEventArgs.Item == null)
        {
            _logger.LogError("Event details incomplete. Cannot process current media");
            return;
        }

        var item = playbackStoppedEventArgs.Item;
        if (item is not Movie && item is not Episode)
        {
            _logger.LogDebug("Syncing playback of {Item} is not supported by bingers.app.", item.Path);
            return;
        }

        if (!playbackStoppedEventArgs.PlayedToCompletion)
        {
            _logger.LogDebug(
                "Item {Item} was not played to completion ({Position} of {Runtime}). Not marking it as watched.",
                item.Name,
                TimeSpan.FromTicks(playbackStoppedEventArgs.PlaybackPositionTicks ?? 0),
                TimeSpan.FromTicks(item.RunTimeTicks ?? 0));
            return;
        }

        foreach (var user in playbackStoppedEventArgs.Users)
        {
            var bingersUser = UserHelper.GetBingersUser(user, true);

            if (bingersUser == null)
            {
                _logger.LogDebug("Could not match user {User} with any linked bingers.app account.", user.Username);
                continue;
            }

            var verbose = bingersUser.ExtraLogging;
            if (!bingersUser.Scrobble)
            {
                _logger.LogVerbose(verbose, "User {User} finished {Item} but disabled scrobbling to bingers.app.", user.Username, item.Name);
                continue;
            }

            var reason = UserHelper.GetSyncBlockReason(item, bingersUser);
            if (reason != null)
            {
                _logger.LogVerbose(verbose, "User {User} finished {Item}; not sent to bingers.app: {Reason}.", user.Username, item.Name, reason);
                continue;
            }

            try
            {
                // The play count already includes this play. Only pass it for rewatches, like scroblarr does.
                var playCount = _userDataManager.GetUserData(user, item)?.PlayCount ?? 0;
                var allowRewatch = item is Movie ? bingersUser.MarkMoviesAsRewatched : bingersUser.MarkEpisodesAsRewatched;

                _logger.LogVerbose(verbose, "User {User} completed watching item {Item} (Jellyfin play count {PlayCount}). Marking it as watched on bingers.app.", user.Username, item.Name, playCount);
                await _bingersApi.MarkWatchedAsync(
                    bingersUser,
                    item,
                    allowRewatch,
                    playCount > 1 ? playCount : null,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (BingersApiException ex) when (ex.IsAuthError)
            {
                _logger.LogWarning("Bingers session of user {User} is no longer valid; link the account again in the plugin settings.", user.Username);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception occurred while marking {Item} as watched on bingers.app for user {User}.", item.Name, user.Username);
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _sessionManager.PlaybackStopped += KernelPlaybackStopped;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _sessionManager.PlaybackStopped -= KernelPlaybackStopped;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Dispose.
    /// </summary>
    /// <param name="disposing">Whether to dispose.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _userDataManagerEventsHelper?.Dispose();
        }
    }
}
