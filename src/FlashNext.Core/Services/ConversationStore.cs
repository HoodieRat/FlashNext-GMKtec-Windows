using System.Text.Json;
using System.Collections.Concurrent;
using FlashNext.Core.Models;

namespace FlashNext.Core.Services;

public sealed class ConversationStore(string rootDirectory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _root = Path.GetFullPath(rootDirectory);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private SemaphoreSlim Gate => Gates.GetOrAdd(_root, static _ => new(1, 1));

    public Task SaveAsync(string name, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) =>
        SaveDocumentAsync(new ConversationDocument { Name = name, Messages = [.. messages] }, cancellationToken);

    public async Task SaveDocumentAsync(ConversationDocument document, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = Resolve(document.Name);
            if (File.Exists(path))
            {
                ConversationDocument previous = await LoadDocumentAsync(document.Name, cancellationToken).ConfigureAwait(false);
                Dictionary<string, PromptRunReport> merged = previous.Runs.ToDictionary(run => run.Id, StringComparer.Ordinal);
                foreach (PromptRunReport incoming in document.Runs)
                {
                    PromptRunReport run = incoming;
                    if (merged.TryGetValue(run.Id, out PromptRunReport? saved))
                    {
                        // Rating writes and completion saves can arrive in either order.
                        if (saved.Status != PromptRunStatus.Running && run.Status == PromptRunStatus.Running) run = saved;
                        if (saved.RatingRevision >= run.RatingRevision)
                            run = run with { Rating = saved.Rating, RatingRevision = saved.RatingRevision };
                    }
                    merged[run.Id] = run;
                }
                document.Runs = merged.Values.OrderBy(run => run.StartedAtUtc).ToList();
            }
            document.Name = NormalizeName(document.Name);
            document.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await AtomicFile.WriteTextAsync(path, JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    public async Task<PromptRunReport> UpdateRatingAsync(string name, string runId, int? rating, CancellationToken cancellationToken = default)
    {
        if (rating is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(rating), "Rating must be unrated or 1–5.");
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = Resolve(name);
            ConversationDocument document = await LoadDocumentAsync(name, cancellationToken).ConfigureAwait(false);
            int index = document.Runs.FindIndex(run => run.Id == runId);
            if (index < 0) throw new InvalidDataException("The selected run is no longer in this session.");
            PromptRunReport updated = document.Runs[index] with { Rating = rating, RatingRevision = document.Runs[index].RatingRevision + 1 };
            document.Runs[index] = updated;
            DateTime lastWrite = File.GetLastWriteTimeUtc(path);
            await AtomicFile.WriteTextAsync(path, JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
            // Rating another session must not make it the startup conversation.
            File.SetLastWriteTimeUtc(path, lastWrite);
            return updated;
        }
        finally { Gate.Release(); }
    }

    public async Task<IReadOnlyList<ChatMessage>> LoadAsync(string name, CancellationToken cancellationToken = default) =>
        (await LoadDocumentAsync(name, cancellationToken).ConfigureAwait(false)).Messages;

    public async Task<ConversationDocument> LoadDocumentAsync(string name, CancellationToken cancellationToken = default)
    {
        string path = Resolve(name);
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
        ConversationDocument? document = await JsonSerializer.DeserializeAsync<ConversationDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (document is null || document.Version != 1) throw new InvalidDataException("Conversation format is not supported.");
        if (document.Messages is null || document.Messages.Any(static item => item is null || item.Content is null || item.Role is not ("system" or "user" or "assistant" or "tool"))) throw new InvalidDataException("Conversation contains an invalid message.");
        document.Runs ??= [];
        if (document.Runs.Any(run => run is null || string.IsNullOrWhiteSpace(run.Id) || run.SystemPrompt is null || run.Rating is < 1 or > 5) || document.Runs.Select(run => run.Id).Distinct(StringComparer.Ordinal).Count() != document.Runs.Count)
            throw new InvalidDataException("Conversation contains an invalid run report.");
        document.Name = NormalizeName(name);
        return document;
    }

    public IReadOnlyList<string> List(bool mostRecentFirst = false)
    {
        Directory.CreateDirectory(_root);
        IEnumerable<string> files = Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly);
        files = mostRecentFirst ? files.OrderByDescending(File.GetLastWriteTimeUtc) : files.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase);
        return files.Select(path => Path.GetFileNameWithoutExtension(path)).ToArray();
    }

    private string Resolve(string name)
    {
        Directory.CreateDirectory(_root);
        string normalized = NormalizeName(name);
        return PathExpander.EnsureUnder(_root, Path.Combine(_root, normalized + ".json"));
    }

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Conversation name is invalid.", nameof(name));
        string trimmed = name.Trim();
        if (trimmed.Contains("..", StringComparison.Ordinal) || trimmed.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            throw new ArgumentException("Conversation name is invalid.", nameof(name));
        }
        string value = Path.GetFileNameWithoutExtension(trimmed);
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value is "." or "..") throw new ArgumentException("Conversation name is invalid.", nameof(name));
        return value;
    }
}
