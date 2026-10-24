using System;
using System.Collections.Generic;

namespace FrameCacheSpike;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (Array.Exists(args, value => string.Equals(value, "--worker", StringComparison.Ordinal)))
        {
            return WorkerMain.Run();
        }

        if (Array.Exists(args, value => string.Equals(value, "--benchmark-producer", StringComparison.Ordinal)))
        {
            return FrameTransportBenchmark.RunProducer(args);
        }

        if (Array.Exists(args, value => string.Equals(value, "--self-test", StringComparison.Ordinal)))
        {
            return SelfTest.Run();
        }

        if (Array.Exists(args, value => string.Equals(value, "--benchmark", StringComparison.Ordinal)))
        {
            return FrameTransportBenchmark.Run(args);
        }

        Console.WriteLine("FrameCacheSpike");
        Console.WriteLine("  dotnet run -- --self-test");
        Console.WriteLine("  dotnet run -- --benchmark [--width 1920 --height 1080 --ring-samples 300 --frames 90]");
        Console.WriteLine("  dotnet run -- --worker   (stdio worker; normally launched by the host)");
        return 2;
    }
}

internal static class SelfTest
{
    internal static int Run()
    {
        const string identity = "game=ADOFAI-0.7.7|mods=baseline|level=golden|visual=1920x1080|seed=locked";
        const string sessionId = "spike-session";
        FrameCache cache = new(512);
        List<RenderRequest> requests = new();
        ScrubController controller = new(cache, request => requests.Add(request));

        using WorkerProcess worker = WorkerProcess.Start();
        Assert(worker.ReadEvent().Type == "ready", "worker handshake");

        worker.Send(new WorkerRequest
        {
            Type = "load",
            SessionId = sessionId,
            RenderIdentity = identity
        });
        WorkerEvent loaded = worker.WaitFor("loaded");
        Assert(loaded.RenderIdentity == identity, "worker loaded identity");

        ScrubView started = controller.Begin(sessionId, identity, 0d);
        Assert(started.Status == ScrubStatus.NoFrame, "scrub starts without inventing a frame");

        ScrubView miss = controller.SetTarget(4.5d);
        Assert(miss.Status == ScrubStatus.WaitingForNative, "uncached target waits for native frame");
        Assert(requests.Count == 1, "one render request is emitted");
        ScrubView duplicateMiss = controller.SetTarget(4.5d);
        Assert(duplicateMiss.Status == ScrubStatus.WaitingForNative, "duplicate target keeps waiting state");
        Assert(requests.Count == 1, "duplicate target does not emit another request");

        RenderRequest request = requests[0];
        controller.OnFrame(new NativeFrame(
            sessionId,
            identity,
            4.5d,
            -1,
            "stale-frame",
            "RGBA32",
            1920,
            1080,
            request.Generation - 1,
            DateTimeOffset.UtcNow));
        Assert(controller.Snapshot("after stale frame").Status == ScrubStatus.WaitingForNative, "stale generation cannot display over the active target");

        worker.Send(new WorkerRequest
        {
            Type = "render_window",
            SessionId = sessionId,
            RenderIdentity = request.RenderIdentity,
            StartSeconds = 0d,
            EndSeconds = 10d,
            Width = 1920,
            Height = 1080,
            FrameRate = 30,
            Generation = request.Generation
        });

        WorkerEvent complete = worker.WaitFor(
            "window_complete",
            message =>
            {
                if (message.Type == "frame")
                {
                    controller.OnFrame(message.ToNativeFrame());
                }
            });
        Assert(complete.FrameCount == 301, "ten-second window has deterministic 30 fps frame count");
        Assert(cache.Count == 301, "all native frames are cached");
        Assert(controller.Snapshot("after render").Status == ScrubStatus.ShowingCachedNative, "target becomes visible when native frame arrives");

        ScrubView forward = controller.SetTarget(9.91d);
        Assert(forward.Status == ScrubStatus.ShowingCachedNative, "forward drag hits cached native frame");
        ScrubView reverse = controller.SetTarget(0.116d);
        Assert(reverse.Status == ScrubStatus.ShowingCachedNative, "reverse drag reads cached native frame");
        Assert(reverse.DisplayedFrame is not null, "reverse drag has a displayed frame");
        NativeFrame reverseFrame = reverse.DisplayedFrame!;
        Assert(Math.Abs(reverseFrame.TimeSeconds - 0.1d) < 0.000001d, "arbitrary target snaps to the nearest cached frame");
        Assert(reverseFrame.RequestGeneration == request.Generation, "displayed frame belongs to the active request generation");

        int requestCountBeforeEnd = requests.Count;
        ScrubView held = controller.End();
        Assert(held.Status == ScrubStatus.HeldCachedNative, "release holds the last native frame");
        Assert(requests.Count == requestCountBeforeEnd, "release does not restart native playback");

        string editedIdentity = identity + "|edited";
        controller.Begin(sessionId, editedIdentity, 0d);
        ScrubView invalidated = controller.SetTarget(4.5d);
        Assert(invalidated.Status == ScrubStatus.WaitingForNative, "edited snapshot misses old cache");
        Assert(invalidated.DisplayedFrame is null, "edited snapshot clears the previous displayed frame");

        NativeFrame foreignFrame = new(
            "other-session",
            editedIdentity,
            4.5d,
            -2,
            "foreign-frame",
            "RGBA32",
            1920,
            1080,
            invalidated.PendingRequest!.Generation,
            DateTimeOffset.UtcNow);
        ScrubView foreign = controller.OnFrame(foreignFrame);
        Assert(foreign.DisplayedFrame is null, "foreign session frame is ignored");

        ScrubView releasedBeforeFrame = controller.End();
        Assert(releasedBeforeFrame.Status == ScrubStatus.WaitingForNative, "release keeps an uncached target pending");
        NativeFrame editedFrame = new(
            sessionId,
            editedIdentity,
            4.5d,
            -3,
            "edited-frame",
            "RGBA32",
            1920,
            1080,
            invalidated.PendingRequest!.Generation,
            DateTimeOffset.UtcNow);
        ScrubView releasedAfterFrame = controller.OnFrame(editedFrame);
        Assert(releasedAfterFrame.Status == ScrubStatus.HeldCachedNative, "late native frame completes the held target");
        Assert(releasedAfterFrame.DisplayedFrame?.RenderIdentity == editedIdentity, "held frame belongs to the edited snapshot");

        bool cancelledWindowCompleted = false;
        worker.Send(new WorkerRequest
        {
            Type = "render_window",
            SessionId = sessionId,
            RenderIdentity = identity,
            StartSeconds = 0d,
            EndSeconds = 20d,
            Width = 1920,
            Height = 1080,
            FrameRate = 30,
            DelayMilliseconds = 5,
            Generation = 99
        });
        worker.Send(new WorkerRequest
        {
            Type = "cancel",
            SessionId = sessionId,
            Generation = 99
        });
        WorkerEvent cancelled = worker.WaitFor(
            "cancelled",
            message =>
            {
                if (message.Type == "window_complete" && message.Generation == 99)
                {
                    cancelledWindowCompleted = true;
                }
            });
        Assert(cancelled.Type == "cancelled", "cancel command is acknowledged");
        Assert(!cancelledWindowCompleted, "cancel stops the active render window before completion");

        worker.Send(new WorkerRequest { Type = "shutdown" });
        Assert(worker.WaitFor("stopped").Type == "stopped", "worker shutdown");

        using WorkerProcess timeoutWorker = WorkerProcess.Start();
        Assert(timeoutWorker.ReadEvent().Type == "ready", "timeout worker handshake");
        bool timedOut = false;
        try
        {
            timeoutWorker.WaitFor("never", timeout: TimeSpan.FromMilliseconds(100));
        }
        catch (TimeoutException)
        {
            timedOut = true;
        }

        Assert(timedOut, "host wait has a finite timeout");
        Assert(FrameTransportBenchmark.RunSmokeTest(), "frame transport benchmark smoke test");
        Console.WriteLine("FRAME_CACHE_SPIKE_SELF_TEST_PASS");
        Console.WriteLine("native_frames=301 cache_entries=" + cache.Count + " requests=" + requests.Count);
        Console.WriteLine("strict_mode=uncached_targets_wait; release=held_cached_native; playback_restart=disabled");
        return 0;
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("SELF_TEST_FAIL: " + name);
        }

        Console.WriteLine("PASS " + name);
    }
}
