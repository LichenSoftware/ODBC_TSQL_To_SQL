using Microsoft.Extensions.Logging;
using PgPassthrough.Core.Abstractions;
using PgPassthrough.Core.Models;
using PgPassthrough.Tds.Messages;
using System.Net.Sockets;

namespace PgPassthrough.Tds.Protocol;

/// <summary>
/// Manages the full lifecycle of one client connection.
/// 
/// State machine:
///   New → PreLogin → Login → Active → Closed
/// 
/// Each state handles one or more inbound TDS message types.
/// On error the session logs, sends an ERROR+DONE response, and closes.
/// 
/// This class drives an async loop; it does not use threads directly.
/// The caller (TdsListener) awaits ProcessAsync() per client.
/// </summary>
internal sealed class TdsSession : IAsyncDisposable
{
    private enum SessionState { PreLogin, Login, Active, Closed }

    private readonly TcpClient? _tcpClient;  // null when constructed from a Stream directly
    private readonly Stream? _injectedStream; // non-null when constructed from Stream (testing)
    private readonly IQueryHandler _queryHandler;
    private readonly ICredentialValidator _credentialValidator;
    private readonly ILogger _logger;
    private readonly CancellationToken _serverShutdownToken;

    private TdsPacketReader? _reader;
    private TdsPacketWriter? _writer;
    private TdsResponseWriter? _responseWriter;
    private SessionContext? _session;
    private SessionState _state = SessionState.PreLogin;
    private int _negotiatedPacketSize = TdsProtocol.DefaultPacketSize;

    /// <summary>Primary constructor used by TdsListener for real TCP connections.</summary>
    public TdsSession(
        TcpClient tcpClient,
        IQueryHandler queryHandler,
        ICredentialValidator credentialValidator,
        ILogger logger,
        CancellationToken serverShutdownToken)
    {
        _tcpClient           = tcpClient;
        _queryHandler        = queryHandler;
        _credentialValidator = credentialValidator;
        _logger              = logger;
        _serverShutdownToken = serverShutdownToken;
    }

    /// <summary>
    /// Test-only constructor that accepts a pre-existing stream.
    /// Avoids the need for a real TcpClient in unit tests.
    /// </summary>
    internal TdsSession(
        Stream stream,
        IQueryHandler queryHandler,
        ICredentialValidator credentialValidator,
        ILogger logger,
        CancellationToken serverShutdownToken)
    {
        _injectedStream      = stream;
        _queryHandler        = queryHandler;
        _credentialValidator = credentialValidator;
        _logger              = logger;
        _serverShutdownToken = serverShutdownToken;
    }

    /// <summary>
    /// Runs the session message loop to completion.
    /// Returns when the client disconnects or the server shuts down.
    /// </summary>
    public async Task ProcessAsync()
    {
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_serverShutdownToken);
        var ct = sessionCts.Token;

        try
        {
            Stream stream;
            if (_injectedStream != null)
            {
                stream = _injectedStream;
            }
            else
            {
                _tcpClient!.NoDelay = true;
                stream = _tcpClient.GetStream();
            }

            _reader        = new TdsPacketReader(stream);
            _writer        = new TdsPacketWriter(stream, _negotiatedPacketSize);
            _responseWriter = new TdsResponseWriter(_writer);

            _logger.LogDebug("TDS session opened.");

            while (!ct.IsCancellationRequested && _state != SessionState.Closed)
            {
                TdsMessage message;
                try
                {
                    message = await _reader.ReadMessageAsync(ct).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    _logger.LogDebug("Client disconnected cleanly.");
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }

                await DispatchMessageAsync(message, ct).ConfigureAwait(false);
            }
        }
        catch (TdsProtocolException ex)
        {
            _logger.LogWarning("Protocol error in session: {Message}", ex.Message);
            await TrySendFatalErrorAsync(ex.Message, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unhandled error in TDS session");
            await TrySendFatalErrorAsync("Internal server error", ct).ConfigureAwait(false);
        }
        finally
        {
            _state = SessionState.Closed;
            _logger.LogDebug("TDS session closed.");
        }
    }

    private async Task DispatchMessageAsync(TdsMessage message, CancellationToken ct)
    {
        switch (_state)
        {
            case SessionState.PreLogin:
                await HandlePreLoginAsync(message, ct).ConfigureAwait(false);
                break;
            case SessionState.Login:
                await HandleLoginAsync(message, ct).ConfigureAwait(false);
                break;
            case SessionState.Active:
                await HandleActiveMessageAsync(message, ct).ConfigureAwait(false);
                break;
        }
    }

    // =========================================================================
    // State: PreLogin
    // =========================================================================

