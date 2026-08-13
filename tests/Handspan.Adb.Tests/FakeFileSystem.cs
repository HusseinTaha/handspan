using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Handspan.Adb.Tests;

/// <summary>One node in the fake device's filesystem.</summary>
internal sealed class FakeNode
{
    public bool IsDirectory { get; init; }

    public string? SymlinkTarget { get; init; }

    public byte[] Content { get; set; } = [];

    public int Mode { get; set; }

    public long ModifiedUnix { get; set; } = 1_760_000_000;

    /// <summary>
    /// Overrides the reported length, so a multi-gigabyte file can be modelled without allocating it.
    /// Used to exercise the 64-bit size path.
    /// </summary>
    public long? ReportedLength { get; init; }

    /// <summary>Directories report zero length, matching real devices closely enough.</summary>
    public long Length => IsDirectory ? 0 : ReportedLength ?? Content.LongLength;

    public static FakeNode Directory() => new() { IsDirectory = true, Mode = 0x4000 | 0x1ED };

    public static FakeNode File(byte[] content) => new() { Content = content, Mode = 0x8000 | 0x1A4 };

    /// <summary>A file that claims a size without holding the bytes.</summary>
    public static FakeNode SparseFile(long length)
        => new() { ReportedLength = length, Mode = 0x8000 | 0x1A4 };

    public static FakeNode Symlink(string target)
        => new() { SymlinkTarget = target, Mode = 0xA000 | 0x1FF };
}

/// <summary>
/// A flat path-keyed filesystem for the fake server.
/// </summary>
/// <remarks>
/// Flat rather than a tree because every protocol operation is path-addressed, and it keeps
/// fault-injection setup in tests to a single line.
/// </remarks>
internal sealed class FakeFileSystem
{
    private readonly Dictionary<string, FakeNode> _nodes = new(StringComparer.Ordinal);

    /// <summary>
    /// Guards every access. The fake serves each connection on its own thread, and the client opens a
    /// socket per operation — so uploads, stats and listings genuinely run concurrently here. An
    /// unsynchronized dictionary produced intermittent "file not found" failures that looked like
    /// product bugs.
    /// </summary>
    private readonly object _gate = new();

    public static FakeFileSystem WithTypicalAndroidLayout()
    {
        var files = new FakeFileSystem();

        files.AddDirectory("/");
        files.AddDirectory("/storage");
        files.AddDirectory("/storage/emulated");
        files.AddDirectory("/storage/emulated/0");

        // /sdcard is a symlink on every modern device — the client must resolve it (spec §1.4).
        files._nodes["/sdcard"] = FakeNode.Symlink("/storage/emulated/0");

        foreach (var folder in new[] { "DCIM", "Pictures", "Movies", "Music", "Download", "Documents" })
        {
            files.AddDirectory($"/storage/emulated/0/{folder}");
        }

        files.AddDirectory("/storage/emulated/0/DCIM/Camera");

        files.AddDirectory("/data");
        files.AddDirectory("/system");

        return files;
    }

    public void AddDirectory(string path) => Set(path, FakeNode.Directory());

    public void AddFile(string path, byte[] content) => Set(path, FakeNode.File(content));

    private void Set(string path, FakeNode node)
    {
        lock (_gate)
        {
            _nodes[Normalize(path)] = node;
        }
    }

    public void AddFile(string path, string content)
        => AddFile(path, Encoding.UTF8.GetBytes(content));

    /// <summary>Adds a file that reports a size without holding the bytes.</summary>
    public void AddSparseFile(string path, long length) => Set(path, FakeNode.SparseFile(length));

    /// <summary>Adds a file of a given size with deterministic, verifiable content.</summary>
    public byte[] AddGeneratedFile(string path, int size)
    {
        var content = new byte[size];
        for (var i = 0; i < size; i++)
        {
            content[i] = (byte)(i * 31 % 251);
        }

        AddFile(path, content);
        return content;
    }

    public FakeNode? Get(string path)
    {
        lock (_gate)
        {
            return _nodes.GetValueOrDefault(Normalize(path));
        }
    }

    /// <summary>Gets a node, following one level of symlink as <c>stat</c> would.</summary>
    public FakeNode? Resolve(string path)
    {
        var node = Get(path);
        if (node?.SymlinkTarget is { } target)
        {
            return Get(target);
        }

        return node;
    }

    public bool Exists(string path) => Resolve(path) is not null;

