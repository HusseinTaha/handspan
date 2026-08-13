namespace Handspan.Core.Models;

/// <summary>
/// One entry in a device directory listing (spec §14, §76).
/// </summary>
/// <remarks>
/// Produced from the ADB sync protocol's structured listing records, never from parsing
/// <c>ls -la</c> output (spec §73) — which is why names containing spaces, quotes, newlines,
/// emoji and RTL text are safe here by construction.
/// </remarks>
public sealed record DeviceEntry
{
    public required DeviceId DeviceId { get; init; }

    public required DevicePath Path { get; init; }

    public required DeviceEntryKind Kind { get; init; }

    /// <summary>Size in bytes. See <see cref="IsSizeKnown"/> before displaying it.</summary>
    public long Size { get; init; }

    /// <summary>
    /// False when the device only offered a 32-bit size field and the value saturated, meaning the
    /// real size is unknown and at least 4 GiB. Display "unknown" rather than a wrong number.
    /// </summary>
    public bool IsSizeKnown { get; init; } = true;

    /// <summary>Last modification time, converted from the protocol's Unix UTC seconds (spec §25).</summary>
    public DateTimeOffset Modified { get; init; }

    /// <summary>POSIX mode bits as reported by the device.</summary>
    public int Mode { get; init; }

    /// <summary>True when the underlying entry is a symlink, regardless of what it resolved to.</summary>
    public bool IsSymlink { get; init; }

    public string Name => Path.Name;

    public string Extension => Path.Extension;

    public bool IsDirectory => Kind == DeviceEntryKind.Directory;

    /// <summary>True for dotfiles, per POSIX convention.</summary>
    public bool IsHidden => Name.StartsWith('.');
}

/// <summary>
/// Full information about a single path, from a stat call (spec §14).
/// </summary>
public sealed record DeviceFileInfo
{
    public required DeviceId DeviceId { get; init; }

    public required DevicePath Path { get; init; }

    public required DeviceEntryKind Kind { get; init; }

    public long Size { get; init; }

    public bool IsSizeKnown { get; init; } = true;

    public DateTimeOffset Modified { get; init; }

    public DateTimeOffset? Accessed { get; init; }

    public DateTimeOffset? Created { get; init; }

    public int Mode { get; init; }

    public int? OwnerUserId { get; init; }

    public int? OwnerGroupId { get; init; }

    public bool IsSymlink { get; init; }

    /// <summary>Resolved target when this entry is a symlink.</summary>
    public DevicePath? SymlinkTarget { get; init; }

    /// <summary>MIME type, sniffed or mapped from the extension. Null when undetermined.</summary>
    public string? MimeType { get; init; }

    public bool IsDirectory => Kind == DeviceEntryKind.Directory;

    /// <summary>POSIX permissions rendered as "rwxr-xr-x".</summary>
    public string PermissionString => FormatMode(Mode);

    private static string FormatMode(int mode)
    {
        Span<char> result = stackalloc char[9];
        const string flags = "rwx";
        for (var i = 0; i < 9; i++)
        {
            var bit = 1 << (8 - i);
            result[i] = (mode & bit) != 0 ? flags[i % 3] : '-';
        }

        return new string(result);
    }
}
