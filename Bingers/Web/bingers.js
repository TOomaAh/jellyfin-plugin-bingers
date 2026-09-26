const BingersConfigurationPage = {
    pluginUniqueId: 'ac5439e6-35f8-4c3c-b2ef-b69950d1c991',
    defaultUserConfig: function () {
        // You don't have to put every property in here, just the ones the UI is expecting (below)
        return {
            Scrobble: true,
            PostSetWatched: true,
            PostSetUnwatched: true,
            PostWatchedHistory: true,
            SkipWatchedImportFromBingers: false,
            CleanupWatchedHistory: false,
            CleanupDryRun: true,
            MarkMoviesAsRewatched: false,
            MarkEpisodesAsRewatched: false,
            ExtraLogging: false
        };
    },
    loadConfiguration: function (userId, page) {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(BingersConfigurationPage.pluginUniqueId).then(function (config) {
            let currentUserConfig = config.BingersUsers.filter(function (curr) {
                return curr.LinkedMbUserId == userId;
            })[0];
            // User doesn't have a config, so create a default one.
            if (!currentUserConfig) {
                currentUserConfig = BingersConfigurationPage.defaultUserConfig();
            }
            // Default this to an empty array so the rendering code doesn't have to worry about it
            currentUserConfig.LocationsExcluded = currentUserConfig.LocationsExcluded || [];
            page.querySelector('#chkScrobble').checked = currentUserConfig.Scrobble;
            page.querySelector('#chkPostSetWatched').checked = currentUserConfig.PostSetWatched;
            page.querySelector('#chkPostSetUnwatched').checked = currentUserConfig.PostSetUnwatched;
            page.querySelector('#chkPostWatchedHistory').checked = currentUserConfig.PostWatchedHistory;
            page.querySelector('#chkSkipWatchedImportFromBingers').checked = currentUserConfig.SkipWatchedImportFromBingers;
            page.querySelector('#chkCleanupWatchedHistory').checked = currentUserConfig.CleanupWatchedHistory;
            page.querySelector('#chkCleanupDryRun').checked = currentUserConfig.CleanupDryRun;
            page.querySelector('#chkMarkMoviesAsRewatched').checked = currentUserConfig.MarkMoviesAsRewatched;
            page.querySelector('#chkMarkEpisodesAsRewatched').checked = currentUserConfig.MarkEpisodesAsRewatched;
            page.querySelector('#chkExtraLogging').checked = currentUserConfig.ExtraLogging;
            // List the folders the user can access
            ApiClient.getVirtualFolders(userId).then(function (result) {
                BingersConfigurationPage.loadFolders(currentUserConfig, result);
            });
            BingersConfigurationPage.loadStatus(userId, page);
        });
    },
    loadStatus: function (userId, page) {
        ApiClient.fetch({
            url: ApiClient.getUrl('Bingers/Users/' + userId + '/Status'),
            dataType: 'json',
            type: 'GET',
            headers: { accept: 'application/json' }
        }).then(function (status) {
            setLinkElements(page, status);
        }).catch(function () {
            setLinkElements(page, { IsLinked: false });
        }).finally(function () {
            Dashboard.hideLoadingMsg();
        });
    },
    populateUsers: function (users) {
        let html = '';
        for (let i = 0, length = users.length; i < length; i++) {
            const user = users[i];
            html += '<option value="' + user.Id + '">' + escapeHtml(user.Name) + '</option>';
        }
        document.querySelector('#selectUser').innerHTML = html;
    },
    loadFolders: function (currentUserConfig, virtualFolders) {
        let html = '';
        html += '<div data-role="controlgroup">';
        for (let i = 0, length = virtualFolders.length; i < length; i++) {
            const virtualFolder = virtualFolders[i];
            html += BingersConfigurationPage.getFolderHtml(currentUserConfig, virtualFolder, i);
        }
        html += '</div>';
        const divBingersLocations = document.querySelector('#divBingersLocations');
        divBingersLocations.innerHTML = html;
        divBingersLocations.dispatchEvent(new Event('create'));
    },
    getFolderHtml: function (currentUserConfig, virtualFolder, index) {
        let html = '';
        for (let i = 0, length = virtualFolder.Locations.length; i < length; i++) {
            const id = 'chkFolder' + index + '_' + i;
            const location = virtualFolder.Locations[i];
            const isChecked = currentUserConfig.LocationsExcluded.filter(function (current) {
                return current.toLowerCase() == location.toLowerCase();
            }).length;
            const checkedAttribute = isChecked ? 'checked="checked"' : '';
            html += '<label><input is="emby-checkbox" class="chkBingersLocation" type="checkbox" data-mini="true" id="' + id + '" name="' + id + '" data-location="' + escapeHtml(location) + '" ' + checkedAttribute + ' /><span>' + escapeHtml(location) + '</span></label>';
        }
        return html;
    }
};

