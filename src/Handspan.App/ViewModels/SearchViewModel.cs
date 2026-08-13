using System.Collections.ObjectModel;
using Handspan.Core.Exceptions;
using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Handspan.App.ViewModels;

/// <summary>
/// Device-wide search over the local index (spec §27).
/// </summary>
/// <remarks>
/// Queries hit the index, never the device, so results appear as the user types. If the device has never been
/// indexed the page says so and offers to build it, rather than silently returning nothing.
/// </remarks>
public sealed partial class SearchViewModel : ViewModelBase
{
    private IDeviceSession? _session;
    private CancellationTokenSource? _search;
    private CancellationTokenSource? _index;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private string _status = "Connect a device to search it.";

    [ObservableProperty]
    private bool _isIndexing;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _includeImages;

    [ObservableProperty]
    private bool _includeVideos;

    [ObservableProperty]
    private bool _includeAudio;

    [ObservableProperty]
    private bool _includeDocuments;

    /// <summary>0 any · 1 under 10 MB · 2 10–100 MB · 3 over 100 MB (spec §27).</summary>
    [ObservableProperty]
    private int _sizeFilterIndex;

    /// <summary>0 any · 1 today · 2 this week · 3 this month.</summary>
    [ObservableProperty]
    private int _dateFilterIndex;

    public ObservableCollection<EntryRowViewModel> Results { get; } = [];

    public bool HasSession => _session is not null;

    public bool HasResults => Results.Count > 0;

    public async Task AttachAsync(IDeviceSession session)
    {
        _session = session;
        OnPropertyChanged(nameof(HasSession));
        await UpdateIndexStatusAsync().ConfigureAwait(true);
    }

    public void Detach()
    {
        _search?.Cancel();
        _index?.Cancel();
        _session = null;
        Results.Clear();
        Status = "Connect a device to search it.";
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(HasResults));
    }

    private async Task UpdateIndexStatusAsync()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            var count = await _session.Search.GetIndexedCountAsync(CancellationToken.None)
                .ConfigureAwait(true);
            var last = await _session.Search.GetLastIndexedAsync(CancellationToken.None)
                .ConfigureAwait(true);

            Status = count == 0
                ? "This device has not been indexed yet. Build the index to search it."
                : $"{count:N0} files indexed"
                  + (last is { } when ? $" · last scan {when.ToLocalTime():g}" : string.Empty);
        }
        catch (DeviceException ex)
        {
            Status = ex.UserMessage;
        }
    }

    partial void OnQueryChanged(string value) => _ = RunSearchAsync();

    partial void OnIncludeImagesChanged(bool value) => _ = RunSearchAsync();

    partial void OnIncludeVideosChanged(bool value) => _ = RunSearchAsync();

    partial void OnIncludeAudioChanged(bool value) => _ = RunSearchAsync();

    partial void OnIncludeDocumentsChanged(bool value) => _ = RunSearchAsync();

    partial void OnSizeFilterIndexChanged(int value) => _ = RunSearchAsync();

    partial void OnDateFilterIndexChanged(int value) => _ = RunSearchAsync();

    [RelayCommand]
    private async Task RunSearchAsync()
    {
        if (_session is null)
        {
            return;
        }

        var text = Query.Trim();

        if (text.Length == 0)
        {
            Results.Clear();
            OnPropertyChanged(nameof(HasResults));
            await UpdateIndexStatusAsync().ConfigureAwait(true);
            return;
        }

        // Supersede the previous keystroke's query rather than racing it.
        _search?.Cancel();
        _search = new CancellationTokenSource();
        var cancellationToken = _search.Token;

        IsSearching = true;
        try
        {
            var kinds = new List<MediaKind>();
            if (IncludeImages)
            {
                kinds.Add(MediaKind.Image);
            }

            if (IncludeVideos)
            {
                kinds.Add(MediaKind.Video);
            }

            if (IncludeAudio)
            {
                kinds.Add(MediaKind.Audio);
            }

            if (IncludeDocuments)
            {
                kinds.Add(MediaKind.Document);
            }

            var (minSize, maxSize) = SizeFilterIndex switch
            {
                1 => (null, (long?)10L * 1024 * 1024),
                2 => ((long?)10L * 1024 * 1024, (long?)100L * 1024 * 1024),
                3 => ((long?)100L * 1024 * 1024, null),
                _ => (null, null),
            };

            DateTimeOffset? after = DateFilterIndex switch
            {
                1 => DateTimeOffset.Now.Date,
                2 => DateTimeOffset.Now.Date.AddDays(-7),
                3 => DateTimeOffset.Now.Date.AddMonths(-1),
                _ => null,
            };

            var results = await _session.Search.SearchAsync(new SearchQuery
            {
                Text = text,
                Kinds = kinds,
                MinSize = minSize,
                MaxSize = maxSize,
                ModifiedAfter = after,
                IncludeDirectories = true,
            }, cancellationToken).ConfigureAwait(true);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            Results.Clear();
            foreach (var entry in results)
            {
                Results.Add(new EntryRowViewModel(entry));
            }

            OnPropertyChanged(nameof(HasResults));

            Status = results.Count switch
            {
                0 => $"No matches for \"{text}\". If the device was changed recently, rebuild the index.",
                1 => "1 match",
                var count => $"{count} matches" + (count >= 500 ? " (showing the first 500)" : string.Empty),
            };
        }
        catch (OperationCanceledException)
        {
            // A newer query owns the results now.
        }
        catch (DeviceException ex)
        {
            Status = ex.UserMessage;
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task BuildIndexAsync()
    {
        if (_session is null || IsIndexing)
        {
            return;
        }

        _index?.Cancel();
        _index = new CancellationTokenSource();

        IsIndexing = true;
        try
        {
            var progress = new Progress<IndexProgress>(update => Dispatcher.UIThread.Post(() =>
                Status = $"Indexing… {update.FilesIndexed:N0} files across "
                         + $"{update.DirectoriesScanned:N0} folders"));

            await _session.Search.IndexAsync(progress, _index.Token).ConfigureAwait(true);
            await UpdateIndexStatusAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Status = "Indexing cancelled.";
        }
        catch (DeviceException ex)
        {
            Status = ex.UserMessage;
        }
        finally
        {
            IsIndexing = false;
        }
    }

    [RelayCommand]
    private void CancelIndex() => _index?.Cancel();
}
