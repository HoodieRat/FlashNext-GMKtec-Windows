using System.Net;
using System.Text;
using FlashNext.Core.Interfaces;
using FlashNext.Infrastructure.Windows.System;

namespace FlashNext.Infrastructure.Windows.Control;

public sealed class ControlApiServer : IAsyncDisposable
{
    private readonly ControlApiRouter _router;
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _run;
    private Task? _loop;

    public ControlApiServer(ISettingsStore settings, IRuntimeSupervisor supervisor, ISecretStore secrets, PlatformPaths paths)
    {
        _router = new ControlApiRouter(settings, supervisor, secrets, paths);
    }

    public string Prefix { get; private set; } = string.Empty;

    public Task StartAsync(int port, CancellationToken cancellationToken = default)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        Prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Clear();
        _listener.Prefixes.Add(Prefix);
        _listener.Start();
        _run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => ListenAsync(_run.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        try { _run?.Cancel(); } catch (ObjectDisposedException) { }
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _run?.Dispose();
        try { _listener.Close(); } catch (ObjectDisposedException) { }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException or OperationCanceledException) { return; }
            _ = Task.Run(() => ServeAsync(context, cancellationToken), CancellationToken.None);
        }
    }

    private async Task ServeAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            string body;
            using (StreamReader reader = new(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }
            IPAddress? remote = context.Request.RemoteEndPoint?.Address;
            ControlApiResult result = await _router.HandleAsync(context.Request.HttpMethod, context.Request.Url?.AbsolutePath ?? "/", context.Request.Headers["Authorization"], body, remote, cancellationToken).ConfigureAwait(false);
            byte[] bytes = Encoding.UTF8.GetBytes(result.Body);
            context.Response.StatusCode = result.StatusCode;
            context.Response.ContentType = result.ContentType + "; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try { context.Response.StatusCode = 500; } catch (ObjectDisposedException) { }
        }
        finally
        {
            try { context.Response.Close(); } catch (ObjectDisposedException) { }
        }
    }
}
