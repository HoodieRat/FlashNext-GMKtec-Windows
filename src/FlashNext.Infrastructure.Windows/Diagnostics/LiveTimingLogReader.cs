using System.Text;
using FlashNext.Core.Services;

namespace FlashNext.Infrastructure.Windows.Diagnostics;

/// <summary>Bounded, read-only tail starting at the request boundary. Never replays old log history.</summary>
public sealed class LiveTimingLogReader
{
    private const int MaximumRead = 65536;
    private readonly string _path;
    private readonly LiveGenerationTimingParser _parser = new();
    private readonly StringBuilder _partial = new();
    private long _offset = -1;
    private bool _discardLine;

    public LiveTimingLogReader(string path)
    {
        _path = path;
        try { _offset = new FileInfo(path).Length; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async Task<LiveGenerationTiming?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream file = new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        long length = file.Length;
        // Missing/replaced/truncated logs or a large backlog must not produce a stale speed.
        if (_offset < 0 || length < _offset || length - _offset > MaximumRead)
        {
            _offset = length;
            _partial.Clear();
            _parser.Reset();
            file.Position = Math.Max(0, length - 1);
            _discardLine = length > 0 && file.ReadByte() != '\n';
            return null;
        }
        if (length == _offset) return null;
        file.Position = _offset;
        byte[] buffer = new byte[(int)(length - _offset)];
        int count = await file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _offset += count;
        LiveGenerationTiming? latest = null;
        // All parsed fields are ASCII; fragmented UTF-8 in unrelated log text is immaterial.
        foreach (char character in Encoding.ASCII.GetString(buffer, 0, count))
        {
            if (character == '\n')
            {
                if (!_discardLine)
                    latest = _parser.AcceptLine(_partial.ToString().TrimEnd('\r')) ?? latest;
                _partial.Clear();
                _discardLine = false;
            }
            else if (!_discardLine)
            {
                if (_partial.Length >= 16384) { _partial.Clear(); _discardLine = true; }
                else _partial.Append(character);
            }
        }
        return latest?.TaskId == _parser.CurrentTaskId ? latest : null;
    }
}
