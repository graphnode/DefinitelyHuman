using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.JSInterop;

namespace DefinitelyHuman.Web.Components.Chat;

/// <summary>
/// A virtualized list for chat logs that tracks per-item heights instead of a global average.
/// Supports both <see cref="Items"/> (full in-memory list) and <see cref="ItemsProvider"/>
/// (paged on demand). Bottom-anchored: auto-follows new items when scrolled to the bottom.
/// </summary>
public sealed partial class ChatVirtualize<TItem> : IAsyncDisposable
{
    /// <summary>Full item list. Mutually exclusive with <see cref="ItemsProvider"/>.</summary>
    [Parameter]
    public IReadOnlyList<TItem>? Items { get; set; }

    /// <summary>Paged item source. Mutually exclusive with <see cref="Items"/>.</summary>
    [Parameter]
    public ItemsProviderDelegate<TItem>? ItemsProvider { get; set; }

    [Parameter, EditorRequired]
    public RenderFragment<TItem> ItemContent { get; set; } = default!;

    /// <summary>Estimated height (px) for items not yet measured by the ResizeObserver.</summary>
    [Parameter]
    public float ItemSize { get; set; } = 28f;

    /// <summary>Extra items rendered above and below the viewport (each direction).</summary>
    [Parameter]
    public int OverscanCount { get; set; } = 200;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    private DotNetObjectReference<ChatVirtualize<TItem>>? _dotNetRef;
    private IJSObjectReference? _jsModule;
    private int _jsInstanceId;
    private bool _jsReady;

    private ElementReference _spacerBefore;
    private ElementReference _spacerAfter;

    private readonly Dictionary<int, float> _itemHeights = new();
    private int _itemsBefore;
    private int _visibleItemCapacity;

    private bool _initialized;
    private int _knownItemCount;
    private bool _pendingScrollToBottom;

    // Provider-mode buffer
    private IReadOnlyList<TItem> _loadedItems = [];
    private int _loadedOffset;
    private int _totalItemCount;

    // ------------------------------------------------------------------ Unified accessors

    private int TotalItemCount => Items?.Count ?? _totalItemCount;
    private IReadOnlyList<TItem> CurrentItems => Items ?? _loadedItems;
    private int CurrentOffset => Items is not null ? 0 : _loadedOffset;

    // ------------------------------------------------------------------ Height map
    private float GetItemHeight(int index)
        => _itemHeights.TryGetValue(index, out var h) ? h : ItemSize;

    private float GetTotalHeight(int startIndex, int count)
    {
        float total = 0;
        for (int i = startIndex; i < startIndex + count; i++)
            total += GetItemHeight(i);
        return total;
    }

    private string SpacerStyle(int startIndex, int count)
    {
        var h = GetTotalHeight(startIndex, count);
        return string.Create(CultureInfo.InvariantCulture,
            $"height:{h:F0}px;flex-shrink:0;overflow-anchor:none;");
    }

    // ------------------------------------------------------------------ Provider helpers

    private async Task LoadDataAsync()
    {
        if (ItemsProvider is null) return;
        var result = await ItemsProvider(new ItemsProviderRequest(
            _itemsBefore, _visibleItemCapacity, default));
        _totalItemCount = result.TotalItemCount;
        _loadedItems = result.Items is IReadOnlyList<TItem> list ? list : result.Items.ToList();
        _loadedOffset = _itemsBefore;
    }

    // ------------------------------------------------------------------ Lifecycle

