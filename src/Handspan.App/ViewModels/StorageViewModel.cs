using System.Collections.ObjectModel;
using Handspan.Core.Exceptions;
using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Handspan.App.ViewModels;

/// <summary>
/// Storage analysis and duplicate detection (spec §61–§63).
/// </summary>
/// <remarks>
/// Everything is computed from the search index, so it needs a crawl first but then costs nothing. The
/// unaccounted figure is shown explicitly rather than folded into a category, because the index can only
/// see storage Android lets us read.
/// </remarks>
public sealed partial class StorageViewModel : ViewModelBase
{
    private IDeviceSession? _session;
    private CancellationTokenSource? _work;

    [ObservableProperty]
    private string _status = "Connect a device to analyze its storage.";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _volumeSummary = string.Empty;

    [ObservableProperty]
    private string _unaccountedSummary = string.Empty;

    [ObservableProperty]
    private bool _verifyDuplicatesWithHash;

    [ObservableProperty]
    private string _duplicateSummary = string.Empty;

    [ObservableProperty]
    private bool _isConfirmOpen;

    [ObservableProperty]
    private string _confirmMessage = string.Empty;

    private Func<Task>? _confirmAction;

    public ObservableCollection<StorageCategoryViewModel> Categories { get; } = [];

    public ObservableCollection<EntryRowViewModel> LargestFiles { get; } = [];

    public ObservableCollection<DuplicateGroupViewModel> Duplicates { get; } = [];

    public bool HasSession => _session is not null;

    public bool HasCategories => Categories.Count > 0;

    public bool HasDuplicates => Duplicates.Count > 0;

    public async Task AttachAsync(IDeviceSession session)
    {
        _session = session;
        OnPropertyChanged(nameof(HasSession));
        await RefreshAsync().ConfigureAwait(true);
    }

    public void Detach()
    {
        _work?.Cancel();
        _session = null;
        Categories.Clear();
        LargestFiles.Clear();
        Duplicates.Clear();
        VolumeSummary = string.Empty;
        UnaccountedSummary = string.Empty;
        DuplicateSummary = string.Empty;
        Status = "Connect a device to analyze its storage.";
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(HasCategories));
        OnPropertyChanged(nameof(HasDuplicates));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_session is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var breakdown = await _session.Storage.AnalyzeAsync(CancellationToken.None)
                .ConfigureAwait(true);

            Categories.Clear();

            var largest = breakdown.Categories.Count > 0
                ? breakdown.Categories.Max(category => category.Bytes)
                : 0;

            foreach (var category in breakdown.Categories)
            {
                Categories.Add(new StorageCategoryViewModel(category, largest));
            }

            VolumeSummary = breakdown.Volume is { } volume
                ? $"{FormatSize.Bytes(volume.UsedBytes)} used of {FormatSize.Bytes(volume.TotalBytes)} · "
                  + $"{FormatSize.Bytes(volume.FreeBytes)} free"
                : "The device did not report its capacity.";

            // Named honestly: app data and protected areas are not readable, so they cannot be categorized.
            UnaccountedSummary = breakdown.UnaccountedBytes > 0
                ? $"{FormatSize.Bytes(breakdown.UnaccountedBytes)} is used by apps and areas Android does "
                  + "not allow reading, so it cannot be broken down here."
                : string.Empty;

            Status = breakdown.IndexedFiles == 0
                ? "Nothing indexed yet. Build the index on the Search page first."
                : $"{breakdown.IndexedFiles:N0} files · {FormatSize.Bytes(breakdown.IndexedBytes)} indexed";

            LargestFiles.Clear();
            foreach (var file in await _session.Storage
                         .GetLargestFilesAsync(25, 50L * 1024 * 1024, CancellationToken.None)
                         .ConfigureAwait(true))
            {
                LargestFiles.Add(new EntryRowViewModel(file));
            }

