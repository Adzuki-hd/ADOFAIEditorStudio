using System;

namespace FrameCacheSpike;

internal enum ScrubStatus
{
    Inactive,
    WaitingForNative,
    ShowingCachedNative,
    HeldCachedNative,
    NoFrame,
    Error
}

internal sealed record RenderRequest(
    string SessionId,
    string RenderIdentity,
    double TargetSeconds,
    double LookAheadSeconds,
    long Generation,
    int FrameRate);

internal sealed record ScrubView(
    ScrubStatus Status,
    string? RenderIdentity,
    double TargetSeconds,
    NativeFrame? DisplayedFrame,
    RenderRequest? PendingRequest,
    string Detail);

internal sealed class ScrubController
{
    private readonly FrameCache cache;
    private readonly Action<RenderRequest> requestRenderer;
    private readonly double toleranceSeconds;
    private readonly int frameRate;
    private bool active;
    private bool holdRequested;
    private string? sessionId;
    private string? renderIdentity;
    private double targetSeconds;
    private long generation;
    private RenderRequest? pendingRequest;
    private NativeFrame? displayedFrame;
    private ScrubStatus status = ScrubStatus.Inactive;

    internal ScrubController(
        FrameCache cache,
        Action<RenderRequest> requestRenderer,
        double toleranceSeconds = 0.0015d,
        int frameRate = 30)
    {
        if (frameRate < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate));
        }

        this.cache = cache;
        this.requestRenderer = requestRenderer;
        this.frameRate = frameRate;
        this.toleranceSeconds = Math.Max(toleranceSeconds, (0.5d / frameRate) + 0.000001d);
    }

    internal ScrubView Begin(string sessionId, string identity, double initialSeconds)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("SessionId 不能为空。", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(identity))
        {
            throw new ArgumentException("Render identity 不能为空。", nameof(identity));
        }

        active = true;
        holdRequested = false;
        this.sessionId = sessionId;
        renderIdentity = identity;
        targetSeconds = Math.Max(0d, initialSeconds);
        pendingRequest = null;
        displayedFrame = null;
        generation++;
        status = ScrubStatus.NoFrame;
        return Snapshot("scrub started; no frame requested yet");
    }

    internal ScrubView SetTarget(double seconds)
    {
        if (!active || sessionId is null || renderIdentity is null)
        {
            status = ScrubStatus.Error;
            return Snapshot("scrub is not active");
        }

        targetSeconds = Math.Max(0d, seconds);
        if (cache.TryGet(sessionId, renderIdentity, targetSeconds, toleranceSeconds, out NativeFrame cached))
        {
            displayedFrame = cached;
            pendingRequest = null;
            status = ScrubStatus.ShowingCachedNative;
            return Snapshot("cache hit; displaying native frame");
        }

        if (pendingRequest is not null
            && Math.Abs(pendingRequest.TargetSeconds - targetSeconds) <= toleranceSeconds)
        {
            return Snapshot("same target is already being rendered");
        }

        generation++;
        pendingRequest = new RenderRequest(
            sessionId,
            renderIdentity,
            targetSeconds,
            LookAheadSeconds: 10d,
            generation,
            frameRate);
        status = ScrubStatus.WaitingForNative;
        requestRenderer(pendingRequest);
        return Snapshot("cache miss; retaining last native frame while rendering");
    }

    internal ScrubView OnFrame(NativeFrame frame)
    {
        if (sessionId is null
            || renderIdentity is null
            || !string.Equals(frame.SessionId, sessionId, StringComparison.Ordinal)
            || !string.Equals(frame.RenderIdentity, renderIdentity, StringComparison.Ordinal))
        {
            return Snapshot("ignored frame from another session or identity");
        }

        cache.Add(frame);
        RenderRequest? currentRequest = pendingRequest;
        if ((active || holdRequested)
            && currentRequest is not null
            && string.Equals(frame.SessionId, currentRequest.SessionId, StringComparison.Ordinal)
            && frame.RequestGeneration == currentRequest.Generation
            && string.Equals(frame.RenderIdentity, renderIdentity, StringComparison.Ordinal)
            && Math.Abs(frame.TimeSeconds - targetSeconds) <= toleranceSeconds)
        {
            displayedFrame = frame;
            pendingRequest = null;
            status = holdRequested ? ScrubStatus.HeldCachedNative : ScrubStatus.ShowingCachedNative;
        }

        return Snapshot("native frame received");
    }

    internal ScrubView End()
    {
        active = false;
        holdRequested = true;
        status = pendingRequest is not null
            ? ScrubStatus.WaitingForNative
            : displayedFrame is null ? ScrubStatus.NoFrame : ScrubStatus.HeldCachedNative;
        return Snapshot("scrub ended; frame is held without playback restart");
    }

    internal ScrubView Cancel()
    {
        active = false;
        holdRequested = false;
        pendingRequest = null;
        status = ScrubStatus.Inactive;
        return Snapshot("scrub cancelled");
    }

    internal ScrubView Snapshot(string detail)
    {
        return new ScrubView(status, renderIdentity, targetSeconds, displayedFrame, pendingRequest, detail);
    }
}
