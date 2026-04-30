using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.TreeSitter;

/// <summary>
/// Hosts the tree-sitter sidecar process and parses files through a JSON-line protocol.
/// </summary>
public sealed class TreeSitterClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ParsedFile>> _pending = new(StringComparer.Ordinal);
    private readonly string _nodeExecutable;
    private readonly string _sidecarPath;
    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _stdoutReaderTask;
    private Task? _stderrReaderTask;
    private bool _disposed;
    private readonly EventHandler _processExitedHandler;

    /// <summary>
    /// Initializes a new instance of the <see cref="TreeSitterClient"/> class.
    /// </summary>
    /// <param name="nodeExecutable">The node executable to run.</param>
    /// <param name="sidecarPath">The sidecar script path. When omitted, the repository default is used.</param>
    public TreeSitterClient(string nodeExecutable = "node", string? sidecarPath = null)
    {
        _nodeExecutable = nodeExecutable;
        _sidecarPath = sidecarPath ?? ResolveDefaultSidecarPath();
        _processExitedHandler = (_, _) => OnProcessExited();
    }

    /// <summary>
    /// Parses source text through the sidecar.
    /// </summary>
    /// <param name="path">The repository-relative file path.</param>
    /// <param name="lang">The file language.</param>
    /// <param name="source">The file source text.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The parsed file AST.</returns>
    public async Task<ParsedFile> ParseAsync(string path, Language lang, string source, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        await EnsureProcessAsync(cancellationToken).ConfigureAwait(false);

        string requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ParsedFile>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, tcs))
        {
            throw new InvalidOperationException("Failed to register tree-sitter request.");
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state =>
            {
                ((TaskCompletionSource<ParsedFile>)state!).TrySetCanceled();
            },
            tcs);

        try
        {
            ParseRequest request = new(requestId, path, LanguageToProtocolValue(lang), source);
            string payload = JsonSerializer.Serialize(request, SerializerOptions);

            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_stdin is null)
                {
                    throw new InvalidOperationException("Tree-sitter sidecar is not available.");
                }

                await _stdin.WriteLineAsync(payload).ConfigureAwait(false);
                await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }

            return await tcs.Task.ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(requestId, out _);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            CleanupProcess(new ObjectDisposedException(nameof(TreeSitterClient)));
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
            _writeGate.Dispose();
        }
    }

    private async Task EnsureProcessAsync(CancellationToken cancellationToken)
    {
        if (IsProcessHealthy())
        {
            return;
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsProcessHealthy())
            {
                return;
            }

            CleanupProcess(new InvalidOperationException("Tree-sitter sidecar exited."));

            if (!File.Exists(_sidecarPath))
            {
                throw new FileNotFoundException("Tree-sitter sidecar script was not found.", _sidecarPath);
            }

            var startInfo = new ProcessStartInfo(_nodeExecutable, Quote(_sidecarPath))
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_sidecarPath) ?? Environment.CurrentDirectory,
            };

            Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start tree-sitter sidecar process.");

            _process = process;
            _stdin = process.StandardInput;
            _stdoutReaderTask = Task.Run(ReadStdOutAsync);
            _stderrReaderTask = Task.Run(ReadStdErrAsync);
            process.EnableRaisingEvents = true;
            process.Exited += _processExitedHandler;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CleanupProcess(ex);
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task ReadStdOutAsync()
    {
        try
        {
            while (_process is { HasExited: false } process)
            {
                string? line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                HandleResponseLine(line);
            }

            CleanupProcess(new InvalidOperationException("Tree-sitter sidecar closed stdout."));
        }
        catch (Exception ex)
        {
            CleanupProcess(ex);
        }
    }

    private async Task ReadStdErrAsync()
    {
        try
        {
            while (_process is { HasExited: false } process)
            {
                string? line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            CleanupProcess(ex);
        }
    }

    private void HandleResponseLine(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;

        string? requestId = GetString(root, "id") ?? GetString(root, "requestId");
        JsonElement payload = root;
        if (TryGetProperty(root, "result", out JsonElement result))
        {
            payload = result;
        }

        if (TryGetProperty(root, "error", out JsonElement error))
        {
            string message = GetString(error, "message")
                ?? (error.ValueKind == JsonValueKind.String ? error.GetString() : null)
                ?? "Tree-sitter sidecar returned an error.";

            if (!string.IsNullOrWhiteSpace(requestId) && _pending.TryRemove(requestId, out TaskCompletionSource<ParsedFile>? failed))
            {
                failed.TrySetException(new InvalidOperationException(message));
            }

            return;
        }

        ParsedFile parsedFile = ParsedFile.FromJsonElement(payload);
        requestId ??= parsedFile.RequestId;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            if (_pending.Count == 1)
            {
                requestId = _pending.Keys.Single();
            }
            else
            {
                throw new InvalidOperationException("Tree-sitter response did not include a request identifier.");
            }
        }

        if (_pending.TryRemove(requestId, out TaskCompletionSource<ParsedFile>? pending))
        {
            pending.TrySetResult(parsedFile with { RequestId = requestId });
        }
    }

    private void OnProcessExited()
    {
        CleanupProcess(new InvalidOperationException("Tree-sitter sidecar exited unexpectedly."));
    }

    private void CleanupProcess(Exception exception)
    {
        Process? process = Interlocked.Exchange(ref _process, null);
        StreamWriter? stdin = Interlocked.Exchange(ref _stdin, null);
        _stdoutReaderTask = null;
        _stderrReaderTask = null;

        try
        {
            stdin?.Dispose();
        }
        catch
        {
        }

        if (process is not null)
        {
            process.Exited -= _processExitedHandler;

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        foreach ((string requestId, TaskCompletionSource<ParsedFile> pending) in _pending.ToArray())
        {
            if (_pending.TryRemove(requestId, out _))
            {
                pending.TrySetException(exception);
            }
        }
    }

    private bool IsProcessHealthy()
    {
        return _process is { HasExited: false } && _stdin is not null;
    }

    private static string ResolveDefaultSidecarPath()
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            string candidate = Path.Combine(directory, "tools", "treesitter-sidecar", "index.js");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            DirectoryInfo? parent = Directory.GetParent(directory);
            if (parent is null)
            {
                break;
            }

            directory = parent.FullName;
        }

        return Path.Combine(Environment.CurrentDirectory, "tools", "treesitter-sidecar", "index.js");
    }

    private static string LanguageToProtocolValue(Language language)
    {
        return language switch
        {
            Language.CSharp => "csharp",
            Language.TypeScript => "typescript",
            Language.Tsx => "tsx",
            Language.JavaScript => "javascript",
            Language.Jsx => "jsx",
            Language.Python => "python",
            _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Unsupported tree-sitter language."),
        };
    }

    private static string Quote(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private sealed record ParseRequest(string Id, string File, string Language, string Source);
}

/// <summary>
/// Represents a parsed source file returned by the tree-sitter sidecar.
/// </summary>
/// <param name="Path">The file path.</param>
/// <param name="Language">The parsed language.</param>
/// <param name="Root">The root AST node.</param>
/// <param name="RequestId">The request identifier when available.</param>
public sealed record ParsedFile(string Path, string? Language, AstNode Root, string? RequestId = null)
{
    /// <summary>
    /// Creates a <see cref="ParsedFile"/> from JSON.
    /// </summary>
    /// <param name="element">The JSON element.</param>
    /// <returns>The parsed file.</returns>
    public static ParsedFile FromJsonElement(JsonElement element)
    {
        string path = GetString(element, "path")
            ?? GetString(element, "file")
            ?? string.Empty;

        string? language = GetString(element, "language");
        string? requestId = GetString(element, "id") ?? GetString(element, "requestId");

        JsonElement root = TryGetProperty(element, "root", out JsonElement rootElement)
            ? rootElement
            : TryGetProperty(element, "rootNode", out rootElement)
                ? rootElement
                : TryGetProperty(element, "ast", out rootElement)
                    ? rootElement
                : element;

        return new ParsedFile(path, language, AstNode.FromJsonElement(root), requestId);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
