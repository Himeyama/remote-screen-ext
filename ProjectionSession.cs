using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RemoteScreen;

sealed class ProjectionSession : IAsyncDisposable
{
    const int Port = 8765;
    const string DeviceCookie = "RemoteScreenDevice";
    readonly CancellationTokenSource _cancel = new();
    readonly string _key = RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 6);
    readonly WebApplication _app;
    readonly Display _display;
    readonly RememberedDevices _devices = new();
    readonly LatestFrame _frames = new();
    Task? _capture;
    int _viewers;

    public ProjectionSession(Display display)
    {
        _display = display;
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddFixedWindowLimiter("share-code", settings =>
            {
                settings.PermitLimit = 30;
                settings.Window = TimeSpan.FromMinutes(1);
            });
        });
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 1024;
            options.Listen(IPAddress.Any, Port);
        });
        _app = builder.Build();

        using Stream page = typeof(ProjectionSession).Assembly.GetManifestResourceStream("RemoteScreen.Viewer.html")
            ?? throw new InvalidOperationException("投影ページを読み込めませんでした。");
        using StreamReader reader = new(page);
        string html = reader.ReadToEnd();

        _app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
                $"connect-src 'self' ws://{context.Request.Host.ToUriComponent()}; " +
                "frame-ancestors 'none'; base-uri 'none'";
            await next(context);
        });
        _app.UseRouting();
        _app.UseRateLimiter();
        _app.UseWebSockets();
        _app.MapGet("/", () => Results.Content(html, "text/html; charset=utf-8"));
        _app.MapPost("/connect", ConnectDevice).RequireRateLimiting("share-code");
        _app.MapGet("/status", (HttpRequest request) => IsRemembered(request) ? Results.NoContent() : Results.Unauthorized());
        _app.MapGet("/stream", StreamFramesAsync);
    }

    public event Action<string>? CaptureFailed;
    internal event Action? ViewerCountChanged;

    internal string DisplayLabel => _display.Label;
    internal int ViewerCount => Volatile.Read(ref _viewers);

    public async Task StartAsync()
    {
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _capture = Task.Run(() => CaptureLoopAsync(ready));
        await ready.Task;
        await _app.StartAsync();
    }

    public string GetLink(IPAddress address) => $"http://{address}:{Port}/#{_key}";

    public string Code => _key;

    IResult ConnectDevice(HttpContext context, CodeEntry entry)
    {
        if (IsRemembered(context.Request))
        {
            return Results.NoContent();
        }

        if (entry.Code is null || !IsShareKey(entry.Code))
        {
            return Results.Unauthorized();
        }

        string token;
        try
        {
            token = _devices.Add();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        context.Response.Cookies.Append(DeviceCookie, token, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = RememberedDevices.Lifetime
        });
        return Results.NoContent();
    }

    bool IsRemembered(HttpRequest request) => _devices.Contains(request.Cookies[DeviceCookie]);

    async Task StreamFramesAsync(HttpContext context)
    {
        if (!IsRemembered(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        HttpRequest request = context.Request;
        string origin = $"{request.Scheme}://{request.Host.ToUriComponent()}";
        if (!context.WebSockets.IsWebSocketRequest || !string.Equals(request.Headers.Origin, origin,
            StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        using CancellationTokenSource disconnect = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, _cancel.Token);
        CancellationToken token = disconnect.Token;
        ScreenFrame? previous = null;
        byte[] acknowledgement = new byte[1];
        Interlocked.Increment(ref _viewers);
        try
        {
            ViewerCountChanged?.Invoke();
            while (socket.State == WebSocketState.Open)
            {
                Task<ScreenFrame> available = _frames.AfterAsync(previous, token).AsTask();
                Task<ValueWebSocketReceiveResult> reply = socket.ReceiveAsync(acknowledgement.AsMemory(), token).AsTask();
                if (await Task.WhenAny(available, reply) == reply)
                {
                    await reply;
                    break;
                }

                ScreenFrame frame = await available;
                disconnect.CancelAfter(TimeSpan.FromSeconds(15));
                int length = frame.PacketSize(previous);
                byte[] packet = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    frame.WritePacket(previous, packet.AsSpan(0, length));
                    await socket.SendAsync(packet.AsMemory(0, length), WebSocketMessageType.Binary, endOfMessage: true, token);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(packet);
                }
                ValueWebSocketReceiveResult received = await reply;
                if (received.MessageType != WebSocketMessageType.Binary ||
                    !received.EndOfMessage || received.Count != 1 || acknowledgement[0] != 1)
                {
                    break;
                }

                disconnect.CancelAfter(Timeout.InfiniteTimeSpan);
                previous = frame;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            await disconnect.CancelAsync();
            Interlocked.Decrement(ref _viewers);
            ViewerCountChanged?.Invoke();
        }
    }

    bool IsShareKey(string supplied) => supplied.Length == _key.Length && CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(_key), Encoding.UTF8.GetBytes(supplied));

    async Task CaptureLoopAsync(TaskCompletionSource ready)
    {
        TimeSpan frameInterval = TimeSpan.FromMilliseconds(17);
        TimeSpan directXInterval = TimeSpan.FromMilliseconds(8);
        TimeSpan idleInterval = TimeSpan.FromMilliseconds(250);
        using PeriodicTimer timer = new(frameInterval);
        DesktopCapture.Encoder? encoder = null;
        long lastMonitorCheck = 0;
        long lastCapture = 0;
        try
        {
            do
            {
                if (ViewerCount == 0 && lastCapture != 0 &&
                    Stopwatch.GetElapsedTime(lastCapture) < idleInterval)
                {
                    continue;
                }

                lastCapture = Stopwatch.GetTimestamp();
                if (encoder is null || Stopwatch.GetElapsedTime(lastMonitorCheck) >= idleInterval)
                {
                    lastMonitorCheck = lastCapture;
                    Display display = GetDisplay();
                    if (encoder is null || encoder.Display != display)
                    {
                        encoder?.Dispose();
                        encoder = new DesktopCapture.Encoder(display);
                    }
                }

                if (encoder.CaptureChangedFrame() is { } frame)
                {
                    _frames.Publish(frame);
                }
                TimeSpan interval = encoder.UsesDesktopDuplication ? directXInterval : frameInterval;
                if (timer.Period != interval)
                {
                    timer.Period = interval;
                }
                ready.TrySetResult();
            }
            while (await timer.WaitForNextTickAsync(_cancel.Token));
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
            ready.TrySetCanceled(_cancel.Token);
        }
        catch (Exception exception)
        {
            if (!ready.TrySetException(exception))
            {
                CaptureFailed?.Invoke(exception.Message);
            }
        }
        finally
        {
            encoder?.Dispose();
        }
    }

    Display GetDisplay() => DesktopCapture.ListDisplays().Find(candidate => candidate.Name == _display.Name)
        ?? throw new InvalidOperationException("共有するディスプレイが見つかりません。");

    public async ValueTask DisposeAsync()
    {
        _cancel.Cancel();
        try
        {
            if (_capture is not null)
            {
                await _capture;
            }
        }
        finally
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
            try
            {
                await _app.StopAsync(timeout.Token);
            }
            finally
            {
                try
                {
                    await _app.DisposeAsync();
                }
                finally
                {
                    _cancel.Dispose();
                }
            }
        }
    }

    sealed record CodeEntry(string? Code);
}