    /// <summary>
    /// Immediate children of a directory, after resolving it if it is a symlink. Materialized inside the
    /// lock rather than yielded, so a concurrent write cannot invalidate the enumeration.
    /// </summary>
    public IReadOnlyList<(string Name, FakeNode Node)> Children(string path)
    {
        lock (_gate)
        {
            var directory = Normalize(path);

            if (_nodes.GetValueOrDefault(directory)?.SymlinkTarget is { } target)
            {
                directory = Normalize(target);
            }

            var prefix = directory == "/" ? "/" : directory + "/";
            var children = new List<(string, FakeNode)>();

            foreach (var (key, node) in _nodes)
            {
                if (key == directory || !key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var remainder = key[prefix.Length..];
                if (remainder.Length == 0 || remainder.Contains('/'))
                {
                    continue;
                }

                children.Add((remainder, node));
            }

            return children;
        }
    }

    public void WriteFile(string path, byte[] content, int modifiedUnix)
        => Set(path, new FakeNode
        {
            Content = content,
            Mode = 0x8000 | 0x1A4,
            ModifiedUnix = modifiedUnix > 0 ? modifiedUnix : 1_760_000_000,
        });

    /// <summary>
    /// Writes at a byte offset, extending the file as needed. Models <c>dd seek=N conv=notrunc</c>.
    /// </summary>
    public void WriteAt(string path, long offset, byte[] data)
    {
        lock (_gate)
        {
            var normalized = Normalize(path);
            var existing = _nodes.GetValueOrDefault(normalized)?.Content ?? [];

            var required = offset + data.Length;
            var content = new byte[Math.Max(existing.LongLength, required)];
            existing.CopyTo(content, 0);
            data.CopyTo(content, offset);

            _nodes[normalized] = new FakeNode { Content = content, Mode = 0x8000 | 0x1A4 };
        }
    }

    public void Delete(string path, bool recursive)
    {
        lock (_gate)
        {
            var normalized = Normalize(path);
            _nodes.Remove(normalized);

            if (!recursive)
            {
                return;
            }

            var prefix = normalized + "/";
            foreach (var key in _nodes.Keys
                         .Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                _nodes.Remove(key);
            }
        }
    }

    public void Move(string source, string destination)
    {
        lock (_gate)
        {
            MoveLocked(source, destination);
        }
    }

    private void MoveLocked(string source, string destination)
    {
        var from = Normalize(source);
        var to = Normalize(destination);

        if (!_nodes.TryGetValue(from, out var node))
        {
            return;
        }

        _nodes[to] = node;
        _nodes.Remove(from);

        var prefix = from + "/";
        foreach (var key in _nodes.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _nodes[to + key[from.Length..]] = _nodes[key];
            _nodes.Remove(key);
        }
    }

    public void Copy(string source, string destination)
    {
        lock (_gate)
        {
            var from = Normalize(source);
            var to = Normalize(destination);

            if (_nodes.TryGetValue(from, out var node))
            {
                _nodes[to] = new FakeNode
                {
                    IsDirectory = node.IsDirectory,
                    SymlinkTarget = node.SymlinkTarget,
                    Content = node.Content.ToArray(),
                    Mode = node.Mode,
                    ModifiedUnix = node.ModifiedUnix,
                };
            }
        }
    }

    public string Sha256(string path)
    {
        var content = Resolve(path)?.Content ?? [];
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }

    /// <summary>
    /// Canonicalizes a path, resolving the <c>/sdcard</c> symlink for anything <em>below</em> it.
    /// </summary>
    /// <remarks>
    /// This is what a real device does, and it matters: <c>/sdcard/DCIM</c> and
    /// <c>/storage/emulated/0/DCIM</c> are the same directory. The symlink node itself is left alone so
    /// that stat-ting <c>/sdcard</c> still exercises symlink resolution in the client.
    /// </remarks>
    public static string Canonical(string path) => Normalize(path);

    private static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return "/";
        }

        const string sdcard = "/sdcard";
        if (trimmed.StartsWith(sdcard + "/", StringComparison.Ordinal))
        {
            return "/storage/emulated/0" + trimmed[sdcard.Length..];
        }

        return trimmed;
    }
}

