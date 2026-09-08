using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlashNext.Core.Models;
using FlashNext.Core.Services;
using FlashNext.Infrastructure.Windows.Control;
using FlashNext.Infrastructure.Windows.Diagnostics;
using FlashNext.Infrastructure.Windows.Hardware;
using FlashNext.Infrastructure.Windows.Models;
using FlashNext.Infrastructure.Windows.Runtime;
using FlashNext.Infrastructure.Windows.Security;
using FlashNext.Infrastructure.Windows.System;

namespace FlashNext.Dashboard;

public sealed partial class DashboardSession : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private AppSettings _settings = new();
    private CancellationTokenSource? _chat;
    private ControlApiServer? _control;
    private bool _busy;
    private TaskCompletionSource? _sendFinished;
    private bool _disposed;
    private bool _serverOp;
    private bool _adaptiveDraft;
    private string _draft = string.Empty;
    private string _specType = "draft-mtp";
    private string _mtpMode = "fixed-4";
    private double _specDraftPMin;
    private string _visionDetail = "balanced";
    private int _batchSize = 2048;
    private int _ubatchSize = 512;
    private string? _attachedImagePath;
    private string _liveTpsText = "—";
    private string _liveTpsState = "IDLE";

    public DashboardSession(PlatformPaths? paths = null)
    {
        Paths = paths ?? new PlatformPaths();
        Paths.EnsureUserDirectories();
        Redactor = new SecretRedactor();
        Logger = new StructuredFileLogger(Paths.LogsDirectory, Redactor);
        ProcessRunner = new WindowsProcessRunner();
        Secrets = new WindowsSecretStore(Paths, Redactor);
        Settings = new SettingsStore(Paths.SettingsPath, Paths.FactorySettingsPath);
        Supervisor = new RuntimeSupervisor(Paths, Secrets, ProcessRunner, Logger);
        Hardware = new WindowsHardwareProbe(ProcessRunner, Paths);
        Runtime = new RuntimeManager(Paths, ProcessRunner, Logger);
        Model = new ModelManager(Paths, Logger);
        Telemetry = new WindowsSystemTelemetryProvider();
        Diagnostics = new DiagnosticsBundleService(Paths, Secrets);
        Conversations = new ConversationStore(Paths.ConversationsDirectory);
        _control = new ControlApiServer(Settings, Supervisor, Secrets, Paths);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? StatusChanged;
    public event Action? TranscriptUpdated;

    public PlatformPaths Paths { get; }
    public SecretRedactor Redactor { get; }
    public StructuredFileLogger Logger { get; }
    public WindowsProcessRunner ProcessRunner { get; }
    public WindowsSecretStore Secrets { get; }
    public SettingsStore Settings { get; }
    public RuntimeSupervisor Supervisor { get; }
    public WindowsHardwareProbe Hardware { get; }
    public RuntimeManager Runtime { get; }
    public ModelManager Model { get; }
    public WindowsSystemTelemetryProvider Telemetry { get; }
    public DiagnosticsBundleService Diagnostics { get; }
    public ConversationStore Conversations { get; }
    public ObservableCollection<ChatLine> Messages { get; } = [];

    public string ServerState { get; set; } = "Stopped";
    public string StatusLine { get; set; } = "Server stopped.";
    public string LastError { get; set; } = string.Empty;
    public string Endpoint { get; set; } = "http://127.0.0.1:8080/v1";
    public string ActiveProfile { get; set; } = "coding-balanced";
    public string[] ProfileNames { get; private set; } = [];
    public bool Thinking { get; set; }
    public string ReasoningEffort { get; set; } = "medium";
    public string[] ReasoningEfforts { get; } = ["none", "low", "medium", "high"];
    public double Temperature { get; set; } = 0.7;
    public double TopP { get; set; } = 0.8;
    public int TopK { get; set; } = 20;
    public double MinP { get; set; }
    public double PresencePenalty { get; set; }
    public double RepetitionPenalty { get; set; } = 1.0;
    public int MaxOutputTokens { get; set; } = 2048;
    public int MtpNMax { get; set; } = 4;
    public int ContextSize { get; set; } = 32768;
    public int InferencePort { get; set; } = 8080;
    public string SeedText { get; set; } = string.Empty;
    public bool AdaptiveDraft { get => _adaptiveDraft; set { if (_adaptiveDraft == value) return; _adaptiveDraft = value; OnPropertyChanged(); } }
    public string SpecType
    {
        get => _specType;
        set
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? "draft-mtp" : value;
            if (_specType == normalized) return;
            _specType = normalized;
            if (!IsMtpEnabled) AdaptiveDraft = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsMtpEnabled));
        }
    }
    public bool IsMtpEnabled => !SpecType.Equals("none", StringComparison.OrdinalIgnoreCase);
    public SpeculationOption[] SpecTypes { get; } = [new("none", "None (baseline)"), new("draft-mtp", "draft-mtp"), new("ngram-simple", "ngram-simple")];
    public string MtpMode { get => _mtpMode; set { if (_mtpMode == value) return; _mtpMode = value; OnPropertyChanged(); } }
    public double SpecDraftPMin { get => _specDraftPMin; set { if (_specDraftPMin == value) return; _specDraftPMin = value; OnPropertyChanged(); } }
    public DraftConfidenceOption[] DraftConfidences { get; } =
    [
        new(0.00, "0.00 — Off / current behavior (default)"),
        new(0.50, "0.50"),
        new(0.65, "0.65"),
        new(0.75, "0.75"),
        new(0.80, "0.80")
    ];
    public MtpModeOption[] MtpModes { get; } =
    [
        new("off", "Off"),
        new("fixed-1", "Fixed 1"),
        new("fixed-2", "Fixed 2"),
        new("fixed-3", "Fixed 3"),
        new("fixed-4", "Fixed 4"),
        new("fixed-5", "Fixed 5"),
        new("fixed-6", "Fixed 6"),
        new("adaptive-2-2", "Adaptive 2–2"),
        new("adaptive-2-3", "Adaptive 2–3"),
        new("adaptive-2-4", "Adaptive 2–4"),
        new("adaptive-2-5", "Adaptive 2–5"),
        new("adaptive-2-6", "Adaptive 2–6")
    ];
    public string VisionDetail { get => _visionDetail; set { if (_visionDetail == value) return; _visionDetail = value; OnPropertyChanged(); } }
    public VisionDetailOption[] VisionDetails { get; } =
    [
        new("fast", "Fast · 1,024–2,048"),
        new("balanced", "Balanced · 1,024–4,096"),
        new("detailed", "Detailed · 1,024–8,192"),
        new("maximum", "Maximum · model default")
    ];
    public int BatchSize { get => _batchSize; set { if (_batchSize == value) return; _batchSize = value; OnPropertyChanged(); } }
    public int[] BatchSizes { get; } = [1024, 2048, 4096];
    public int UBatchSize { get => _ubatchSize; set { if (_ubatchSize == value) return; _ubatchSize = value; OnPropertyChanged(); } }
    public int[] UBatchSizes { get; } = [256, 512, 1024, 2048];
    public string SystemPrompt { get; set; } = string.Empty;
    public string Draft { get => _draft; set { _draft = value ?? string.Empty; OnPropertyChanged(); } }
    public string AttachedImageName => _attachedImagePath is null ? "No image attached" : Path.GetFileName(_attachedImagePath);
    public ImageSource? AttachedImagePreview { get; private set; }
    public bool HasAttachedImage => _attachedImagePath is not null;
    public string ChatProgress { get; set; } = "Click Start, wait until the header says Running, then send.";
    public string MetricsText { get; set; } = string.Empty;
    public string ServerLog { get; set; } = string.Empty;
    public string TelemetryText { get; set; } = string.Empty;
    public string LiveTpsText => _liveTpsText;
    public string LiveTpsState => _liveTpsState;
    public bool RestartRequired { get; set; }
    public string SettingsNote { get; set; } = "Thinking, sampling, max tokens, seed, and system prompt apply on the next Send. MTP mode, MTP Draft Confidence, Vision Detail, batch sizes, context, and port need Restart (reloads the model).";
    public bool CanSend => CanChangeSession && Supervisor.Status.State is ServerLifecycleState.Running or ServerLifecycleState.External;
    public bool CanStopChat => _chat is not null;

    public async Task InitializeAsync()
    {
        _settings = await Settings.LoadAsync().ConfigureAwait(true);
        _ = await Secrets.GetOrCreateApiKeyAsync().ConfigureAwait(true);
        try
        {
            await _control!.StartAsync(_settings.Server.ControlPort).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LastError = "Control API did not start: " + ex.Message;
        }
        try { await Supervisor.AttachIfHealthyAsync(_settings).ConfigureAwait(true); } catch { }
        LoadFormFromSettings();
        RestartRequired = Supervisor.Status.Configuration is { } initializedConfiguration && initializedConfiguration != RuntimeConfiguration.Capture(_settings);
        OnPropertyChanged(nameof(RestartRequired));
        RefreshStatus();
        RefreshLog();
        RefreshSessions();
        if (_settings.Chat.SaveConversations && SessionNames.FirstOrDefault() is string latest)
            await ChangeSessionAsync(latest).ConfigureAwait(true);
    }

    public async Task StartServerAsync()
    {
        if (!BeginServerOp("Starting server… model load can take about a minute.")) return;
        try
        {
            _settings = await Settings.LoadAsync().ConfigureAwait(true);
            await Supervisor.StartAsync(_settings).ConfigureAwait(true);
            RestartRequired = Supervisor.Status.Configuration is { } startedConfiguration && startedConfiguration != RuntimeConfiguration.Capture(_settings);
            OnPropertyChanged(nameof(RestartRequired));
            LastError = string.Empty;
            SetNotice("Server running. Type a prompt and press Enter or Send.");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            SetNotice("Server failed to start: " + ex.Message, error: true);
        }
        finally
        {
            EndServerOp();
        }
    }

    public async Task StopServerAsync()
    {
        if (!BeginServerOp("Stopping server…")) return;
        try
        {
            await Supervisor.StopAsync().ConfigureAwait(true);
            LastError = string.Empty;
            SetNotice("Server stopped.");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            SetNotice("Stop failed: " + ex.Message, error: true);
        }
        finally
        {
            EndServerOp();
        }
    }

    public async Task RestartServerAsync()
    {
        if (!BeginServerOp("Saving settings, then restarting… this reloads the model and can take about a minute.")) return;
        try
        {
            await SaveSettingsAsync().ConfigureAwait(true);
            bool wasUp = Supervisor.Status.State is ServerLifecycleState.Running or ServerLifecycleState.External or ServerLifecycleState.Starting;
            SetNotice(wasUp
                ? "Restarting server… model reload can take about a minute. The header will say Running when it is ready."
                : "Starting server… model load can take about a minute. The header will say Running when it is ready.");
            _settings = await Settings.LoadAsync().ConfigureAwait(true);
            await Supervisor.RestartAsync(_settings).ConfigureAwait(true);
            RestartRequired = Supervisor.Status.Configuration is not { } runningConfiguration || runningConfiguration != RuntimeConfiguration.Capture(_settings);
            LastError = string.Empty;
            SetNotice(RestartRequired
                ? "An existing server is still running with settings that could not be applied here. Restart it from the manager that owns it, then Start again."
                : "Restart complete. Server running. Next Send uses the saved settings.");
            OnPropertyChanged(nameof(RestartRequired));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            SetNotice("Restart failed: " + ex.Message, error: true);
        }
        finally
        {
            EndServerOp();
        }
    }

    public async Task SaveSettingsAsync()
    {
        _settings = await Settings.LoadAsync().ConfigureAwait(true);
        int previousContext = _settings.GetActiveProfile().ContextSize;
        string previousMtpMode = GetMtpMode(_settings);
        double previousSpecDraftPMin = _settings.Server.SpecDraftPMin;
        string previousVisionDetail = _settings.Server.VisionDetail;
        int previousPort = _settings.Server.Port;
        int previousBatchSize = _settings.Server.BatchSize;
        int previousUBatchSize = _settings.Server.UBatchSize;
        if (!_settings.Profiles.ContainsKey(ActiveProfile)) throw new InvalidDataException("Unknown profile.");
        _settings.ActiveProfile = ActiveProfile;
        InferenceProfile profile = _settings.GetActiveProfile();
        profile.Thinking = Thinking;
        profile.ReasoningEffort = ReasoningEffort;
        profile.Temperature = Temperature;
        profile.TopP = TopP;
        profile.TopK = TopK;
        profile.MinP = MinP;
        profile.PresencePenalty = PresencePenalty;
        profile.RepetitionPenalty = RepetitionPenalty;
        profile.MaxOutputTokens = MaxOutputTokens;
        profile.ContextSize = ContextSize;
        profile.Seed = int.TryParse(SeedText, out int seed) ? seed : null;
        _settings.Chat.SystemPrompt = SystemPrompt;
        _settings.Server.Port = InferencePort;
        ApplyMtpMode(_settings, MtpMode);
        _settings.Server.SpecDraftPMin = SpecDraftPMin;
        _settings.Server.VisionDetail = VisionDetail;
        _settings.Server.BatchSize = BatchSize;
        _settings.Server.UBatchSize = UBatchSize;
        MtpNMax = profile.MtpNMax;
        SpecType = _settings.Server.SpecType;
        AdaptiveDraft = HasAdaptiveDraft(_settings);
        await Settings.SaveAsync(_settings).ConfigureAwait(true);
        RestartRequired = Supervisor.Status.Configuration is { } running
            ? running != RuntimeConfiguration.Capture(_settings)
            : RestartRequired || previousContext != ContextSize || previousPort != InferencePort || previousBatchSize != BatchSize || previousUBatchSize != UBatchSize || previousSpecDraftPMin != SpecDraftPMin || !string.Equals(previousMtpMode, MtpMode, StringComparison.Ordinal) || !string.Equals(previousVisionDetail, VisionDetail, StringComparison.Ordinal);
        SettingsNote = RestartRequired
            ? "Saved. Restart required for MTP mode, MTP Draft Confidence, Vision Detail, batch sizes, context, or port. Click Restart now — the header will say Running when the model is back."
            : "Saved. Sampling, thinking, max tokens, seed, and system prompt apply on the next Send. No restart needed.";
        OnPropertyChanged(nameof(RestartRequired));
        OnPropertyChanged(nameof(SettingsNote));
        if (!_serverOp) RefreshStatus();
    }

    public async Task ResetActiveProfileToFactoryAsync()
    {
        if (!BeginServerOp("Restoring the active profile to its factory defaults…")) return;
        try
        {
            _settings = await Settings.LoadAsync().ConfigureAwait(true);
            if (!_settings.Profiles.ContainsKey(ActiveProfile)) throw new InvalidDataException("Unknown profile.");
            int previousContext = _settings.Profiles[ActiveProfile].ContextSize;
            int previousMtp = _settings.Profiles[ActiveProfile].MtpNMax;
            AppSettings factory = await Settings.LoadFactoryAsync().ConfigureAwait(true);
            if (!factory.Profiles.TryGetValue(ActiveProfile, out InferenceProfile? factoryProfile))
                throw new InvalidDataException($"Profile '{ActiveProfile}' has no factory default.");

            _settings.ActiveProfile = ActiveProfile;
            _settings.Profiles[ActiveProfile] = factoryProfile;
            await Settings.SaveAsync(_settings).ConfigureAwait(true);
            RestartRequired = Supervisor.Status.Configuration is { } running
                ? running != RuntimeConfiguration.Capture(_settings)
                : RestartRequired || previousContext != factoryProfile.ContextSize || previousMtp != factoryProfile.MtpNMax;
            LoadFormFromSettings();
            SetNotice(RestartRequired
                ? "Factory profile restored. Restart now to apply its context and server settings."
                : "Factory profile restored. Its sampling settings apply on the next Send.");
            OnPropertyChanged(nameof(RestartRequired));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            SetNotice("Could not restore the factory profile: " + ex.Message, error: true);
        }
        finally
        {
            EndServerOp();
        }
    }

    public async Task SendAsync(string? textOverride = null)
    {
        string text = (textOverride ?? Draft).Trim();
        if (text.Length == 0) return;

        ServerLifecycleState state = Supervisor.Status.State;
        if (state is not ServerLifecycleState.Running and not ServerLifecycleState.External)
        {
            string why = state switch
            {
                ServerLifecycleState.Starting => "Server is still starting. Wait until the header says Running, then send.",
                ServerLifecycleState.Stopping => "Server is stopping. Wait, then click Start.",
                ServerLifecycleState.Faulted => "Server failed to start. Open the Server tab, then click Start.",
                _ => "Server is stopped. Click Start and wait until the header says Running, then send."
            };
            ShowSendBlock(why);
            return;
        }

        if (!CanChangeSession)
        {
            ShowSendBlock("Already generating. Click Stop reply, or wait for the current answer.");
            return;
        }

        InferenceProfile profile = ProfileForRequest();
        string requestSystemPrompt = string.IsNullOrWhiteSpace(SystemPrompt) ? _settings.Chat.SystemPrompt : SystemPrompt;
        PromptRunReport run = CaptureRun(requestSystemPrompt, profile, _attachedImagePath is not null);
        Draft = string.Empty;
        ContextNote = "Checking the running server's context before sending…";
        OnPropertyChanged(nameof(ContextNote));
        string? imageDataUrl = CreateAttachedImageDataUrl();
        ChatLine user = new("You", text, false) { ImageDataUrl = imageDataUrl, RunId = run.Id };
        Messages.Add(user);
        ChatLine assistant = new("Assistant", string.Empty, true) { Status = "Connecting…", RunId = run.Id };
        Messages.Add(assistant);
        ReplaceRun(run);
        ChatProgress = "Connecting to llama-server…";
        OnPropertyChanged(nameof(ChatProgress));
        TranscriptUpdated?.Invoke();

        List<ChatMessage> history = [new ChatMessage("system", requestSystemPrompt)];
        foreach (ChatLine line in Messages)
        {
            if (line.Pending || line.IsError || line.Role is "FlashNext" || (line.Text.Length == 0 && line.Thinking.Length == 0)) continue;
            history.Add(new ChatMessage(line.Role == "You" ? "user" : "assistant", line.Text)
            {
                ReasoningContent = line.Thinking.Length > 0 ? line.Thinking : null,
                ImageDataUrl = line.ImageDataUrl
            });
        }
        _busy = true;
        _sendFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NotifyChatAvailability();
        _chat = new CancellationTokenSource();
        OnPropertyChanged(nameof(CanStopChat));
        SetLiveSpeed(null, "WAITING FOR TOKENS");
        string phase = "Connecting…";
        Stopwatch clock = Stopwatch.StartNew();
        string lastAnswerPath = Path.Combine(Paths.UserDataRoot, "last-answer.txt");
        string lastAnswerSnapshot = string.Empty;
        string lastAnswerFlushed = string.Empty;
        long lastAnswerFlushTick = 0;
        using CancellationTokenSource progress = CancellationTokenSource.CreateLinkedTokenSource(_chat.Token);
        Task ticker = Task.Run(async () =>
        {
            try
            {
                while (!progress.Token.IsCancellationRequested)
                {
                    await Task.Delay(200, progress.Token).ConfigureAwait(false);
                    string status = phase + "  " + clock.Elapsed.ToString(@"m\:ss");
                    RunOnUi(() =>
                    {
                        if (!assistant.Pending) return;
                        assistant.Status = status;
                        ChatProgress = status;
                        OnPropertyChanged(nameof(ChatProgress));
                        TranscriptUpdated?.Invoke();
                    });
                    if (lastAnswerSnapshot.Length > 0 && lastAnswerSnapshot != lastAnswerFlushed && Environment.TickCount64 - lastAnswerFlushTick >= 250)
                    {
                        await FlushLastAnswerAsync(lastAnswerPath, lastAnswerSnapshot).ConfigureAwait(false);
                        lastAnswerFlushed = lastAnswerSnapshot;
                        lastAnswerFlushTick = Environment.TickCount64;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        try
        {
            await SaveCurrentSessionAsync().ConfigureAwait(true);
            if (!_settings.Chat.SaveConversations)
            {
                SettingsNote = "Conversation saving is disabled. This session's reports are temporary and will be lost when the app closes.";
                OnPropertyChanged(nameof(SettingsNote));
            }
            string key = await Secrets.GetApiKeyAsync().ConfigureAwait(true);
            Uri uri = Supervisor.Status.BaseUri ?? new Uri($"http://127.0.0.1:{_settings.Server.Port}/");
            using LlamaApiClient client = new(uri, key, TimeSpan.FromSeconds(_settings.Chat.RequestTimeoutSeconds));
            string serverLogPath = Path.Combine(Paths.LogsDirectory, "server-error.log");
            long serverLogStart = GetFileLength(serverLogPath);
            phase = "Checking context…";
            PreparedChat prepared = await client.PrepareChatAsync(history, profile, _chat.Token).ConfigureAwait(true);
            RuntimeConfiguration? requestRuntime = run.Runtime;
            run = PromptRunCapture.Prepared(run, prepared, profile);
            ReplaceRun(run);
            ContextNote = $"Working context: {prepared.PromptTokens:N0} input + up to {prepared.MaxOutputTokens:N0} reply tokens / {prepared.ContextSize:N0}.";
            if (prepared.OmittedTurns > 0) ContextNote += $" {prepared.OmittedTurns} older exchange(s) left out of this request; still in the transcript. Reattach older details if needed.";
            if (prepared.MaxOutputTokens < run.Sampling!.RequestedMaxOutputTokens) ContextNote += " Reply limit reduced to fit the latest exchange.";
            OnPropertyChanged(nameof(ContextNote));
            await SaveCurrentSessionAsync().ConfigureAwait(true);
            LastError = string.Empty;
            OnPropertyChanged(nameof(LastError));
            phase = "Waiting for first token…";
            RunOnUi(() =>
            {
                assistant.Status = phase;
                ChatProgress = phase;
                OnPropertyChanged(nameof(ChatProgress));
            });
            MetricSnapshot before;
            try { before = await client.GetMetricsAsync(_chat.Token).ConfigureAwait(true); }
            catch { before = new MetricSnapshot(); }
            StringBuilder thinking = new();
            StringBuilder answer = new();
            ChatCompletionResult completion;
            using (CancellationTokenSource liveSpeed = CancellationTokenSource.CreateLinkedTokenSource(_chat.Token))
            {
                // The runtime updates Prometheus generation totals at completion, not while streaming.
                // Start at EOF immediately before this request and accept only new slot/task timings.
                LiveTimingLogReader? timingReader = Supervisor.Status.IsOwnedProcess ? new(serverLogPath) : null;
                int? timingProcess = Supervisor.Status.ProcessId;
                int receivedOutput = 0;
                Task liveSpeedTask = TrackLiveSpeedAsync(timingReader, () => Volatile.Read(ref receivedOutput) != 0, timingProcess, liveSpeed.Token);
                try
                {
                    completion = await client.StreamChatAsync(prepared.Messages, profile, chunk =>
                    {
                        if (chunk.ReasoningContent.Length == 0 && chunk.Content.Length == 0) return Task.CompletedTask;
                        Interlocked.Exchange(ref receivedOutput, 1);
                        RunOnUi(() =>
                        {
                            if (_liveTpsState == "WAITING FOR TOKENS") SetLiveSpeed(null, "GENERATING • WAITING FOR TIMING");
                            if (chunk.ReasoningContent.Length > 0)
                            {
                                phase = "Thinking…";
                                thinking.Append(chunk.ReasoningContent);
                                assistant.Thinking = thinking.ToString();
                            }
                            if (chunk.Content.Length > 0)
                            {
                                phase = "Generating…";
                                answer.Append(chunk.Content);
                                assistant.Text = answer.ToString();
                                lastAnswerSnapshot = assistant.Text;
                            }
                            assistant.Status = phase + "  " + clock.Elapsed.ToString(@"m\:ss");
                            ChatProgress = assistant.Status;
                            OnPropertyChanged(nameof(ChatProgress));
                            TranscriptUpdated?.Invoke();
                        });
                        return Task.CompletedTask;
                    }, _chat.Token).ConfigureAwait(true);
                }
                finally
                {
                    liveSpeed.Cancel();
                    try { await liveSpeedTask.ConfigureAwait(true); } catch (OperationCanceledException) { }
                }
            }
            run = run with { EffectiveGenerationSettings = completion.EffectiveGenerationSettings, ServerFingerprint = completion.ServerFingerprint };
            if (imageDataUrl is not null) ClearAttachedImage();
            assistant.Pending = false;
            assistant.Status = completion.FinishReason == "length" ? "Reply reached its token limit. You can ask to continue in this session." : string.Empty;
            if (thinking.Length == 0 && completion.ReasoningContent.Length > 0) assistant.Thinking = completion.ReasoningContent;
            if (answer.Length == 0 && completion.Content.Length > 0) assistant.Text = completion.Content;
            if (assistant.Text.Length == 0) assistant.Status = "No answer text was produced. " + assistant.Status;
            MetricSnapshot after;
            try { after = await client.GetMetricsAsync().ConfigureAwait(true); }
            catch { after = before; }
            SystemTelemetry telemetry;
            try { telemetry = await Telemetry.CaptureAsync(Supervisor.Status.ProcessId).ConfigureAwait(true); }
            catch { telemetry = new(); }
            ResponseMetrics metrics = MetricCalculator.Build(completion, before, after, telemetry, run.Profile, prepared.ContextSize, runtime: requestRuntime, launch: run.Launch);
            SetLiveSpeed(metrics.GenerationTokensPerSecond, "LAST COMPLETED RUN");
            await Task.Delay(100).ConfigureAwait(true);
            string completionLog = ReadAppendedText(serverLogPath, serverLogStart);
            (int? graphReuse, double? meanAcceptedSpan) = PrometheusParser.ParseCompletionLog(completionLog);
            metrics.GraphReuseCount = graphReuse;
            metrics.AverageAcceptedDraftLength = PrometheusParser.AcceptedDraftTokensFromMeanSpan(meanAcceptedSpan, metrics.MtpNMax);
            metrics.VisualTokens = imageDataUrl is null
                ? 0
                : PrometheusParser.ParseImageTokenCount(completionLog) ?? completion.VisualTokens ?? Math.Max(0, Math.Max(completion.TotalPromptTokens, completion.PromptTokens) - prepared.PromptTokens);
            run = run with
            {
                Status = completion.FinishReason == "length" ? PromptRunStatus.TokenLimited
                    : completion.FinishReason is null ? PromptRunStatus.Interrupted : PromptRunStatus.Completed,
                FinishedAtUtc = DateTimeOffset.UtcNow, ElapsedMilliseconds = completion.TotalElapsedMilliseconds,
                HasOutput = !string.IsNullOrWhiteSpace(assistant.Text), Metrics = completion.CompletionTokens > 0 ? metrics : null,
                EffectiveGenerationSettings = completion.EffectiveGenerationSettings, ServerFingerprint = completion.ServerFingerprint
            };
            ReplaceRun(run);
            // A report or optional metrics-log failure must not turn a completed reply into a failed generation.
            try { await SaveCurrentSessionAsync().ConfigureAwait(true); }
            catch (Exception ex) { ReportSessionError(ex); }
            try { await new MetricsWriter(Paths.MetricsDirectory, _settings.Metrics).WriteAsync(metrics).ConfigureAwait(true); }
            catch (Exception ex) { LastError = "Reply report retained; optional metric log could not be written: " + ex.Message; OnPropertyChanged(nameof(LastError)); }
            TelemetryText = BenchmarkReportFormatter.Headline(metrics) + Environment.NewLine + $"Working set {FormatGiB(metrics.ProcessWorkingSetBytes)}   GPU {FormatGiB(metrics.GpuMemoryUsedBytes)}";
            lastAnswerSnapshot = assistant.Text;
            ChatProgress = "Done.  " + BenchmarkReportFormatter.Headline(metrics) + "  Saved last-answer.txt.";
            OnPropertyChanged(nameof(MetricsText));
            OnPropertyChanged(nameof(TelemetryText));
            OnPropertyChanged(nameof(ChatProgress));
            TranscriptUpdated?.Invoke();
        }
        catch (OperationCanceledException)
        {
            run = run with { Status = PromptRunStatus.Stopped, FinishedAtUtc = DateTimeOffset.UtcNow, ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds, HasOutput = !string.IsNullOrWhiteSpace(assistant.Text) };
            ReplaceRun(run);
            RetainInterruptedReply("Stopped.");
            ContextNote = "Stopped. The transcript and any unfinished draft have been kept.";
            OnPropertyChanged(nameof(ContextNote));
            ChatProgress = "Cancelled.";
            SetLiveSpeed(null, "REPLY STOPPED");
            OnPropertyChanged(nameof(ChatProgress));
        }
        catch (Exception ex)
        {
            run = run with { Status = PromptRunStatus.Failed, FinishedAtUtc = DateTimeOffset.UtcNow, ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds, HasOutput = !string.IsNullOrWhiteSpace(assistant.Text), Error = ex.Message };
            ReplaceRun(run);
            RetainInterruptedReply("Reply interrupted: " + ex.Message);
            ContextNote = "Send interrupted. The transcript and any unfinished draft have been kept.";
            OnPropertyChanged(nameof(ContextNote));
            LastError = ex.Message;
            ChatProgress = "Send failed: " + ex.Message;
            SetLiveSpeed(null, "ERROR");
            OnPropertyChanged(nameof(LastError));
            OnPropertyChanged(nameof(ChatProgress));
        }
        finally
        {
            progress.Cancel();
            try { await ticker.ConfigureAwait(true); } catch (OperationCanceledException) { }
            await FlushLastAnswerAsync(lastAnswerPath, lastAnswerSnapshot).ConfigureAwait(true);
            _chat.Dispose();
            _chat = null;
            try { await SaveCurrentSessionAsync().ConfigureAwait(true); }
            catch (Exception ex) { ReportSessionError(ex); }
            RefreshInlineReports();
            _busy = false;
            NotifyChatAvailability();
            if (_reportSessions.Count > 0) await RefreshReportsAsync().ConfigureAwait(true);
            OnPropertyChanged(nameof(CanStopChat));
            TranscriptUpdated?.Invoke();
            _sendFinished.TrySetResult();
        }

        void RetainInterruptedReply(string notice)
        {
            assistant.Pending = false;
            if (assistant.Text.Length == 0 && assistant.Thinking.Length == 0)
            {
                // This attempt leaves no user message to link to. Keep a fallback
                // for report search without adding failed turns to model history.
                run = run with { Prompt = text };
                ReplaceRun(run);
                Messages.Remove(assistant);
                Messages.Remove(user);
                Draft = text;
            }
            else assistant.Status = notice + " Partial reply retained.";
        }
    }

    public void StopChat()
    {
        _chat?.Cancel();
    }

    public string LastAnswerText()
    {
        for (int i = Messages.Count - 1; i >= 0; i--)
        {
            ChatLine line = Messages[i];
            if (line.Role == "Assistant" && !line.IsError && line.Text.Length > 0) return line.Text;
        }
        return string.Empty;
    }

    public bool CopyAnswer(ChatLine line)
    {
        string text = line.Text.Length > 0 ? line.Text : line.Thinking;
        if (text.Length == 0) return false;
        System.Windows.Clipboard.SetText(text);
        NoteCopied("Copied " + text.Length.ToString("N0") + " characters.");
        return true;
    }

    public bool CopyLastAnswer()
    {
        string text = LastAnswerText();
        if (text.Length == 0) return false;
        System.Windows.Clipboard.SetText(text);
        NoteCopied("Copied last answer (" + text.Length.ToString("N0") + " characters).");
        return true;
    }

    public void NoteCopied(string notice)
    {
        ChatProgress = notice;
        OnPropertyChanged(nameof(ChatProgress));
    }

    public void AttachImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("The selected image is no longer available.", path);
        BitmapImage preview = new();
        preview.BeginInit();
        preview.CacheOption = BitmapCacheOption.OnLoad;
        preview.UriSource = new Uri(path, UriKind.Absolute);
        preview.DecodePixelWidth = 96;
        preview.EndInit();
        preview.Freeze();
        _attachedImagePath = path;
        AttachedImagePreview = preview;
        OnPropertyChanged(nameof(AttachedImageName));
        OnPropertyChanged(nameof(AttachedImagePreview));
        OnPropertyChanged(nameof(HasAttachedImage));
    }

    public void ClearAttachedImage()
    {
        _attachedImagePath = null;
        AttachedImagePreview = null;
        OnPropertyChanged(nameof(AttachedImageName));
        OnPropertyChanged(nameof(AttachedImagePreview));
        OnPropertyChanged(nameof(HasAttachedImage));
    }

    private string? CreateAttachedImageDataUrl()
    {
        if (_attachedImagePath is null) return null;
        string extension = Path.GetExtension(_attachedImagePath).ToLowerInvariant();
        string mime = extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => throw new InvalidDataException("Attach a PNG, JPEG, GIF, WEBP, or BMP image.")
        };
        return "data:" + mime + ";base64," + Convert.ToBase64String(File.ReadAllBytes(_attachedImagePath));
    }

    private static bool HasAdaptiveDraft(AppSettings settings) =>
        settings.Server.ExtraArguments.Any(static value => value.Equals("--spec-draft-adaptive", StringComparison.OrdinalIgnoreCase));

    private static void ApplyMtpMode(AppSettings settings, string mode)
    {
        List<string> args = [];
        IReadOnlyList<string> source = settings.Server.ExtraArguments;
        for (int i = 0; i < source.Count; i++)
        {
            string value = source[i];
            if (value.Equals("--spec-draft-adaptive", StringComparison.OrdinalIgnoreCase)) continue;
            if (value.Equals("--spec-draft-n-min", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < source.Count) i++;
                continue;
            }
            args.Add(value);
        }
        InferenceProfile profile = settings.GetActiveProfile();
        switch (mode)
        {
            case "off":
                settings.Server.SpecType = "none";
                break;
            case "fixed-1":
            case "fixed-2":
            case "fixed-3":
            case "fixed-4":
            case "fixed-5":
            case "fixed-6":
                settings.Server.SpecType = "draft-mtp";
                profile.MtpNMax = int.Parse(mode[^1..], System.Globalization.CultureInfo.InvariantCulture);
                break;
            case "adaptive-2-2":
            case "adaptive-2-3":
            case "adaptive-2-4":
            case "adaptive-2-5":
            case "adaptive-2-6":
                settings.Server.SpecType = "draft-mtp";
                profile.MtpNMax = int.Parse(mode[^1..], System.Globalization.CultureInfo.InvariantCulture);
                args.Add("--spec-draft-adaptive");
                args.Add("--spec-draft-n-min");
                args.Add("2");
                break;
            default:
                throw new InvalidDataException($"Unsupported MTP mode '{mode}'.");
        }
        settings.Server.ExtraArguments = args;
    }

    private static string GetMtpMode(AppSettings settings)
    {
        if (settings.Server.SpecType.Equals("none", StringComparison.OrdinalIgnoreCase)) return "off";
        InferenceProfile profile = settings.GetActiveProfile();
        bool adaptive = HasAdaptiveDraft(settings);
        int minimum = ReadDraftMinimum(settings.Server.ExtraArguments);
        if (adaptive && profile.MtpNMax is >= 2 and <= 6 && minimum == 2) return "adaptive-2-" + profile.MtpNMax.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return profile.MtpNMax is >= 1 and <= 6 ? "fixed-" + profile.MtpNMax.ToString(System.Globalization.CultureInfo.InvariantCulture) : "fixed-4";
    }

    private static int ReadDraftMinimum(IReadOnlyList<string> arguments)
    {
        for (int i = 0; i + 1 < arguments.Count; i++)
        {
            if (arguments[i].Equals("--spec-draft-n-min", StringComparison.OrdinalIgnoreCase) && int.TryParse(arguments[i + 1], out int value)) return value;
        }
        return 0;
    }

    public void RefreshLog()
    {
        string path = Path.Combine(Paths.LogsDirectory, "server-error.log");
        if (!File.Exists(path))
        {
            ServerLog = "No server log yet.";
            OnPropertyChanged(nameof(ServerLog));
            return;
        }
        try
        {
            ServerLog = string.Join(Environment.NewLine, File.ReadLines(path).TakeLast(40));
        }
        catch (IOException)
        {
            ServerLog = "Log is busy.";
        }
        OnPropertyChanged(nameof(ServerLog));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _chat?.Cancel();
        if (_sendFinished is not null) await _sendFinished.Task.ConfigureAwait(true);
        if (_sessionFinished is not null) await _sessionFinished.Task.ConfigureAwait(true);
        try { await SaveCurrentSessionAsync().ConfigureAwait(true); }
        catch (Exception ex) { ReportSessionError(ex); }
        if (_control is not null) await _control.DisposeAsync().ConfigureAwait(false);
        await Supervisor.DisposeAsync().ConfigureAwait(false);
    }

    private void LoadFormFromSettings()
    {
        ActiveProfile = _settings.ActiveProfile;
        ProfileNames = [.. _settings.Profiles.Keys];
        InferenceProfile profile = _settings.GetActiveProfile();
        Thinking = profile.Thinking;
        ReasoningEffort = profile.ReasoningEffort;
        Temperature = profile.Temperature;
        TopP = profile.TopP;
        TopK = profile.TopK;
        MinP = profile.MinP;
        PresencePenalty = profile.PresencePenalty;
        RepetitionPenalty = profile.RepetitionPenalty;
        MaxOutputTokens = profile.MaxOutputTokens;
        MtpNMax = profile.MtpNMax;
        ContextSize = profile.ContextSize;
        InferencePort = _settings.Server.Port;
        SeedText = profile.Seed?.ToString() ?? string.Empty;
        AdaptiveDraft = HasAdaptiveDraft(_settings);
        SpecType = string.IsNullOrWhiteSpace(_settings.Server.SpecType) ? "draft-mtp" : _settings.Server.SpecType;
        MtpMode = GetMtpMode(_settings);
        SpecDraftPMin = _settings.Server.SpecDraftPMin;
        VisionDetail = _settings.Server.VisionDetail;
        BatchSize = _settings.Server.BatchSize;
        UBatchSize = _settings.Server.UBatchSize;
        SystemPrompt = _settings.Chat.SystemPrompt;
        Endpoint = $"http://{_settings.Server.Host}:{_settings.Server.Port}/v1";
        OnPropertyChanged(nameof(ActiveProfile));
        OnPropertyChanged(nameof(ProfileNames));
        OnPropertyChanged(nameof(Thinking));
        OnPropertyChanged(nameof(ReasoningEffort));
        OnPropertyChanged(nameof(Temperature));
        OnPropertyChanged(nameof(TopP));
        OnPropertyChanged(nameof(TopK));
        OnPropertyChanged(nameof(MinP));
        OnPropertyChanged(nameof(PresencePenalty));
        OnPropertyChanged(nameof(RepetitionPenalty));
        OnPropertyChanged(nameof(MaxOutputTokens));
        OnPropertyChanged(nameof(MtpNMax));
        OnPropertyChanged(nameof(MtpMode));
        OnPropertyChanged(nameof(SpecDraftPMin));
        OnPropertyChanged(nameof(VisionDetail));
        OnPropertyChanged(nameof(BatchSize));
        OnPropertyChanged(nameof(UBatchSize));
        OnPropertyChanged(nameof(ContextSize));
        OnPropertyChanged(nameof(InferencePort));
        OnPropertyChanged(nameof(SeedText));
        OnPropertyChanged(nameof(AdaptiveDraft));
        OnPropertyChanged(nameof(SpecType));
        OnPropertyChanged(nameof(SystemPrompt));
        OnPropertyChanged(nameof(Endpoint));
    }

    private void RefreshStatus()
    {
        ServerStatus status = Supervisor.Status;
        ServerState = status.State.ToString();
        Endpoint = status.BaseUri?.ToString() ?? $"http://{_settings.Server.Host}:{_settings.Server.Port}/v1";
        StatusLine = $"{ServerState}   {ActiveProfile}   {Endpoint}";
        if (status.Configuration is { } running)
        {
            TelemetryText = $"Active: {running.MtpMode}   Spec Draft P-Min {running.SpecDraftPMin?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "Unknown"}   Batch {running.BatchSize}   UBatch {running.UBatchSize}";
            OnPropertyChanged(nameof(TelemetryText));
        }
        if (status.LastError is string error && error.Length > 0) LastError = error;
        OnPropertyChanged(nameof(ServerState));
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(Endpoint));
        OnPropertyChanged(nameof(LastError));
        OnPropertyChanged(nameof(CanSend));
        if (!_busy)
        {
            ChatProgress = Supervisor.Status.State switch
            {
                ServerLifecycleState.Running or ServerLifecycleState.External => "Server running. Type a prompt and press Enter or Send.",
                ServerLifecycleState.Starting => "Starting server… model load can take about a minute.",
                ServerLifecycleState.Faulted => string.IsNullOrWhiteSpace(LastError) ? "Server failed. Click Start to retry." : LastError,
                _ => "Click Start, wait until the header says Running, then send."
            };
            OnPropertyChanged(nameof(ChatProgress));
        }
        StatusChanged?.Invoke();
    }

    private void ShowSendBlock(string why)
    {
        LastError = why;
        ChatProgress = why;
        Messages.Add(new ChatLine("FlashNext", why, false, true));
        OnPropertyChanged(nameof(LastError));
        OnPropertyChanged(nameof(ChatProgress));
        TranscriptUpdated?.Invoke();
    }

    private async Task TrackLiveSpeedAsync(LiveTimingLogReader? reader, Func<bool> hasOutput, int? processId, CancellationToken cancellationToken)
    {
        long lastTimingTick = Environment.TickCount64;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(750, cancellationToken).ConfigureAwait(false);
                // Snapshot before reading: a queued request's earlier timings must not be used
                // just because our first output happens to arrive during this read.
                bool receiving = hasOutput();
                LiveGenerationTiming? timing = null;
                if (reader is not null && Supervisor.Status.ProcessId == processId)
                {
                    try { timing = await reader.ReadAsync(cancellationToken).ConfigureAwait(false); }
                    catch (IOException) { } // Retry next tick; optional telemetry must not interrupt the reply.
                    catch (UnauthorizedAccessException) { }
                }
                if (!receiving) { lastTimingTick = Environment.TickCount64; continue; }
                if (timing is not null)
                {
                    lastTimingTick = Environment.TickCount64;
                    RunOnUi(() => { if (!cancellationToken.IsCancellationRequested) SetLiveSpeed(timing.RecentTps, "LIVE • ~3 SECOND WINDOW"); });
                }
                else if (Environment.TickCount64 - lastTimingTick > 10000)
                {
                    RunOnUi(() => { if (!cancellationToken.IsCancellationRequested) SetLiveSpeed(null, "GENERATING • TIMING UNAVAILABLE"); });
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SetLiveSpeed(double? tokensPerSecond, string state)
    {
        _liveTpsText = tokensPerSecond is double value && double.IsFinite(value) && value > 0
            ? value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            : "—";
        _liveTpsState = state;
        OnPropertyChanged(nameof(LiveTpsText));
        OnPropertyChanged(nameof(LiveTpsState));
        StatusChanged?.Invoke();
    }

    private bool BeginServerOp(string notice)
    {
        if (_serverOp)
        {
            SetNotice("Already starting, stopping, or restarting. Wait until the header changes.", error: true);
            return false;
        }
        _serverOp = true;
        LastError = string.Empty;
        SetNotice(notice);
        return true;
    }

    private void EndServerOp()
    {
        _serverOp = false;
        RefreshStatus();
        RefreshLog();
    }

    private void SetNotice(string notice, bool error = false)
    {
        StatusLine = notice;
        ChatProgress = notice;
        SettingsNote = notice;
        if (error) LastError = notice;
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(ChatProgress));
        OnPropertyChanged(nameof(SettingsNote));
        OnPropertyChanged(nameof(LastError));
    }

    private InferenceProfile ProfileForRequest()
    {
        InferenceProfile source = _settings.GetActiveProfile();
        return new InferenceProfile
        {
            Thinking = Thinking,
            ReasoningEffort = ReasoningEffort,
            Temperature = Temperature,
            TopP = TopP,
            TopK = TopK,
            MinP = MinP,
            PresencePenalty = PresencePenalty,
            RepetitionPenalty = RepetitionPenalty,
            MaxOutputTokens = MaxOutputTokens,
            ContextSize = ContextSize,
            MtpNMax = MtpNMax,
            Seed = int.TryParse(SeedText, out int seed) ? seed : null
        };
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.Invoke(action);
    }

    private static async Task FlushLastAnswerAsync(string path, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { await AtomicFile.WriteTextAsync(path, text).ConfigureAwait(false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static long GetFileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
    }

    private static string ReadAppendedText(string path, long offset)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (offset > stream.Length) offset = 0;
            stream.Seek(offset, SeekOrigin.Begin);
            using StreamReader reader = new(stream, Encoding.UTF8, true, 4096, false);
            return reader.ReadToEnd();
        }
        catch (IOException) { return string.Empty; }
    }

    private static string FormatGiB(long? bytes) => bytes is long value && value > 0 ? $"{value / (1024d * 1024d * 1024d):N1} GiB" : "N/A";

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record SpeculationOption(string Value, string DisplayName);
public sealed record MtpModeOption(string Value, string DisplayName);
public sealed record DraftConfidenceOption(double Value, string DisplayName);
public sealed record VisionDetailOption(string Value, string DisplayName);

public sealed class ChatLine : INotifyPropertyChanged
{
    private string _text;
    private string _thinking = string.Empty;
    private string _status = string.Empty;
    private bool _pending;
    private bool _isError;
    private PromptRunReport? _runReport;

    public ChatLine(string role, string text, bool pending, bool isError = false)
    {
        Role = role;
        _text = text;
        _pending = pending;
        _isError = isError;
    }

    public string Role { get; }
    public string Text { get => _text; set { _text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); } }
    public string Thinking { get => _thinking; set { _thinking = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thinking))); } }
    public string Status { get => _status; set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); } }
    public bool Pending { get => _pending; set { _pending = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Pending))); } }
    public bool IsError { get => _isError; set { _isError = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsError))); } }
    // Kept off the visual transcript but included when this user turn is replayed.
    public string? ImageDataUrl { get; init; }
    public string? RunId { get; init; }
    public PromptRunReport? RunReport
    {
        get => _runReport;
        set { _runReport = value; PropertyChanged?.Invoke(this, new(nameof(ReportHeadline))); PropertyChanged?.Invoke(this, new(nameof(ReportDetails))); }
    }
    public string ReportHeadline => RunReport is { } run ? PromptRunFormatter.Headline(run) : "Report not recorded";
    public string ReportDetails => RunReport is { } run ? PromptRunFormatter.Format(run) : "This older reply has no saved performance or settings report.";
    public event PropertyChangedEventHandler? PropertyChanged;
}
