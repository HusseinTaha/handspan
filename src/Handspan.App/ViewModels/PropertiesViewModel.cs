using System.Collections.ObjectModel;
using Handspan.Core.Exceptions;
using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Handspan.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Handspan.App.ViewModels;

/// <summary>One labelled row in the properties dialog.</summary>
public sealed record PropertyRow(string Label, string Value);

/// <summary>
/// File details, including EXIF where present (spec §33).
/// </summary>
/// <remarks>
/// Location is handled in two stages on purpose. Whether a photo carries GPS is shown at once, because that is
/// what someone needs to know before sharing it. The coordinates themselves are only fetched when asked for,
/// and are never written to the index or the log (spec §43).
/// </remarks>
public sealed partial class PropertiesViewModel(EntryRowViewModel entry, IDeviceSession session)
    : ViewModelBase
{
    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _hasLocation;

    [ObservableProperty]
    private string? _locationText;

    [ObservableProperty]
    private bool _isLocationRevealed;

    public string Name => entry.Name;

    public string FullPath => entry.Path.Value;

    /// <summary>General facts, available from the listing without reading the file.</summary>
    public ObservableCollection<PropertyRow> General { get; } = [];

    /// <summary>Camera details, only present for photos that carry EXIF.</summary>
    public ObservableCollection<PropertyRow> Camera { get; } = [];

    public bool HasCamera => Camera.Count > 0;

    public async Task LoadAsync()
    {
        General.Add(new PropertyRow("Type", entry.TypeText));

        if (!entry.IsDirectory)
        {
            General.Add(new PropertyRow("Size",
                entry.Entry.IsSizeKnown
                    ? $"{FormatSize.Bytes(entry.Entry.Size)} ({entry.Entry.Size:N0} bytes)"
                    : "unknown"));
        }

        General.Add(new PropertyRow("Modified", entry.ModifiedText));
        General.Add(new PropertyRow("Permissions", DescribePermissions()));

        if (entry.IsDirectory)
        {
            IsLoading = false;
            return;
        }

        try
        {
            var info = await session.FileSystem.GetInfoAsync(entry.Path, CancellationToken.None)
                .ConfigureAwait(true);

            if (info.OwnerUserId is { } uid)
            {
                General.Add(new PropertyRow("Owner", $"uid {uid}"));
            }

            var metadata = await session.Metadata.GetMetadataAsync(entry.Path, CancellationToken.None)
                .ConfigureAwait(true);

            if (metadata.MimeType is { } mime)
            {
                General.Insert(1, new PropertyRow("Format", mime));
            }

            if (metadata.Resolution is { } resolution)
            {
                General.Add(new PropertyRow("Resolution", resolution));
            }

            if (metadata.Duration is { } duration)
            {
                General.Add(new PropertyRow("Duration", Describe(duration)));
            }

            if (metadata.Exif is { } exif)
            {
                AddCameraRows(exif);

                HasLocation = exif.HasGpsCoordinates;
                LocationText = exif.HasGpsCoordinates
                    ? "This photo contains location data."
                    : null;
            }
        }
        catch (DeviceException ex)
        {
            General.Add(new PropertyRow("Details", ex.UserMessage));
        }
        finally
        {
            OnPropertyChanged(nameof(HasCamera));
            IsLoading = false;
        }
    }

    private void AddCameraRows(ExifMetadata exif)
    {
        if (exif.DateTaken is { } taken)
        {
            Camera.Add(new PropertyRow("Date taken", taken.ToLocalTime().ToString("f")));
        }

        var camera = string.Join(' ', new[] { exif.CameraMake, exif.CameraModel }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

        if (camera.Length > 0)
        {
            Camera.Add(new PropertyRow("Camera", camera));
        }

        if (exif.LensModel is { } lens)
        {
            Camera.Add(new PropertyRow("Lens", lens));
        }

        if (exif.FNumber is { } aperture)
        {
            Camera.Add(new PropertyRow("Aperture", $"f/{aperture:0.#}"));
        }

        if (exif.ExposureTime is { } exposure)
        {
            Camera.Add(new PropertyRow("Exposure", exposure));
        }

        if (exif.IsoSpeed is { } iso)
        {
            Camera.Add(new PropertyRow("ISO", iso.ToString()));
        }

        if (exif.FocalLength is { } focal)
        {
            Camera.Add(new PropertyRow("Focal length", $"{focal:0.#} mm"));
        }
    }

    /// <summary>
    /// Fetches and shows the coordinates, only when the user asks (spec §33, §43).
    /// </summary>
    [RelayCommand]
    private async Task RevealLocationAsync()
    {
        if (!HasLocation || IsLocationRevealed)
        {
            return;
        }

        try
        {
            // A second read, with the flag set: coordinates are never carried around unasked.
            if (session.Metadata is MetadataService service)
            {
                var detailed = await service
                    .GetMetadataAsync(entry.Path, includeGpsCoordinates: true, CancellationToken.None)
                    .ConfigureAwait(true);

                if (detailed.Exif?.GpsCoordinates is { } coordinates)
                {
                    LocationText = $"{coordinates.Latitude:0.######}, {coordinates.Longitude:0.######}";
                    IsLocationRevealed = true;
                }
            }
        }
        catch (DeviceException ex)
        {
            LocationText = ex.UserMessage;
        }
    }

    private string DescribePermissions()
    {
        var mode = entry.Entry.Mode;

        if (mode == 0)
        {
            return "unknown";
        }

        Span<char> result = stackalloc char[9];
        const string flags = "rwx";

        for (var i = 0; i < 9; i++)
        {
            result[i] = (mode & (1 << (8 - i))) != 0 ? flags[i % 3] : '-';
        }

        return new string(result);
    }

    private static string Describe(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
        : $"{duration.Minutes}:{duration.Seconds:00}";
}
