using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Jellyfin.Plugin.Dlna.Configuration;
using Jellyfin.Plugin.Dlna.Model;
using Jellyfin.Plugin.Dlna.PlayTo;
using Jellyfin.Plugin.Dlna.Ssdp;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rssdp;
using Rssdp.Infrastructure;

namespace Jellyfin.Plugin.Dlna.Main;

/// <summary>
/// An <see cref="IHostedService"/> that manages a DLNA server.
/// </summary>
public sealed class DlnaHost : IHostedService, IDisposable
{
    private readonly ILogger<DlnaHost> _logger;
    private readonly IServerConfigurationManager _config;
    private readonly IServerApplicationHost _appHost;
    private readonly ISessionManager _sessionManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IDlnaManager _dlnaManager;
    private readonly IImageProcessor _imageProcessor;
    private readonly IUserDataManager _userDataManager;
    private readonly ILocalizationManager _localization;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IDeviceDiscovery _deviceDiscovery;
    private readonly ISsdpCommunicationsServer _communicationsServer;
    private readonly INetworkManager _networkManager;
    private readonly object _syncLock = new();

    private SsdpDevicePublisher? _publisher;
    private PlayToManager? _manager;
    private bool _disposed;
    private readonly object _manualDiscoveryLock = new();
    private CancellationTokenSource? _manualDiscoveryCancellation;
    private Task? _manualDiscoveryTask;
    private TimeSpan _manualDiscoveryInterval = TimeSpan.FromSeconds(60);
    private IReadOnlyList<IPEndPoint> _manualDiscoveryTargets = Array.Empty<IPEndPoint>();
    private static readonly char[] ManualAddressSeparators = [',', ';', '\r', '\n'];
    private static readonly byte[] ManualDiscoveryPayload = BuildManualDiscoveryPayload();