    protected override void OnParametersSet()
    {
        if (Items is not null && !_initialized && Items.Count > 0)
        {
            InitWindow(Items.Count);
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (ItemsProvider is not null && !_initialized)
        {
            // Initial load: one call to learn totalItemCount, then load from the end.
            var probe = await ItemsProvider(new ItemsProviderRequest(0, 1, default));
            _totalItemCount = probe.TotalItemCount;
            if (_totalItemCount > 0)
            {
                InitWindow(_totalItemCount);
                await LoadDataAsync();
            }
            return;
        }

        // Items mode: detect appended items
        if (Items is not null)
        {
            int count = Items.Count;
            if (count > _knownItemCount && _jsReady)
            {
                bool atBottom = await _jsModule!.InvokeAsync<bool>("isAtBottom", _jsInstanceId);
                if (atBottom)
                {
                    _itemsBefore = Math.Max(0, count - _visibleItemCapacity);
                    _pendingScrollToBottom = true;
                }
            }
            _knownItemCount = count;
        }
    }

    private void InitWindow(int totalCount)
    {
        _initialized = true;
        int estimated = (int)Math.Ceiling(800f / ItemSize) + OverscanCount * 2;
        _visibleItemCapacity = Math.Min(estimated, totalCount);
        _itemsBefore = Math.Max(0, totalCount - _visibleItemCapacity);
        _pendingScrollToBottom = true;
        _knownItemCount = totalCount;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _dotNetRef = DotNetObjectReference.Create(this);
            _jsModule = await JS.InvokeAsync<IJSObjectReference>(
                "import", "./Web/Components/Chat/ChatVirtualize.razor.js");
            _jsInstanceId = await _jsModule.InvokeAsync<int>(
                "init", _spacerBefore, _spacerAfter, _dotNetRef);
            _jsReady = true;
        }

        if (_jsReady)
        {
            await _jsModule!.InvokeVoidAsync("refreshObservedElements", _jsInstanceId);

            if (_pendingScrollToBottom)
            {
                _pendingScrollToBottom = false;
                await _jsModule!.InvokeVoidAsync("scrollToBottom", _jsInstanceId);
            }
        }
    }

    // ------------------------------------------------------------------ JS callbacks

    [JSInvokable]
    public async Task OnScroll(float scrollTop, float clientHeight)
    {
        int total = TotalItemCount;
        if (total == 0) return;

        float overscanPx = OverscanCount * ItemSize;
        float viewTop = Math.Max(0, scrollTop - overscanPx);
        float viewBottom = scrollTop + clientHeight + overscanPx;

        int newItemsBefore = 0;
        float cumHeight = 0;
        for (int i = 0; i < total; i++)
        {
            float h = GetItemHeight(i);
            if (cumHeight + h > viewTop)
            {
                newItemsBefore = i;
                break;
            }
            cumHeight += h;
            if (i == total - 1) newItemsBefore = total;
        }

        int capacity = 0;
        float bottom = cumHeight;
        for (int i = newItemsBefore; i < total; i++)
        {
            capacity++;
            bottom += GetItemHeight(i);
            if (bottom >= viewBottom) break;
        }
        capacity = Math.Max(1, capacity);

        if (newItemsBefore != _itemsBefore || capacity != _visibleItemCapacity)
        {
            _itemsBefore = newItemsBefore;
            _visibleItemCapacity = capacity;
            if (ItemsProvider is not null) await LoadDataAsync();
            StateHasChanged();
        }
    }

    [JSInvokable]
    public void OnItemResized(int index, float newHeight)
    {
        if (newHeight <= 0) return;
        _itemHeights[index] = newHeight;
        StateHasChanged();
    }

    // ------------------------------------------------------------------ Public API

    /// <summary>Programmatically scroll to the bottom of the chat.</summary>
    public async Task ScrollToBottomAsync()
    {
        if (_jsReady)
            await _jsModule!.InvokeVoidAsync("scrollToBottom", _jsInstanceId);
    }

    /// <summary>
    /// Re-requests data from the <see cref="ItemsProvider"/>. Call when the underlying
    /// data changes (new message, new event). For <see cref="Items"/> mode this is a no-op.
    /// </summary>
    public async Task RefreshDataAsync()
    {
        if (ItemsProvider is null) return;

        int oldCount = _totalItemCount;
        await LoadDataAsync();

        if (_totalItemCount > oldCount && _jsReady)
        {
            bool atBottom = await _jsModule!.InvokeAsync<bool>("isAtBottom", _jsInstanceId);
            if (atBottom)
            {
                _itemsBefore = Math.Max(0, _totalItemCount - _visibleItemCapacity);
                await LoadDataAsync();
                _pendingScrollToBottom = true;
            }
        }
        _knownItemCount = _totalItemCount;
        StateHasChanged();
    }

    // ------------------------------------------------------------------ Disposal
    public async ValueTask DisposeAsync()
    {
        if (_jsReady && _jsModule is not null)
        {
            try
            {
                await _jsModule.InvokeVoidAsync("dispose", _jsInstanceId);
                await _jsModule.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
        }
        _dotNetRef?.Dispose();
    }
}
