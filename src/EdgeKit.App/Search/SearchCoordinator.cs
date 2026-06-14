namespace EdgeKit.App.Search;

public sealed class SearchCoordinator
{
    private readonly SearchEngine _localSearchEngine;
    private readonly EverythingSearchService _everythingSearchService;

    public SearchCoordinator(SearchEngine localSearchEngine, EverythingSearchService everythingSearchService)
    {
        _localSearchEngine = localSearchEngine;
        _everythingSearchService = everythingSearchService;
    }

    public event EventHandler? EverythingStateChanged
    {
        add => _everythingSearchService.StateChanged += value;
        remove => _everythingSearchService.StateChanged -= value;
    }

    public EverythingSearchState EverythingState => _everythingSearchService.State;

    public bool IsEverythingBusy => _everythingSearchService.IsBusy;

    public void SetEverythingEnabled(bool enabled)
    {
        _everythingSearchService.SetEnabled(enabled);
    }

    public Task<IReadOnlyList<SearchItem>> SearchAsync(string query, bool useEverything, CancellationToken cancellationToken)
    {
        if (useEverything)
        {
            return _everythingSearchService.SearchAsync(query, cancellationToken);
        }

        var directAction = SearchEngine.TryCreateDirectAction(query);
        if (directAction is not null)
        {
            return Task.FromResult<IReadOnlyList<SearchItem>>(new[] { directAction });
        }

        return Task.Run<IReadOnlyList<SearchItem>>(() => _localSearchEngine.Search(query), cancellationToken);
    }
}
