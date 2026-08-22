using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace SessionDeck.Services;

public sealed record PipeResponse(int ExitCode, string Output);

/// <summary>A state snapshot pushed by the VSCode extension (stage D): the workspace
/// folder, current branch and the open Claude Code tabs. Sent on connect and on every
/// tab/branch change.</summary>
public sealed class VscodeSyncMessage
{
    public string? Type { get; set; }                // "vscode-sync"
    public string? Workspace { get; set; }           // first workspace folder path
    public string? Branch { get; set; }
    public int Pid { get; set; }
    public bool Focused { get; set; }                // VSCode window has OS focus
    public List<VscodeTab> Tabs { get; set; } = new();
}

public sealed class VscodeTab
{
    public string Label { get; set; } = "";
    public bool Active { get; set; }
}

/// <summary>
/// A live VSCode-extension connection. The extension keeps its pipe connection open;
/// SessionDeck pushes commands (e.g. openSession) down it as JSON lines.
/// TrySend is safe from any thread.
/// </summary>
public sealed class VscodeConnection
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private volatile bool _dead;

    public string WorkspacePath { get; set; } = "";
    public int Pid { get; set; }

    /// <summary>This window's own Claude tabs, from its last sync. Held per connection
    /// rather than merged straight onto the workspace: two VSCode windows can have the
    /// same folder open (a second account, a second profile), and each sync carries only
    /// the sending window's tabs — so a shared list is overwritten by whichever window
    /// synced last and the card ends up describing one window at a time (21-08-2026).</summary>
    public List<VscodeTab> Tabs { get; set; } = new();

    /// <summary>The window had OS focus at its last sync.</summary>
    public bool Focused { get; set; }

    /// <summary>When this window was last seen focused — the tie-break for which window a
    /// command goes to when several share a folder.</summary>
    public DateTime LastFocusedAt { get; set; }

    /// <summary>The VSCode INSTANCE this connector belongs to (the extension host's parent,
    /// which is that instance's Electron main process). Resolved once on connect; 0 when it
    /// couldn't be read. It does NOT identify the window - every window of one instance
    /// reports this same pid, see Hwnd below.</summary>
    public int OwnerPid { get; set; }

    /// <summary>The OS window this connector lives in, or IntPtr.Zero while it is still
    /// unknown. Learned by focus correlation (MainWindow.CorrelateConnectorWindow), because
    /// neither the pid nor the title can produce it: all windows of one VSCode instance
    /// share one pid, two windows on one folder share one title, and a window with a custom
    /// `window.title` matches no title pattern at all.</summary>
    public IntPtr Hwnd { get; set; }

    internal VscodeConnection(StreamWriter writer) => _writer = writer;

    /// <summary>Fire-and-forget: the write happens off-thread so a stalled client can
    /// never block the UI (a pipe write blocks once the out-buffer fills). A failed
    /// write marks the connection dead; the read loop also surfaces the disconnect.</summary>
    public bool TrySend(object message)
    {
        if (_dead) return false;
        string json = JsonSerializer.Serialize(message);
        Task.Run(() =>
        {
            lock (_gate)
            {
                try { _writer.WriteLine(json); }
                catch { _dead = true; }
            }
        });
        return true;
    }
}

/// <summary>
/// Named-pipe server. Two client kinds share the pipe, distinguished by the
/// first line: CLI requests ({"Argv":[...]} → one response line, then close) and VSCode
/// connectors ({"Type":"vscode-sync",...} → connection stays open; further syncs flow in,
/// commands are pushed out). Multiple instances so a persistent connector never blocks CLI.
/// </summary>
public sealed class PipeServer : IDisposable
{
    public const string PipeName = "sessiondeck";

    private readonly Func<string[], PipeResponse> _cliHandler;
    private readonly Action<VscodeSyncMessage, VscodeConnection> _syncHandler;
    private readonly Action<VscodeConnection> _closedHandler;
    private readonly CancellationTokenSource _cts = new();

    public PipeServer(Func<string[], PipeResponse> cliHandler,
                      Action<VscodeSyncMessage, VscodeConnection> syncHandler,
                      Action<VscodeConnection> closedHandler)
    {
        _cliHandler = cliHandler;
        _syncHandler = syncHandler;
        _closedHandler = closedHandler;
    }

    public void Start() => _ = Task.Run(AcceptLoopAsync);

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                // Non-zero out-buffer so short pushes don't block even momentarily.
                server = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    inBufferSize: 16384, outBufferSize: 65536);
                await server.WaitForConnectionAsync(_cts.Token);
                var connected = server;
                server = null;
                _ = Task.Run(() => ServeAsync(connected));
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch
            {
                server?.Dispose();
                try { await Task.Delay(200, _cts.Token); } catch { break; }
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream server)
    {
        VscodeConnection? connector = null;
        try
        {
            using var reader = new StreamReader(server, leaveOpen: true);
            await using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };

            string? line = await reader.ReadLineAsync(_cts.Token);
            if (line == null) return;

            if (TryParseSync(line, out var sync))
            {
                connector = new VscodeConnection(writer);
                while (sync != null)
                {
                    _syncHandler(sync, connector);
                    line = await reader.ReadLineAsync(_cts.Token);
                    if (line == null) break;
                    TryParseSync(line, out sync);
                }
                return;
            }

            var response = HandleCli(line);
            await writer.WriteLineAsync(JsonSerializer.Serialize(response));
            server.WaitForPipeDrain();
        }
        catch (OperationCanceledException) { }
        catch
        {
            // Broken client connection — nothing to do.
        }
        finally
        {
            if (connector != null) _closedHandler(connector);
            try { server.Dispose(); } catch { }
        }
    }

    private static bool TryParseSync(string line, out VscodeSyncMessage? sync)
    {
        sync = null;
        try
        {
            var parsed = JsonSerializer.Deserialize<VscodeSyncMessage>(line);
            if (parsed?.Type != "vscode-sync") return false;
            sync = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private PipeResponse HandleCli(string line)
    {
        try
        {
            var request = JsonSerializer.Deserialize<PipeRequest>(line);
            if (request?.Argv is not { Length: > 0 })
                return new PipeResponse(1, "malformed request");
            return _cliHandler(request.Argv);
        }
        catch (Exception ex)
        {
            return new PipeResponse(1, "error: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

public sealed class PipeRequest
{
    public string[]? Argv { get; set; }
}
