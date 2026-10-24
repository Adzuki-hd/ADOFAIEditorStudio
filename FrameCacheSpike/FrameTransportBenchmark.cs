using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Threading;
using System.Text.Json;

namespace FrameCacheSpike;

internal sealed record FrameTransportBenchmarkOptions(
    int Width,
    int Height,
    int FrameRate,
    int Frames,
    int RingSamples,
    int RingSlots,
    int SegmentFrames,
    int RandomReads,
    string TempRoot,
    bool FlushToDisk);

internal sealed record FrameTransportMeasurement(
    int SampleCount,
    double ThroughputMiBPerSecond,
    double P50Milliseconds,
    double P95Milliseconds,
    double? P99Milliseconds,
    double MaximumMilliseconds);

internal sealed record RingTransportMeasurement(
    FrameTransportMeasurement ProducerWriteToCopy,
    FrameTransportMeasurement PublishToCopy,
    double ProducerSemaphoreWaitMilliseconds,
    int TornFrames,
    int DroppedFrames,
    double ProducerPeakWorkingSetMiB,
    double ProducerPrivateMemoryMiB,
    double ProducerCpuMilliseconds);

internal sealed record RingTransportBenchmarkResult(
    RingTransportMeasurement Burst,
    RingTransportMeasurement Paced);

internal sealed record FrameTransportBenchmarkResult(
    FrameTransportBenchmarkOptions Options,
    long FrameBytes,
    long TenSecondBytes,
    long SixtySecondBytes,
    long RingBytes,
    string SegmentDrive,
    RingTransportBenchmarkResult Ring,
    FrameTransportMeasurement SegmentWrites,
    FrameTransportMeasurement SegmentReads,
    double DurableFlushMilliseconds,
    double DurableWriteThroughputMiBPerSecond,
    int SegmentFileCount,
    bool RingIntegrityPassed,
    bool SegmentIntegrityPassed);

internal static class FrameTransportBenchmark
{
    private const long MaximumBenchmarkSegmentBytes = 2L * 1024L * 1024L * 1024L;
    private const long MaximumBenchmarkRingBytes = 512L * 1024L * 1024L;
    private const long MaximumHostBufferBytes = 256L * 1024L * 1024L;
    private const long MaximumFrameBytes = 64L * 1024L * 1024L;
    private const int PageSignatureStride = 4096;
    private const int SegmentHeaderBytes = 4096;
    private const int GlobalHeaderBytes = 4096;
    private const int SlotHeaderBytes = 64;
    private const int SequenceStartOffset = 0;
    private const int FrameIndexOffset = 8;
    private const int PayloadLengthOffset = 12;
    private const int ProducerWriteStartedTimestampOffset = 16;
    private const int SequenceEndOffset = 24;
    private const int PublishedTimestampOffset = 32;
    private const int ProducerSemaphoreWaitTicksOffset = 0;
    private const int ProducerFrameCountOffset = 8;

