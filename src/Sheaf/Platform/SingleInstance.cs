using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Sheaf.Platform;

/// <summary>
/// Keeps one Sheaf window. A second launch (for example "Open with Sheaf" on another file) hands its
/// arguments to the running copy through a named pipe and exits, and the running copy comes to the front.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    const string BaseName = "Sheaf-9d41c7e2-5b8a-4f0e-a6d3-1c2e8b7f40a9";
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    readonly Mutex _mutex;
    readonly string _pipeName;
    readonly CancellationTokenSource _stop = new();

    SingleInstance(Mutex mutex, string pipeName, bool isFirst)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        IsFirst = isFirst;
    }

    public bool IsFirst { get; }

    /// <summary>Sandbox runs get their own name, so a test copy never talks to the real one.</summary>
    public static SingleInstance Claim()
    {
        string name = AppPaths.IsSandbox ? $"{BaseName}-{ShortHash(AppPaths.DataFolder)}" : BaseName;
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{name}", out bool createdNew);
        return new SingleInstance(mutex, $"{name}-{Environment.UserName}", createdNew);
    }

    public bool SendToFirst(IEnumerable<string> args)
    {
        try
        {
            Native.AllowSetForegroundWindow(Native.AnyProcess);
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            pipe.Connect(ConnectTimeout);
            byte[] message = Encoding.UTF8.GetBytes(string.Join('\n', args));
            pipe.Write(message);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            ActivityLog.Error("passing arguments to the running copy", ex);
            return false;
        }
    }

    public void Listen(Action<string[]> received) => _ = ListenAsync(received);

    async Task ListenAsync(Action<string[]> received)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                string text = await reader.ReadToEndAsync(_stop.Token);
                received(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException ex)
            {
                ActivityLog.Error("listening for a second launch", ex);
            }
        }
    }

    static string ShortHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToUpperInvariant())))[..12];

    public void Dispose()
    {
        _stop.Cancel();
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
        _stop.Dispose();
    }
}
