using System.Collections;
using System.Collections.Specialized;

namespace Hayabumi.App.ViewModels;

/// <summary>
/// 一覧に表示されている部分だけをデータベースから読み込むリスト。
/// WPF の仮想化表示と組み合わせて、数十万通のフォルダでも軽く表示する。
/// </summary>
public sealed class VirtualMessageList : IList, INotifyCollectionChanged
{
    private const int PageSize = 200;
    private const int MaxCachedPages = 30;

    private readonly Func<int, int, IReadOnlyList<MessageItemViewModel>> _fetch;
    private readonly Dictionary<int, IReadOnlyList<MessageItemViewModel>> _pages = [];
    private readonly LinkedList<int> _recent = new();

    public VirtualMessageList(int count, Func<int, int, IReadOnlyList<MessageItemViewModel>> fetch)
    {
        Count = count;
        _fetch = fetch;
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public int Count { get; private set; }

    public bool IsReadOnly => true;

    public bool IsFixedSize => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public object? this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            var page = GetPage(index / PageSize);
            var offset = index % PageSize;
            // 同期中に件数が減った場合などは空行を返し、次の再読み込みで正しくなる
            return offset < page.Count ? page[offset] : MessageItemViewModel.Placeholder;
        }
        set => throw new NotSupportedException();
    }

    /// <summary>読み込み済みの項目から探す。</summary>
    public int IndexOf(object? value)
    {
        if (value is not MessageItemViewModel item)
        {
            return -1;
        }

        foreach (var (pageIndex, items) in _pages)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Id == item.Id)
                {
                    return (pageIndex * PageSize) + i;
                }
            }
        }

        return -1;
    }

    public MessageItemViewModel? FindLoaded(long id)
        => _pages.Values.SelectMany(p => p).FirstOrDefault(m => m.Id == id);

    /// <summary>件数を更新し、読み込み済みのページを捨てて表示し直す。</summary>
    public void Reset(int count)
    {
        Count = count;
        _pages.Clear();
        _recent.Clear();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public IEnumerator GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    public void CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (var i = 0; i < Count; i++)
        {
            array.SetValue(this[i], index + i);
        }
    }

    public int Add(object? value) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();

    private IReadOnlyList<MessageItemViewModel> GetPage(int pageIndex)
    {
        if (_pages.TryGetValue(pageIndex, out var page))
        {
            _recent.Remove(pageIndex);
            _recent.AddFirst(pageIndex);
            return page;
        }

        page = _fetch(pageIndex * PageSize, PageSize);
        _pages[pageIndex] = page;
        _recent.AddFirst(pageIndex);
        if (_recent.Count > MaxCachedPages)
        {
            _pages.Remove(_recent.Last!.Value);
            _recent.RemoveLast();
        }

        return page;
    }
}