    internal static int Run(string[] args)
    {
        try
        {
            FrameTransportBenchmarkOptions options = ParseOptions(args);
            FrameTransportBenchmarkResult result = Measure(options);
            Print(result);
            return result.RingIntegrityPassed && result.SegmentIntegrityPassed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FRAME_TRANSPORT_BENCHMARK_FAIL");
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    internal static bool RunSmokeTest()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ADOFAIEditorStudio", "FrameCacheSpikeSmoke");
        FrameTransportBenchmarkOptions options = new(
            Width: 64,
            Height: 36,
            FrameRate: 30,
            Frames: 8,
            RingSamples: 8,
            RingSlots: 4,
            SegmentFrames: 3,
            RandomReads: 8,
            tempRoot,
            FlushToDisk: false);
        FrameTransportBenchmarkResult result = Measure(options);
        return result.RingIntegrityPassed
            && result.SegmentIntegrityPassed
            && result.SegmentFileCount == 3
            && result.FrameBytes == 64L * 36L * 4L
            && RunIntegrityUnitChecks();
    }

    internal static int RunProducer(string[] args)
    {
        try
        {
            string mappingPath = ReadRequiredString(args, "--mapping-path");
            string freeSemaphoreName = ReadRequiredString(args, "--free-semaphore");
            string readySemaphoreName = ReadRequiredString(args, "--ready-semaphore");
            string producerReadyEventName = ReadRequiredString(args, "--producer-ready-event");
            string startEventName = ReadRequiredString(args, "--start-event");
            int width = ReadInt(args, "--width", 1920, 16, 8192);
            int height = ReadInt(args, "--height", 1080, 16, 8192);
            int frames = ReadInt(args, "--frames", 300, 4, 10_000);
            int slots = ReadInt(args, "--ring-slots", 8, 3, 32);
            int paceFps = ReadInt(args, "--pace-fps", 0, 0, 240);
            int frameBytes = checked(width * height * 4);
            long slotStride = checked((long)SlotHeaderBytes + frameBytes);
            long mappingBytes = checked(GlobalHeaderBytes + slotStride * slots);

            byte[] source = GC.AllocateUninitializedArray<byte>(frameBytes);
            FillDeterministicFrame(source);
            using Semaphore free = Semaphore.OpenExisting(freeSemaphoreName);
            using Semaphore ready = Semaphore.OpenExisting(readySemaphoreName);
            using EventWaitHandle producerReady = EventWaitHandle.OpenExisting(producerReadyEventName);
            using EventWaitHandle start = EventWaitHandle.OpenExisting(startEventName);
            using FileStream mappingFile = new(
                mappingPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);
            using MemoryMappedFile mapping = MemoryMappedFile.CreateFromFile(
                mappingFile,
                null,
                mappingBytes,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                leaveOpen: true);
            using MemoryMappedViewAccessor headers = mapping.CreateViewAccessor(
                0L,
                mappingBytes,
                MemoryMappedFileAccess.ReadWrite);
            using MemoryMappedViewStream payloads = mapping.CreateViewStream(
                0L,
                mappingBytes,
                MemoryMappedFileAccess.ReadWrite);

            producerReady.Set();
            if (!start.WaitOne(TimeSpan.FromSeconds(15)))
            {
                throw new TimeoutException("Producer 等待基准开始信号超时。");
            }

            long semaphoreWaitTicks = 0L;
            long producerStarted = Stopwatch.GetTimestamp();
            for (int frameIndex = 0; frameIndex < frames; frameIndex++)
            {
                if (paceFps > 0)
                {
                    WaitUntil(producerStarted + frameIndex * Stopwatch.Frequency / paceFps);
                }

                if (!free.WaitOne(0))
                {
                    long waitStarted = Stopwatch.GetTimestamp();
                    if (!free.WaitOne(TimeSpan.FromSeconds(15)))
                    {
                        throw new TimeoutException("Producer 等待空闲 MMF 槽超时。");
                    }
                    semaphoreWaitTicks += Stopwatch.GetTimestamp() - waitStarted;
                }

                int slot = frameIndex % slots;
                long slotOffset = checked(GlobalHeaderBytes + slotStride * slot);
                long completedSequence = checked((frameIndex + 1L) * 2L);
                StampFramePages(source, frameIndex);
                long producerWriteStarted = Stopwatch.GetTimestamp();
                headers.Write(slotOffset + SequenceStartOffset, completedSequence | 1L);
                headers.Write(slotOffset + FrameIndexOffset, frameIndex);
                headers.Write(slotOffset + PayloadLengthOffset, frameBytes);
                headers.Write(slotOffset + ProducerWriteStartedTimestampOffset, producerWriteStarted);
                headers.Write(slotOffset + SequenceEndOffset, completedSequence | 1L);
                payloads.Position = slotOffset + SlotHeaderBytes;
                payloads.Write(source, 0, frameBytes);
                headers.Write(slotOffset + PublishedTimestampOffset, Stopwatch.GetTimestamp());
                headers.Write(slotOffset + SequenceEndOffset, completedSequence);
                Thread.MemoryBarrier();
                headers.Write(slotOffset + SequenceStartOffset, completedSequence);
                ready.Release();
            }

            headers.Write(ProducerSemaphoreWaitTicksOffset, semaphoreWaitTicks);
            headers.Write(ProducerFrameCountOffset, frames);
            Console.WriteLine("FRAME_TRANSPORT_PRODUCER_PASS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FRAME_TRANSPORT_PRODUCER_FAIL");
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static FrameTransportBenchmarkOptions ParseOptions(string[] args)
    {
        int width = ReadInt(args, "--width", 1920, 16, 8192);
        int height = ReadInt(args, "--height", 1080, 16, 8192);
        int frameRate = ReadInt(args, "--fps", 30, 1, 240);
        int frames = ReadInt(args, "--frames", 90, 4, 10_000);
        int ringSamples = ReadInt(args, "--ring-samples", 300, 4, 10_000);
        int ringSlots = ReadInt(args, "--ring-slots", 8, 3, 32);
        int segmentFrames = ReadInt(args, "--segment-frames", 32, 2, 1024);
        int randomReads = ReadInt(args, "--random-reads", 512, 1, 10_000);
        string tempRoot = ReadString(
            args,
            "--temp-root",
            Path.Combine(Path.GetTempPath(), "ADOFAIEditorStudio", "FrameCacheSpike"));
        bool flushToDisk = !Array.Exists(
            args,
            value => string.Equals(value, "--no-durable-flush", StringComparison.Ordinal));

        long frameBytes = checked((long)width * height * 4L);
        if (frameBytes > MaximumFrameBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                "单帧缓冲超过 64 MiB 合成基准安全上限；降低分辨率。");
        }

        long segmentBytes = checked(frameBytes * frames);
        long ringBytes = checked(GlobalHeaderBytes + (frameBytes + SlotHeaderBytes) * ringSlots);
        if (segmentBytes > MaximumBenchmarkSegmentBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                "合成分段文件超过 2 GiB 安全上限；减少 --frames。本基准会单独计算 10 秒和 60 秒理论预算。");
        }
        if (ringBytes > MaximumBenchmarkRingBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                "MMF 环超过 512 MiB 合成基准安全上限；降低分辨率或 --ring-slots。");
        }
        if (checked(frameBytes * 2L) > MaximumHostBufferBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                "Host 源/目标帧缓冲超过 256 MiB 合成基准安全上限；降低分辨率。");
        }

