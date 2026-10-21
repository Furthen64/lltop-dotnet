using System.Net;
using System.Text;
using System.Text.Json;

internal sealed record OpenCodeContextConfig(string Path, long Context, byte[] OriginalBytes);
internal sealed record OpenCodeContextUpdate(string BackupPath);

internal sealed class OpenCodeContextLaunchState
{
    readonly object gate = new();
    int generation;
    int promptedGeneration;

    public int BeginLaunch()
    {
        lock (gate)
        {
            promptedGeneration = 0;
            return ++generation;
        }
    }
    public int Invalidate() { lock (gate) return ++generation; }
    public bool IsCurrent(int candidate) { lock (gate) return candidate == generation; }

    public bool TryClaimPrompt(int candidate)
    {
        lock (gate)
        {
            if (candidate != generation || promptedGeneration == candidate) return false;
            promptedGeneration = candidate;
            return true;
        }
    }
}

internal sealed class OpenCodeContextSync(HttpClient? httpClient = null)
{
    const string TargetProperty = "provider.66_local.models.qwen.limit.context";
    readonly HttpClient client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    readonly TimeSpan discoveryTimeout = TimeSpan.FromSeconds(45);
    readonly TimeSpan retryDelay = TimeSpan.FromMilliseconds(750);

    public OpenCodeContextSync(HttpClient? httpClient, TimeSpan discoveryTimeout, TimeSpan retryDelay) : this(httpClient)
    {
        this.discoveryTimeout = discoveryTimeout;
        this.retryDelay = retryDelay;
    }

    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode", "opencode.json");

