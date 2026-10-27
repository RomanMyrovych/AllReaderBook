using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BookHub.Core.Parsers;

namespace BookHub.Reader.Services;

public sealed class ReaderLaunchRequest
{
    public string? FilePath { get; set; }
    public long? BookId { get; set; }
    public string? DbPath { get; set; }
    public int? InitialPage { get; set; }

    public bool HasFile => !string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath);
    public bool IsSupportedFormat => HasFile && BookFormatParser.IsReaderExtensionSupported(FilePath, out _);
}

public static class ReaderCliParser
{
    /// <summary>
    /// Parses CLI arguments:
    /// 1) No arguments -> empty ReaderLaunchRequest (opens Start Screen with Recent 10 files &amp; Drag-and-Drop)
    /// 2) AllReaderBook.exe "path\to\book.pdf"
    /// 3) AllReaderBook.exe --book-id 147 --db-path "E:\...\bookhub.db" "path\to\book.pdf"
    /// </summary>
    public static ReaderLaunchRequest Parse(string[] args)
    {
        var req = new ReaderLaunchRequest();
        if (args == null || args.Length == 0)
            return req;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i]?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(arg))
                continue;

            if (arg.Equals("--book-id", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (long.TryParse(args[++i], out long id) && id > 0)
                    req.BookId = id;
            }
            else if (arg.StartsWith("--book-id=", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(arg["--book-id=".Length..], out long id) && id > 0)
                    req.BookId = id;
            }
            else if (arg.Equals("--db-path", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                req.DbPath = args[++i].Trim('"', ' ');
            }
            else if (arg.StartsWith("--db-path=", StringComparison.OrdinalIgnoreCase))
            {
                req.DbPath = arg["--db-path=".Length..].Trim('"', ' ');
            }
            else if (arg.Equals("--page", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out int page) && page > 0)
                    req.InitialPage = page;
            }
            else if (!arg.StartsWith('-'))
            {
                string candidate = arg.Trim('"', ' ');
                try
                {
                    candidate = Path.GetFullPath(candidate);
                }
                catch
                {
                    // Keep raw path
                }
                req.FilePath = candidate;
            }
        }

        return req;
    }
}

public sealed class SingleInstanceIpcService : IDisposable
{
    private const string MutexName = @"Local\AllReaderBook_Standalone_Mutex_v09";
    private const string PipeName = @"AllReaderBook_Ipc_Pipe";

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private CancellationTokenSource? _pipeServerCts;
    private bool _disposed;

    public bool TryAcquirePrimaryInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, name: MutexName, out bool createdNew);
            _ownsMutex = createdNew;
            return createdNew;
        }
        catch
        {
            _ownsMutex = false;
            return true; // Fallback to allow launch if mutex creation fails in restricted sandbox
        }
    }

    public static bool TryForwardToPrimaryInstance(ReaderLaunchRequest request, int timeoutMs = 1800)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            client.Connect(timeoutMs);

            string payload = JsonSerializer.Serialize(request);
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void StartListening(Action<ReaderLaunchRequest> onRequestReceived)
    {
        _pipeServerCts = new CancellationTokenSource();
        var token = _pipeServerCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token);

                    using var ms = new MemoryStream();
                    byte[] buffer = new byte[4096];
                    int read;
                    while ((read = await server.ReadAsync(buffer, token)) > 0)
                    {
                        ms.Write(buffer, 0, read);
                    }

                    if (ms.Length > 0)
                    {
                        string json = Encoding.UTF8.GetString(ms.ToArray());
                        var request = JsonSerializer.Deserialize<ReaderLaunchRequest>(json);
                        if (request != null)
                        {
                            onRequestReceived(request);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(100, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
        }, token);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _pipeServerCts?.Cancel();
            _pipeServerCts?.Dispose();
        }
        catch { }

        try
        {
            if (_ownsMutex && _instanceMutex != null)
            {
                _instanceMutex.ReleaseMutex();
            }
            _instanceMutex?.Dispose();
        }
        catch { }
    }
}