        return new FrameTransportBenchmarkOptions(
            width,
            height,
            frameRate,
            frames,
            ringSamples,
            ringSlots,
            segmentFrames,
            randomReads,
            Path.GetFullPath(tempRoot),
            flushToDisk);
    }

    private static FrameTransportBenchmarkResult Measure(FrameTransportBenchmarkOptions options)
    {
        CleanupStaleRunDirectories(options.TempRoot);
        Directory.CreateDirectory(options.TempRoot);
        string runRoot = Path.Combine(
            options.TempRoot,
            "run-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(runRoot);
        File.WriteAllText(Path.Combine(runRoot, ".frame-cache-spike-owned"), "FrameCacheSpike\n");
        Exception? operationFailure = null;
        try
        {
            return MeasureInOwnedDirectory(options, runRoot);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                DeleteOwnedRunDirectory(runRoot, options.TempRoot);
            }
            catch (Exception cleanupException)
            {
                if (operationFailure is null)
                {
                    throw new IOException("无法清理基准临时目录：" + runRoot, cleanupException);
                }

                Console.Error.WriteLine(
                    "FRAME_TRANSPORT_CLEANUP_FAIL path=" + runRoot + " error=" + cleanupException.Message);
            }
        }
    }

    private static FrameTransportBenchmarkResult MeasureInOwnedDirectory(
        FrameTransportBenchmarkOptions options,
        string runRoot)
    {
        long frameBytesLong = checked((long)options.Width * options.Height * 4L);
        int frameBytes = checked((int)frameBytesLong);
        long ringBytes = checked(GlobalHeaderBytes + (frameBytesLong + SlotHeaderBytes) * options.RingSlots);
        long segmentBytes = checked(frameBytesLong * options.Frames);
        int segmentFileCount = (options.Frames + options.SegmentFrames - 1) / options.SegmentFrames;
        long segmentStorageBytes = checked(segmentBytes + (long)segmentFileCount * SegmentHeaderBytes);
        EnsureSpace(runRoot, Math.Max(ringBytes, segmentStorageBytes));

        byte[] source = GC.AllocateUninitializedArray<byte>(frameBytes);
        byte[] destination = GC.AllocateUninitializedArray<byte>(frameBytes);
        FillDeterministicFrame(source);

        (RingTransportMeasurement burstRing, bool burstIntegrity) =
            MeasureRing(options, runRoot, destination, frameBytes, ringBytes, paceFps: 0);
        (RingTransportMeasurement pacedRing, bool pacedIntegrity) =
            MeasureRing(options, runRoot, destination, frameBytes, ringBytes, paceFps: options.FrameRate);
        (FrameTransportMeasurement segmentWrites, FrameTransportMeasurement segmentReads,
            double durableFlushMilliseconds, double durableWriteThroughput, bool segmentIntegrity,
            string drive, int actualSegmentFileCount) =
            MeasureSegment(options, runRoot, source, destination, frameBytes, segmentBytes);

        return new FrameTransportBenchmarkResult(
            options,
            frameBytesLong,
            checked(frameBytesLong * (options.FrameRate * 10L + 1L)),
            checked(frameBytesLong * (options.FrameRate * 60L + 1L)),
            ringBytes,
            drive,
            new RingTransportBenchmarkResult(burstRing, pacedRing),
            segmentWrites,
            segmentReads,
            durableFlushMilliseconds,
            durableWriteThroughput,
            actualSegmentFileCount,
            burstIntegrity && pacedIntegrity,
            segmentIntegrity);
    }

    private static (RingTransportMeasurement Ring, bool Integrity) MeasureRing(
        FrameTransportBenchmarkOptions options,
        string runRoot,
        byte[] destination,
        int frameBytes,
        long ringBytes,
        int paceFps)
    {
        string id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        string mappingPath = Path.Combine(runRoot, "mmf-" + id + ".ring");
        string freeSemaphoreName = "ADOFAIEditorStudio_Free_" + id;
        string readySemaphoreName = "ADOFAIEditorStudio_Ready_" + id;
        string producerReadyEventName = "ADOFAIEditorStudio_ProducerReady_" + id;
        string startEventName = "ADOFAIEditorStudio_Start_" + id;
        long slotStride = checked((long)SlotHeaderBytes + frameBytes);
        List<double> producerWriteToCopyDurations = new(options.RingSamples);
        List<double> publishToCopyDurations = new(options.RingSamples);
        int tornFrames = 0;
        int droppedFrames = 0;
        bool integrity = true;
        long producerPeakWorkingSet = 0L;
        long producerPeakPrivateBytes = 0L;
        double producerCpuMilliseconds = 0d;
        Process? producer = null;

        try
        {
            using (FileStream file = new(mappingPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                file.SetLength(ringBytes);
            }

            using Semaphore free = new(options.RingSlots, options.RingSlots, freeSemaphoreName, out _);
            using Semaphore ready = new(0, options.RingSlots, readySemaphoreName, out _);
            using EventWaitHandle producerReady = new(
                false,
                EventResetMode.ManualReset,
                producerReadyEventName,
                out _);
            using EventWaitHandle start = new(false, EventResetMode.ManualReset, startEventName, out _);
            using FileStream mappingFile = new(
                mappingPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);
            using MemoryMappedFile mapping = MemoryMappedFile.CreateFromFile(
                mappingFile,
                null,
                ringBytes,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                leaveOpen: true);
            using MemoryMappedViewAccessor headers = mapping.CreateViewAccessor(
                0L,
                ringBytes,
                MemoryMappedFileAccess.ReadWrite);
            using MemoryMappedViewStream payloads = mapping.CreateViewStream(
                0L,
                ringBytes,
                MemoryMappedFileAccess.ReadWrite);

            producer = StartProducer(
                options,
                mappingPath,
                freeSemaphoreName,
                readySemaphoreName,
                producerReadyEventName,
                startEventName,
                paceFps);
            if (!producerReady.WaitOne(TimeSpan.FromSeconds(15)))
            {
                throw new TimeoutException("等待 MMF producer 就绪超时。" + ReadProducerDiagnostics(producer));
            }

            Stopwatch total = Stopwatch.StartNew();
            start.Set();
            for (int expectedFrameIndex = 0; expectedFrameIndex < options.RingSamples; expectedFrameIndex++)
            {
                if (!ready.WaitOne(TimeSpan.FromSeconds(15)))
                {
                    throw new TimeoutException("等待 MMF 帧超时。" + ReadProducerDiagnostics(producer));
                }

                int slot = expectedFrameIndex % options.RingSlots;
                long slotOffset = checked(GlobalHeaderBytes + slotStride * slot);
                long sequenceBefore = headers.ReadInt64(slotOffset + SequenceStartOffset);
                int actualFrameIndex = headers.ReadInt32(slotOffset + FrameIndexOffset);
                int payloadLength = headers.ReadInt32(slotOffset + PayloadLengthOffset);
                long producerWriteStartedTimestamp = headers.ReadInt64(
                    slotOffset + ProducerWriteStartedTimestampOffset);
                long publishedTimestamp = headers.ReadInt64(slotOffset + PublishedTimestampOffset);
                payloads.Position = slotOffset + SlotHeaderBytes;
                ReadExactly(payloads, destination);
                Thread.MemoryBarrier();
                long sequenceAfter = headers.ReadInt64(slotOffset + SequenceStartOffset);
                long sequenceEnd = headers.ReadInt64(slotOffset + SequenceEndOffset);
                long copiedTimestamp = Stopwatch.GetTimestamp();

                bool sequenceValid = IsCompletedSequence(sequenceBefore, sequenceAfter, sequenceEnd);
                if (!sequenceValid)
                {
                    tornFrames++;
                }
                if (actualFrameIndex != expectedFrameIndex)
                {
                    droppedFrames += Math.Max(1, actualFrameIndex - expectedFrameIndex);
                }

                free.Release();
                bool payloadValid = VerifyFramePages(destination, expectedFrameIndex);
                integrity &= sequenceValid
                    && payloadLength == frameBytes
                    && actualFrameIndex == expectedFrameIndex
                    && payloadValid;
                producerWriteToCopyDurations.Add(
                    (copiedTimestamp - producerWriteStartedTimestamp) * 1000d / Stopwatch.Frequency);
                publishToCopyDurations.Add(
                    (copiedTimestamp - publishedTimestamp) * 1000d / Stopwatch.Frequency);
                if (expectedFrameIndex % Math.Max(1, options.RingSamples / 8) == 0)
                {
                    SampleProducerMetrics(
                        producer,
                        ref producerPeakWorkingSet,
                        ref producerPeakPrivateBytes,
                        ref producerCpuMilliseconds);
                }
            }
            total.Stop();

            if (!producer.WaitForExit(15_000))
            {
                producer.Kill(entireProcessTree: true);
                throw new TimeoutException("MMF producer 未在基准完成后退出。" + ReadProducerDiagnostics(producer));
            }
            if (producer.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "MMF producer 退出码为 " + producer.ExitCode + "." + ReadProducerDiagnostics(producer));
            }

            long producerSemaphoreWaitTicks = headers.ReadInt64(ProducerSemaphoreWaitTicksOffset);
            int producerFrameCount = headers.ReadInt32(ProducerFrameCountOffset);
            integrity &= producerFrameCount == options.RingSamples;
            RingTransportMeasurement result = new(
                Summarize(
                    producerWriteToCopyDurations,
                    checked((long)frameBytes * options.RingSamples),
                    total.Elapsed),
                Summarize(
                    publishToCopyDurations,
                    checked((long)frameBytes * options.RingSamples),
                    total.Elapsed),
                producerSemaphoreWaitTicks * 1000d / Stopwatch.Frequency,
                tornFrames,
                droppedFrames,
                ToMiB(producerPeakWorkingSet),
                ToMiB(producerPeakPrivateBytes),
                producerCpuMilliseconds);
            return (result, integrity);
        }
        finally
        {
            if (producer is not null)
            {
                try
                {
                    if (!producer.HasExited)
                    {
                        producer.Kill(entireProcessTree: true);
                        producer.WaitForExit(5_000);
                    }
                }
                catch
                {
                    // Cleanup is best effort.
                }
                producer.Dispose();
            }

        }
    }

    private static (
        FrameTransportMeasurement Writes,
        FrameTransportMeasurement Reads,
        double DurableFlushMilliseconds,
        double DurableWriteThroughputMiBPerSecond,
        bool Integrity,
        string Drive,
        int SegmentFileCount) MeasureSegment(
            FrameTransportBenchmarkOptions options,
            string runRoot,
            byte[] source,
            byte[] destination,
            int frameBytes,
            long segmentBytes)
    {
        List<double> writeDurations = new(options.Frames);
        List<double> readDurations = new(options.RandomReads);
        List<string> segmentPaths = new();
        long bufferedWriteTicks = 0L;
        double durableFlushMilliseconds = 0d;
        bool integrity = true;
        int firstFrameIndex = 0;

        while (firstFrameIndex < options.Frames)
        {
            int segmentIndex = segmentPaths.Count;
            int framesInSegment = Math.Min(options.SegmentFrames, options.Frames - firstFrameIndex);
            string stem = "segment-" + segmentIndex.ToString("D5", CultureInfo.InvariantCulture);
            string temporaryPath = Path.Combine(runRoot, stem + ".tmp");
            string publishedPath = Path.Combine(runRoot, stem + ".raw");
            byte[] header = CreateSegmentHeader(
                options,
                frameBytes,
                firstFrameIndex,
                framesInSegment);

            long bufferedStarted = Stopwatch.GetTimestamp();
            using (FileStream writer = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                1024 * 1024,
                FileOptions.SequentialScan))
            {
                writer.SetLength(checked(SegmentHeaderBytes + (long)framesInSegment * frameBytes));
                writer.Position = 0L;
                writer.Write(header, 0, header.Length);
                for (int localFrame = 0; localFrame < framesInSegment; localFrame++)
                {
                    int frameIndex = firstFrameIndex + localFrame;
                    StampFramePages(source, frameIndex);
                    long started = Stopwatch.GetTimestamp();
                    writer.Write(source, 0, frameBytes);
                    writeDurations.Add(ElapsedMilliseconds(started));
                }

                writer.Flush(flushToDisk: false);
                bufferedWriteTicks += Stopwatch.GetTimestamp() - bufferedStarted;
                if (options.FlushToDisk)
                {
                    long flushStarted = Stopwatch.GetTimestamp();
                    writer.Flush(flushToDisk: true);
                    durableFlushMilliseconds += ElapsedMilliseconds(flushStarted);
                }
            }

            long renameStarted = Stopwatch.GetTimestamp();
            File.Move(temporaryPath, publishedPath);
            bufferedWriteTicks += Stopwatch.GetTimestamp() - renameStarted;
            segmentPaths.Add(publishedPath);
            firstFrameIndex += framesInSegment;
        }

        FileStream[] readers = segmentPaths.Select(path => new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            1024 * 1024,
            FileOptions.RandomAccess)).ToArray();
        try
        {
            for (int segmentIndex = 0; segmentIndex < readers.Length; segmentIndex++)
            {
                integrity &= ValidateSegmentHeader(readers[segmentIndex], options, frameBytes, segmentIndex);
            }

            Random random = new(0xCA6E);
            Stopwatch totalReads = Stopwatch.StartNew();
            for (int i = 0; i < options.RandomReads; i++)
            {
                int frameIndex = random.Next(options.Frames);
                FileStream reader = readers[frameIndex / options.SegmentFrames];
                int localFrame = frameIndex % options.SegmentFrames;
                long started = Stopwatch.GetTimestamp();
                reader.Position = checked(SegmentHeaderBytes + (long)localFrame * frameBytes);
                ReadExactly(reader, destination);
                readDurations.Add(ElapsedMilliseconds(started));
                integrity &= VerifyFramePages(destination, frameIndex);
            }
            totalReads.Stop();

            for (int frameIndex = 0; frameIndex < options.Frames; frameIndex++)
            {
                FileStream reader = readers[frameIndex / options.SegmentFrames];
                int localFrame = frameIndex % options.SegmentFrames;
                reader.Position = checked(SegmentHeaderBytes + (long)localFrame * frameBytes);
                ReadExactly(reader, destination);
                integrity &= VerifyFramePages(destination, frameIndex);
            }

            TimeSpan bufferedWriteElapsed = TimeSpan.FromSeconds(
                bufferedWriteTicks / (double)Stopwatch.Frequency);
            double effectiveSeconds = bufferedWriteElapsed.TotalSeconds
                + durableFlushMilliseconds / 1000d;
            string drive = Path.GetPathRoot(runRoot) ?? string.Empty;
            return (
                Summarize(writeDurations, segmentBytes, bufferedWriteElapsed),
                Summarize(readDurations, checked((long)frameBytes * options.RandomReads), totalReads.Elapsed),
                durableFlushMilliseconds,
                segmentBytes / (1024d * 1024d) / Math.Max(effectiveSeconds, 0.000001d),
                integrity,
                drive,
                segmentPaths.Count);
        }
        finally
        {
            foreach (FileStream reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    private static void Print(FrameTransportBenchmarkResult result)
    {
        double frameBudgetMilliseconds = 1000d / result.Options.FrameRate;
        double requiredMiBPerSecond = result.FrameBytes * result.Options.FrameRate / (1024d * 1024d);
        bool passed = result.RingIntegrityPassed && result.SegmentIntegrityPassed;
        Console.WriteLine(passed ? "FRAME_TRANSPORT_BENCHMARK_PASS" : "FRAME_TRANSPORT_BENCHMARK_FAIL");
        Console.WriteLine("scope=synthetic_rgba32_only; native_capture=not_tested; pixel_equivalence=not_tested");
        Console.WriteLine("resolution=" + result.Options.Width + "x" + result.Options.Height
            + " fps=" + result.Options.FrameRate
            + " ring_samples=" + result.Options.RingSamples
            + " segment_frames_total=" + result.Options.Frames);
        Console.WriteLine("frame_mib=" + ToMiB(result.FrameBytes).ToString("F3", CultureInfo.InvariantCulture)
            + " raw_10s_gib=" + ToGiB(result.TenSecondBytes).ToString("F3", CultureInfo.InvariantCulture)
            + " raw_60s_gib=" + ToGiB(result.SixtySecondBytes).ToString("F3", CultureInfo.InvariantCulture));
        Console.WriteLine("ring_slots=" + result.Options.RingSlots
            + " ring_mib=" + ToMiB(result.RingBytes).ToString("F3", CultureInfo.InvariantCulture)
            + " integrity=" + result.RingIntegrityPassed);
        PrintRing("ring_burst", result.Ring.Burst);
        PrintRing("ring_paced", result.Ring.Paced);
        Console.WriteLine("segment_drive=" + result.SegmentDrive
            + " integrity=" + result.SegmentIntegrityPassed
            + " files=" + result.SegmentFileCount
            + " frames_per_segment=" + result.Options.SegmentFrames
            + " durable_flush_total_ms=" + result.DurableFlushMilliseconds.ToString("F3", CultureInfo.InvariantCulture)
            + " durable_effective_mib_s="
            + result.DurableWriteThroughputMiBPerSecond.ToString("F3", CultureInfo.InvariantCulture));
        PrintMeasurement("segment_buffered_write", result.SegmentWrites);
        PrintMeasurement("segment_warm_cache_read", result.SegmentReads);
        Console.WriteLine("target_frame_budget_ms=" + frameBudgetMilliseconds.ToString("F3", CultureInfo.InvariantCulture)
            + " required_raw_mib_s=" + requiredMiBPerSecond.ToString("F3", CultureInfo.InvariantCulture));
        Console.WriteLine("go_no_go=PARTIAL_ONLY; remaining=gpu_capture+two_unity_processes+texture_upload+real_frame_encoding");
        Console.WriteLine("BENCHMARK_JSON " + JsonSerializer.Serialize(result, ProtocolJson.Options));
    }

    private static void PrintMeasurement(string name, FrameTransportMeasurement measurement)
    {
        Console.WriteLine(name
            + "_samples=" + measurement.SampleCount
            + " mib_s=" + measurement.ThroughputMiBPerSecond.ToString("F3", CultureInfo.InvariantCulture)
            + " p50_ms=" + measurement.P50Milliseconds.ToString("F3", CultureInfo.InvariantCulture)
            + " p95_ms=" + measurement.P95Milliseconds.ToString("F3", CultureInfo.InvariantCulture)
            + " p99_ms=" + FormatNullableMilliseconds(measurement.P99Milliseconds)
            + " max_ms=" + measurement.MaximumMilliseconds.ToString("F3", CultureInfo.InvariantCulture));
    }

    private static void PrintRing(string name, RingTransportMeasurement ring)
    {
        PrintMeasurement(name + "_producer_write_to_copy", ring.ProducerWriteToCopy);
        PrintMeasurement(name + "_publish_to_copy", ring.PublishToCopy);
        Console.WriteLine(name + "_producer_semaphore_wait_ms="
            + ring.ProducerSemaphoreWaitMilliseconds.ToString("F3", CultureInfo.InvariantCulture)
            + " torn_frames=" + ring.TornFrames
            + " dropped_frames=" + ring.DroppedFrames
            + " producer_peak_working_set_mib="
            + ring.ProducerPeakWorkingSetMiB.ToString("F3", CultureInfo.InvariantCulture)
            + " producer_private_mib="
            + ring.ProducerPrivateMemoryMiB.ToString("F3", CultureInfo.InvariantCulture)
            + " producer_cpu_ms="
            + ring.ProducerCpuMilliseconds.ToString("F3", CultureInfo.InvariantCulture));
    }

    private static FrameTransportMeasurement Summarize(
        List<double> durations,
        long totalBytes,
        TimeSpan elapsed)
    {
        if (durations.Count == 0)
        {
            throw new InvalidOperationException("基准没有产生计时样本。");
        }

        durations.Sort();
        double seconds = Math.Max(elapsed.TotalSeconds, 0.000001d);
        return new FrameTransportMeasurement(
            durations.Count,
            totalBytes / (1024d * 1024d) / seconds,
            Percentile(durations, 0.50d),
            Percentile(durations, 0.95d),
            durations.Count >= 100 ? Percentile(durations, 0.99d) : null,
            durations[^1]);
    }

    private static string FormatNullableMilliseconds(double? value)
    {
        return value.HasValue
            ? value.Value.ToString("F3", CultureInfo.InvariantCulture)
            : "n/a";
    }

    private static Process StartProducer(
        FrameTransportBenchmarkOptions options,
        string mappingPath,
        string freeSemaphoreName,
        string readySemaphoreName,
        string producerReadyEventName,
        string startEventName,
        int paceFps)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        startInfo.ArgumentList.Add("--benchmark-producer");
        AddArgument(startInfo, "--mapping-path", mappingPath);
        AddArgument(startInfo, "--free-semaphore", freeSemaphoreName);
        AddArgument(startInfo, "--ready-semaphore", readySemaphoreName);
        AddArgument(startInfo, "--producer-ready-event", producerReadyEventName);
        AddArgument(startInfo, "--start-event", startEventName);
        AddArgument(startInfo, "--width", options.Width.ToString(CultureInfo.InvariantCulture));
        AddArgument(startInfo, "--height", options.Height.ToString(CultureInfo.InvariantCulture));
        AddArgument(startInfo, "--frames", options.RingSamples.ToString(CultureInfo.InvariantCulture));
        AddArgument(startInfo, "--ring-slots", options.RingSlots.ToString(CultureInfo.InvariantCulture));
        AddArgument(startInfo, "--pace-fps", paceFps.ToString(CultureInfo.InvariantCulture));
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动跨进程 MMF producer。");
    }

    private static void AddArgument(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private static string ReadProducerDiagnostics(Process producer)
    {
        if (!producer.HasExited)
        {
            return string.Empty;
        }

        string stdout = producer.StandardOutput.ReadToEnd().Trim();
        string stderr = producer.StandardError.ReadToEnd().Trim();
        return " producer_stdout=" + stdout + " producer_stderr=" + stderr;
    }

    private static void SampleProducerMetrics(
        Process producer,
        ref long peakWorkingSet,
        ref long peakPrivateBytes,
        ref double cpuMilliseconds)
    {
        try
        {
            if (producer.HasExited)
            {
                return;
            }

            producer.Refresh();
            peakWorkingSet = Math.Max(peakWorkingSet, producer.WorkingSet64);
            peakPrivateBytes = Math.Max(peakPrivateBytes, producer.PrivateMemorySize64);
            cpuMilliseconds = Math.Max(cpuMilliseconds, producer.TotalProcessorTime.TotalMilliseconds);
        }
        catch (InvalidOperationException)
        {
            // The producer can exit between HasExited and metric reads.
        }
    }

    private static void CleanupStaleRunDirectories(string tempRoot)
    {
        if (!Directory.Exists(tempRoot))
        {
            return;
        }

        foreach (string runRoot in Directory.EnumerateDirectories(
            tempRoot,
            "run-*",
            SearchOption.TopDirectoryOnly))
        {
            string marker = Path.Combine(runRoot, ".frame-cache-spike-owned");
            if (!File.Exists(marker)
                || DateTime.UtcNow - Directory.GetLastWriteTimeUtc(runRoot) < TimeSpan.FromMinutes(30))
            {
                continue;
            }

            try
            {
                DeleteOwnedRunDirectory(runRoot, tempRoot);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    "FRAME_TRANSPORT_STALE_CLEANUP_FAIL path=" + runRoot + " error=" + exception.Message);
            }
        }
    }

    private static void DeleteOwnedRunDirectory(string runRoot, string tempRoot)
    {
        string fullTempRoot = Path.GetFullPath(tempRoot).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string fullRunRoot = Path.GetFullPath(runRoot).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullRunRoot.StartsWith(fullTempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullRunRoot.TrimEnd(Path.DirectorySeparatorChar))
                .StartsWith("run-", StringComparison.Ordinal)
            || !File.Exists(Path.Combine(fullRunRoot, ".frame-cache-spike-owned")))
        {
            throw new InvalidOperationException("拒绝清理非 FrameCacheSpike 自有目录：" + fullRunRoot);
        }

        Directory.Delete(fullRunRoot, recursive: true);
        if (Directory.Exists(fullRunRoot))
        {
            throw new IOException("临时目录删除后仍然存在：" + fullRunRoot);
        }
    }

    private static void WaitUntil(long targetTimestamp)
    {
        while (true)
        {
            long remaining = targetTimestamp - Stopwatch.GetTimestamp();
            if (remaining <= 0L)
            {
                return;
            }

            double remainingMilliseconds = remaining * 1000d / Stopwatch.Frequency;
            if (remainingMilliseconds > 2d)
            {
                Thread.Sleep(Math.Max(1, (int)remainingMilliseconds - 1));
            }
            else
            {
                Thread.SpinWait(64);
            }
        }
    }

    private static double Percentile(List<double> sorted, double percentile)
    {
        int index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static void FillDeterministicFrame(byte[] frame)
    {
        uint state = 0xA0D0FA1u;
        for (int i = 0; i < frame.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            frame[i] = (byte)state;
        }
    }

    private static void StampFramePages(byte[] frame, int frameIndex)
    {
        int pageIndex = 0;
        for (int pageStart = 0; pageStart < frame.Length; pageStart += PageSignatureStride, pageIndex++)
        {
            int remaining = frame.Length - pageStart;
            BinaryPrimitives.WriteInt32LittleEndian(
                frame.AsSpan(pageStart, sizeof(int)),
                frameIndex);
            if (remaining >= sizeof(int) * 2)
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    frame.AsSpan(pageStart + sizeof(int), sizeof(int)),
                    pageIndex);
            }
        }
    }

    private static bool VerifyFramePages(byte[] frame, int expectedFrameIndex)
    {
        int pageIndex = 0;
        for (int pageStart = 0; pageStart < frame.Length; pageStart += PageSignatureStride, pageIndex++)
        {
            int remaining = frame.Length - pageStart;
            if (BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(pageStart, sizeof(int)))
                    != expectedFrameIndex)
            {
                return false;
            }
            if (remaining >= sizeof(int) * 2
                && BinaryPrimitives.ReadInt32LittleEndian(
                    frame.AsSpan(pageStart + sizeof(int), sizeof(int))) != pageIndex)
            {
                return false;
            }
        }

        return true;
    }

    private static bool RunIntegrityUnitChecks()
    {
        byte[] frame = GC.AllocateUninitializedArray<byte>(64 * 36 * 4);
        FillDeterministicFrame(frame);
        StampFramePages(frame, 7);
        if (!VerifyFramePages(frame, 7)
            || !IsCompletedSequence(16L, 16L, 16L)
            || IsCompletedSequence(17L, 17L, 17L)
            || IsCompletedSequence(16L, 18L, 18L))
        {
            return false;
        }

        frame[PageSignatureStride] ^= 0x01;
        return !VerifyFramePages(frame, 7);
    }

    private static bool IsCompletedSequence(long before, long after, long end)
    {
        return before == after && after == end && (after & 1L) == 0L;
    }

    private static byte[] CreateSegmentHeader(
        FrameTransportBenchmarkOptions options,
        int frameBytes,
        int firstFrameIndex,
        int framesInSegment)
    {
        byte[] header = GC.AllocateUninitializedArray<byte>(SegmentHeaderBytes);
        Array.Clear(header, 0, header.Length);
        Encoding.ASCII.GetBytes("AESFRAME").CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, sizeof(int)), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, sizeof(int)), options.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, sizeof(int)), options.Height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20, sizeof(int)), frameBytes);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24, sizeof(int)), firstFrameIndex);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28, sizeof(int)), framesInSegment);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32, sizeof(int)), options.SegmentFrames);
        return header;
    }

    private static bool ValidateSegmentHeader(
        FileStream reader,
        FrameTransportBenchmarkOptions options,
        int frameBytes,
        int segmentIndex)
    {
        byte[] header = GC.AllocateUninitializedArray<byte>(SegmentHeaderBytes);
        reader.Position = 0L;
        ReadExactly(reader, header);
        int expectedFirstFrame = segmentIndex * options.SegmentFrames;
        int expectedFrameCount = Math.Min(
            options.SegmentFrames,
            options.Frames - expectedFirstFrame);
        return Encoding.ASCII.GetString(header, 0, 8) == "AESFRAME"
            && BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, sizeof(int))) == 1
            && BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12, sizeof(int))) == options.Width
            && BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16, sizeof(int))) == options.Height
            && BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20, sizeof(int))) == frameBytes
            && BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24, sizeof(int))) == expectedFirstFrame
            && BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(28, sizeof(int))) == expectedFrameCount
            && BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(32, sizeof(int))) == options.SegmentFrames
            && reader.Length == checked(SegmentHeaderBytes + (long)expectedFrameCount * frameBytes);
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException("帧载荷提前结束。");
            }

            offset += read;
        }
    }

    private static void EnsureSpace(string tempRoot, long segmentBytes)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(tempRoot))
            ?? throw new InvalidOperationException("无法确定临时缓存所在磁盘。");
        DriveInfo drive = new(root);
        long reserve = Math.Max(512L * 1024L * 1024L, segmentBytes / 4L);
        if (drive.AvailableFreeSpace < segmentBytes + reserve)
        {
            throw new IOException(
                "临时缓存磁盘空间不足。需要 " + ToGiB(segmentBytes + reserve).ToString("F2", CultureInfo.InvariantCulture)
                + " GiB，可用 " + ToGiB(drive.AvailableFreeSpace).ToString("F2", CultureInfo.InvariantCulture) + " GiB。");
        }
    }

    private static int ReadInt(string[] args, string name, int defaultValue, int minimum, int maximum)
    {
        int index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.Ordinal));
        if (index < 0)
        {
            return defaultValue;
        }

        if (index + 1 >= args.Length
            || !int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            || parsed < minimum
            || parsed > maximum)
        {
            throw new ArgumentException(name + " 必须是 " + minimum + " 到 " + maximum + " 之间的整数。");
        }

        return parsed;
    }

    private static string ReadString(string[] args, string name, string defaultValue)
    {
        int index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.Ordinal));
        if (index < 0)
        {
            return defaultValue;
        }

        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException(name + " 缺少路径值。");
        }

        return args[index + 1];
    }

    private static string ReadRequiredString(string[] args, string name)
    {
        string value = ReadString(args, name, string.Empty);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(name + " 缺少值。");
        }

        return value;
    }

    private static double ElapsedMilliseconds(long startedTimestamp)
    {
        return (Stopwatch.GetTimestamp() - startedTimestamp) * 1000d / Stopwatch.Frequency;
    }

    private static double ToMiB(long bytes) => bytes / (1024d * 1024d);

    private static double ToGiB(long bytes) => bytes / (1024d * 1024d * 1024d);
}
