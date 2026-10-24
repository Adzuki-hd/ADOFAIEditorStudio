using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace FrameCacheSpike;

internal static class WorkerMain
{
    private static readonly object OutputLock = new();

    private sealed class FrameCounter
    {
        internal int Value;
    }

    internal static int Run()
    {
        return RunAsync().GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync()
    {
        string? loadedIdentity = null;
        FrameCounter frameCounter = new();
        CancellationTokenSource? renderCancellation = null;
        Task? renderTask = null;

        async Task StopRenderAsync()
        {
            if (renderCancellation is not null)
            {
                renderCancellation.Cancel();
            }

            if (renderTask is not null)
            {
                try
                {
                    await renderTask.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    Write(new WorkerEvent
                    {
                        Type = "error",
                        Error = "render task failed: " + exception
                    });
                }
            }

            renderTask = null;
            renderCancellation?.Dispose();
            renderCancellation = null;
        }

        Write(new WorkerEvent { Type = "ready" });

        try
        {
            while (true)
            {
                string? line = await Console.In.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    await StopRenderAsync().ConfigureAwait(false);
                    return 0;
                }

                WorkerRequest request;
                try
                {
                    request = ProtocolJson.Deserialize<WorkerRequest>(line);
                }
                catch (Exception exception)
                {
                    Write(new WorkerEvent { Type = "error", Error = "invalid request: " + exception.Message });
                    continue;
                }

                try
                {
                    switch (request.Type)
                    {
                        case "load":
                            await StopRenderAsync().ConfigureAwait(false);
                            loadedIdentity = request.RenderIdentity;
                            Write(new WorkerEvent
                            {
                                Type = "loaded",
                                SessionId = request.SessionId,
                                RenderIdentity = loadedIdentity
                            });
                            break;

                        case "render_window":
                            await StopRenderAsync().ConfigureAwait(false);
                            renderCancellation = new CancellationTokenSource();
                            renderTask = RunRenderWindowAsync(
                                request,
                                loadedIdentity,
                                frameCounter,
                                renderCancellation.Token);
                            break;

                        case "cancel":
                            if (renderCancellation is not null)
                            {
                                renderCancellation.Cancel();
                            }

                            await StopRenderAsync().ConfigureAwait(false);
                            Write(new WorkerEvent
                            {
                                Type = "cancelled",
                                SessionId = request.SessionId,
                                Generation = request.Generation
                            });
                            break;

                        case "shutdown":
                            await StopRenderAsync().ConfigureAwait(false);
                            Write(new WorkerEvent { Type = "stopped" });
                            return 0;

                        default:
                            Write(new WorkerEvent
                            {
                                Type = "error",
                                SessionId = request.SessionId,
                                Error = "unknown request type: " + request.Type
                            });
                            break;
                    }
                }
                catch (Exception exception)
                {
                    Write(new WorkerEvent
                    {
                        Type = "error",
                        SessionId = request.SessionId,
                        Error = exception.ToString()
                    });
                }
            }
        }
        finally
        {
            await StopRenderAsync().ConfigureAwait(false);
        }
    }

    private static async Task RunRenderWindowAsync(
        WorkerRequest request,
        string? loadedIdentity,
        FrameCounter frameCounter,
        CancellationToken cancellationToken)
    {
        try
        {
            await RenderWindowAsync(
                request,
                loadedIdentity,
                frameCounter,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Write(new WorkerEvent
            {
                Type = "window_cancelled",
                SessionId = request.SessionId,
                RenderIdentity = request.RenderIdentity,
                Generation = request.Generation
            });
        }
        catch (Exception exception)
        {
            Write(new WorkerEvent
            {
                Type = "error",
                SessionId = request.SessionId,
                RenderIdentity = request.RenderIdentity,
                Generation = request.Generation,
                Error = exception.ToString()
            });
        }
    }

    private static async Task RenderWindowAsync(
        WorkerRequest request,
        string? loadedIdentity,
        FrameCounter frameCounter,
        CancellationToken cancellationToken)
    {
        if (request.SessionId is null
            || request.RenderIdentity is null
            || request.StartSeconds is null
            || request.EndSeconds is null
            || request.Width is null
            || request.Height is null)
        {
            throw new InvalidOperationException("render_window 缺少必需字段。");
        }

        if (!string.Equals(request.RenderIdentity, loadedIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("render identity 与已加载谱面不一致。");
        }

        double start = Math.Max(0d, request.StartSeconds.Value);
        double end = Math.Max(start, request.EndSeconds.Value);
        int frameRate = Math.Clamp(request.FrameRate ?? 30, 1, 240);
        int frameCount = (int)Math.Ceiling((end - start) * frameRate) + 1;
        int delayMilliseconds = Math.Clamp(request.DelayMilliseconds ?? 0, 0, 1000);

        for (int i = 0; i < frameCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            double time = Math.Min(end, start + i / (double)frameRate);
            int frameIndex = Interlocked.Increment(ref frameCounter.Value) - 1;
            Write(new WorkerEvent
            {
                Type = "frame",
                SessionId = request.SessionId,
                RenderIdentity = request.RenderIdentity,
                TimeSeconds = time,
                FrameIndex = frameIndex,
                Generation = request.Generation,
                PayloadToken = "native-frame://" + request.RenderIdentity + "/" + time.ToString("F6", CultureInfo.InvariantCulture),
                PixelFormat = "RGBA32",
                Width = request.Width.Value,
                Height = request.Height.Value
            });

            if (delayMilliseconds > 0)
            {
                await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        Write(new WorkerEvent
        {
            Type = "window_complete",
            SessionId = request.SessionId,
            RenderIdentity = request.RenderIdentity,
            Generation = request.Generation,
            FrameCount = frameCount
        });
    }

    private static void Write(WorkerEvent message)
    {
        lock (OutputLock)
        {
            Console.WriteLine(ProtocolJson.Serialize(message));
            Console.Out.Flush();
        }
    }
}
