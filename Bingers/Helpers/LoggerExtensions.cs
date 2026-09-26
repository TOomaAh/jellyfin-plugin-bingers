using Microsoft.Extensions.Logging;

namespace Bingers.Helpers;

/// <summary>
/// Logging helpers for the per-user "Enable debug logging" option.
/// </summary>
internal static class LoggerExtensions
{
    /// <summary>
    /// Logs a detailed message: at <see cref="LogLevel.Information"/> when the user enabled debug logging, so it shows
    /// up without changing the server log level, and at <see cref="LogLevel.Debug"/> otherwise.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="verbose">Whether debug logging is enabled for the user.</param>
    /// <param name="message">The message template.</param>
    /// <param name="args">The message arguments.</param>
    public static void LogVerbose(this ILogger logger, bool verbose, string message, params object[] args)
    {
#pragma warning disable CA2254 // Template should be a static expression: callers pass constant templates.
#pragma warning disable CA1848 // Use the LoggerMessage delegates
        logger.Log(verbose ? LogLevel.Information : LogLevel.Debug, message, args);
#pragma warning restore CA1848
#pragma warning restore CA2254
    }
}