    private async Task HandlePreLoginAsync(TdsMessage message, CancellationToken ct)
    {
        if (message.Type != TdsPacketType.PreLogin)
        {
            throw new TdsProtocolException(
                $"Expected PRELOGIN (0x12), got 0x{message.Type:X2} in PreLogin state.");
        }

        var preLogin = PreLoginMessage.Parse(message.PayloadSpan);
        _logger.LogDebug("PRELOGIN received. TdsVersion=0x{Ver:X8} Encryption={Enc}",
            preLogin.TdsVersion, preLogin.Encryption);

        // Determine encryption response.
        // ENCRYPT_NOT_SUP (0x02) tells the client we don't support encryption at all.
        // This causes Driver 17+ to skip the TLS handshake entirely.
        byte encryptionResponse = PreLoginMessage.EncryptionNotSupported;

        byte[] responsePayload = PreLoginMessage.BuildResponse(TdsProtocol.TdsVersion74, encryptionResponse);
        _writer!.BeginMessage(TdsPacketType.TabularResult);
        _writer.WriteBytes(responsePayload);
        await _writer.EndMessageAsync(ct).ConfigureAwait(false);

        _state = SessionState.Login;
    }

    // =========================================================================
    // State: Login
    // =========================================================================

    private async Task HandleLoginAsync(TdsMessage message, CancellationToken ct)
    {
        if (message.Type != TdsPacketType.Login7)
        {
            throw new TdsProtocolException(
                $"Expected LOGIN7 (0x10), got 0x{message.Type:X2} in Login state.");
        }

        Login7Message login;
        try
        {
            login = Login7Message.Parse(message.PayloadSpan);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to parse LOGIN7: {Msg}", ex.Message);
            await _responseWriter!.WriteLoginErrorAsync(new ServerError
            {
                Message  = "Login failed: malformed login packet.",
                Number   = 18456,
                Severity = 14,
                State    = 1
            }, ct).ConfigureAwait(false);
            _state = SessionState.Closed;
            return;
        }

        _logger.LogDebug("LOGIN7 received. User={User} Database={Db} App={App} PwdLen={PwdLen}",
            login.UserName, login.Database, login.AppName, login.Password.Length);

        bool authenticated = await _credentialValidator
            .ValidateAsync(login.UserName, login.Password, ct)
            .ConfigureAwait(false);

        if (!authenticated)
        {
            _logger.LogWarning("Login failed for user '{User}' (decoded password length: {Len})",
                login.UserName, login.Password.Length);
            await _responseWriter!.WriteLoginErrorAsync(new ServerError
            {
                Message  = $"Login failed for user '{login.UserName}'.",
                Number   = 18456,
                Severity = 14,
                State    = 1
            }, ct).ConfigureAwait(false);
            _state = SessionState.Closed;
            return;
        }

        _negotiatedPacketSize = (int)Math.Clamp(
            login.RequestedPacketSize,
            (uint)TdsProtocol.MinPacketSize,
            (uint)TdsProtocol.MaxPacketSize);
        _reader!.SetPacketSize(_negotiatedPacketSize);
        _writer!.SetPacketSize(_negotiatedPacketSize);

        _session = new SessionContext
        {
            ApplicationName = login.AppName,
            ClientHostName  = login.HostName,
            LoginName       = login.UserName,
            DatabaseName    = string.IsNullOrEmpty(login.Database) ? "master" : login.Database,
        };

        await _responseWriter!.WriteLoginResponseAsync(
            _session.DatabaseName,
            TdsProtocol.TdsVersion74,
            _negotiatedPacketSize,
            ct).ConfigureAwait(false);

        _state = SessionState.Active;
        _logger.LogInformation("Session authenticated: user={User} db={Db} packetSize={Size}",
            _session.LoginName, _session.DatabaseName, _negotiatedPacketSize);
    }

    // =========================================================================
    // State: Active
    // =========================================================================

    private async Task HandleActiveMessageAsync(TdsMessage message, CancellationToken ct)
    {
        switch (message.Type)
        {
            case TdsPacketType.SqlBatch:
                await HandleSqlBatchAsync(message, ct).ConfigureAwait(false);
                break;
            case TdsPacketType.Rpc:
                await HandleRpcAsync(message, ct).ConfigureAwait(false);
                break;
            case TdsPacketType.AttentionSignal:
                _logger.LogDebug("Attention signal received for session {Id}", _session?.SessionId);
                await SendAttentionAckAsync(ct).ConfigureAwait(false);
                break;
            case TdsPacketType.TransactionManagerRequest:
                await HandleTransactionManagerRequestAsync(message, ct).ConfigureAwait(false);
                break;
            default:
                _logger.LogWarning("Unhandled packet type 0x{Type:X2} in Active state; ignoring.",
                    message.Type);
                break;
        }
    }

