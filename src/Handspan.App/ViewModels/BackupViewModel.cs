using Handspan.App.Platform;
using Handspan.Core.Exceptions;
using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Handspan.Core.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Handspan.App.ViewModels;

/// <summary>
/// Incremental phone-to-PC backup of photos and videos (spec §92).
/// </summary>
/// <remarks>
/// The flagship reason to plug a phone into a computer. Everything is queued through the normal transfer
/// manager, so a backup is pausable, resumable and retried like any other transfer — a 60 GB first run over a
/// cable that drops has to survive being interrupted, and reinventing that here would be strictly worse.
/// </remarks>
public sealed partial class BackupViewModel : ViewModelBase
{
    private readonly IShellIntegration _shell;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<BackupViewModel> _logger;

    private IDeviceSession? _session;
    private BackupPlan? _plan;
    private int _remaining;

    [ObservableProperty]
    private string _status = "Connect a device to back it up.";

    [ObservableProperty]
    private string _destination = string.Empty;

    [ObservableProperty]
    private string _lastBackupText = string.Empty;

    [ObservableProperty]
    private string _planSummary = string.Empty;

    [ObservableProperty]
    private bool _isPlanning;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _hasPlan;

    [ObservableProperty]
    private double _progressPercent;

    /// <summary>Ignore the stored mark and offer everything, for a re-run onto a fresh disk.</summary>
    [ObservableProperty]
    private bool _backUpEverything;

    public BackupViewModel(
        IShellIntegration shell,
        IUiDispatcher dispatcher,
        ILogger<BackupViewModel> logger)
    {
        _shell = shell;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public bool HasSession => _session is not null;

    public async Task AttachAsync(IDeviceSession session)
    {
        Detach();
        _session = session;
        OnPropertyChanged(nameof(HasSession));

        session.Transfers.JobChanged += OnJobChanged;

        var (at, folder) = await session.Backup.GetLastBackupAsync(CancellationToken.None)
            .ConfigureAwait(true);

        Destination = folder ?? Path.Combine(
            _shell.GetDefaultDownloadFolder(), "Handspan Backup", session.Info.DisplayName);

        LastBackupText = at is { } when
            ? $"Last backup: {when.ToLocalTime():g}"
            : "This device has never been backed up.";

        Status = "Check for new photos and videos to see what would be copied.";
    }

    public void Detach()
    {
        if (_session is not null)
        {
            _session.Transfers.JobChanged -= OnJobChanged;
        }

        _session = null;
        _plan = null;
        HasPlan = false;
        PlanSummary = string.Empty;
        LastBackupText = string.Empty;
        ProgressPercent = 0;
        Status = "Connect a device to back it up.";
        OnPropertyChanged(nameof(HasSession));
    }

    /// <summary>Works out what is new, without copying anything (spec §34).</summary>
    [RelayCommand]
    private async Task CheckAsync()
    {
        if (_session is null || IsPlanning)
        {
            return;
        }

        IsPlanning = true;
        HasPlan = false;

        try
        {
            // The plan reads the media index, so it is only as current as the last gallery scan.
            await _session.Gallery.RefreshAsync(null, CancellationToken.None).ConfigureAwait(true);

            _plan = await _session.Backup
                .PlanAsync(null, BackUpEverything ? DateTimeOffset.MinValue : null, CancellationToken.None)
                .ConfigureAwait(true);

            HasPlan = !_plan.IsEmpty;

            if (_plan.IsEmpty)
            {
                PlanSummary = string.Empty;
                Status = _plan.Since is null
                    ? "No photos or videos found on the device."
                    : "Everything is already backed up.";
                return;
            }

            PlanSummary =
                $"{_plan.Items.Count:N0} new item{(_plan.Items.Count == 1 ? "" : "s")} · "
                + $"{FormatSize.Bytes(_plan.TotalBytes)}\n"
                + $"{_plan.PhotoCount:N0} photo{(_plan.PhotoCount == 1 ? "" : "s")}, "
                + $"{_plan.VideoCount:N0} video{(_plan.VideoCount == 1 ? "" : "s")}";

            Status = _plan.Since is { } since
                ? $"New since {since.ToLocalTime():g}. Files are grouped into year and month folders."
                : "First backup: everything on the device. Files are grouped into year and month folders.";
        }
        catch (DeviceException ex)
        {
            Status = ex.UserMessage;
            _logger.LogWarning("Backup planning failed: {Reason}", ex.UserMessage);
        }
        finally
        {
            IsPlanning = false;
        }
    }

    /// <summary>
    /// Queues the plan, one batch per destination folder (spec §11).
    /// </summary>
    /// <remarks>
    /// Grouped by capture month rather than queued as one flat batch, because the transfer manager takes a
    /// single destination per call and a backup must land in dated folders — 8,000 photos in one directory is
    /// unusable. Grouping also means far fewer calls than one per file.
    /// </remarks>
    [RelayCommand]
    private async Task RunAsync()
    {
        if (_session is null || _plan is null || _plan.IsEmpty || IsRunning)
        {
            return;
        }

        IsRunning = true;
        ProgressPercent = 0;

        try
        {
            var byFolder = _plan.Items
                .GroupBy(item => _session.Backup.GetRelativeFolder(item))
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToList();

            _remaining = _plan.Items.Count;
            var queued = 0;

            foreach (var group in byFolder)
            {
                var folder = Path.Combine(Destination, group.Key);
                Directory.CreateDirectory(folder);

                // Rename on conflict: a backup must never overwrite a file already saved.
                await _session.Transfers.EnqueueDownloadAsync(
                        group.Select(item => item.Path).ToList(),
                        folder,
                        ConflictPolicy.Rename,
                        CancellationToken.None)
                    .ConfigureAwait(true);

                queued += group.Count();
                Status = $"Queued {queued:N0} of {_plan.Items.Count:N0}…";
            }

            // The mark advances at queue time, which is a deliberate trade: the queue is journalled and
            // survives restarts, so anything queued will eventually arrive, whereas waiting for every job to
            // finish would leave the mark unset if the app closed mid-backup.
            await _session.Backup.RecordAsync(_plan, Destination, CancellationToken.None)
                .ConfigureAwait(true);

            var (at, _) = await _session.Backup.GetLastBackupAsync(CancellationToken.None)
                .ConfigureAwait(true);
            LastBackupText = at is { } when ? $"Last backup: {when.ToLocalTime():g}" : LastBackupText;

            Status = $"Backing up {_plan.Items.Count:N0} items into {Destination}. "
                     + "Progress and controls are on the Transfers page.";

            HasPlan = false;
        }
        catch (Exception ex) when (ex is DeviceException or IOException)
        {
            Status = ex is DeviceException device
                ? device.UserMessage
                : $"Could not write to the destination: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private Task OpenDestinationAsync()
        => Directory.Exists(Destination)
            ? _shell.RevealInFileManagerAsync(Destination)
            : Task.CompletedTask;

    private void OnJobChanged(object? sender, TransferJobChangedEventArgs e)
    {
        if (e.Job.Status != TransferStatus.Completed || e.Job.Direction != TransferDirection.Download)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            if (_remaining <= 0)
            {
                return;
            }

            _remaining--;
            var total = _plan?.Items.Count ?? 0;

            if (total > 0)
            {
                ProgressPercent = (total - _remaining) * 100.0 / total;
            }
        });
    }
}
