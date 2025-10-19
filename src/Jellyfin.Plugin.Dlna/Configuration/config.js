const DlnaConfigurationPage = {
    pluginUniqueId: '33EBA9CD-7DA1-4720-967F-DD7DAE7B74A1',
    defaultDiscoveryInterval: 60,
    defaultAliveInterval: 100,
    loadConfiguration: function (page) {
        const defaults = DlnaConfigurationPage;
        ApiClient.getPluginConfiguration(this.pluginUniqueId)
            .then((config) => {
                page.querySelector('#dlnaPlayTo').checked = config.EnablePlayTo;
                page.querySelector('#dlnaDiscoveryInterval').value = parseInt(config.ClientDiscoveryIntervalSeconds, 10) || defaults.defaultDiscoveryInterval;
                page.querySelector('#dlnaBlastAlive').checked = config.BlastAliveMessages;
                page.querySelector('#dlnaAliveInterval').value = parseInt(config.AliveMessageIntervalSeconds, 10) || defaults.defaultAliveInterval;
                page.querySelector('#dlnaMatchedHost').checked = config.SendOnlyMatchedHost;
                page.querySelector('#dlnaManualDeviceAddresses').value = (config.ManualDeviceAddresses || '').trim();

                ApiClient.getUsers()
                    .then((users) => {
                        DlnaConfigurationPage.populateUsers(page, users, config.DefaultUserId);
                    })
                    .finally(() => {
                        Dashboard.hideLoadingMsg();
                    });
            });
    },
    populateUsers: function(page, users, selectedId){
        let html = '';
        html += '<option value="">None</option>';
        for(let i = 0, length = users.length; i < length; i++) {
            const user = users[i];
            html += '<option value="' + user.Id + '">' + user.Name + '</option>';
        }
        
        page.querySelector('#dlnaSelectUser').innerHTML = html;
        page.querySelector('#dlnaSelectUser').value = selectedId;
    },
    save: function(page) {
        Dashboard.showLoadingMsg();
        return new Promise((_) => {
            const defaults = DlnaConfigurationPage;
            ApiClient.getPluginConfiguration(this.pluginUniqueId)
                .then((config) => {
                    config.EnablePlayTo = page.querySelector('#dlnaPlayTo').checked;
                    config.ClientDiscoveryIntervalSeconds = parseInt(page.querySelector('#dlnaDiscoveryInterval').value, 10) || defaults.defaultDiscoveryInterval;
                    config.BlastAliveMessages = page.querySelector('#dlnaBlastAlive').checked;
                    config.AliveMessageIntervalSeconds = parseInt(page.querySelector('#dlnaAliveInterval').value, 10) || defaults.defaultAliveInterval;
                    config.SendOnlyMatchedHost = page.querySelector('#dlnaMatchedHost').checked;
                    config.ManualDeviceAddresses = (page.querySelector('#dlnaManualDeviceAddresses').value || '').trim();

                    const selectedUser = page.querySelector('#dlnaSelectUser').value;
                    config.DefaultUserId = selectedUser.length > 0 ? selectedUser : null;

                    ApiClient.updatePluginConfiguration(DlnaConfigurationPage.pluginUniqueId, config).then(Dashboard.processPluginConfigurationUpdateResult);
                });
        });
    }
}

export default function(view) {
    view.querySelector('#dlnaForm').addEventListener('submit', function(e) {
        DlnaConfigurationPage.save(view);
        e.preventDefault();
        return false;
    });
    
    window.addEventListener('pageshow', function(_) {
        Dashboard.showLoadingMsg();
        DlnaConfigurationPage.loadConfiguration(view);
    });
}