    /// <summary>
    /// Initializes a new instance of the <see cref="DlnaHost"/> class.
    /// </summary>
    /// <param name="config">The <see cref="IServerConfigurationManager"/>.</param>
    /// <param name="loggerFactory">The <see cref="ILoggerFactory"/>.</param>
    /// <param name="appHost">The <see cref="IServerApplicationHost"/>.</param>
    /// <param name="sessionManager">The <see cref="ISessionManager"/>.</param>
    /// <param name="httpClientFactory">The <see cref="IHttpClientFactory"/>.</param>
    /// <param name="libraryManager">The <see cref="ILibraryManager"/>.</param>
    /// <param name="userManager">The <see cref="IUserManager"/>.</param>
    /// <param name="dlnaManager">The <see cref="IDlnaManager"/>.</param>
    /// <param name="imageProcessor">The <see cref="IImageProcessor"/>.</param>
    /// <param name="userDataManager">The <see cref="IUserDataManager"/>.</param>
    /// <param name="localizationManager">The <see cref="ILocalizationManager"/>.</param>
    /// <param name="mediaSourceManager">The <see cref="IMediaSourceManager"/>.</param>
    /// <param name="deviceDiscovery">The <see cref="IDeviceDiscovery"/>.</param>
    /// <param name="mediaEncoder">The <see cref="IMediaEncoder"/>.</param>
    /// <param name="communicationsServer">The <see cref="ISsdpCommunicationsServer"/>.</param>
    /// <param name="networkManager">The <see cref="INetworkManager"/>.</param>
    public DlnaHost(
        IServerConfigurationManager config,
        ILoggerFactory loggerFactory,
        IServerApplicationHost appHost,
        ISessionManager sessionManager,
        IHttpClientFactory httpClientFactory,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IDlnaManager dlnaManager,
        IImageProcessor imageProcessor,
        IUserDataManager userDataManager,
        ILocalizationManager localizationManager,
        IMediaSourceManager mediaSourceManager,
        IDeviceDiscovery deviceDiscovery,
        IMediaEncoder mediaEncoder,
        ISsdpCommunicationsServer communicationsServer,
        INetworkManager networkManager)
    {
        _config = config;
        _appHost = appHost;
        _sessionManager = sessionManager;
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _dlnaManager = dlnaManager;
        _imageProcessor = imageProcessor;
        _userDataManager = userDataManager;
        _localization = localizationManager;
        _mediaSourceManager = mediaSourceManager;
        _deviceDiscovery = deviceDiscovery;
        _mediaEncoder = mediaEncoder;
        _communicationsServer = communicationsServer;
        _networkManager = networkManager;
        _logger = loggerFactory.CreateLogger<DlnaHost>();
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var netConfig = _config.GetConfiguration<NetworkConfiguration>(NetworkConfigurationStore.StoreKey);
        if (_appHost.ListenWithHttps && netConfig.RequireHttps)
        {
            // No use starting as dlna won't work, as we're running purely on HTTPS.
            _logger.LogError("The DLNA specification does not support HTTPS.");
            return;
        }

        await ((DlnaManager)_dlnaManager).InitProfilesAsync().ConfigureAwait(false);
        ReloadComponents();

        _config.NamedConfigurationUpdated += OnNamedConfigurationUpdated;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Stop();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            Stop();
            StopManualDiscoveryLoop();
            _disposed = true;
        }
    }

    private void OnNamedConfigurationUpdated(object? sender, ConfigurationUpdateEventArgs e)
    {
        if (string.Equals(e.Key, "dlna", StringComparison.OrdinalIgnoreCase))
        {
            ReloadComponents();
        }
    }

    private void ReloadComponents()
    {
        var options = DlnaPlugin.Instance.Configuration;
        StartDeviceDiscovery();
        StartDevicePublisher(options);

        if (options.EnablePlayTo)
        {
            StartPlayToManager();
        }
        else
        {
            DisposePlayToManager();
        }

        ConfigureManualDiscovery(options);
    }

    private void ConfigureManualDiscovery(DlnaPluginConfiguration options)
    {
        var endpoints = ParseManualDiscoveryTargets(options.ManualDeviceAddresses).ToArray();
        if (endpoints.Length == 0)
        {
            StopManualDiscoveryLoop();
            return;
        }

        CancellationTokenSource? previousCancellation;
        Task? previousTask;

        lock (_manualDiscoveryLock)
        {
            previousCancellation = _manualDiscoveryCancellation;
            previousTask = _manualDiscoveryTask;

            _manualDiscoveryTargets = endpoints;
            _manualDiscoveryInterval = TimeSpan.FromSeconds(Math.Max(1, options.ClientDiscoveryIntervalSeconds));

            _logger.LogInformation(
                "Manual SSDP discovery enabled for {Count} endpoint(s): {Targets}",
                endpoints.Length,
                string.Join(", ", endpoints.Select(p => p.ToString())));

            var cancellation = new CancellationTokenSource();
            _manualDiscoveryCancellation = cancellation;
            _manualDiscoveryTask = ManualDiscoveryLoopAsync(cancellation.Token);
        }

        DisposeManualDiscoveryResources(previousCancellation, previousTask);
    }

    private static string CreateUuid(string text)
    {
        if (!Guid.TryParse(text, out var guid))
        {
            guid = text.GetMD5();
        }

        return guid.ToString("D", CultureInfo.InvariantCulture);
    }

    private async Task ManualDiscoveryLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                await SendManualDiscoveryCycleAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(_manualDiscoveryInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual SSDP discovery loop terminated unexpectedly.");
        }
    }

    private async Task SendManualDiscoveryCycleAsync(CancellationToken cancellationToken)
    {
        var endpoints = _manualDiscoveryTargets;
        if (endpoints.Count == 0 || _communicationsServer is null)
        {
            return;
        }

        var localAddresses = GetManualDiscoveryLocalAddresses();
        if (localAddresses.Count == 0)
        {
            _logger.LogDebug("Skipping manual SSDP discovery because no IPv4 source addresses are available.");
            return;
        }

        foreach (var endpoint in endpoints)
        {
            try
            {
                var sendTasks = localAddresses
                    .Select(address => _communicationsServer.SendMessage(ManualDiscoveryPayload, endpoint, address, cancellationToken))
                    .ToArray();

                if (sendTasks.Length == 0)
                {
                    continue;
                }

                await Task.WhenAll(sendTasks).ConfigureAwait(false);
                _logger.LogDebug("Manual SSDP discovery request sent to {Endpoint} using {Count} interface(s).", endpoint, localAddresses.Count);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send manual SSDP discovery to {Endpoint}.", endpoint);
            }
        }
    }

    private IEnumerable<IPEndPoint> ParseManualDiscoveryTargets(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            yield break;
        }

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var segments = rawValue.Split(ManualAddressSeparators, StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            var trimmed = segment.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (TryParseManualEndpoint(trimmed, out var endpoint) && endpoint is not null)
            {
                var key = endpoint.ToString();
                if (unique.Add(key))
                {
                    yield return endpoint;
                }
            }
            else
            {
                _logger.LogWarning("Ignoring invalid manual DLNA discovery target '{ManualTarget}'.", trimmed);
            }
        }
    }

    private static bool TryParseManualEndpoint(string value, out IPEndPoint? endpoint)
    {
        if (IPEndPoint.TryParse(value, out var parsed))
        {
            endpoint = parsed;
            if (endpoint.Port == 0)
            {
                endpoint = new IPEndPoint(endpoint.Address, SsdpConstants.MulticastPort);
            }

            return true;
        }

        if (IPAddress.TryParse(value, out var address))
        {
            endpoint = new IPEndPoint(address, SsdpConstants.MulticastPort);
            return true;
        }

        endpoint = null;
        return false;
    }

    private static bool IsPreferredBroadcastAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length > 0 && bytes[0] == 192;
    }

    private List<IPAddress> GetManualDiscoveryLocalAddresses()
    {
        var addresses = _networkManager.GetInternalBindAddresses()
            .Where(x => x.Address is not null)
            .Where(x => x.AddressFamily == AddressFamily.InterNetwork)
            .Where(x => !x.Address!.Equals(IPAddress.Loopback))
            .Select(x => x.Address!)
            .Where(IsPreferredBroadcastAddress)
            .Distinct()
            .ToList();

        if (addresses.Count == 0)
        {
            addresses = _networkManager.GetLoopbacks()
                .Select(x => x.Address)
                .Where(x => x is not null)
                .Select(x => x!)
                .Where(IsPreferredBroadcastAddress)
                .Distinct()
                .ToList();
        }

        return addresses;
    }

    private void StopManualDiscoveryLoop()
    {
        CancellationTokenSource? cancellation;
        Task? task;

        lock (_manualDiscoveryLock)
        {
            cancellation = _manualDiscoveryCancellation;
            task = _manualDiscoveryTask;
            _manualDiscoveryCancellation = null;
            _manualDiscoveryTask = null;
            _manualDiscoveryTargets = Array.Empty<IPEndPoint>();
        }

        DisposeManualDiscoveryResources(cancellation, task);
    }

    private void DisposeManualDiscoveryResources(CancellationTokenSource? cancellation, Task? task)
    {
        if (cancellation is not null)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (task is not null)
        {
            _ = task.ContinueWith(
                t =>
                {
                    if (t.IsFaulted && t.Exception is not null)
                    {
                        _logger.LogWarning(t.Exception, "Manual SSDP discovery loop ended with an error.");
                    }

                    t.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        cancellation?.Dispose();
    }

    private static byte[] BuildManualDiscoveryPayload()
    {
        var builder = new StringBuilder();
        var userAgent = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/{1} UPnP/1.0 JellyfinDLNA/1.0",
            Environment.OSVersion.Platform,
            Environment.OSVersion.Version);

        builder.Append("M-SEARCH * HTTP/1.1\r\n");
        builder.AppendFormat(
            CultureInfo.InvariantCulture,
            "HOST: {0}:{1}\r\n",
            SsdpConstants.MulticastLocalAdminAddress,
            SsdpConstants.MulticastPort);
        builder.Append("MAN: \"ssdp:discover\"\r\n");
        builder.Append("MX: 3\r\n");
        builder.Append("ST: ssdp:all\r\n");
        builder.AppendFormat(CultureInfo.InvariantCulture, "USER-AGENT: {0}\r\n", userAgent);
        builder.Append("\r\n");

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void SetProperties(SsdpDevice device, string fullDeviceType)
    {
        var serviceParts = fullDeviceType
            .Replace("urn:", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(":1", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Split(':');

        device.DeviceTypeNamespace = serviceParts[0].Replace('.', '-');
        device.DeviceClass = serviceParts[1];
        device.DeviceType = serviceParts[2];
    }

    private void StartDeviceDiscovery()
    {
        try
        {
            ((DeviceDiscovery)_deviceDiscovery).Start(_communicationsServer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting device discovery");
        }
    }

    private void StartDevicePublisher(DlnaPluginConfiguration options)
    {
        if (_publisher is not null)
        {
            return;
        }

        try
        {
            _publisher = new SsdpDevicePublisher(
                _communicationsServer,
                Environment.OSVersion.Platform.ToString(),
                // Can not use VersionString here since that includes OS and version
                Environment.OSVersion.Version.ToString(),
                options.SendOnlyMatchedHost)
            {
                LogFunction = msg => _logger.LogDebug("{Msg}", msg),
                SupportPnpRootDevice = false
            };

            RegisterServerEndpoints();

            if (options.BlastAliveMessages)
            {
                _publisher.StartSendingAliveNotifications(TimeSpan.FromSeconds(options.AliveMessageIntervalSeconds));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering endpoint");
        }
    }

    private void RegisterServerEndpoints()
    {
        var udn = CreateUuid(_appHost.SystemId);
        var netConfig = _config.GetConfiguration<NetworkConfiguration>(NetworkConfigurationStore.StoreKey);
        var baseURL = netConfig.BaseUrl;
        var descriptorUri = baseURL + "/dlna/" + udn + "/description.xml";

        // Only get bind addresses in LAN
        // IPv6 is currently unsupported
        var validInterfaces =  _networkManager.GetInternalBindAddresses()
            .Where(x => x.Address is not null)
            .Where(x => x.AddressFamily != AddressFamily.InterNetworkV6)
            .Where(x => x.AddressFamily == AddressFamily.InterNetwork)
            .Where(x => x.SupportsMulticast)
            .Where(x => !x.Address.Equals(IPAddress.Loopback))
            .Where(x => IsPreferredBroadcastAddress(x.Address!))
            .ToList();

        var httpBindPort = _appHost.HttpPort;

        if (validInterfaces.Count == 0)
        {
            // No interfaces returned, fall back to loopback
            validInterfaces = _networkManager.GetLoopbacks().ToList();
        }

        foreach (var intf in validInterfaces)
        {
            var fullService = "urn:schemas-upnp-org:device:MediaServer:1";

            var uri = new UriBuilder(intf.Address + descriptorUri);
            uri.Scheme = "http://";
            uri.Port = httpBindPort;

            _logger.LogInformation("Registering publisher for {ResourceName} on {DeviceAddress} with uri {FullUri}", fullService, intf.Address, uri);

            var device = new SsdpRootDevice
            {
                CacheLifetime = TimeSpan.FromSeconds(1800), // How long SSDP clients can cache this info.
                Location = uri.Uri, // Must point to the URL that serves your devices UPnP description document.
                Address = intf.Address,
                PrefixLength = NetworkUtils.MaskToCidr(intf.Subnet.Prefix),
                FriendlyName = "Jellyfin",
                Manufacturer = "Jellyfin",
                ModelName = "Jellyfin Server",
                Uuid = udn
                // This must be a globally unique value that survives reboots etc. Get from storage or embedded hardware etc.
            };

            SetProperties(device, fullService);
            _publisher!.AddDevice(device);

            var embeddedDevices = new[]
            {
                "urn:schemas-upnp-org:service:ContentDirectory:1",
                "urn:schemas-upnp-org:service:ConnectionManager:1",
                // "urn:microsoft.com:service:X_MS_MediaReceiverRegistrar:1"
            };

            foreach (var subDevice in embeddedDevices)
            {
                var embeddedDevice = new SsdpEmbeddedDevice
                {
                    FriendlyName = device.FriendlyName,
                    Manufacturer = device.Manufacturer,
                    ModelName = device.ModelName,
                    Uuid = udn
                    // This must be a globally unique value that survives reboots etc. Get from storage or embedded hardware etc.
                };

                SetProperties(embeddedDevice, subDevice);
                device.AddDevice(embeddedDevice);
            }
        }
    }

    private void StartPlayToManager()
    {
        lock (_syncLock)
        {
            if (_manager is not null)
            {
                return;
            }

            try
            {
                _manager = new PlayToManager(
                    _logger,
                    _sessionManager,
                    _libraryManager,
                    _userManager,
                    _dlnaManager,
                    _appHost,
                    _imageProcessor,
                    _deviceDiscovery,
                    _httpClientFactory,
                    _userDataManager,
                    _localization,
                    _mediaSourceManager,
                    _mediaEncoder);

                _manager.Start();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting PlayTo manager");
            }
        }
    }

    private void DisposePlayToManager()
    {
        lock (_syncLock)
        {
            if (_manager is not null)
            {
                try
                {
                    _logger.LogInformation("Disposing PlayToManager");
                    _manager.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error disposing PlayTo manager");
                }

                _manager = null;
            }
        }
    }

    private void DisposeDevicePublisher()
    {
        if (_publisher is not null)
        {
            _logger.LogInformation("Disposing SsdpDevicePublisher");
            _publisher.Dispose();
            _publisher = null;
        }
    }

    private void Stop()
    {
        StopManualDiscoveryLoop();
        DisposeDevicePublisher();
        DisposePlayToManager();
    }
}
