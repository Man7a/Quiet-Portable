using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace QuietGPT;

// Each worker owns its models and alignment child. Never stop an unrelated TTS UI.
internal sealed class LocalSpeech : IDisposable
{
    private Process? worker;
    private string? workerEngine;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object lifecycle = new();
    private readonly Timer hiddenTimer;
    private readonly Func<string, ProcessStartInfo> startInfo;
    internal TimeSpan HiddenIdleDelay { get; }
    private bool hidden, disposed;
    private long unloadAt = long.MaxValue;
    internal bool IsHidden { get { lock(lifecycle)return hidden; } }
    internal int? WorkerProcessId { get { lock(lifecycle) return worker is { HasExited: false } ? worker.Id : null; } }
    internal LocalSpeech(Func<string, ProcessStartInfo>? createStart = null, TimeSpan? hiddenDelay = null)
    {
        startInfo = createStart ?? CreateStart;
        HiddenIdleDelay = hiddenDelay ?? TimeSpan.FromSeconds(30);
        hiddenTimer = new Timer(_ => UnloadHiddenWorker(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }
    private static ProcessStartInfo CreateStart(string engine)
    {
        if(engine != "chatterbox-turbo")throw new ArgumentException("This edition supports Chatterbox Turbo only.");
        if(!PortablePaths.VoiceReady)throw new IOException("Voice is not installed. Open Read aloud and choose Set up voice. The avatar still works without speech.");
        var start = new ProcessStartInfo(PortablePaths.VoicePython)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["QUIET_VOICE_RUNTIME"] = PortablePaths.VoiceRoot;
        start.Environment["PYTHONPATH"] = Path.Combine(PortablePaths.VoiceRoot,"packages");
        start.Environment["HF_HOME"] = Path.Combine(PortablePaths.VoiceRoot,"hf-cache");
        start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Voice", "quiet-speech-worker.py"));
        start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        return start;
    }
    internal void SetHidden(bool value)
    {
        lock(lifecycle)
        {
            if(disposed)return;
            hidden = value;
            if(value)ArmHiddenTimer();
            else { unloadAt=long.MaxValue;hiddenTimer.Change(Timeout.InfiniteTimeSpan,Timeout.InfiniteTimeSpan); }
        }
    }
    internal async Task UnloadForSetup()
    {
        await gate.WaitAsync();
        try { lock(lifecycle)ReleaseWorker(); }
        finally { gate.Release(); }
    }
    private void ArmHiddenTimer()
    {
        unloadAt=Stopwatch.GetTimestamp()+(long)(HiddenIdleDelay.TotalSeconds*Stopwatch.Frequency);
        hiddenTimer.Change(HiddenIdleDelay,Timeout.InfiniteTimeSpan);
    }
    private void UnloadHiddenWorker()
    {
        // An in-flight request owns the worker. Its finally starts a fresh idle delay.
        if(!gate.Wait(0))return;
        try
        {
            lock(lifecycle)
            {
                if(!hidden||disposed)return;
                long remaining=unloadAt-Stopwatch.GetTimestamp();
                // Ignore an old callback queued before a reveal or a fresh request.
                if(remaining>0)hiddenTimer.Change(TimeSpan.FromSeconds((double)remaining/Stopwatch.Frequency),Timeout.InfiniteTimeSpan);
                else ReleaseWorker();
            }
        }
        finally { gate.Release(); }
    }
    private void ReleaseWorker()
    {
        try { if(worker is { HasExited: false })worker.Kill(true); } catch { }
        worker?.Dispose(); worker = null; workerEngine = null;
    }
    public async Task<JsonElement> Generate(string text, string engine, string reference, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            Process process;
            lock(lifecycle)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                unloadAt=long.MaxValue;
                hiddenTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                if(worker is null || worker.HasExited || workerEngine != engine)
                {
                    ReleaseWorker();
                    worker = Process.Start(startInfo(engine)) ?? throw new IOException("Could not start the local voice engine.");
                    workerEngine = engine;
                    worker.ErrorDataReceived += (_, _) => { }; // Drain diagnostics; never log reply text.
                    worker.BeginErrorReadLine();
                }
                process = worker;
            }
            using var cancel = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { text, engine, reference }));
            var line = await process.StandardOutput.ReadLineAsync(token);
            token.ThrowIfCancellationRequested();
            using var result = JsonDocument.Parse(line ?? throw new IOException("The local voice engine stopped."));
            if (!result.RootElement.GetProperty("ok").GetBoolean()) throw new IOException(result.RootElement.GetProperty("error").GetString());
            return result.RootElement.Clone();
        }
        finally
        {
            lock(lifecycle)
            {
                if(!disposed && hidden)ArmHiddenTimer();
            }
            gate.Release();
        }
    }
    public void Dispose()
    {
        lock(lifecycle)
        {
            if(disposed)return;
            disposed = true; hiddenTimer.Dispose(); ReleaseWorker();
        }
    }
}
