namespace SolixBle;

/// <summary>
/// Common UPS-relevant read surface implemented by the ported Solix device models.
/// Property getters may throw <see cref="KeyNotFoundException"/> when a parameter is
/// absent from the latest telemetry — callers guard.
/// </summary>
public interface ISolixUpsDevice
{
    /// <summary>Battery percentage; -1 unknown.</summary>
    int BatteryPercentage { get; }

    /// <summary>AC power in, watts; -1 unknown; 0 == on battery.</summary>
    int AcPowerIn { get; }

    /// <summary>AC power out, watts; -1 if the device/model doesn't report it.</summary>
    int AcPowerOut { get; }

    /// <summary>Total power out, watts; -1 if not reported.</summary>
    int PowerOut { get; }

    /// <summary>Temperature in degrees C; -1 if not reported.</summary>
    int Temperature { get; }

    /// <summary>Device serial number; "Unknown" if not reported.</summary>
    string SerialNumber { get; }

    /// <summary>
    /// User-set charge limit percentage at which the device stops charging;
    /// -1 = not reported by this model. Read-only: the BLE protocol has no
    /// known command to change it.
    /// </summary>
    int MaxChargeLimitPercent => -1;

    /// <summary>
    /// User-set discharge limit percentage at which the device cuts its outputs;
    /// -1 = not reported by this model. Read-only: the BLE protocol has no
    /// known command to change it.
    /// </summary>
    int MinDischargeLimitPercent => -1;
}
