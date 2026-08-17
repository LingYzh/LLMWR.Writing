namespace LLMW.Writing.UI.WebView;

internal sealed class BridgeMessageProcessor
{
    private readonly BridgeSessionState _session = new();
    private readonly IBridgeLog _log;
    private readonly object _gate = new();

    public BridgeMessageProcessor(IBridgeLog? log = null)
    {
        _log = log ?? NullBridgeLog.Instance;
    }

    public string? DocumentSessionId
    {
        get
        {
            lock (_gate)
            {
                return _session.DocumentSessionId;
            }
        }
    }

    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _session.IsReady;
            }
        }
    }

    public void InvalidateSession()
    {
        lock (_gate)
        {
            _session.Invalidate();
        }
    }

    public string BeginDocumentSession()
    {
        lock (_gate)
        {
            var sessionId = _session.BeginHello();
            return BridgeOutboundJson.HostHello(sessionId, NewId());
        }
    }

    public BridgeProcessResult ProcessIncoming(IncomingWebMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.AdditionalObjectCount > 0)
        {
            _log.Write(BridgeErrorCodes.AdditionalObjectsDenied, null, null, _session.DocumentSessionId, message.Source);
            return new BridgeProcessResult
            {
                Error = new BridgeError(BridgeErrorCodes.AdditionalObjectsDenied, "Additional WebMessage objects are not accepted.")
            };
        }

        if (!AppOriginPolicy.IsTrustedMessageSource(message.Source, message.CurrentDocument))
        {
            _log.Write(BridgeErrorCodes.WrongOrigin, null, null, _session.DocumentSessionId, message.Source);
            return new BridgeProcessResult
            {
                Error = new BridgeError(BridgeErrorCodes.WrongOrigin, "Message source is not the application origin.")
            };
        }

        var parsed = BridgeEnvelopeParser.Parse(message.Json);
        if (!parsed.Success)
        {
            var parseError = parsed.Error!;
            _log.Write(parseError.Code, null, null, _session.DocumentSessionId, message.Source);
            return ErrorResult(parseError, replyTo: null);
        }

        var envelope = parsed.Message!;
        lock (_gate)
        {
            if (!_session.Matches(envelope.DocumentSessionId))
            {
                _log.Write(BridgeErrorCodes.StaleSession, envelope.SemanticType, envelope.MessageId, envelope.DocumentSessionId, message.Source);
                return ErrorResult(new BridgeError(BridgeErrorCodes.StaleSession, "Document session is not current."), envelope.MessageId);
            }

            var replay = _session.RecordMessageId(envelope.MessageId);
            if (replay == ReplayRecordResult.Duplicate)
            {
                _log.Write(BridgeErrorCodes.Replay, envelope.SemanticType, envelope.MessageId, envelope.DocumentSessionId, message.Source);
                return ErrorResult(new BridgeError(BridgeErrorCodes.Replay, "Message id was already processed."), envelope.MessageId);
            }

            if (replay == ReplayRecordResult.Overflow)
            {
                _session.Invalidate();
                _log.Write(BridgeErrorCodes.Replay, envelope.SemanticType, envelope.MessageId, envelope.DocumentSessionId, message.Source);
                return ErrorResult(new BridgeError(BridgeErrorCodes.Replay, "Message replay cache is exhausted."), envelope.MessageId);
            }

            switch (envelope.SemanticType)
            {
                case BridgeSemanticTypes.RendererReady:
                    if (_session.Phase != BridgeSessionPhase.HelloSent)
                    {
                        return ErrorResult(new BridgeError(BridgeErrorCodes.InvalidSchema, "Renderer ready is not valid in this session state."), envelope.MessageId);
                    }

                    _session.MarkReady();
                    _log.Write("BRIDGE_READY", envelope.SemanticType, envelope.MessageId, envelope.DocumentSessionId, message.Source);
                    return new BridgeProcessResult
                    {
                        Dispatched = true,
                        OutboundJson =
                        [
                            BridgeOutboundJson.BridgeAck(envelope.DocumentSessionId, NewId(), envelope.MessageId, accepted: true),
                            BridgeOutboundJson.HostStatusReady(envelope.DocumentSessionId, NewId())
                        ]
                    };
                case BridgeSemanticTypes.BridgePing:
                    if (!_session.IsReady)
                    {
                        return ErrorResult(new BridgeError(BridgeErrorCodes.NotReady, "Bridge handshake is not complete."), envelope.MessageId);
                    }

                    return new BridgeProcessResult
                    {
                        Dispatched = true,
                        OutboundJson =
                        [
                            BridgeOutboundJson.BridgePong(envelope.DocumentSessionId, NewId(), envelope.MessageId, envelope.Nonce)
                        ]
                    };
                case BridgeSemanticTypes.ExternalLinkRequest:
                    if (!_session.IsReady)
                    {
                        return ErrorResult(new BridgeError(BridgeErrorCodes.NotReady, "Bridge handshake is not complete."), envelope.MessageId);
                    }

                    if (!ExternalUriPolicy.TryValidate(envelope.ExternalUri, out var validated, out _))
                    {
                        return ErrorResult(new BridgeError(BridgeErrorCodes.ExternalUrlDenied, "External URL is not allowed."), envelope.MessageId);
                    }

                    return new BridgeProcessResult
                    {
                        Dispatched = true,
                        ExternalUri = validated,
                        RequestMessageId = envelope.MessageId,
                        RequestDocumentSessionId = envelope.DocumentSessionId,
                        OutboundJson = []
                    };
                default:
                    return ErrorResult(new BridgeError(BridgeErrorCodes.UnknownMessageType, "Unknown bridge message type."), envelope.MessageId);
            }
        }
    }

    public static string CompleteExternalLink(string documentSessionId, string replyTo, bool accepted)
    {
        ArgumentException.ThrowIfNullOrEmpty(replyTo);
        return BridgeOutboundJson.BridgeAck(documentSessionId, NewId(), replyTo, accepted);
    }

    private BridgeProcessResult ErrorResult(BridgeError error, string? replyTo)
    {
        var sessionId = _session.DocumentSessionId ?? "none";
        return new BridgeProcessResult
        {
            Error = error,
            OutboundJson = [BridgeOutboundJson.BridgeError(sessionId, NewId(), replyTo, error)]
        };
    }

    private static string NewId() => Guid.NewGuid().ToString("D");
}
