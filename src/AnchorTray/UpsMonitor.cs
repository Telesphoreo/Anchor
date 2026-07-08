using SolixBle;

namespace Anchor;

/// <summary>Point-in-time snapshot of device and policy state for the UI.</summary>
public sealed record UpsStatus(
    bool Configured,
    string DeviceName,
    string DeviceMac,
    string ModelName,
    bool Connected,
    bool Negotiated,
    bool Receiving,
    bool Available,
    int BatteryPercent,
    int AcPowerIn,
    int AcPowerOut,
    int PowerOut,
    int Temperature,
    string SerialNumber,
    DateTimeOffset? LastUpdate,
    PolicyState PolicyState,
    DateTimeOffset? ShutdownDeadline,
    int MaxChargeLimit,
    int MinDischargeLimit,
    int EffectiveFloorPercent,
    int ConfiguredFloorPercent);

/// <summary>
/// Owns the BLE device and the policy engine. Connects (with retry) to the
/// configured power station, feeds telemetry samples into the
/// <see cref="PolicyEngine"/>, checks staleness once per second, and raises
/// <see cref="StatusChanged"/> / <see cref="EffectOccurred"/> for the UI.
/// Events may fire on any thread and must not block; the UI layer marshals.
/// </summary>
public sealed class UpsMonitor : IAsyncDisposable
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(5);

    private readonly FileLog _log;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly PolicyEngine _policy;
    private readonly System.Threading.Timer _timer;

    private AppConfig _config;
    private Session? _session;
    private bool _disposed;
    private int _effectiveFloorPercent;

    public UpsMonitor(AppConfig config, FileLog log)
    {
        _log = log;
        _config = config;
        _effectiveFloorPercent = config.BatteryFloorPercent;
        _policy = new PolicyEngine(
            config.Configured, config.BatteryFloorPercent, config.DebounceSamples, config.CountdownSeconds);
        _timer = new System.Threading.Timer(_ => OnTimer(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Raised with a fresh snapshot after every evaluation. Any thread; do not block.</summary>
    public event Action<UpsStatus>? StatusChanged;

    /// <summary>Raised for every non-None policy effect. Any thread; do not block.</summary>
    public event Action<PolicyEffect>? EffectOccurred;

    /// <summary>
    /// Apply (new) settings: updates the policy live and, when the device MAC or
    /// model changed, disposes the current device and starts a new one.
    /// Also used for the initial start. Serialized against itself and disposal.
    /// </summary>
    public async Task ApplySettingsAsync(AppConfig config)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            bool deviceChanged;
            lock (_gate)
            {
                _config = config;
                deviceChanged = _session is null
                    ? config.Configured
                    : !config.Configured
                      || !string.Equals(_session.Mac, config.DeviceMac, StringComparison.OrdinalIgnoreCase)
                      || _session.Model != config.ParseModel();
                if (deviceChanged)
                {
                    // Never carry policy state (a running countdown, disarm, or
                    // grid/battery state) from the previously configured device.
                    var wasPending = _policy.State == PolicyState.Pending;
                    _policy.Reset();
                    if (wasPending)
                        RaiseEffect(PolicyEffect.CountdownCancelledReconfigured);
                }

                // Recompute the effective floor against the currently known
                // discharge limit (a changed device's limit is not carried over).
                var minLimit = deviceChanged ? -1 : _session?.LastKnownMinDischargeLimit ?? -1;
                var effectiveFloor = ComputeEffectiveFloor(config.BatteryFloorPercent, minLimit);
                var newlyRaised = effectiveFloor != _effectiveFloorPercent
                                  && effectiveFloor > config.BatteryFloorPercent;
                _effectiveFloorPercent = effectiveFloor;
                _policy.UpdateSettings(
                    config.Configured, effectiveFloor, config.DebounceSamples, config.CountdownSeconds);
                if (newlyRaised)
                {
                    LogFloorRaised(minLimit, config.BatteryFloorPercent, effectiveFloor);
                    RaiseEffect(PolicyEffect.FloorRaisedByDeviceLimit);
                }
            }

            if (deviceChanged)
            {
                await StopSessionAsync().ConfigureAwait(false);
                if (config.Configured)
                    StartSession(config);
            }
        }
        finally
        {
            _lifecycle.Release();
        }

        PublishStatus();
    }

    /// <summary>Cancel a running countdown on behalf of the user (disarms until AC returns).</summary>
    public void CancelCountdownByUser()
    {
        lock (_gate)
        {
            if (!_policy.CancelByUser(DateTimeOffset.Now))
                return;
            _log.Info("Shutdown countdown cancelled by user; disarmed until AC power is seen again.");
            StatusChanged?.Invoke(BuildStatusLocked());
        }
    }

    /// <summary>Get a current status snapshot.</summary>
    public UpsStatus GetSnapshot()
    {
        lock (_gate)
        {
            return BuildStatusLocked();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            _timer.Dispose();
            await StopSessionAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void StartSession(AppConfig config)
    {
        ulong address;
        try
        {
            address = SolixScanner.MacToAddress(config.DeviceMac);
        }
        catch (FormatException ex)
        {
            _log.Error($"Configured MAC '{config.DeviceMac}' is invalid: {ex.Message}");
            return;
        }

        var model = config.ParseModel();
        var device = SolixDeviceFactory.Create(model, address);
        var session = new Session
        {
            Mac = config.DeviceMac,
            Model = model,
            Device = device,
            Ups = (ISolixUpsDevice)device,
            Cts = new CancellationTokenSource(),
        };

        lock (_gate)
        {
            _session = session;
        }

        device.StateChanged += Evaluate;
        session.ConnectTask = Task.Run(() => ConnectLoopAsync(device, session.Cts.Token));
        _log.Info($"Monitoring {model} at {config.DeviceMac}.");
    }

    private async Task StopSessionAsync()
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        if (session is null)
            return;

        session.Cts.Cancel();
        if (session.ConnectTask is not null)
        {
            try
            {
                await session.ConnectTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error($"Connect loop ended with an error: {ex}");
            }
        }

        session.Device.StateChanged -= Evaluate;
        try
        {
            await session.Device.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error($"Error disconnecting from device: {ex}");
        }

        session.Cts.Dispose();
        _log.Info($"Stopped monitoring {session.Model} at {session.Mac}.");
    }

    /// <summary>
    /// Retry until the first successful connection or until stopped. After the
    /// first success the library's own auto-reconnect task takes over.
    /// </summary>
    private async Task ConnectLoopAsync(SolixBleDevice device, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await device.ConnectAsync(ct: ct).ConfigureAwait(false))
                {
                    _log.Info($"Connected to '{device.Name}' ({device.Address}).");
                    return;
                }

                _log.Warning(
                    $"Could not connect to '{device.Address}'; retrying in {ConnectRetryDelay.TotalSeconds:0} s.");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error($"Unexpected error connecting to '{device.Address}': {ex}");
            }

            try
            {
                await Task.Delay(ConnectRetryDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void OnTimer()
    {
        if (_disposed)
            return;
        try
        {
            Evaluate();
        }
        catch (Exception ex)
        {
            _log.Error($"Monitor tick failed: {ex}");
        }
    }

    /// <summary>
    /// Core evaluation, run on device state changes and once per second: sync
    /// device availability (including staleness) into the policy, feed a sample
    /// when new telemetry arrived, tick the countdown deadline, publish status.
    /// Events are raised while holding the gate to preserve ordering; handlers
    /// only post to the UI thread.
    /// </summary>
    private void Evaluate()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            var session = _session;
            if (session is not null)
            {
                var device = session.Device;
                var lastUpdate = device.LastUpdate;
                var available = device.Available
                                && lastUpdate is not null
                                && now - lastUpdate.Value <= StaleAfter;

                if (available != _policy.DeviceAvailable)
                    RaiseEffect(_policy.Update(-1, -1, available, now));

                if (lastUpdate is not null && lastUpdate != session.LastFedUpdate)
                {
                    session.LastFedUpdate = lastUpdate;
                    var battery = SafeInt(() => session.Ups.BatteryPercentage);
                    var acPowerIn = SafeInt(() => session.Ups.AcPowerIn);

                    // Cache the station's own discharge cutoff for the session so
                    // a reconnect gap (telemetry briefly missing the parameter)
                    // does not flap the effective floor back down and up again.
                    var minLimit = SafeInt(() => session.Ups.MinDischargeLimitPercent);
                    if (minLimit is >= 1 and <= 99)
                        session.LastKnownMinDischargeLimit = minLimit;
                    ApplyEffectiveFloorLocked(session.LastKnownMinDischargeLimit);

                    RaiseEffect(_policy.Update(battery, acPowerIn, available, now));
                }
            }

            RaiseEffect(_policy.Tick(now));
            StatusChanged?.Invoke(BuildStatusLocked());
        }
    }

    private void PublishStatus()
    {
        lock (_gate)
        {
            StatusChanged?.Invoke(BuildStatusLocked());
        }
    }

    private void RaiseEffect(PolicyEffect effect)
    {
        if (effect == PolicyEffect.None)
            return;
        _log.Info($"Policy effect: {effect} (state now {_policy.State}).");
        EffectOccurred?.Invoke(effect);
    }

    /// <summary>
    /// The floor the policy actually acts on. Normally this is exactly the
    /// configured floor. It is only raised when the configured floor is at or
    /// below the station's own discharge cutoff — in that case the station would
    /// cut output before the countdown ever fired, so the floor is nudged to
    /// cutoff+1 (capped at 95) so the countdown fires while output still exists.
    /// A configured floor already above the cutoff is left untouched.
    /// </summary>
    private static int ComputeEffectiveFloor(int configuredFloor, int minDischargeLimit) =>
        minDischargeLimit is >= 1 and <= 99 && configuredFloor <= minDischargeLimit
            ? Math.Min(95, minDischargeLimit + 1)
            : configuredFloor;

    /// <summary>
    /// Push a recomputed effective floor into the policy, but only when the value
    /// actually changed — <see cref="PolicyEngine.UpdateSettings"/> resets the
    /// debounce counter, so calling it every sample would defeat debouncing.
    /// Runs under the gate.
    /// </summary>
    private void ApplyEffectiveFloorLocked(int minDischargeLimit)
    {
        var configuredFloor = _config.BatteryFloorPercent;
        var effectiveFloor = ComputeEffectiveFloor(configuredFloor, minDischargeLimit);
        if (effectiveFloor == _effectiveFloorPercent)
            return;

        var previous = _effectiveFloorPercent;
        _effectiveFloorPercent = effectiveFloor;
        _policy.UpdateSettings(
            _config.Configured, effectiveFloor, _config.DebounceSamples, _config.CountdownSeconds);

        if (effectiveFloor > configuredFloor)
        {
            LogFloorRaised(minDischargeLimit, configuredFloor, effectiveFloor);
            RaiseEffect(PolicyEffect.FloorRaisedByDeviceLimit);
        }
        else
        {
            // The raise went away (or was never there): log, but no effect.
            _log.Info($"Effective shutdown floor is {effectiveFloor}% (was {previous}%).");
        }
    }

    private void LogFloorRaised(int minDischargeLimit, int configuredFloor, int effectiveFloor)
    {
        _log.Warning(
            $"Station discharge limit is {minDischargeLimit}%, at/above the configured floor of " +
            $"{configuredFloor}%: raising the effective shutdown floor to {effectiveFloor}% so the " +
            "countdown fires before the station cuts output.");
    }

    private UpsStatus BuildStatusLocked()
    {
        var session = _session;
        if (session is null)
        {
            return new UpsStatus(
                _config.Configured,
                DeviceName: "Unknown",
                _config.DeviceMac,
                _config.ParseModel().ToString(),
                Connected: false,
                Negotiated: false,
                Receiving: false,
                Available: false,
                BatteryPercent: -1,
                AcPowerIn: -1,
                AcPowerOut: -1,
                PowerOut: -1,
                Temperature: -1,
                SerialNumber: "Unknown",
                LastUpdate: null,
                _policy.State,
                _policy.ShutdownDeadline,
                MaxChargeLimit: -1,
                MinDischargeLimit: -1,
                EffectiveFloorPercent: _effectiveFloorPercent,
                ConfiguredFloorPercent: _config.BatteryFloorPercent);
        }

        var device = session.Device;
        return new UpsStatus(
            Configured: true,
            device.Name,
            device.Address,
            session.Model.ToString(),
            device.Connected,
            device.Negotiated,
            Receiving: device.Available,
            Available: _policy.DeviceAvailable,
            SafeInt(() => session.Ups.BatteryPercentage),
            SafeInt(() => session.Ups.AcPowerIn),
            SafeInt(() => session.Ups.AcPowerOut),
            SafeInt(() => session.Ups.PowerOut),
            SafeInt(() => session.Ups.Temperature),
            SafeString(() => session.Ups.SerialNumber),
            device.LastUpdate,
            _policy.State,
            _policy.ShutdownDeadline,
            SafeInt(() => session.Ups.MaxChargeLimitPercent),
            SafeInt(() => session.Ups.MinDischargeLimitPercent),
            _effectiveFloorPercent,
            _config.BatteryFloorPercent);
    }

    private static int SafeInt(Func<int> getter)
    {
        try
        {
            return getter();
        }
        catch (KeyNotFoundException)
        {
            return -1;
        }
    }

    private static string SafeString(Func<string> getter)
    {
        try
        {
            return getter();
        }
        catch (KeyNotFoundException)
        {
            return "Unknown";
        }
    }

    private sealed class Session
    {
        public required string Mac { get; init; }
        public required SolixDeviceModel Model { get; init; }
        public required SolixBleDevice Device { get; init; }
        public required ISolixUpsDevice Ups { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public Task? ConnectTask { get; set; }
        public DateTimeOffset? LastFedUpdate { get; set; }

        /// <summary>
        /// Last valid discharge cutoff reported this session; -1 = never seen.
        /// Cached so a reconnect does not flap the effective floor.
        /// </summary>
        public int LastKnownMinDischargeLimit { get; set; } = -1;
    }
}