    private async Task HandleSqlBatchAsync(TdsMessage message, CancellationToken ct)
    {
        string sqlText = SqlBatchMessage.Parse(message.PayloadSpan);
        _logger.LogDebug("SQLBatch: {Sql}", sqlText.Length > 200 ? sqlText[..200] + "…" : sqlText);

        var request = new SqlBatchRequest
        {
            Session = _session!,
            SqlText = sqlText
        };

        _responseWriter!.BeginTabularResult();
        try
        {
            await _queryHandler.HandleAsync(request, _responseWriter, ct).ConfigureAwait(false);
            await _responseWriter.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error handling SQLBatch");
            try
            {
                _responseWriter!.BeginTabularResult();
                await _responseWriter.WriteErrorAsync(new ServerError
                {
                    Message  = ex.Message,
                    Number   = 50000,
                    Severity = 16,
                    State    = 1
                }, ct).ConfigureAwait(false);
                await _responseWriter.FlushAsync(ct).ConfigureAwait(false);
            }
            catch { /* stream broken */ }
        }
    }

    private async Task HandleRpcAsync(TdsMessage message, CancellationToken ct)
    {
        var rpc = RpcRequestMessage.Parse(message.PayloadSpan);
        _logger.LogDebug("RPC: {Proc}", rpc.ProcedureName);

        var request = new RpcRequest
        {
            Session       = _session!,
            ProcedureName = rpc.ProcedureName,
            Parameters    = rpc.Parameters.Select(p => p.ToQueryParameter()).ToList()
        };

        _responseWriter!.BeginTabularResult();
        try
        {
            await _queryHandler.HandleAsync(request, _responseWriter, ct).ConfigureAwait(false);
            await _responseWriter.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error handling RPC {Proc}", rpc.ProcedureName);
            try
            {
                _responseWriter!.BeginTabularResult();
                await _responseWriter.WriteErrorAsync(new ServerError
                {
                    Message  = ex.Message,
                    Number   = 50000,
                    Severity = 16,
                    State    = 1
                }, ct).ConfigureAwait(false);
                await _responseWriter.FlushAsync(ct).ConfigureAwait(false);
            }
            catch { /* stream broken */ }
        }
    }

    private async Task HandleTransactionManagerRequestAsync(TdsMessage message, CancellationToken ct)
    {
        int offset = SkipAllHeaders(message.PayloadSpan);
        if (offset + 2 > message.Payload.Length) return;

        ushort requestType = (ushort)(message.Payload[offset] | (message.Payload[offset + 1] << 8));

        const ushort TmBeginXact    = 5;
        const ushort TmCommitXact   = 7;
        const ushort TmRollbackXact = 8;
        const ushort TmSavepoint    = 9;

        TransactionAction action = requestType switch
        {
            TmBeginXact    => TransactionAction.Begin,
            TmCommitXact   => TransactionAction.Commit,
            TmRollbackXact => TransactionAction.Rollback,
            TmSavepoint    => TransactionAction.Savepoint,
            _              => TransactionAction.Begin
        };

        var request = new TransactionRequest
        {
            Session = _session!,
            Action  = action
        };

        _responseWriter!.BeginTabularResult();
        try
        {
            await _queryHandler.HandleAsync(request, _responseWriter, ct).ConfigureAwait(false);
            await _responseWriter.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error handling transaction manager request");
        }
    }

    private async Task SendAttentionAckAsync(CancellationToken ct)
    {
        _responseWriter!.BeginTabularResult();
        await _responseWriter.WriteDoneAsync(DoneStatus.Final | DoneStatus.Attention, 0, ct)
            .ConfigureAwait(false);
        await _responseWriter.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task TrySendFatalErrorAsync(string message, CancellationToken ct)
    {
        try
        {
            if (_responseWriter == null || _writer == null) return;
            _responseWriter.BeginTabularResult();
            await _responseWriter.WriteErrorAsync(new ServerError
            {
                Message  = message,
                Number   = 0,
                Severity = 20,
                State    = 1
            }, ct).ConfigureAwait(false);
            await _responseWriter.FlushAsync(ct).ConfigureAwait(false);
        }
        catch { /* cannot recover if the stream is broken */ }
    }

    private static int SkipAllHeaders(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return 0;
        uint totalLength = (uint)(payload[0] | (payload[1] << 8) | (payload[2] << 16) | (payload[3] << 24));
        if (totalLength < 4 || totalLength >= (uint)payload.Length) return 0;
        if (payload.Length < 8) return 0;
        uint firstHeaderLen = (uint)(payload[4] | (payload[5] << 8) | (payload[6] << 16) | (payload[7] << 24));
        if (firstHeaderLen < 6 || firstHeaderLen > totalLength) return 0;
        return (int)totalLength;
    }

    public async ValueTask DisposeAsync()
    {
        _state = SessionState.Closed;
        if (_writer != null) await _writer.DisposeAsync().ConfigureAwait(false);
        _reader?.Dispose();
        _tcpClient?.Dispose();
    }
}
