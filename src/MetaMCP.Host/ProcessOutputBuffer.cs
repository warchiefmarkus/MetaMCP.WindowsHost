namespace MetaMCP.Host;

internal sealed class ProcessOutputBuffer
{
    private readonly Queue<string> _lines = new();
    private readonly object _sync = new();
    private readonly int _capacity;

    public ProcessOutputBuffer(int capacity = 40)
    {
        _capacity = Math.Max(5, capacity);
    }

    public void Add(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_sync)
        {
            _lines.Enqueue(line.TrimEnd());
            while (_lines.Count > _capacity)
            {
                _lines.Dequeue();
            }
        }
    }

    public string ReadTail()
    {
        lock (_sync)
        {
            return string.Join(Environment.NewLine, _lines);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _lines.Clear();
        }
    }
}