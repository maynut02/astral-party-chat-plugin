using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace AstralPartyChatPlugin;

internal delegate Task<byte[]> UpdateDownload(Uri url, int limit, bool noCache, CancellationToken token);

internal sealed class RuntimeHttp : IDisposable
{
    private readonly HttpClient _client;

    public RuntimeHttp(string version)
    {
        // Platform TLS validation stays on; responses are streamed within the caller's size limit.
        _client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("AstralPartyChatPlugin/" + version);
        _client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
    }

    public async Task<byte[]> DownloadAsync(Uri url, int limit, bool noCache, CancellationToken token)
    {
        // Allow at least 64 KiB/s for large bodies; the per-read idle timeout still catches stalls.
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(Math.Max(15, limit / (64 * 1024))));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (noCache) request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > limit)
            throw new InvalidDataException("HTTP response exceeds size limit: " + url);
        using var input = await response.Content.ReadAsStreamAsync(cancellation.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            idle.CancelAfter(TimeSpan.FromSeconds(5));
            int read;
            try { read = await input.ReadAsync(buffer.AsMemory(), idle.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            { throw new TimeoutException("HTTP download stalled: " + url); }
            if (read == 0) break;
            if (output.Length + read > limit) throw new InvalidDataException("HTTP response exceeds size limit: " + url);
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// Checks the latest GitHub release once per game start and installs a newer,
/// signed plugin DLL. Nothing is published to the UI unless the player has
/// something to do: restart, or update by hand.
/// </summary>
internal sealed class PluginUpdateClient : IDisposable
{
    public static readonly Uri ManifestUrl = new("https://github.com/maynut02/astral-party-chat-plugin/releases/latest/download/update.json");
    public static readonly Uri SignatureUrl = new(ManifestUrl.AbsoluteUri + ".sig");

    private static readonly PluginUpdateState Quiet = new(null, UpdateSeverity.Success, false);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Action<string> _log;
    private PluginUpdateState _state = Quiet;
    private int _revision;

    public PluginUpdateClient(string installed, string runningBepInEx, bool autoUpdate,
        UpdateDownload download, Action<byte[]> install, Action<string> log,
        string publicKey = UpdateManifest.PublicKey)
    {
        _log = log;
        var token = _lifetime.Token;
        RefreshTask = Task.Run(() => CheckAsync(installed, runningBepInEx, autoUpdate, download, install, publicKey, token));
    }

    public Task RefreshTask { get; }
    public int Revision => Volatile.Read(ref _revision);
    public PluginUpdateState Snapshot() => Volatile.Read(ref _state);

    private void Publish(PluginUpdateState next)
    {
        Volatile.Write(ref _state, next);
        Interlocked.Increment(ref _revision);
    }

    public void RestartFailed(Exception ex)
    {
        _log("Plugin restart failed: " + ex.GetType().Name + ": " + ex.Message);
        Publish(Snapshot() with
        {
            Message = "다시 시작하지 못했습니다 · 게임을 종료한 뒤 다시 실행해 주세요",
            Severity = UpdateSeverity.Warning
        });
    }

    private async Task CheckAsync(string installed, string runningBepInEx, bool autoUpdate,
        UpdateDownload download, Action<byte[]> install, string publicKey, CancellationToken token)
    {
        string? latest = null;
        try
        {
            var bytes = await download(ManifestUrl, UpdateManifest.ManifestLimit, true, token).ConfigureAwait(false);
            var signature = await download(SignatureUrl, UpdateManifest.SignatureLimit, true, token).ConfigureAwait(false);
            var manifest = UpdateManifest.Verify(bytes, signature, publicKey);
            latest = manifest.Version.Text;
            if (manifest.Version.CompareTo(SemanticVersion.Parse(installed)) <= 0)
            {
                _log("Installed plugin v" + installed + " is up to date (latest v" + latest + ")");
                return;
            }
            if (!autoUpdate)
            {
                _log("Plugin update v" + latest + " available; automatic installation disabled");
                Publish(new PluginUpdateState("새 버전 v" + latest + " 출시 · 자동 업데이트가 꺼져 있어 직접 설치해야 합니다",
                    UpdateSeverity.Warning, false));
                return;
            }
            if (string.IsNullOrEmpty(runningBepInEx)
                || SemanticVersion.Parse(runningBepInEx).CompareTo(manifest.MinimumBepInEx) < 0)
            {
                _log("Plugin update v" + latest + " requires BepInEx >= " + manifest.MinimumBepInEx.Text + "; running=" + runningBepInEx);
                Publish(new PluginUpdateState("v" + latest + " 업데이트에는 BepInEx " + manifest.MinimumBepInEx.Text
                    + " 이상이 필요합니다 · 직접 업데이트해 주세요", UpdateSeverity.Warning, false));
                return;
            }
            _log("Downloading plugin update package: " + manifest.PackageUrl);
            var package = await download(manifest.PackageUrl, manifest.PackageSize, false, token).ConfigureAwait(false);
            var dll = manifest.Extract(package);
            token.ThrowIfCancellationRequested();
            install(dll);
            Publish(new PluginUpdateState("v" + latest + " 설치 완료 · 게임을 다시 시작하면 적용됩니다",
                UpdateSeverity.Success, true));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log("Plugin update failed: " + ex.GetType().Name + ": " + ex.Message);
            // An unreachable or unverifiable manifest (offline, GitHub outage, a release without
            // update files) needs no player action. Only a failure after verification does.
            if (latest != null)
                Publish(new PluginUpdateState("v" + latest + " 자동 업데이트 실패 · 게임 종료 후 직접 설치해 주세요",
                    UpdateSeverity.Warning, false));
        }
    }

    /// <summary>Stops an in-flight check; a cancelled check installs and publishes nothing.</summary>
    public void Dispose() => _lifetime.Cancel();
}
