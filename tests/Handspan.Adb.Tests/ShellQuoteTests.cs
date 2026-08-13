using Handspan.Core.Models;

namespace Handspan.Adb.Tests;

/// <summary>
/// Shell quoting is the only barrier between an Android filename and a device command line (spec §71).
/// Android filenames legitimately contain every character below, so these are not hypothetical inputs.
/// </summary>
public class ShellQuoteTests
{
    [Theory]
    [InlineData("photo.jpg", "'photo.jpg'")]
    [InlineData("file with spaces.jpg", "'file with spaces.jpg'")]
    [InlineData("dollar$sign", "'dollar$sign'")]
    [InlineData("back`tick`", "'back`tick`'")]
    [InlineData("semi;colon", "'semi;colon'")]
    [InlineData("pipe|char", "'pipe|char'")]
    [InlineData("amper&sand", "'amper&sand'")]
    [InlineData("redirect>file", "'redirect>file'")]
    [InlineData("glob*star?", "'glob*star?'")]
    [InlineData("صور العائلة", "'صور العائلة'")]
    [InlineData("旅行 🌴", "'旅行 🌴'")]
    public void Wraps_arguments_in_single_quotes(string input, string expected)
        => Assert.Equal(expected, ShellQuote.Quote(input));

    [Fact]
    public void Escapes_embedded_single_quotes()
    {
        // The standard POSIX dance: close the quote, emit an escaped quote, reopen.
        Assert.Equal(@"'it'\''s mine.jpg'", ShellQuote.Quote("it's mine.jpg"));
        Assert.Equal(@"''\'''", ShellQuote.Quote("'"));
        Assert.Equal(@"'a'\''b'\''c'", ShellQuote.Quote("a'b'c"));
    }

    [Fact]
    public void Neutralizes_command_injection_attempts()
    {
        // A file really can be named this. Quoting must make it inert rather than executable.
        foreach (var hostile in new[]
                 {
                     "; rm -rf /",
                     "$(rm -rf /)",
                     "`rm -rf /`",
                     "&& reboot",
                     "| cat /etc/passwd",
                     "\n rm -rf /",
                 })
        {
            var quoted = ShellQuote.Quote(hostile);

            Assert.StartsWith("'", quoted, StringComparison.Ordinal);
            Assert.EndsWith("'", quoted, StringComparison.Ordinal);

            // Every quote in the result is either the outer pair or part of an escape sequence, so no
            // unescaped quote can terminate the string early and let the payload out.
            var inner = quoted[1..^1];
            Assert.DoesNotContain("'", inner.Replace(@"'\''", string.Empty, StringComparison.Ordinal),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Newlines_stay_inside_the_quoted_string()
    {
        var quoted = ShellQuote.Quote("line1\nline2");

        Assert.Equal("'line1\nline2'", quoted);
    }

    [Fact]
    public void Quotes_device_paths()
    {
        var path = KnownPaths.Camera.Combine("it's a photo.jpg");

        Assert.Equal(@"'/sdcard/DCIM/Camera/it'\''s a photo.jpg'", ShellQuote.Quote(path));
    }

    [Fact]
    public void Builds_commands_with_every_argument_quoted()
    {
        var command = ShellQuote.Command("rm -rf", "/sdcard/a b", "/sdcard/it's");

        Assert.Equal(@"rm -rf '/sdcard/a b' '/sdcard/it'\''s'", command);
    }

    [Fact]
    public void Rejects_null()
        => Assert.Throws<ArgumentNullException>(() => ShellQuote.Quote((string)null!));
}