    public async Task<long> WaitForEffectiveContextAsync(int port, CancellationToken cancellationToken)
    {
        var endpoint = new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", port, "slots").Uri;
        var deadline = DateTimeOffset.UtcNow + discoveryTimeout;
        string lastError = "llama-server did not become ready.";
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.GetAsync(endpoint, cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var json = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    return ParseSlotsContext(json);
                }
                lastError = $"/slots returned {(int)response.StatusCode} {response.ReasonPhrase}.";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = "Timed out waiting for the local /slots endpoint.";
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or UriFormatException)
            {
                lastError = ex.Message;
            }
            await Task.Delay(retryDelay, cancellationToken);
        }
        throw new InvalidOperationException($"Could not discover llama-server context: {lastError}");
    }

    public static long ParseSlotsContext(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The /slots response must be a JSON array.");

        var contexts = new List<long>();
        foreach (var slot in document.RootElement.EnumerateArray())
        {
            if (slot.ValueKind != JsonValueKind.Object
                || !slot.TryGetProperty("n_ctx", out var value)
                || !value.TryGetInt64(out var context)
                || context <= 0)
                throw new InvalidDataException("The /slots response contains a slot with an invalid n_ctx value.");
            contexts.Add(context);
        }

        if (contexts.Count == 0) throw new InvalidDataException("The /slots response contains no slots.");
        if (contexts.Distinct().Count() != 1)
            throw new InvalidDataException("The server reports different n_ctx values across slots; context synchronization was skipped as ambiguous.");
        return contexts[0];
    }

    public static OpenCodeContextConfig ReadConfig(string path)
    {
        var fullPath = Path.GetFullPath(path);
        byte[] bytes;
        try { bytes = File.ReadAllBytes(fullPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Could not read OpenCode configuration '{fullPath}': {ex.Message}", ex);
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var context = document.RootElement
                .GetProperty("provider").GetProperty("66_local").GetProperty("models")
                .GetProperty("qwen").GetProperty("limit").GetProperty("context");
            if (!context.TryGetInt64(out var configured) || configured <= 0)
                throw new InvalidDataException($"{TargetProperty} must be a positive integer.");
            return new(fullPath, configured, bytes);
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidDataException($"OpenCode configuration is missing {TargetProperty}.", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidDataException($"OpenCode configuration does not contain a valid {TargetProperty} object path.", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"OpenCode configuration is not valid JSON: {ex.Message}", ex);
        }
    }

    public static bool NeedsUpdate(long configuredContext, long effectiveContext) => configuredContext != effectiveContext;

    public static OpenCodeContextUpdate? ApplyDecision(OpenCodeContextConfig snapshot, long effectiveContext, bool approved)
        => approved ? UpdateConfig(snapshot, effectiveContext) : null;

    public static OpenCodeContextUpdate UpdateConfig(OpenCodeContextConfig snapshot, long newContext)
    {
        if (newContext <= 0) throw new ArgumentOutOfRangeException(nameof(newContext));
        var path = snapshot.Path;
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("OpenCode configuration has no parent directory.");
        var current = File.ReadAllBytes(path);
        if (!current.AsSpan().SequenceEqual(snapshot.OriginalBytes))
            throw new InvalidOperationException("OpenCode configuration changed while confirmation was open. No update was made; review the current configuration and launch again.");

        var updated = ReplaceContextNumber(current, newContext);
        var backupPath = CreateBackup(path, current);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(updated);
                stream.Flush(flushToDisk: true);
            }

            // Recheck after preparing the temporary file so an edit made during the write is not replaced.
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(snapshot.OriginalBytes))
                throw new InvalidOperationException("OpenCode configuration changed before the update could be applied. No update was made.");
            File.Move(tempPath, path, overwrite: true);
            return new(backupPath);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    static string CreateBackup(string path, byte[] original)
    {
        var directory = Path.GetDirectoryName(path)!;
        var stamp = DateTime.Now.ToString("yyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        for (var collision = 0; collision < 10000; collision++)
        {
            var suffix = collision == 0 ? "" : $"_{collision}";
            var candidate = Path.Combine(directory, $"opencodejson_{stamp}{suffix}.bak");
            FileStream stream;
            try
            {
                stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(candidate)) { continue; }
            try
            {
                using (stream)
                {
                    stream.Write(original);
                    stream.Flush(flushToDisk: true);
                }
                return candidate;
            }
            catch
            {
                try { File.Delete(candidate); } catch { }
                throw;
            }
        }
        throw new IOException("Could not create a unique timestamped backup for opencode.json.");
    }

    static byte[] ReplaceContextNumber(byte[] json, long newContext)
    {
        using var document = JsonDocument.Parse(json);
        var context = document.RootElement.GetProperty("provider").GetProperty("66_local").GetProperty("models")
            .GetProperty("qwen").GetProperty("limit").GetProperty("context");
        if (!context.TryGetInt64(out _) || context.ValueKind != JsonValueKind.Number)
            throw new InvalidDataException($"{TargetProperty} must be a positive integer.");

        var reader = new Utf8JsonReader(json);
        var keys = new List<string?>();
        var start = -1;
        var length = 0;
        var matches = 0;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    keys.Add(null);
                    break;
                case JsonTokenType.PropertyName:
                    if (keys.Count > 0) keys[^1] = reader.GetString();
                    break;
                case JsonTokenType.Number:
                    if (keys.SequenceEqual(new string?[] { "provider", "66_local", "models", "qwen", "limit", "context" }))
                    {
                        matches++;
                        start = checked((int)reader.TokenStartIndex);
                        length = checked((int)(reader.BytesConsumed - reader.TokenStartIndex));
                    }
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    if (keys.Count > 0) keys.RemoveAt(keys.Count - 1);
                    break;
            }
        }
        if (matches != 1 || start < 0 || length == 0)
            throw new InvalidDataException($"Could not locate one unambiguous {TargetProperty} value in the original JSON.");
        var replacement = Encoding.UTF8.GetBytes(newContext.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var output = new byte[json.Length - length + replacement.Length];
        Buffer.BlockCopy(json, 0, output, 0, start);
        Buffer.BlockCopy(replacement, 0, output, start, replacement.Length);
        Buffer.BlockCopy(json, start + length, output, start + replacement.Length, json.Length - start - length);
        return output;
    }
}
