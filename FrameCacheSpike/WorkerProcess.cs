using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace FrameCacheSpike;

internal sealed class WorkerProcess : IDisposable
{
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(5);

    private readonly Process process;
    private readonly StreamWriter input;
    private readonly StreamReader output;
    private readonly StringBuilder errorOutput = new();
    private readonly object errorLock = new();

    private WorkerProcess(Process process)
    {
        this.process = process;
        input = process.StandardInput;
        output = process.StandardOutput;
    }

    internal static WorkerProcess Start()
    {
        string assemblyPath = typeof(Program).Assembly.Location;
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            Arguments = Quote(assemblyPath) + " --worker",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 FrameCache Worker。");
        WorkerProcess worker = new(process);
        process.ErrorDataReceived += worker.OnErrorData;
        process.BeginErrorReadLine();
        return worker;
    }

    internal void Send(WorkerRequest request)
    {
        input.WriteLine(ProtocolJson.Serialize(request));
        input.Flush();
    }

    internal WorkerEvent ReadEvent(TimeSpan? timeout = null)
    {
        string? line = ReadLineWithTimeout(timeout ?? DefaultReadTimeout);
        if (line is null)
        {
            throw new InvalidOperationException("Worker 提前关闭了 stdout。" + FormatErrorSuffix());
        }

        return ProtocolJson.Deserialize<WorkerEvent>(line);
    }

    internal WorkerEvent WaitFor(
        string type,
        Action<WorkerEvent>? onEvent = null,
        TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? DefaultReadTimeout;
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (true)
        {
            TimeSpan remaining = limit - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                KillWorker();
                throw new TimeoutException(
                    "等待 Worker 事件超时：" + type + FormatErrorSuffix());
            }

            WorkerEvent message = ReadEvent(remaining);
            onEvent?.Invoke(message);
            if (string.Equals(message.Type, type, StringComparison.Ordinal))
            {
                return message;
            }

            if (string.Equals(message.Type, "error", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Worker error: " + message.Error + FormatErrorSuffix());
            }
        }
    }

    public void Dispose()
    {
        try
        {
            KillWorker();
            input.Dispose();
            output.Dispose();
        }
        catch
        {
            // The spike must not mask the assertion that caused cleanup.
        }
        finally
        {
            process.ErrorDataReceived -= OnErrorData;
            process.Dispose();
        }
    }

    private string? ReadLineWithTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new TimeoutException("Worker stdout 读取超时。" + FormatErrorSuffix());
        }

        Task<string?> readTask = output.ReadLineAsync();
        if (!readTask.Wait(timeout))
        {
            KillWorker();
            throw new TimeoutException("Worker stdout 读取超时。" + FormatErrorSuffix());
        }

        return readTask.GetAwaiter().GetResult();
    }

    private void KillWorker()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Cleanup is best effort.
        }
    }

    private void OnErrorData(object sender, DataReceivedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.Data))
        {
            return;
        }

        lock (errorLock)
        {
            errorOutput.AppendLine(args.Data);
        }
    }

    private string FormatErrorSuffix()
    {
        lock (errorLock)
        {
            return errorOutput.Length == 0
                ? string.Empty
                : " stderr: " + errorOutput.ToString().Trim();
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
