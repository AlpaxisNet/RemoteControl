using System.Net;
using System.Net.Http.Headers;

namespace RemoteControl.Scripting;

/// <summary>Where loaded definitions came from.</summary>
public enum ApiDefinitionOrigin
{
    /// <summary>Nothing usable was found: neither the source nor the cache gave definitions.</summary>
    None,

    /// <summary>From the configured source (URL, file URI or path).</summary>
    Source,

    /// <summary>From the cache file, because the source failed.</summary>
    Cache,
}

/// <summary>The outcome of loading the script API.</summary>
/// <param name="Definitions">The merged definition set (built-ins included even when nothing loaded).</param>
/// <param name="Origin">Where the definitions came from.</param>
/// <param name="SourceError">Why the source failed, or null when it succeeded.</param>
public sealed record ApiLoadResult(ApiDefinitionSet Definitions, ApiDefinitionOrigin Origin, string? SourceError);

/// <summary>
/// Loads the script API from the <c>autocompletesource</c> option (C++ <c>fetchCompletionDefinitions</c>): an
/// <c>http://</c>/<c>https://</c> URL (GET with <c>Accept: application/json</c>, 15 s timeout, 8 MiB limit, TLS certificates
/// verified, only status 200 accepted, redirects not followed), a <c>file://</c> URI or a plain local path. A source that
/// yields definitions is written to the cache file (atomically); when the source fails, the cache file is read instead.
/// </summary>
public sealed class ApiDefinitionLoader
{
    /// <summary>The C++ default source.</summary>
    public const string DefaultSource = "https://api.gscript.dev/";

    /// <summary>Largest accepted response or file, 8 MiB.</summary>
    public const int MaximumBytes = 8 * 1024 * 1024;

    /// <summary>Timeout for the whole HTTP request.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly Lazy<HttpMessageHandler> SharedHandler = new(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = Timeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    });

    private readonly HttpMessageHandler handler;

    /// <summary>Creates a loader.</summary>
    /// <param name="handler">The HTTP handler to use (tests inject one); null uses a shared handler with default certificate validation.</param>
    public ApiDefinitionLoader(HttpMessageHandler? handler = null)
    {
        this.handler = handler ?? SharedHandler.Value;
    }

    /// <summary>Loads the definitions.</summary>
    /// <param name="source">The source option; empty means no source (only the cache is tried).</param>
    /// <param name="cacheFile">The cache file (for example <c>&lt;cache&gt;/editors/scriptapi.json</c>), or null for no cache.</param>
    /// <param name="cancellationToken">Cancels the load; a cancelled load throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>The result; never throws for network, file or format problems.</returns>
    public async Task<ApiLoadResult> LoadAsync(string? source, string? cacheFile, CancellationToken cancellationToken = default)
    {
        string? error;
        byte[]? bytes;
        (bytes, error) = await FetchAsync(source?.Trim() ?? string.Empty, cancellationToken).ConfigureAwait(false);
        if (bytes != null)
        {
            IReadOnlyList<ApiDefinition> parsed = ApiDefinitionParser.Parse(bytes);
            if (parsed.Count > 0)
            {
                if (!string.IsNullOrEmpty(cacheFile)) await WriteCacheAsync(cacheFile, bytes, cancellationToken).ConfigureAwait(false);
                return new ApiLoadResult(ApiDefinitionSet.Create(parsed), ApiDefinitionOrigin.Source, null);
            }
            error = "The API source contains no definitions";
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(cacheFile))
        {
            byte[]? cached = await ReadFileAsync(cacheFile, cancellationToken).ConfigureAwait(false);
            if (cached != null)
            {
                IReadOnlyList<ApiDefinition> parsed = ApiDefinitionParser.Parse(cached);
                if (parsed.Count > 0) return new ApiLoadResult(ApiDefinitionSet.Create(parsed), ApiDefinitionOrigin.Cache, error);
            }
        }
        return new ApiLoadResult(ApiDefinitionSet.Create(Array.Empty<ApiDefinition>()), ApiDefinitionOrigin.None, error);
    }

    private async Task<(byte[]? Bytes, string? Error)> FetchAsync(string source, CancellationToken cancellationToken)
    {
        if (source.Length == 0) return (null, "No API source is configured");
        if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return await DownloadAsync(source, cancellationToken).ConfigureAwait(false);
        }
        string path = source;
        if (source.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) || !uri.IsFile) return (null, "Invalid file URI");
            path = uri.LocalPath;
        }
        byte[]? bytes = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        return bytes == null ? (null, "Cannot read " + path) : (bytes, null);
    }

    private async Task<(byte[]? Bytes, string? Error)> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || uri.Host.Length == 0)
        {
            return (null, "Invalid API URL");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("RemoteControl/1.0");
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) return (null, "The API source answered " + (int)response.StatusCode);
            if (response.Content.Headers.ContentLength is long length && length > MaximumBytes) return (null, "The API source is larger than 8 MiB");
            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            byte[]? bytes = await ReadLimitedAsync(stream, timeout.Token).ConfigureAwait(false);
            return bytes == null ? (null, "The API source is larger than 8 MiB") : (bytes, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "The API source did not answer within 15 seconds");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (null, "The API source failed: " + exception.Message);
        }
    }

    private static async Task<byte[]?> ReadLimitedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        while (true)
        {
            int read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > MaximumBytes) return null;
            buffer.Write(chunk, 0, read);
        }
    }

    private static async Task<byte[]?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete, Options = FileOptions.Asynchronous };
            await using var stream = new FileStream(path, options);
            if (stream.Length > MaximumBytes) return null;
            return await ReadLimitedAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Writes the cache: a temporary file beside it, then a move over the old one, so a reader never sees half a file.</summary>
    private static async Task WriteCacheAsync(string cacheFile, byte[] bytes, CancellationToken cancellationToken)
    {
        string? temporary = null;
        try
        {
            string full = Path.GetFullPath(cacheFile);
            string? directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, full, overwrite: true);
            temporary = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OperationCanceledException)
        {
            // The cache is an optimisation; failing to write it is not an error of the load.
        }
        finally
        {
            if (temporary != null)
            {
                try { File.Delete(temporary); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
