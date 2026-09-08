using System.Text;
using System.Text.Json;
using FlashNext.Core.Services;

namespace FlashNext.Infrastructure.Windows.System;

public sealed class StructuredFileLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly SecretRedactor _redactor;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StructuredFileLogger(string directory, SecretRedactor redactor)
    {
        _directory = Path.GetFullPath(directory);
        _redactor = redactor;
        Directory.CreateDirectory(_directory);
    }

    public Task InfoAsync(string eventName, string message, object? data = null, CancellationToken cancellationToken = default) => WriteAsync("information", eventName, message, data, cancellationToken);
    public Task WarningAsync(string eventName, string message, object? data = null, CancellationToken cancellationToken = default) => WriteAsync("warning", eventName, message, data, cancellationToken);
    public Task ErrorAsync(string eventName, string message, Exception? exception = null, object? data = null, CancellationToken cancellationToken = default) => WriteAsync("error", eventName, message, new { data, exception = exception is null ? null : new { type = exception.GetType().FullName, exception.Message, exception.StackTrace } }, cancellationToken);

    private async Task WriteAsync(string level, string eventName, string message, object? data, CancellationToken cancellationToken)
    {
        var entry = new { timestampUtc = DateTimeOffset.UtcNow, level, eventName, message = _redactor.Redact(message), data };
        string line = _redactor.Redact(JsonSerializer.Serialize(entry, JsonOptions)) + Environment.NewLine;
        string path = Path.Combine(_directory, $"manager-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await File.AppendAllTextAsync(path, line, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
        Purge(TimeSpan.FromDays(30));
    }

    private void Purge(TimeSpan retention)
    {
        DateTime cutoff = DateTime.UtcNow - retention;
        foreach (string file in Directory.EnumerateFiles(_directory, "manager-*.jsonl")) if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
    }
}
