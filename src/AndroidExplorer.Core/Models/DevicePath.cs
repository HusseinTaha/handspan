namespace AndroidExplorer.Core.Models;

/// <summary>
/// An absolute POSIX path on an Android device.
/// </summary>
/// <remarks>
/// <para>
/// Android paths are deliberately <em>not</em> represented as strings or as
/// <see cref="System.IO.Path"/> values (spec §75). Mixing <c>C:\</c> and <c>/sdcard/</c> is a
/// whole class of bug that this type exists to make impossible: every device filesystem API
/// accepts a <see cref="DevicePath"/> and nothing else.
/// </para>
/// <para>
/// The default value is <see cref="Root"/> ("/"), so an uninitialized instance is safe.
/// </para>
/// <para>
/// Comparison is <b>ordinal and case-sensitive</b>, because the filesystems underlying Android
/// shared storage are case-sensitive. <c>/sdcard/DCIM</c> and <c>/sdcard/dcim</c> are treated as
/// different paths; note that some FUSE/emulated-storage implementations disagree, so never rely
/// on two case variants both being creatable.
/// </para>
/// </remarks>
public readonly struct DevicePath : IEquatable<DevicePath>, IComparable<DevicePath>
{
    /// <summary>The POSIX path separator.</summary>
    public const char Separator = '/';

    /// <summary>Maximum length of a single path segment, in UTF-8 bytes, on typical Android filesystems.</summary>
    public const int MaxFileNameBytes = 255;

    private readonly string? _value;

    private DevicePath(string normalized) => _value = normalized;

    /// <summary>The filesystem root, "/".</summary>
    public static DevicePath Root => default;

    /// <summary>The normalized absolute path. Never null, never empty, never has a trailing separator.</summary>
    public string Value => _value ?? "/";

    /// <summary>True when this is the filesystem root.</summary>
    public bool IsRoot => Value.Length == 1;

    /// <summary>The final segment, or an empty string for <see cref="Root"/>.</summary>
    public string Name
    {
        get
        {
            var value = Value;
            return value.Length == 1 ? string.Empty : value[(value.LastIndexOf(Separator) + 1)..];
        }
    }

    /// <summary>
    /// The extension including the leading dot, or an empty string when there is none.
    /// Dotfiles such as <c>.nomedia</c> are treated as having no extension, matching POSIX convention.
    /// </summary>
    public string Extension
    {
        get
        {
            var name = Name;
            var dot = name.LastIndexOf('.');
            return dot > 0 ? name[dot..] : string.Empty;
        }
    }

    /// <summary>The containing directory. The parent of <see cref="Root"/> is <see cref="Root"/>.</summary>
    public DevicePath Parent
    {
        get
        {
            var value = Value;
            if (value.Length == 1)
            {
                return Root;
            }

            var slash = value.LastIndexOf(Separator);
            return slash == 0 ? Root : new DevicePath(value[..slash]);
        }
    }

    /// <summary>Number of segments below the root. <see cref="Root"/> has depth 0.</summary>
    public int Depth => IsRoot ? 0 : Value.Count(c => c == Separator);

    /// <summary>The path segments, excluding the root. Empty for <see cref="Root"/>.</summary>
    public string[] Segments => IsRoot ? [] : Value[1..].Split(Separator);

    /// <summary>
    /// Parses an absolute POSIX path, collapsing duplicate separators, resolving "." and ".." and
    /// stripping any trailing separator.
    /// </summary>
    /// <exception cref="FormatException">
    /// The path is not absolute, contains a backslash or NUL, or escapes above the root.
    /// </exception>
    public static DevicePath Parse(string path)
    {
        if (!TryParse(path, out var result))
        {
            throw new FormatException(
                $"'{path}' is not a valid absolute Android path. Android paths must start with '/' " +
                "and must not be Windows paths. To build a path containing a literal backslash, " +
                $"use {nameof(Combine)}.");
        }

        return result;
    }

    /// <summary>Attempts to parse an absolute POSIX path. See <see cref="Parse"/>.</summary>
    /// <remarks>
    /// Rejecting backslashes is a deliberate guard against Windows paths leaking in (spec §75).
    /// A backslash is technically legal in a POSIX filename, so entries whose names really contain
    /// one must be built with <see cref="Combine(string)"/> from a listing rather than parsed.
    /// </remarks>
    public static bool TryParse(string? path, out DevicePath result)
    {
        result = Root;

        if (string.IsNullOrEmpty(path) || path[0] != Separator)
        {
            return false;
        }

        if (path.Contains('\\') || path.Contains('\0'))
        {
            return false;
        }

        var stack = new List<string>();
        foreach (var segment in path.Split(Separator))
        {
            switch (segment)
            {
                case "" or ".":
                    continue;
                case "..":
                    if (stack.Count == 0)
                    {
                        return false; // escapes above the root
                    }

                    stack.RemoveAt(stack.Count - 1);
                    continue;
                default:
                    if (!IsValidFileName(segment))
                    {
                        return false;
                    }

                    stack.Add(segment);
                    continue;
            }
        }

        result = stack.Count == 0 ? Root : new DevicePath(Separator + string.Join(Separator, stack));
        return true;
    }

    /// <summary>Appends a single child segment.</summary>
    /// <exception cref="ArgumentException"><paramref name="childName"/> is not a valid file name.</exception>
    public DevicePath Combine(string childName)
    {
        if (!IsValidFileName(childName))
        {
            throw new ArgumentException(
                $"'{childName}' is not a valid Android file name.", nameof(childName));
        }

        return new DevicePath(IsRoot ? Separator + childName : Value + Separator + childName);
    }

    /// <summary>Appends several child segments in order.</summary>
    public DevicePath Combine(params string[] childNames)
    {
        var current = this;
        foreach (var name in childNames)
        {
            current = current.Combine(name);
        }

        return current;
    }

    /// <summary>
    /// True when <paramref name="name"/> is usable as a single path segment: non-empty, not "."
    /// or "..", free of separators and NUL, and within <see cref="MaxFileNameBytes"/>.
    /// </summary>
    /// <remarks>
    /// Backslashes, quotes, newlines, emoji and RTL text are all <em>legal</em> here — Android
    /// filenames routinely contain them (spec §74). Quoting for shell use is the transport
    /// layer's job, never the caller's.
    /// </remarks>
    public static bool IsValidFileName(string? name)
        => !string.IsNullOrEmpty(name)
           && name is not ("." or "..")
           && !name.Contains(Separator)
           && !name.Contains('\0')
           && System.Text.Encoding.UTF8.GetByteCount(name) <= MaxFileNameBytes;

    /// <summary>True when this path is a strict ancestor of <paramref name="other"/>.</summary>
    public bool IsAncestorOf(DevicePath other)
    {
        if (IsRoot)
        {
            return !other.IsRoot;
        }

        var self = Value;
        var candidate = other.Value;

        return candidate.Length > self.Length
               && candidate[self.Length] == Separator
               && candidate.AsSpan(0, self.Length).SequenceEqual(self);
    }

    /// <summary>True when this path is a strict descendant of <paramref name="other"/>.</summary>
    public bool IsDescendantOf(DevicePath other) => other.IsAncestorOf(this);

    /// <inheritdoc />
    public bool Equals(DevicePath other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DevicePath other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public int CompareTo(DevicePath other) => string.CompareOrdinal(Value, other.Value);

    /// <inheritdoc />
    public override string ToString() => Value;

    public static bool operator ==(DevicePath left, DevicePath right) => left.Equals(right);

    public static bool operator !=(DevicePath left, DevicePath right) => !left.Equals(right);

    public static bool operator <(DevicePath left, DevicePath right) => left.CompareTo(right) < 0;

    public static bool operator >(DevicePath left, DevicePath right) => left.CompareTo(right) > 0;

    public static bool operator <=(DevicePath left, DevicePath right) => left.CompareTo(right) <= 0;

    public static bool operator >=(DevicePath left, DevicePath right) => left.CompareTo(right) >= 0;
}
