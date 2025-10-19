#nullable disable

using System;
using System.Linq;
using Jellyfin.Data.Events;
using Jellyfin.Plugin.Dlna.Model;
using Rssdp;
using Rssdp.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dlna.Ssdp;

/// <summary>
/// Defines the <see cref="DeviceDiscovery" />.
/// </summary>
public sealed class DeviceDiscovery : IDeviceDiscovery, IDisposable
{
    private readonly object _syncLock = new();
    private readonly ILogger<DeviceDiscovery> _logger;

    private SsdpDeviceLocator _deviceLocator;
    private ISsdpCommunicationsServer _commsServer;

    private int _listenerCount;
    private bool _disposed;

    private event EventHandler<GenericEventArgs<UpnpDeviceInfo>> DeviceDiscoveredInternal;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceDiscovery"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public DeviceDiscovery(ILogger<DeviceDiscovery> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public event EventHandler<GenericEventArgs<UpnpDeviceInfo>> DeviceDiscovered
    {
        add
        {
            lock (_syncLock)
            {
                _listenerCount++;
                DeviceDiscoveredInternal += value;
            }

            StartInternal();
        }

        remove
        {
            lock (_syncLock)
            {
                _listenerCount--;
                DeviceDiscoveredInternal -= value;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<GenericEventArgs<UpnpDeviceInfo>> DeviceLeft;

    /// <summary>
    /// Starts device discovery.
    /// </summary>
    public void Start(ISsdpCommunicationsServer communicationsServer)
    {
        _commsServer = communicationsServer;

        StartInternal();
    }

    private void StartInternal()
    {
        lock (_syncLock)
        {
            if (_listenerCount > 0 && _deviceLocator is null && _commsServer is not null)
            {
                _logger.LogDebug("Starting SSDP device locator with {ListenerCount} listener(s).", _listenerCount);
                _deviceLocator = new SsdpDeviceLocator(
                    _commsServer,
                    Environment.OSVersion.Platform.ToString(),
                    // Can not use VersionString here since that includes OS and version
                    Environment.OSVersion.Version.ToString());

                // (Optional) Set the filter so we only see notifications for devices we care about
                // (can be any search target value i.e device type, uuid value etc - any value that appears in the
                // DiscoverdSsdpDevice.NotificationType property or that is used with the searchTarget parameter of the Search method).
                // _DeviceLocator.NotificationFilter = "upnp:rootdevice";

                // Connect our event handler so we process devices as they are found
                _deviceLocator.DeviceAvailable += OnDeviceLocatorDeviceAvailable;
                _deviceLocator.DeviceUnavailable += OnDeviceLocatorDeviceUnavailable;

                var dueTime = TimeSpan.FromSeconds(5);
                var options = DlnaPlugin.Instance.Configuration;
                var interval = TimeSpan.FromSeconds(options.ClientDiscoveryIntervalSeconds);

                _deviceLocator.RestartBroadcastTimer(dueTime, interval);
            }
        }
    }

    // Process each found device in the event handler
    private void OnDeviceLocatorDeviceAvailable(object sender, DeviceAvailableEventArgs e)
    {
        var originalHeaders = e.DiscoveredDevice.ResponseHeaders;

        _logger.LogDebug(
            "Received SSDP response from {Remote} with location {Location}.",
            e.RemoteIPAddress,
            e.DiscoveredDevice.DescriptionLocation);

        var headerDict = originalHeaders is null ? [] : originalHeaders.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);

        var headers = headerDict.ToDictionary(i => i.Key, i => i.Value.Value.FirstOrDefault(), StringComparer.OrdinalIgnoreCase);

        if (!headers.TryGetValue("USN", out var usn) || string.IsNullOrWhiteSpace(usn))
        {
            _logger.LogDebug("Discovered device at {Remote} does not include a USN header.", e.RemoteIPAddress);
        }

        var args = new GenericEventArgs<UpnpDeviceInfo>(
            new UpnpDeviceInfo
            {
                Location = e.DiscoveredDevice.DescriptionLocation,
                Headers = headers,
                RemoteIPAddress = e.RemoteIPAddress
            });

        DeviceDiscoveredInternal?.Invoke(this, args);
        if (DeviceDiscoveredInternal is null)
        {
            _logger.LogDebug("No subscribers handled the SSDP response from {Remote}.", e.RemoteIPAddress);
        }
    }

    private void OnDeviceLocatorDeviceUnavailable(object sender, DeviceUnavailableEventArgs e)
    {
        var originalHeaders = e.DiscoveredDevice.ResponseHeaders;

        var headerDict = originalHeaders is null ? [] : originalHeaders.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);

        var headers = headerDict.ToDictionary(i => i.Key, i => i.Value.Value.FirstOrDefault(), StringComparer.OrdinalIgnoreCase);

        var args = new GenericEventArgs<UpnpDeviceInfo>(
            new UpnpDeviceInfo
            {
                Location = e.DiscoveredDevice.DescriptionLocation,
                Headers = headers
            });

        _logger.LogDebug("SSDP device at {Location} became unavailable.", e.DiscoveredDevice.DescriptionLocation);
        DeviceLeft?.Invoke(this, args);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_deviceLocator is not null)
            {
                _deviceLocator.Dispose();
                _deviceLocator = null;
            }
        }
    }
}
