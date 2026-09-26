<h1 align="center">Bingers for Jellyfin Plugin</h1>

## About

Bingers for Jellyfin marks the movies and episodes you watch in Jellyfin as watched on [bingers.app](https://bingers.app).
It is adapted from the [Trakt plugin](https://github.com/jellyfin/jellyfin-plugin-trakt) and ports the Bingers
integration of Scroblarr.

Features, per Jellyfin user:

- **Scrobbling**: an item played to the end is marked watched on bingers.app (optionally counting rewatches).
- **Instant sync**: manually marking an item as played in Jellyfin marks it watched on bingers.app.
- **Export task**: the *Export watched history to bingers.app* scheduled task pushes everything already watched in Jellyfin.
- **Import task**: the *Import watched history from bingers.app* scheduled task marks as played in Jellyfin what is watched
  on bingers.app (and raises the play count to the Bingers value). It never marks items unplayed.
- Library folders can be excluded.

Bingers has no public "unwatch", collection or rating API, so those Trakt features are not available.

## Installation

1. In Jellyfin, open *Dashboard > Plugins > Repositories* (or *Catalog > ⚙*) and add a repository with the URL
   `https://raw.githubusercontent.com/TOomaAh/jellyfin-plugin-bingers/manifest/manifest.json`.
2. Install **Bingers** from the catalog and restart Jellyfin.

The zip of each version is also attached to the [GitHub releases](https://github.com/TOomaAh/jellyfin-plugin-bingers/releases).

## Releasing

1. Merged pull requests are collected in a draft release by the *Create/Update Release Draft* workflow
   (label them `feature`, `bug`, `dependencies`... to group them).
2. Publish the draft (tag `v1`, `v2`, ...). The *Publish Plugin* workflow builds the plugin with
   [JPRM](https://github.com/oddstr13/jellyfin-plugin-repository-manager), attaches the zip to the release and adds the
   version to `manifest.json` on the `manifest` branch. The release notes become the changelog shown in Jellyfin.

## Linking an account

1. In *Dashboard > Plugins > Bingers*, select the Jellyfin user.
2. Open <https://bingers.app/mobile-signin> and request a sign-in link by email.
3. Copy the link from the email **without opening it** (it can only be used once), paste it in the plugin page and click *Link account*.

The plugin stores the bingers.app session cookies and refreshes them automatically. If the session is revoked, the
configuration page asks to link the account again.

Movies and shows are matched in the bingers.app catalog by title, then verified with their IMDb / TMDB / TVDB ids.
Make sure your library metadata includes those provider ids.

## Build

1. To build this plugin you will need [.Net 10.x](https://dotnet.microsoft.com/download/dotnet/10.0).

2. Build plugin with following command
  ```
  dotnet publish --configuration Release --output bin
  ```

3. Place the dll-file in the `plugins/bingers` folder (you might need to create the folders) of your JF install

## Licence

This plugins code and packages are distributed under the MIT License. See [LICENSE](./LICENSE.md) for more information.
