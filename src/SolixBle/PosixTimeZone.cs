using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SolixBle;

/// <summary>
/// The local time zone as the device takes it in x022: the UTC offset and a
/// POSIX TZ string (port of SolixBLE <c>utilities.get_posix_tz</c> and
/// <c>_offset_seconds_west</c>).
/// </summary>
internal static class PosixTimeZone
{
    /// <summary>The current local UTC offset as int32 LE seconds west of UTC (east is negative).</summary>
    public static byte[] OffsetSecondsWest()
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow);
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, -(int)offset.TotalSeconds);
        return bytes;
    }

    /// <summary>
    /// The local time zone as a POSIX TZ string (e.g. "&lt;-05&gt;5&lt;-04&gt;,M3.2.0,M11.1.0"),
    /// or <see cref="SolixConst.FallbackTimeZone"/> if it cannot be expressed as one.
    /// </summary>
    public static string Current()
    {
        try
        {
            return FromTimeZone(TimeZoneInfo.Local, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            SolixLog.Warning(
                $"Unable to express time zone '{TimeZoneInfo.Local.Id}' as a POSIX TZ string," +
                $" sending '{SolixConst.FallbackTimeZone}': {ex.Message}");
            return SolixConst.FallbackTimeZone;
        }
    }

    /// <summary>
    /// Build the POSIX TZ string of a zone from the adjustment rule in effect
    /// now. Windows has no zone abbreviations, so the names are the numeric
    /// form tzdata uses for zones without one (e.g. "&lt;+0530&gt;").
    /// </summary>
    private static string FromTimeZone(TimeZoneInfo zone, DateTime utcNow)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone);
        var rule = zone.GetAdjustmentRules()
            .FirstOrDefault(r => r.DateStart <= localNow.Date && localNow.Date <= r.DateEnd);

        var standard = zone.BaseUtcOffset + (rule?.BaseUtcOffsetDelta ?? TimeSpan.Zero);
        var sb = new StringBuilder();
        sb.Append(Name(standard)).Append(Offset(-standard));

        // No daylight saving in effect this year.
        if (rule is null
            || rule.DaylightDelta == TimeSpan.Zero
            || rule.DaylightTransitionStart == rule.DaylightTransitionEnd)
            return sb.ToString();

        var daylight = standard + rule.DaylightDelta;
        sb.Append(Name(daylight));
        if (rule.DaylightDelta != TimeSpan.FromHours(1))
            sb.Append(Offset(-daylight));
        sb.Append(',').Append(Transition(rule.DaylightTransitionStart));
        sb.Append(',').Append(Transition(rule.DaylightTransitionEnd));
        return sb.ToString();
    }

    /// <summary>Numeric zone name: the UTC offset's sign and hours, plus minutes if any (e.g. "&lt;-03&gt;").</summary>
    private static string Name(TimeSpan utcOffset)
    {
        var sign = utcOffset < TimeSpan.Zero ? '-' : '+';
        var abs = utcOffset.Duration();
        var name = $"{sign}{abs.Hours + abs.Days * 24:00}";
        if (abs.Minutes != 0)
            name += $"{abs.Minutes:00}";
        return $"<{name}>";
    }

    /// <summary>POSIX offset (hours west of UTC): [-]h[:mm[:ss]].</summary>
    private static string Offset(TimeSpan posixOffset) =>
        (posixOffset < TimeSpan.Zero ? "-" : "") + Clock(posixOffset.Duration());

    /// <summary>h[:mm[:ss]] with hours unpadded and allowed past 24.</summary>
    private static string Clock(TimeSpan value)
    {
        var text = ((int)value.TotalHours).ToString(CultureInfo.InvariantCulture);
        if (value.Minutes != 0 || value.Seconds != 0)
            text += $":{value.Minutes:00}";
        if (value.Seconds != 0)
            text += $":{value.Seconds:00}";
        return text;
    }

    /// <summary>A transition rule as POSIX Mm.w.d or Jn, plus /time unless it is the default 02:00.</summary>
    private static string Transition(TimeZoneInfo.TransitionTime transition)
    {
        string date;
        if (transition.IsFixedDateRule)
        {
            // Jn counts 1-365 and never Feb 29.
            var day = Math.Min(transition.Day, DateTime.DaysInMonth(2001, transition.Month));
            date = $"J{new DateTime(2001, transition.Month, day).DayOfYear}";
        }
        else
        {
            date = $"M{transition.Month}.{transition.Week}.{(int)transition.DayOfWeek}";
        }

        // Windows writes a midnight transition at the end of a day as 23:59:59.999.
        var time = TimeSpan.FromSeconds(Math.Round(transition.TimeOfDay.TimeOfDay.TotalSeconds));
        return time == TimeSpan.FromHours(2) ? date : $"{date}/{Clock(time)}";
    }
}
