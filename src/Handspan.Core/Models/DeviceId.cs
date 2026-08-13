namespace Handspan.Core.Models;

/// <summary>
/// Stable identifier for a device: its ADB serial.
/// </summary>
/// <remarks>
/// Every model, cache key and database row carries a <see cref="DeviceId"/> (spec §39). Two
/// connected phones must never share a cache entry, and retrofitting this later is exactly the
/// cross-device collision the spec warns about — so it is a distinct type rather than a string.
/// </remarks>
public readonly record struct DeviceId(string Serial) : IComparable<DeviceId>
{
    /// <summary>True for a wireless connection, whose serial is "host:port".</summary>
    public bool IsWireless => Serial.Contains(':');

    /// <summary>Filesystem-safe form, for use in cache directory names.</summary>
    public string ToCacheKey()
    {
        var chars = Serial.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(chars[i]) && chars[i] is not ('-' or '_'))
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }

    /// <inheritdoc />
    public int CompareTo(DeviceId other) => string.CompareOrdinal(Serial, other.Serial);

    /// <inheritdoc />
    public override string ToString() => Serial;
}
