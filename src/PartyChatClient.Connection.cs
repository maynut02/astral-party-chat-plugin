using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AstralPartyChatPlugin;

internal sealed partial class PartyChatClient
{
    private async Task RunSessionAsync(PartySession session)
    {
        var attempt = 0;
        try
        {
            while (!session.Token.IsCancellationRequested)
            {
                PartyConnection? connection = null;
                var delay = TimeSpan.Zero;
                try
                {
                    await EnsureCharacterNamesAsync(session).ConfigureAwait(false);
                    if (!session.RoomEnsured)
                        await EnsureRoomAsync(session).ConfigureAwait(false);
                    SetStatus(session, attempt == 0 ? "채팅 서버 연결 중" : "채팅 서버 재연결 중");
                    var socket = _options.CreateSocket();
                    connection = new PartyConnection(session, socket);
                    if (socket is ClientWebSocket clientSocket)
                    {
                        clientSocket.Options.SetRequestHeader("Origin", _options.SiteOrigin);
                        clientSocket.Options.SetRequestHeader("X-Astral-Party-Client", "windows-plugin");
                        clientSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                    }
                    lock (_gate)
                    {
                        if (!IsCurrentSessionLocked(session)) return;
                        _connection = connection;
                    }
                    using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(connection.Token))
                    {
                        connectCts.CancelAfter(_options.ConnectTimeout);
                        await _options.ConnectSocket(socket, _options.WebSocketUri, connectCts.Token).ConfigureAwait(false);
                    }
                    SetStatus(session, "채팅방 입장 중");
                    object join;
                    lock (_gate)
                    {
                        if (!IsCurrentConnectionLocked(connection)) return;
                        var payload = new System.Collections.Generic.Dictionary<string, object?>
                        {
                            ["type"] = "JOIN", ["roomId"] = session.RoomId,
                            ["nickname"] = session.Nickname, ["characterId"] = _desiredCharacter
                        };
                        // Server rejects null for a non-spectator order; omit an unknown order.
                        if (_desiredCharacter == "spectator") payload["order"] = null;
                        else if (_desiredOrder.Length > 0) payload["order"] = _desiredOrder;
                        join = payload;
                    }
                    await SendJsonAsync(connection, join).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (!IsCurrentConnectionLocked(connection)) return;
                        connection.StartedAt = DateTime.UtcNow;
                        connection.LastReceivedAt = connection.StartedAt;
                    }
                    var receive = ReceiveLoopAsync(connection);
                    var maintenance = MaintainConnectionAsync(connection);
                    try
                    {
                        var completed = await Task.WhenAny(receive, maintenance).ConfigureAwait(false);
                        await completed.ConfigureAwait(false);
                    }
                    finally
                    {
                        connection.Abort();
                        try { await Task.WhenAll(receive, maintenance).ConfigureAwait(false); } catch { }
                    }
                    throw new IOException("WebSocket connection closed.");
                }
                catch (FatalPartyException ex)
                {
                    SetStatus(session, ex.Message);
                    return;
                }
                catch (OperationCanceledException) when (session.Token.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        if (!IsCurrentSessionLocked(session)) return;
                        if (connection != null && connection.Joined) attempt = 0;
                        _connection = null;
                        FailPendingMessagesLocked("연결이 끊겨 전송 결과를 확인하지 못했습니다.");
                        _status = "채팅 연결 대기: " + ex.Message + " 자동으로 다시 연결합니다.";
                    }
                    connection?.Abort();
                    _log.LogWarning("Chat connection retry: " + ex.GetType().Name + ": " + ex.Message);
                    var seconds = Math.Min(_options.RetryDelay.TotalSeconds * Math.Pow(2, Math.Min(attempt++, 4)),
                        _options.MaxRetryDelay.TotalSeconds);
                    delay = TimeSpan.FromSeconds(seconds);
                    if (ex is RetryPartyException retry && retry.Delay is { } minimum && minimum > delay) delay = minimum;
                }
                finally
                {
                    lock (_gate) { if (ReferenceEquals(_connection, connection)) _connection = null; }
                    connection?.Dispose();
                }
                // Release the failed socket and linked token source before a
                // potentially long server-directed cooldown.
                await Task.Delay(delay, session.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested) { }
        finally { session.Dispose(); }
    }

    private async Task EnsureCharacterNamesAsync(PartySession session)
    {
        lock (_gate) { if (_characterNames.Count > 0) return; }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.CharactersUri);
            request.Headers.TryAddWithoutValidation("X-Astral-Party-Client", "windows-plugin");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;
            var bytes = await RemotePayload.ReadBytesAsync(response.Content, RemotePayload.MaxCharactersBytes, deadline.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 128) return;
            lock (_gate)
            {
                if (!IsCurrentSessionLocked(session)) return;
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    var id = PartyProtocol.NormalizeCharacter(GetString(item, "id"));
                    var name = PartyProtocol.LimitText(GetString(item, "name").Trim(), 64);
                    if (id != "spectator" && name.Length > 0) _characterNames[id] = name;
                }
            }
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.LogDebug("Character name lookup failed: " + ex.Message); }
    }

    private async Task EnsureRoomAsync(PartySession session)
    {
        SetStatus(session, "채팅방 확인 중");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.RoomUri);
        request.Headers.TryAddWithoutValidation("X-Astral-Party-Client", "windows-plugin");
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            // This endpoint creates/locates the room. JOIN validates nickname
            // ownership; an old socket may still be awaiting server cleanup.
            roomId = session.RoomId
        }), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            await RemotePayload.ReadBytesAsync(response.Content, RemotePayload.MaxRoomBytes, deadline.Token).ConfigureAwait(false);
            session.RoomEnsured = true;
            return;
        }
        string message;
        try
        {
            var body = await RemotePayload.ReadBytesAsync(response.Content, RemotePayload.MaxRoomBytes, deadline.Token).ConfigureAwait(false);
            message = ExtractHttpError(Encoding.UTF8.GetString(body));
        }
        catch (InvalidDataException) { message = "서버 오류 응답이 너무 큽니다."; }
        catch (OperationCanceledException) when (!session.Token.IsCancellationRequested)
        { message = "서버 오류 응답 시간이 초과되었습니다."; }
        // Preserve status-based retry policy even when the optional error body is invalid.
        var code = (int)response.StatusCode;
        if (code is 408 or 409 or 429 || code >= 500)
        {
            var delay = code == 429 ? _options.RateLimitRetryDelay : (TimeSpan?)null;
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter?.Delta is { } delta && (delay == null || delta > delay.Value)) delay = delta;
            else if (retryAfter?.Date is { } date && date > DateTimeOffset.UtcNow)
            {
                var remaining = date - DateTimeOffset.UtcNow;
                if (delay == null || remaining > delay.Value) delay = remaining;
            }
            throw new RetryPartyException("채팅방 입장 실패: " + message, delay);
        }
        throw new FatalPartyException("채팅방 입장 실패: " + message);
    }

    private async Task ReceiveLoopAsync(PartyConnection connection)
    {
        var buffer = new byte[16 * 1024];
        while (!connection.Token.IsCancellationRequested && connection.Socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await connection.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), connection.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > 1024 * 1024) throw new InvalidDataException("Incoming chat message is too large.");
                }
            } while (!result.EndOfMessage);
            if (result.MessageType == WebSocketMessageType.Text)
                ProcessServerMessage(Encoding.UTF8.GetString(message.ToArray()), connection);
        }
    }

    private async Task MaintainConnectionAsync(PartyConnection connection)
    {
        var nextHeartbeat = DateTime.UtcNow + _options.HeartbeatInterval;
        var interval = TimeSpan.FromMilliseconds(Math.Max(10, Math.Min(500,
            Math.Min(_options.PresenceTimeout.TotalMilliseconds, _options.JoinTimeout.TotalMilliseconds) / 2)));
        while (!connection.Token.IsCancellationRequested)
        {
            await Task.Delay(interval, connection.Token).ConfigureAwait(false);
            var now = DateTime.UtcNow;
            lock (_gate)
            {
                if (!IsCurrentConnectionLocked(connection)) return;
                if (!connection.Joined && now - connection.StartedAt >= _options.JoinTimeout)
                    throw new TimeoutException("채팅방 입장 응답이 지연되었습니다.");
                if (now - connection.LastReceivedAt >= _options.HeartbeatTimeout)
                    throw new TimeoutException("채팅 서버 응답이 없습니다.");
                if (connection.PendingPresence is { } pending && pending.Deadline <= now)
                    throw new TimeoutException("참가 정보 변경 응답이 지연되었습니다.");
                foreach (var id in _pendingMessages.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray())
                    FailMessageLocked(id, "전송 확인이 지연되었습니다. 다시 보내면 중복될 수 있습니다.");
            }
            SchedulePresenceUpdate(connection);
            if (now >= nextHeartbeat)
            {
                await SendJsonAsync(connection, new { type = "PING" }).ConfigureAwait(false);
                nextHeartbeat = now + _options.HeartbeatInterval;
            }
        }
    }

    private async Task SendJsonAsync(PartyConnection connection, object payload)
    {
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
        sendCts.CancelAfter(_options.SendTimeout);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await connection.SendLock.WaitAsync(sendCts.Token).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (!IsCurrentConnectionLocked(connection)) throw new OperationCanceledException(connection.Token);
            }
            if (connection.Socket.State != WebSocketState.Open) throw new WebSocketException("WebSocket is not open.");
            await connection.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, sendCts.Token).ConfigureAwait(false);
        }
        finally { connection.SendLock.Release(); }
    }
}