            OnPropertyChanged(nameof(HasCategories));
        }
        catch (DeviceException ex)
        {
            Status = ex.UserMessage;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task FindDuplicatesAsync()
    {
        if (_session is null || IsBusy)
        {
            return;
        }

        _work?.Cancel();
        _work = new CancellationTokenSource();

        IsBusy = true;
        Duplicates.Clear();
        DuplicateSummary = "Scanning for duplicates…";

        try
        {
            var progress = new Progress<string>(message =>
                Dispatcher.UIThread.Post(() => DuplicateSummary = message));

            var groups = await _session.Duplicates.FindAsync(
                new DuplicateSearchOptions
                {
                    MinimumBytes = 64 * 1024,
                    VerifyWithFullHash = VerifyDuplicatesWithHash,
                },
                progress,
                _work.Token).ConfigureAwait(true);

            foreach (var group in groups)
            {
                Duplicates.Add(new DuplicateGroupViewModel(group));
            }

            var reclaimable = groups.Sum(group => group.ReclaimableBytes);

            DuplicateSummary = groups.Count == 0
                ? "No duplicates found."
                : $"{groups.Count} group{(groups.Count == 1 ? "" : "s")} · "
                  + $"{FormatSize.Bytes(reclaimable)} could be freed by keeping one copy of each";

            OnPropertyChanged(nameof(HasDuplicates));
        }
        catch (OperationCanceledException)
        {
            DuplicateSummary = "Duplicate scan cancelled.";
        }
        catch (DeviceException ex)
        {
            DuplicateSummary = ex.UserMessage;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _work?.Cancel();

    // --- acting on the findings (spec §51, §61, §63) ---

    /// <summary>
    /// Deletes one of the largest files, after confirmation.
    /// </summary>
    /// <remarks>
    /// An analyser that can only describe the problem is half a tool: the point of finding a 2 GB video is
    /// being able to remove it. Deletion is permanent (§51), so each one is confirmed individually.
    /// </remarks>
    [RelayCommand]
    private void DeleteFile(EntryRowViewModel? row)
    {
        if (_session is null || row is null)
        {
            return;
        }

        Confirm(
            $"Permanently delete \"{row.Name}\" ({row.SizeText}) from the device?",
            async () =>
            {
                await _session.FileSystem
                    .DeleteAsync(row.Path, recursive: false, CancellationToken.None)
                    .ConfigureAwait(true);

                LargestFiles.Remove(row);
                await RefreshAsync().ConfigureAwait(true);
            });
    }

    /// <summary>
    /// Deletes the extra copies in a duplicate group, keeping one (spec §61).
    /// </summary>
    /// <remarks>
    /// Keeping the first copy and removing the rest is the whole point of the feature, but it is also the
    /// riskiest action in the app — so the confirmation names the file that survives, and a group only
    /// verified by a partial hash says so.
    /// </remarks>
    [RelayCommand]
    private void DeleteDuplicates(DuplicateGroupViewModel? group)
    {
        if (_session is null || group is null || group.Paths.Count < 2)
        {
            return;
        }

        var keep = group.Paths[0];
        var remove = group.Paths.Skip(1).ToList();

        var caution = group.IsFullyVerified
            ? string.Empty
            : "\n\nThese were matched by size and a sampled comparison rather than a full hash. "
              + "Verify with full hashes first if you want certainty.";

        Confirm(
            $"Delete {remove.Count} extra cop{(remove.Count == 1 ? "y" : "ies")} and keep:\n{keep}{caution}",
            async () =>
            {
                var failures = 0;

                foreach (var path in remove)
                {
                    try
                    {
                        await _session.FileSystem
                            .DeleteAsync(DevicePath.Parse(path), recursive: false, CancellationToken.None)
                            .ConfigureAwait(true);
                    }
                    catch (DeviceException)
                    {
                        failures++;
                    }
                }

                Duplicates.Remove(group);
                OnPropertyChanged(nameof(HasDuplicates));

                DuplicateSummary = failures == 0
                    ? $"Removed {remove.Count} duplicate{(remove.Count == 1 ? "" : "s")}."
                    : $"Removed {remove.Count - failures}; {failures} could not be deleted.";

                await RefreshAsync().ConfigureAwait(true);
            });
    }

    private void Confirm(string message, Func<Task> action)
    {
        ConfirmMessage = message;
        _confirmAction = action;
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmActionAsync()
    {
        IsConfirmOpen = false;
        var action = _confirmAction;
        _confirmAction = null;

        if (action is null)
        {
            return;
        }

        try
        {
            await action().ConfigureAwait(true);
        }
        catch (DeviceException ex)
        {
            Status = ex.UserMessage;
        }
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        IsConfirmOpen = false;
        _confirmAction = null;
    }
}

/// <summary>One category bar in the breakdown (spec §62).</summary>
public sealed class StorageCategoryViewModel(StorageCategory category, long largestCategoryBytes)
{
    public string Label { get; } = category.Label;

    public string SizeText { get; } = FormatSize.Bytes(category.Bytes);

    public string CountText { get; } = $"{category.FileCount:N0} files";

    /// <summary>Scaled against the biggest category so the bars are comparable at a glance.</summary>
    public double BarPercent { get; } = largestCategoryBytes > 0
        ? category.Bytes * 100.0 / largestCategoryBytes
        : 0;
}

/// <summary>One duplicate group (spec §61).</summary>
public sealed class DuplicateGroupViewModel(DuplicateGroup group)
{
    public string Header { get; } =
        $"{group.Paths.Count} copies · {FormatSize.Bytes(group.Size)} each · "
        + $"{FormatSize.Bytes(group.ReclaimableBytes)} reclaimable";

    /// <summary>Stated plainly so the user knows how much to trust the grouping.</summary>
    public string ConfidenceText { get; } = group.Confidence switch
    {
        DuplicateConfidence.FullHash => "identical (verified by full hash)",
        DuplicateConfidence.PartialHash => "very likely identical (matching size and sampled content)",
        _ => "same size only",
    };

    public IReadOnlyList<string> Paths { get; } = group.Paths.Select(path => path.Value).ToList();

    /// <summary>True only when full hashes confirmed the group, which gates how the delete prompt reads.</summary>
    public bool IsFullyVerified { get; } = group.Confidence == DuplicateConfidence.FullHash;
}
