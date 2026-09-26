using System;
using System.Collections.Generic;
using Bingers.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Bingers;

/// <summary>
/// Plugin class for the bingers.app syncing.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Bingers";

    /// <inheritdoc />
    public override Guid Id => new Guid("ac5439e6-35f8-4c3c-b2ef-b69950d1c991");

    /// <inheritdoc />
    public override string Description => "Sync your watched movies and episodes to bingers.app.";

    /// <summary>
    /// Gets the instance of the bingers.app plugin.
    /// </summary>
    public static Plugin Instance { get; private set; }

    /// <summary>
    /// Gets the plugin configuration.
    /// </summary>
    public PluginConfiguration PluginConfiguration => Configuration;

    /// <summary>
    /// Return the plugin configuration page.
    /// </summary>
    /// <returns>PluginPageInfo.</returns>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "bingers",
                EmbeddedResourcePath = GetType().Namespace + ".Web.bingers.html",
            },
            new PluginPageInfo
            {
                Name = "bingersjs",
                EmbeddedResourcePath = GetType().Namespace + ".Web.bingers.js"
            }
        };
    }
}
