namespace RuijieNetworkAssistant.Services;

/// <summary>
/// CLI 命令历史（↑ / ↓ 调取）。只保留最近若干条，避免长时间运行占用内存。
/// </summary>
public sealed class CommandHistory
{
    private readonly List<string> _items = new();
    private int _cursor;
    private string _draft = string.Empty;

    public CommandHistory(int capacity = 200) => Capacity = Math.Max(10, capacity);

    public int Capacity { get; }

    public int Count => _items.Count;

    public IReadOnlyList<string> Items => _items;

    public void Add(string command)
    {
        var text = command?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        if (_items.Count == 0 || !string.Equals(_items[^1], text, StringComparison.Ordinal))
        {
            _items.Add(text);
        }

        while (_items.Count > Capacity)
        {
            _items.RemoveAt(0);
        }

        _cursor = _items.Count;
        _draft = string.Empty;
    }

    /// <summary>↑：返回上一条命令；没有更早的命令时返回 null。</summary>
    public string? MovePrevious(string currentInput)
    {
        if (_items.Count == 0)
        {
            return null;
        }

        if (_cursor == _items.Count)
        {
            _draft = currentInput ?? string.Empty;
        }

        if (_cursor <= 0)
        {
            return null;
        }

        _cursor--;
        return _items[_cursor];
    }

    /// <summary>↓：返回下一条命令；已经到最后时返回进入历史前的草稿。</summary>
    public string? MoveNext()
    {
        if (_items.Count == 0 || _cursor >= _items.Count)
        {
            return null;
        }

        _cursor++;
        return _cursor == _items.Count ? _draft : _items[_cursor];
    }

    public void Reset()
    {
        _cursor = _items.Count;
        _draft = string.Empty;
    }

    public void Clear()
    {
        _items.Clear();
        Reset();
    }
}
