namespace Anchor;

/// <summary>Shutdown policy state machine states.</summary>
public enum PolicyState
{
    /// <summary>No device configured, or no valid sample seen yet.</summary>
    Unconfigured,

    /// <summary>AC input present; battery is charging or holding.</summary>
    OnGrid,

    /// <summary>Wall power is out; running from the power station battery.</summary>
    OnBattery,

    /// <summary>Battery at/below floor; shutdown countdown running.</summary>
    Pending,

    /// <summary>Shutdown has been issued (terminal).</summary>
    ShutdownIssued,
}

/// <summary>Side effects produced by a policy transition.</summary>
public enum PolicyEffect
{
    None,
    EnteredGrid,
    EnteredBattery,
    CountdownStarted,
    CountdownCancelledAcRestored,
    CountdownCancelledReconfigured,
    ShutdownNow,
    BecameUnavailable,
    BecameAvailable,

    /// <summary>
    /// The station's own discharge cutoff is at/above the configured battery
    /// floor, so the effective shutdown floor was raised above it (the station
    /// would otherwise cut output before the countdown ever fired).
    /// </summary>
    FloorRaisedByDeviceLimit,
}

/// <summary>
/// Pure shutdown-policy state machine: no timers, no BLE, time is passed in.
/// Feed telemetry samples via <see cref="Update"/> and wall-clock ticks via
/// <see cref="Tick"/>; both return the effect the caller must act on.
/// </summary>
public sealed class PolicyEngine
{
    private bool _configured;
    private int _floorPercent;
    private int _debounceSamples;
    private int _countdownSeconds;
    private int _consecutiveLow;
    private bool _disarmed;

    public PolicyEngine(bool configured, int floorPercent, int debounceSamples, int countdownSeconds)
    {
        UpdateSettings(configured, floorPercent, debounceSamples, countdownSeconds);
    }

    /// <summary>Current policy state.</summary>
    public PolicyState State { get; private set; } = PolicyState.Unconfigured;

    /// <summary>Device availability overlay (fresh telemetry is flowing).</summary>
    public bool DeviceAvailable { get; private set; }

    /// <summary>When the running countdown expires, while <see cref="State"/> is Pending.</summary>
    public DateTimeOffset? ShutdownDeadline { get; private set; }

    /// <summary>Apply configuration changes live.</summary>
    public void UpdateSettings(bool configured, int floorPercent, int debounceSamples, int countdownSeconds)
    {
        _configured = configured;
        _floorPercent = floorPercent;
        _debounceSamples = debounceSamples;
        _countdownSeconds = countdownSeconds;
        _consecutiveLow = 0;

        if (!configured)
        {
            State = PolicyState.Unconfigured;
            ShutdownDeadline = null;
            _disarmed = false;
            DeviceAvailable = false;
        }
    }

    /// <summary>
    /// Reset all per-device state (used when the monitored device changes so no
    /// decision is carried over from the previously configured device).
    /// </summary>
    public void Reset()
    {
        State = PolicyState.Unconfigured;
        ShutdownDeadline = null;
        _disarmed = false;
        DeviceAvailable = false;
        _consecutiveLow = 0;
    }

    /// <summary>
    /// Process one telemetry sample. Values of -1 mean unknown and are ignored
    /// for state decisions. An availability change is consumed by itself (the
    /// sample is not applied on that call); callers feed the sample again on a
    /// subsequent call if they need it processed.
    /// </summary>
    public PolicyEffect Update(int batteryPercent, int acPowerIn, bool available, DateTimeOffset now)
    {
        var expired = CheckDeadline(now);
        if (expired != PolicyEffect.None)
            return expired;

        if (available != DeviceAvailable)
        {
            DeviceAvailable = available;
            _consecutiveLow = 0;
            return available ? PolicyEffect.BecameAvailable : PolicyEffect.BecameUnavailable;
        }

        if (!_configured || State == PolicyState.ShutdownIssued)
            return PolicyEffect.None;

        // Samples with unknown battery or AC input do not drive state decisions.
        if (batteryPercent < 0 || acPowerIn < 0)
            return PolicyEffect.None;

        if (acPowerIn > 0)
        {
            _consecutiveLow = 0;
            _disarmed = false; // AC has been seen again: re-arm after a user cancel.
            var previous = State;
            State = PolicyState.OnGrid;
            ShutdownDeadline = null;
            if (previous == PolicyState.Pending)
                return PolicyEffect.CountdownCancelledAcRestored;
            return previous == PolicyState.OnGrid ? PolicyEffect.None : PolicyEffect.EnteredGrid;
        }

        // acPowerIn == 0: on battery.
        if (State is PolicyState.OnGrid or PolicyState.Unconfigured)
        {
            State = PolicyState.OnBattery;
            _consecutiveLow = batteryPercent <= _floorPercent ? 1 : 0;
            return PolicyEffect.EnteredBattery;
        }

        if (State == PolicyState.OnBattery)
        {
            _consecutiveLow = batteryPercent <= _floorPercent ? _consecutiveLow + 1 : 0;
            if (_consecutiveLow >= _debounceSamples && !_disarmed && DeviceAvailable)
            {
                State = PolicyState.Pending;
                ShutdownDeadline = now + TimeSpan.FromSeconds(_countdownSeconds);
                return PolicyEffect.CountdownStarted;
            }
        }

        return PolicyEffect.None;
    }

    /// <summary>
    /// Wall-clock tick (call about once per second). An already-running countdown
    /// keeps counting even while the device is unavailable.
    /// </summary>
    public PolicyEffect Tick(DateTimeOffset now) => CheckDeadline(now);

    /// <summary>
    /// User cancelled the countdown. Returns to OnBattery and disarms: no new
    /// countdown starts until AC power has been seen &gt; 0 again.
    /// </summary>
    /// <returns>True if a countdown was actually cancelled.</returns>
    public bool CancelByUser(DateTimeOffset now)
    {
        _ = now;
        if (State != PolicyState.Pending)
            return false;
        State = PolicyState.OnBattery;
        ShutdownDeadline = null;
        _disarmed = true;
        _consecutiveLow = 0;
        return true;
    }

    private PolicyEffect CheckDeadline(DateTimeOffset now)
    {
        if (State == PolicyState.Pending && ShutdownDeadline is { } deadline && now >= deadline)
        {
            State = PolicyState.ShutdownIssued;
            ShutdownDeadline = null;
            return PolicyEffect.ShutdownNow;
        }

        return PolicyEffect.None;
    }
}