function escapeHtml(value) {
    const div = document.createElement('div');
    div.textContent = value || '';
    return div.innerHTML.replace(/"/g, '&quot;');
}

function setLinkElements(page, status) {
    const account = status.Username || status.Email || 'unknown account';
    if (status.IsLinked) {
        page.querySelector('#linkedDescription').textContent = 'This user is linked to the bingers.app account "' + account + '".';
        page.querySelector('#linkedSection').classList.remove('hide');
        page.querySelector('#linkSection').classList.add('hide');
    } else {
        page.querySelector('#linkedSection').classList.add('hide');
        page.querySelector('#linkSection').classList.remove('hide');
        const reauth = page.querySelector('#reauthDescription');
        if (status.NeedsReauthorization) {
            reauth.textContent = 'The bingers.app session of "' + account + '" expired or was revoked. Link the account again.';
            reauth.classList.remove('hide');
        } else {
            reauth.classList.add('hide');
        }
    }
    page.querySelector('#txtMagicLink').value = '';
}

function save(page) {
    return new Promise((resolve) => {
        const currentUserId = page.querySelector('#selectUser').value;
        ApiClient.getPluginConfiguration(BingersConfigurationPage.pluginUniqueId).then(function (config) {
            let currentUserConfig = config.BingersUsers.filter(function (curr) {
                return curr.LinkedMbUserId == currentUserId;
            })[0];
            // User doesn't have a config, so create a default one.
            if (!currentUserConfig) {
                currentUserConfig = BingersConfigurationPage.defaultUserConfig();
                config.BingersUsers.push(currentUserConfig);
            }
            currentUserConfig.Scrobble = page.querySelector('#chkScrobble').checked;
            currentUserConfig.PostSetWatched = page.querySelector('#chkPostSetWatched').checked;
            currentUserConfig.PostSetUnwatched = page.querySelector('#chkPostSetUnwatched').checked;
            currentUserConfig.PostWatchedHistory = page.querySelector('#chkPostWatchedHistory').checked;
            currentUserConfig.SkipWatchedImportFromBingers = page.querySelector('#chkSkipWatchedImportFromBingers').checked;
            currentUserConfig.CleanupWatchedHistory = page.querySelector('#chkCleanupWatchedHistory').checked;
            currentUserConfig.CleanupDryRun = page.querySelector('#chkCleanupDryRun').checked;
            currentUserConfig.MarkMoviesAsRewatched = page.querySelector('#chkMarkMoviesAsRewatched').checked;
            currentUserConfig.MarkEpisodesAsRewatched = page.querySelector('#chkMarkEpisodesAsRewatched').checked;
            currentUserConfig.ExtraLogging = page.querySelector('#chkExtraLogging').checked;
            currentUserConfig.LinkedMbUserId = currentUserId;
            currentUserConfig.LocationsExcluded = Array.prototype.map.call(page.querySelectorAll('.chkBingersLocation:checked'), elem => {
                return elem.getAttribute('data-location');
            });
            ApiClient.updatePluginConfiguration(BingersConfigurationPage.pluginUniqueId, config).then(function (result) {
                Dashboard.processPluginConfigurationUpdateResult(result);
                BingersConfigurationPage.loadConfiguration(currentUserId, page);
                resolve();
            });
        });
    });
}

function postLinkRequest(userId, action, body) {
    return fetch(ApiClient.getUrl('Bingers/Users/' + userId + '/' + action), {
        method: 'POST',
        headers: {
            'Accept': 'application/json',
            'Content-Type': 'application/json',
            'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"'
        },
        body: JSON.stringify(body || {})
    }).then(function (response) {
        return response.json().catch(function () {
            return {};
        }).then(function (json) {
            if (!response.ok) {
                throw new Error(json.error || (response.status + ' - ' + response.statusText));
            }
            return json;
        });
    });
}

export default function (view) {
    view.querySelector('#selectUser').addEventListener('change', function () {
        BingersConfigurationPage.loadConfiguration(this.value, view);
    });

    view.querySelector('#bingersConfigurationForm').addEventListener('submit', function (e) {
        save(view);
        e.preventDefault();
        return false;
    });

    view.querySelector('#linkAccount').addEventListener('click', function () {
        const currentUserId = view.querySelector('#selectUser').value;
        const magicLink = view.querySelector('#txtMagicLink').value.trim();
        if (!magicLink) {
            Dashboard.alert({ message: 'Paste the magic link received by email first.' });
            return;
        }
        Dashboard.showLoadingMsg();
        postLinkRequest(currentUserId, 'Link', { MagicLink: magicLink }).then(function (status) {
            setLinkElements(view, status);
        }).catch(function (error) {
            Dashboard.alert({ message: 'Could not link the bingers.app account: ' + error.message });
        }).finally(function () {
            Dashboard.hideLoadingMsg();
        });
    });

    view.querySelector('#unlinkAccount').addEventListener('click', function () {
        const currentUserId = view.querySelector('#selectUser').value;
        Dashboard.showLoadingMsg();
        postLinkRequest(currentUserId, 'Unlink').then(function (status) {
            setLinkElements(view, status);
        }).catch(function (error) {
            Dashboard.alert({ message: 'Could not unlink the bingers.app account: ' + error.message });
        }).finally(function () {
            Dashboard.hideLoadingMsg();
        });
    });

    view.addEventListener('viewshow', function () {
        const page = this;
        ApiClient.getUsers().then(function (users) {
            BingersConfigurationPage.populateUsers(users);
            const currentUserId = page.querySelector('#selectUser').value;
            BingersConfigurationPage.loadConfiguration(currentUserId, page);
        });
    });
}