/// <summary>
/// A minimal emulation of the device shell commands this client actually issues.
/// </summary>
/// <remarks>
/// Only the commands the production code uses are supported, and the argument parser honours the
/// single-quoting that <see cref="ShellQuote"/> produces — which means a quoting bug shows up here as
/// a failed command rather than passing silently.
/// </remarks>
internal static class FakeShell
{
    /// <summary>
    /// Runs a command. Returns text to send back, or null when the command has already written to the
    /// stream itself (as <c>cat</c> and <c>dd if=</c> do).
    /// </summary>
    public static async Task<string?> ExecuteAsync(
        FakeAdbServer server,
        string command,
        Stream stream,
        CancellationToken token)
    {
        var files = server.Files;

        // Pipelines: the only one used is `echo -n '' | sha256sum`, for capability probing.
        if (command.Contains('|'))
        {
            return server.Faults.NoSha256Sum
                ? "sha256sum: not found"
                : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([])).ToLowerInvariant()
                  + "  -\n";
        }

        // Append redirection, as used by the resumable-upload path. The adb exec: service runs commands
        // through a shell, so redirection is the shell's job — the fake has to model it to be faithful.
        var appendAt = command.IndexOf(">>", StringComparison.Ordinal);
        if (appendAt >= 0)
        {
            var left = Tokenize(command[..appendAt]);
            var right = Tokenize(command[(appendAt + 2)..]);

            if (left.Count < 2 || right.Count < 1 || left[0] != "cat")
            {
                return "sh: unsupported redirection\n";
            }

            var input = files.Resolve(left[1]);
            if (input is null)
            {
                return "cat: no such file or directory\n";
            }

            var existing = files.Resolve(right[0])?.Content ?? [];
            var combined = new byte[existing.Length + input.Content.Length];
            existing.CopyTo(combined, 0);
            input.Content.CopyTo(combined, existing.Length);

            files.WriteFile(right[0], combined, 0);
            return string.Empty;
        }

        var arguments = Tokenize(command);
        if (arguments.Count == 0)
        {
            return string.Empty;
        }

