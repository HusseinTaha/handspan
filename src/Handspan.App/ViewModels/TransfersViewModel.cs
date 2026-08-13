using System.Collections.ObjectModel;
using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Handspan.Core.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Handspan.App.ViewModels;

/// <summary>
/// The transfers page: active queue plus history (spec §85).
/// </summary>
/// <remarks>
/// Job events arrive on transfer worker threads and are marshalled onto the UI thread here. Progress
/// is already throttled by the transfer manager, so this view model does no additional rate limiting.
/// </remarks>
public sealed partial class TransfersViewModel : ViewModelBase
{
    private readonly IShellIntegration _shell;
    private ITransferManager? _transfers;

    [ObservableProperty]
    private string _summary = "No transfers yet.";

    public TransfersViewModel(IShellIntegration shell) => _shell = shell;

    public ObservableCollection<TransferRowViewModel> Active { get; } = [];

    public ObservableCollection<TransferRowViewModel> Completed { get; } = [];

    public bool HasActive => Active.Count > 0;

    public bool HasCompleted => Completed.Count > 0;

    public void Attach(ITransferManager transfers)
    {
        Detach();

        _transfers = transfers;
        transfers.JobChanged += OnJobChanged;

        foreach (var job in transfers.Jobs)
        {
            Apply(job, null);
        }

        UpdateSummary();
    }

    public void Detach()
    {
        if (_transfers is not null)
        {
            _transfers.JobChanged -= OnJobChanged;
            _transfers = null;
        }

        Active.Clear();
        Completed.Clear();
        UpdateSummary();
    }

    private void OnJobChanged(object? sender, TransferJobChangedEventArgs e)
        => Dispatcher.UIThread.Post(() => Apply(e.Job, e.Progress));

    private void Apply(TransferJob job, TransferProgress? progress)
    {
        var existing = Active.FirstOrDefault(row => row.Id == job.Id)
                       ?? Completed.FirstOrDefault(row => row.Id == job.Id);

        if (existing is null)
        {
            var row = new TransferRowViewModel(job, _transfers!);
            (job.IsTerminal ? Completed : Active).Insert(0, row);
        }
        else
        {
            existing.Update(job, progress);

            // A job that has just finished moves from the queue to history.
            if (job.IsTerminal && Active.Contains(existing))
            {
                Active.Remove(existing);
                Completed.Insert(0, existing);
            }
        }

        OnPropertyChanged(nameof(HasActive));
        OnPropertyChanged(nameof(HasCompleted));
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (_transfers is null)
        {
            Summary = "Connect a device to transfer files.";
            return;
        }

        var active = Active.Count;
        var failed = Completed.Count(row => row.IsFailed);

        Summary = active switch
        {
            0 when failed > 0 => $"All transfers finished · {failed} failed.",
            0 => Completed.Count > 0 ? "All transfers finished." : "No transfers yet.",
            1 => "1 transfer in progress.",
            _ => $"{active} transfers in progress.",
        };
    }

    [RelayCommand]
    private async Task PauseAllAsync()
    {
        if (_transfers is not null)
        {
            await _transfers.PauseAllAsync("Paused by you.").ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ResumeAllAsync()
    {
        if (_transfers is not null)
        {
            await _transfers.ResumeAllAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ClearCompletedAsync()
    {
        if (_transfers is not null)
        {
            await _transfers.ClearCompletedAsync().ConfigureAwait(true);
        }

        Completed.Clear();
        OnPropertyChanged(nameof(HasCompleted));
        UpdateSummary();
    }

    [RelayCommand]
    private Task RevealAsync(TransferRowViewModel? row)
        => row is null ? Task.CompletedTask : _shell.RevealInFileManagerAsync(row.LocalPath);
}

/// <summary>One transfer, with its live progress (spec §11).</summary>
public sealed partial class TransferRowViewModel : ViewModelBase
{
    private readonly ITransferManager _transfers;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _speedText = string.Empty;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _canPause;

    [ObservableProperty]
    private bool _canResume;

    [ObservableProperty]
    private bool _canCancel;

    [ObservableProperty]
    private bool _canRetry;

    [ObservableProperty]
    private bool _isFailed;

    public TransferRowViewModel(TransferJob job, ITransferManager transfers)
    {
        _transfers = transfers;
        Id = job.Id;
        FileName = job.FileName;
        DirectionGlyph = job.Direction == TransferDirection.Download ? "↓" : "↑";
        LocalPath = job.LocalPath;
        RemotePath = job.RemotePath.Value;
        Update(job, null);
    }

    public Guid Id { get; }

    public string FileName { get; }

    public string DirectionGlyph { get; }

    public string LocalPath { get; }

    public string RemotePath { get; }

    public void Update(TransferJob job, TransferProgress? progress)
    {
        Percent = job.Fraction * 100;
        IsFailed = job.Status == TransferStatus.Failed;

        StatusText = job.Status switch
        {
            TransferStatus.Queued => "Waiting",
            TransferStatus.Preparing => "Preparing",
            TransferStatus.Transferring => "Transferring",
            TransferStatus.Paused => "Paused",
            TransferStatus.Completed => "Completed",
            TransferStatus.Failed => "Failed",
            TransferStatus.Cancelled => "Cancelled",
            TransferStatus.Retrying => $"Retrying (attempt {job.RetryCount})",
            _ => job.Status.ToString(),
        };

        ProgressText = job.TotalBytes > 0
            ? $"{FormatSize.Bytes(job.BytesTransferred)} of {FormatSize.Bytes(job.TotalBytes)}"
            : FormatSize.Bytes(job.BytesTransferred);

        if (progress is { BytesPerSecond: > 0 } sample)
        {
            var eta = sample.EstimatedRemaining;
            SpeedText = eta is { } remaining
                ? $"{FormatSize.Bytes((long)sample.BytesPerSecond)}/s · {Describe(remaining)} left"
                : $"{FormatSize.Bytes((long)sample.BytesPerSecond)}/s";
        }
        else if (job.IsTerminal)
        {
            SpeedText = string.Empty;
        }

        Error = job.Error;

        CanPause = job.Status is TransferStatus.Transferring or TransferStatus.Queued;
        CanResume = job.Status is TransferStatus.Paused;
        CanCancel = !job.IsTerminal;
        CanRetry = job.Status is TransferStatus.Failed or TransferStatus.Cancelled;
    }

    private static string Describe(TimeSpan span) => span switch
    {
        { TotalSeconds: < 60 } => $"{span.Seconds}s",
        { TotalMinutes: < 60 } => $"{span.Minutes}m {span.Seconds}s",
        _ => $"{(int)span.TotalHours}h {span.Minutes}m",
    };

    [RelayCommand]
    private Task PauseAsync() => _transfers.PauseAsync(Id);

    [RelayCommand]
    private Task ResumeAsync() => _transfers.ResumeAsync(Id);

    [RelayCommand]
    private Task CancelAsync() => _transfers.CancelAsync(Id);

    [RelayCommand]
    private Task RetryAsync() => _transfers.RetryAsync(Id);
}
