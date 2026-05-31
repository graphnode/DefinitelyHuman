namespace IrcBouncer.Utilities;

internal sealed class RingBuffer(int capacity)
{
    private readonly string[] _buf = new string[capacity];
    private int _head;
    private int _count;

    public int Count => _count;

    public void Add(string line)
    {
        _buf[_head] = line;
        _head = (_head + 1) % capacity;
        if (_count < capacity) _count++;
    }

    public string[] Drain()
    {
        if (_count == 0) return [];

        var result = new string[_count];
        int start = _count == capacity ? _head : 0;
        for (var i = 0; i < _count; i++)
            result[i] = _buf[(start + i) % capacity];

        _head = 0;
        _count = 0;
        Array.Clear(_buf);
        return result;
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
        Array.Clear(_buf);
    }
}