        switch (arguments[0])
        {
            case "getprop":
                return arguments.Count > 1 ? Getprop(arguments[1]) : string.Empty;

            case "cat":
                if (arguments.Count > 1 && arguments[1] == "/sys/class/power_supply/battery/capacity")
                {
                    return "87\n";
                }

                if (arguments.Count > 1 && files.Resolve(arguments[1]) is { IsDirectory: false } file)
                {
                    await stream.WriteAsync(file.Content, token).ConfigureAwait(false);
                    return null;
                }

                return "cat: no such file or directory\n";

            case "head":
                {
                    // Only `head -c N <path>` is used, for prefix reads.
                    var count = arguments.Contains("-c") && arguments.Count > 2
                        ? int.Parse(arguments[arguments.IndexOf("-c") + 1])
                        : 0;

                    var node = files.Resolve(arguments[^1]);
                    if (node is null || node.IsDirectory)
                    {
                        return "head: no such file or directory\n";
                    }

                    var length = Math.Min(count, node.Content.Length);
                    await stream.WriteAsync(node.Content.AsMemory(0, length), token).ConfigureAwait(false);
                    return null;
                }

            case "stat":
                return Stat(files, arguments);

            case "mkdir":
                {
                    var path = arguments[^1];
                    if (files.Exists(path))
                    {
                        return string.Empty; // mkdir -p is idempotent
                    }

                    if (IsProtected(path))
                    {
                        return "mkdir: permission denied\n";
                    }

                    files.AddDirectory(path);
                    return string.Empty;
                }

            case "rm":
                {
                    var path = arguments[^1];
                    if (IsProtected(path))
                    {
                        return "rm: permission denied\n";
                    }

                    files.Delete(path, arguments.Any(a => a.Contains('r')));
                    return string.Empty;
                }

            case "mv":
                {
                    if (IsProtected(arguments[^1]))
                    {
                        return "mv: permission denied\n";
                    }

                    files.Move(arguments[^2], arguments[^1]);
                    return string.Empty;
                }

            case "cp":
                files.Copy(arguments[^2], arguments[^1]);
                return string.Empty;

            case "sha256sum":
                if (server.Faults.NoSha256Sum)
                {
                    return "sha256sum: not found\n";
                }

                return files.Exists(arguments[^1])
                    ? $"{files.Sha256(arguments[^1])}  {arguments[^1]}\n"
                    : "sha256sum: no such file or directory\n";

            case "dd":
                return await DdAsync(server, arguments, stream, token).ConfigureAwait(false);

            case "dumpsys":
                return "  level: 87\n";

            case "truncate":
                return string.Empty;

            default:
                return $"{arguments[0]}: not found\n";
        }
    }

    /// <summary>
    /// Emulates the two <c>dd</c> forms the resume paths use: reading from a block offset, and writing
    /// at a block offset without truncating (spec §13).
    /// </summary>
    private static async Task<string?> DdAsync(
        FakeAdbServer server,
        List<string> arguments,
        Stream stream,
        CancellationToken token)
    {
        string? input = null, output = null;
        long blockSize = 512, skip = 0, seek = 0, count = -1;
        var noTrunc = false;

        foreach (var argument in arguments.Skip(1))
        {
            var split = argument.Split('=', 2);
            if (split.Length != 2)
            {
                continue;
            }

            switch (split[0])
            {
                case "if": input = split[1]; break;
                case "of": output = split[1]; break;
                case "bs": blockSize = long.Parse(split[1]); break;
                case "skip": skip = long.Parse(split[1]); break;
                case "seek": seek = long.Parse(split[1]); break;
                case "count": count = long.Parse(split[1]); break;
                case "conv": noTrunc = split[1].Contains("notrunc"); break;
            }
        }

        if (input is not null)
        {
            var node = server.Files.Resolve(input);
            if (node is null)
            {
                return "dd: no such file or directory\n";
            }

            var offset = skip * blockSize;
            if (offset >= node.Content.LongLength)
            {
                return null;
            }

            var remaining = node.Content.AsMemory((int)offset);

            // `count=` bounds the read, which is how interior range reads are expressed.
            if (count >= 0)
            {
                remaining = remaining[..(int)Math.Min(remaining.Length, count * blockSize)];
            }

            // Honour the mid-transfer drop so resumed transfers can also be interrupted.
            if (server.Faults.DropAfterBytes is { } limit && limit < remaining.Length)
            {
                await stream.WriteAsync(remaining[..(int)limit], token).ConfigureAwait(false);
                ((NetworkStream)stream).Socket.Close();
                return null;
            }

            await stream.WriteAsync(remaining, token).ConfigureAwait(false);
            return null;
        }

        if (output is not null)
        {
            if (noTrunc && server.Faults.DdRejectsNoTrunc)
            {
                return "dd: unknown conversion notrunc\n";
            }

            // Read stdin until the client half-closes, which is how it signals end of input.
            var buffer = new MemoryStream();
            var scratch = new byte[64 * 1024];

            while (true)
            {
                var read = await stream.ReadAsync(scratch, token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                buffer.Write(scratch, 0, read);
            }

            server.Files.WriteAt(output, seek * blockSize, buffer.ToArray());
            return string.Empty;
        }

        return "dd: missing operand\n";
    }

    private static string Stat(FakeFileSystem files, List<string> arguments)
    {
        // Only `stat -f -c '%b %a %S' <path>` is used, for storage capacity.
        if (!arguments.Contains("-f"))
        {
            return "stat: unsupported\n";
        }

        var path = arguments[^1];
        if (!files.Exists(path))
        {
            return "stat: no such file or directory\n";
        }

        // 256 GB total, 82 GB free, in 4 KiB blocks.
        const long blockSize = 4096;
        var total = 256L * 1024 * 1024 * 1024 / blockSize;
        var free = 82L * 1024 * 1024 * 1024 / blockSize;
        return $"{total} {free} {blockSize}\n";
    }

    private static string Getprop(string key) => key switch
    {
        "ro.product.manufacturer" => "samsung\n",
        "ro.product.model" => "SM-S928B\n",
        "ro.build.version.release" => "16\n",
        "ro.build.version.sdk" => "36\n",
        _ => "\n",
    };

    private static bool IsProtected(string path)
        => path.StartsWith("/data", StringComparison.Ordinal)
           || path.StartsWith("/system", StringComparison.Ordinal);

    /// <summary>
    /// Splits a command line, honouring the single-quoting produced by <see cref="ShellQuote"/>
    /// including its <c>'\''</c> escape.
    /// </summary>
    private static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var any = false;

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];

            if (c == '\'')
            {
                // The escape sequence '\'' closes, emits a literal quote, and reopens.
                if (inQuotes && i + 3 < command.Length && command[i + 1] == '\\' && command[i + 2] == '\''
                    && command[i + 3] == '\'')
                {
                    current.Append('\'');
                    i += 3;
                    continue;
                }

                inQuotes = !inQuotes;
                any = true;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0 || any)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    any = false;
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0 || any)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
