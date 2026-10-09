using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Bingers.Api;
using Bingers.Helpers;
using MediaBrowser.Controller.Configuration;
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
    private readonly IServerConfigurationManager _configurationManager;

    // Last playback position reported for each user and item, used when a client stops without reporting it.
    private readonly ConcurrentDictionary<(Guid UserId, Guid ItemId), long> _lastPositions = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerMediator"/> class.
    /// </summary>
    /// <param name="sessionManager">The <see cref="ISessionManager"/>.</param>
    /// <param name="userDataManager">The <see cref="IUserDataManager"/>.</param>
    /// <param name="loggerFactory">The <see cref="ILoggerFactory"/>.</param>
    /// <param name="bingersApi">The <see cref="BingersApi"/>.</param>
    /// <param name="configurationManager">The <see cref="IServerConfigurationManager"/>.</param>
    public ServerMediator(
        ISessionManager sessionManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        BingersApi bingersApi,
        IServerConfigurationManager configurationManager)
    {
        _sessionManager = sessionManager;
        _userDataManager = userDataManager;
        _bingersApi = bingersApi;
        _configurationManager = configurationManager;

        _logger = loggerFactory.CreateLogger<ServerMediator>();
        _userDataManagerEventsHelper = new UserDataManagerEventsHelper(loggerFactory.CreateLogger<UserDataManagerEventsHelper>(), _bingersApi);
    }

    /// <summary>
    /// User data was saved.
    /// Let bingers.app know that the user manually marked an item as played or unplayed.
    /// </summary>
    /// <param name="sender">The sending entity.</param>
    /// <param name="userDataSaveEventArgs">The <see cref="UserDataSaveEventArgs"/>.</param>
    private void OnUserDataSaved(object sender, UserDataSaveEventArgs userDataSaveEventArgs)
    {
        // Ignore change events for any reason other than manually toggling played.
        // Played to completion items are handled by the playback stopped event.
        if (userDataSaveEventArgs.SaveReason != UserDataSaveReason.TogglePlayed
            || userDataSaveEventArgs.Item == null
            || userDataSaveEventArgs.UserData == null)
        {
            return;
        }

        var bingersUser = UserHelper.GetBingersUser(userDataSaveEventArgs.UserId, true);
        if (bingersUser == null)
        {
            return;
        }

        var item = userDataSaveEventArgs.Item;
        var played = userDataSaveEventArgs.UserData.Played;
        var state = played ? "played" : "unplayed";

        var reason = UserHelper.GetSyncBlockReason(item, bingersUser);
        if (reason != null)
        {
            _logger.LogVerbose(bingersUser.ExtraLogging, "{Item} was marked {State} manually; not sent to Bingers: {Reason}", item.Name, state, reason);
            return;
        }

        // Even when this direction is disabled, the change must cancel a pending opposite change of the same item
        // (e.g. marked played by mistake, then unplayed before it was sent).
        var send = played ? bingersUser.PostSetWatched : bingersUser.PostSetUnwatched;
        _logger.LogVerbose(
            bingersUser.ExtraLogging,
            send ? "{Item} was marked {State} manually; queued for Bingers" : "{Item} was marked {State} manually; not sent to Bingers (option disabled)",
            item.Name,
            state);
        _userDataManagerEventsHelper.QueuePlayedState(item, played, send, bingersUser);
    }

    /// <summary>
    /// Media playback has started or progressed: remember the position.
    /// </summary>
    /// <param name="sender">The sending entity.</param>
    /// <param name="args">The <see cref="PlaybackProgressEventArgs"/>.</param>
    private void KernelPlaybackProgress(object sender, PlaybackProgressEventArgs args)
    {
        if (args.Item is not Movie and not Episode || args.Users == null || !args.PlaybackPositionTicks.HasValue)
        {
            return;
        }

        foreach (var user in args.Users)
        {
            _lastPositions[(user.Id, args.Item.Id)] = args.PlaybackPositionTicks.Value;
        }
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
            foreach (var user in playbackStoppedEventArgs.Users)
            {
                _lastPositions.TryRemove((user.Id, item.Id), out _);
            }

            _logger.LogDebug(
                "Item {Item} was not played to completion ({Position} of {Runtime}). Not marking it as watched.",
                item.Name,
                TimeSpan.FromTicks(playbackStoppedEventArgs.PlaybackPositionTicks ?? 0),
                TimeSpan.FromTicks(item.RunTimeTicks ?? 0));
            return;
        }

        foreach (var user in playbackStoppedEventArgs.Users)
        {
            _lastPositions.TryRemove((user.Id, item.Id), out var lastPosition);
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

            // Jellyfin also reports "played to completion" when the client did not send its position, when the runtime
            // is unknown or for short items. Only trust the actual progress, against Jellyfin's own threshold.
            var position = playbackStoppedEventArgs.PlaybackPositionTicks is > 0
                ? playbackStoppedEventArgs.PlaybackPositionTicks.Value
                : lastPosition;
            var runtime = item.RunTimeTicks ?? 0;
            var threshold = _configurationManager.Configuration.MaxResumePct;
            if (position <= 0 || runtime <= 0)
            {
                _logger.LogInformation(
                    "User {User} stopped {Item}: Jellyfin considers it played but its progress is unknown (position {Position}, runtime {Runtime}); not marked as watched on bingers.app.",
                    user.Username,
                    item.Name,
                    TimeSpan.FromTicks(position),
                    TimeSpan.FromTicks(runtime));
                continue;
            }

            var percent = 100d * position / runtime;
            if (percent < threshold && position < runtime - TimeSpan.TicksPerSecond)
            {
                _logger.LogInformation(
                    "User {User} stopped {Item} at {Percent:0}% ({Position} of {Runtime}), below the {Threshold}% needed to count as watched; not marked as watched on bingers.app.",
                    user.Username,
                    item.Name,
                    percent,
                    TimeSpan.FromTicks(position),
                    TimeSpan.FromTicks(runtime),
                    threshold);
                continue;
            }

            // The event's PlayedToCompletion only reflects the last user Jellyfin processed: make sure Jellyfin
            // actually marked the item as played for this user.
            var userData = _userDataManager.GetUserData(user, item);
            if (userData?.Played != true)
            {
                _logger.LogVerbose(verbose, "User {User} stopped {Item} but Jellyfin did not mark it as played for this user; not marked as watched on bingers.app.", user.Username, item.Name);
                continue;
            }

            try
            {
                // The play count already includes this play. Only pass it for rewatches, like scroblarr does.
                var playCount = userData.PlayCount;
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
        _sessionManager.PlaybackStart += KernelPlaybackProgress;
        _sessionManager.PlaybackProgress += KernelPlaybackProgress;
        _sessionManager.PlaybackStopped += KernelPlaybackStopped;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _sessionManager.PlaybackStart -= KernelPlaybackProgress;
        _sessionManager.PlaybackProgress -= KernelPlaybackProgress;
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
