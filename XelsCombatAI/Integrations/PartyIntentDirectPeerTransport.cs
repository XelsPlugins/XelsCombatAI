using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SIPSorcery.Net;

namespace XelsCombatAI.Integrations;

internal sealed record PartyIntentPeer(
    string Sender,
    string ActorKey,
    IReadOnlyList<string> Features,
    bool SupportsDirect);

internal sealed record PartyIntentPeerSignal(
    string Type,
    string Sender,
    string Target,
    JsonElement Payload);

internal sealed record PartyIntentSocialDestackSnapshot(
    string ActorKey,
    Vector2 Bias,
    float Strength,
    DateTime ExpiresUtc);

internal sealed class PartyIntentDirectPeerTransport(string localSender, Func<object, bool> sendSignal) : IDisposable
{
    private const int ProtocolVersion = 1;
    private const string DataChannelLabel = "xcai-intent";
    private const string MovementIntentKind = "movement-intent";
    private const string SocialDestackFeature = "social-destack";
    private static readonly TimeSpan IntentTtl = TimeSpan.FromMilliseconds(750);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Dictionary<string, PeerConnection> peers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PartyIntentSocialDestackSnapshot> socialDestackIntents = new(StringComparer.Ordinal);
    private long directSequence;
    private DateTime nextSocialDestackSendUtc = DateTime.MinValue;
    private string status = "idle";
    private PartyIntentNetworkTestResult lastNetworkTestReceived = PartyIntentNetworkTestResult.None;

    public string Status => this.status;

    public int ConnectedPeerCount => this.peers.Values.Count(peer => peer.Channel?.IsOpened == true);

    public PartyIntentNetworkTestResult LastNetworkTestReceived => this.lastNetworkTestReceived;

    public void UpdatePeers(IReadOnlyList<PartyIntentPeer> activePeers)
    {
        var directPeers = activePeers
            .Where(peer => peer.SupportsDirect)
            .Where(peer => peer.Features.Any(feature => string.Equals(feature, SocialDestackFeature, StringComparison.Ordinal)))
            .Where(peer => !string.Equals(peer.Sender, localSender, StringComparison.Ordinal))
            .ToDictionary(peer => peer.Sender, StringComparer.Ordinal);

        foreach (var stale in this.peers.Keys.Where(sender => !directPeers.ContainsKey(sender)).ToArray())
        {
            this.peers[stale].Dispose();
            this.peers.Remove(stale);
        }

        foreach (var peer in directPeers.Values)
        {
            if (this.peers.ContainsKey(peer.Sender))
            {
                continue;
            }

            var connection = this.CreateConnection(peer.Sender);
            this.peers.Add(peer.Sender, connection);
            if (string.CompareOrdinal(localSender, peer.Sender) < 0)
            {
                _ = this.CreateOfferAsync(connection);
            }
        }

        this.status = this.ConnectedPeerCount > 0 ? $"direct peers={this.ConnectedPeerCount}" : $"peers={this.peers.Count}";
    }

    public void HandleSignal(PartyIntentPeerSignal signal)
    {
        if (!string.Equals(signal.Target, localSender, StringComparison.Ordinal))
        {
            return;
        }

        var connection = this.GetOrCreatePeer(signal.Sender);
        switch (signal.Type)
        {
            case "peer.offer":
                _ = this.HandleOfferAsync(connection, signal.Payload);
                break;
            case "peer.answer":
                this.HandleAnswer(connection, signal.Payload);
                break;
            case "peer.ice":
                this.HandleIce(connection, signal.Payload);
                break;
        }
    }

    public void PublishSocialDestack(string actorKey, Vector2 bias, float strength, DateTime nowUtc, string source = "social-spacing")
    {
        if (this.ConnectedPeerCount == 0 ||
            string.IsNullOrWhiteSpace(actorKey) ||
            nowUtc < this.nextSocialDestackSendUtc)
        {
            return;
        }

        if (bias.LengthSquared() <= 0.0001f)
        {
            return;
        }

        this.nextSocialDestackSendUtc = nowUtc.AddMilliseconds(250);
        var normalized = Vector2.Normalize(bias);
        var sentMs = new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds();
        var message = new MovementIntentWire(
            MovementIntentKind,
            ProtocolVersion,
            localSender,
            ++this.directSequence,
            sentMs,
            (int)IntentTtl.TotalMilliseconds,
            actorKey,
            "destack",
            normalized.X,
            normalized.Y,
            Math.Clamp(strength, 0f, 1f),
            sentMs + (long)IntentTtl.TotalMilliseconds,
            source);
        var json = JsonSerializer.Serialize(message, JsonOptions);

        foreach (var peer in this.peers.Values)
        {
            if (peer.Channel?.IsOpened == true)
            {
                try
                {
                    peer.Channel.send(json);
                }
                catch (InvalidOperationException ex)
                {
                    this.status = $"send failed: {ex.Message}";
                }
            }
        }
    }

    public IReadOnlyList<PartyIntentSocialDestackSnapshot> GetSocialDestackIntents(DateTime nowUtc)
    {
        foreach (var pair in this.socialDestackIntents.ToArray())
        {
            if (pair.Value.ExpiresUtc <= nowUtc)
            {
                this.socialDestackIntents.TryRemove(pair.Key, out _);
            }
        }

        return this.socialDestackIntents.Values
            .Where(intent => intent.ExpiresUtc > nowUtc)
            .ToArray();
    }

    public void Dispose()
    {
        foreach (var peer in this.peers.Values)
        {
            peer.Dispose();
        }

        this.peers.Clear();
        this.socialDestackIntents.Clear();
        this.status = "disposed";
    }

    private PeerConnection GetOrCreatePeer(string sender)
    {
        if (this.peers.TryGetValue(sender, out var existing))
        {
            return existing;
        }

        var connection = this.CreateConnection(sender);
        this.peers.Add(sender, connection);
        return connection;
    }

    private PeerConnection CreateConnection(string peerSender)
    {
        var peerConnection = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers =
            [
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
            ],
            X_GatherTimeoutMs = 1000
        });
        var connection = new PeerConnection(peerSender, peerConnection);

        peerConnection.onicecandidate += candidate =>
        {
            if (candidate == null)
            {
                return;
            }

            var payload = JsonSerializer.SerializeToElement(
                new IcePayload(candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex, candidate.usernameFragment),
                JsonOptions);
            this.SendPeerSignal("peer.ice", peerSender, payload);
        };
        peerConnection.onconnectionstatechange += state =>
        {
            connection.State = state.ToString();
            this.status = $"{peerSender}: {connection.State}";
        };
        peerConnection.ondatachannel += channel => this.AttachDataChannel(connection, channel);
        return connection;
    }

    private async Task CreateOfferAsync(PeerConnection connection)
    {
        try
        {
            if (connection.OfferSent)
            {
                return;
            }

            var channel = await connection.Peer.createDataChannel(DataChannelLabel, new RTCDataChannelInit
            {
                ordered = false,
                maxPacketLifeTime = 500
            }).ConfigureAwait(false);
            this.AttachDataChannel(connection, channel);
            var offer = connection.Peer.createOffer(null);
            await connection.Peer.setLocalDescription(offer).ConfigureAwait(false);
            connection.OfferSent = true;
            this.SendDescription("peer.offer", connection.Sender, offer);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            this.status = $"offer failed: {ex.Message}";
        }
    }

    private async Task HandleOfferAsync(PeerConnection connection, JsonElement payload)
    {
        try
        {
            var offer = DecodeDescription(payload, RTCSdpType.offer);
            connection.Peer.setRemoteDescription(offer);
            var answer = connection.Peer.createAnswer(null);
            await connection.Peer.setLocalDescription(answer).ConfigureAwait(false);
            this.SendDescription("peer.answer", connection.Sender, answer);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException)
        {
            this.status = $"offer rejected: {ex.Message}";
        }
    }

    private void HandleAnswer(PeerConnection connection, JsonElement payload)
    {
        try
        {
            connection.Peer.setRemoteDescription(DecodeDescription(payload, RTCSdpType.answer));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException)
        {
            this.status = $"answer rejected: {ex.Message}";
        }
    }

    private void HandleIce(PeerConnection connection, JsonElement payload)
    {
        try
        {
            var ice = payload.Deserialize<IcePayload>(JsonOptions);
            if (ice == null || string.IsNullOrWhiteSpace(ice.Candidate))
            {
                return;
            }

            connection.Peer.addIceCandidate(new RTCIceCandidateInit
            {
                candidate = ice.Candidate,
                sdpMid = ice.SdpMid,
                sdpMLineIndex = ice.SdpMLineIndex,
                usernameFragment = ice.UsernameFragment
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException)
        {
            this.status = $"ice rejected: {ex.Message}";
        }
    }

    private void AttachDataChannel(PeerConnection connection, RTCDataChannel channel)
    {
        if (!string.Equals(channel.label, DataChannelLabel, StringComparison.Ordinal))
        {
            return;
        }

        connection.Channel = channel;
        channel.onopen += () => this.status = $"direct open: {connection.Sender}";
        channel.onclose += () => this.status = $"direct closed: {connection.Sender}";
        channel.onerror += error => this.status = $"direct error: {error}";
        channel.onmessage += (_, protocol, data) =>
        {
            if (protocol != DataChannelPayloadProtocols.WebRTC_String)
            {
                return;
            }

            this.HandleDirectMessage(Encoding.UTF8.GetString(data));
        };
    }

    private void HandleDirectMessage(string json)
    {
        try
        {
            var message = JsonSerializer.Deserialize<MovementIntentWire>(json, JsonOptions);
            if (message == null ||
                !string.Equals(message.Kind, MovementIntentKind, StringComparison.Ordinal) ||
                !string.Equals(message.Intent, "destack", StringComparison.Ordinal) ||
                string.Equals(message.Sender, localSender, StringComparison.Ordinal))
            {
                return;
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (message.SentMs + message.TtlMs < nowMs || message.ExpiresMs < nowMs)
            {
                return;
            }

#if !XCAI_NETWORK_TEST_CONTROLS
            if (string.Equals(message.Reason, "network-test", StringComparison.Ordinal))
            {
                return;
            }
#endif

            var bias = new Vector2(message.BiasX, message.BiasZ);
            if (bias.LengthSquared() <= 0.0001f)
            {
                return;
            }

            this.socialDestackIntents[message.ActorKey] = new PartyIntentSocialDestackSnapshot(
                message.ActorKey,
                Vector2.Normalize(bias),
                Math.Clamp(message.Strength, 0f, 1f),
                DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(message.SentMs + message.TtlMs, message.ExpiresMs)).UtcDateTime);
#if XCAI_NETWORK_TEST_CONTROLS
            if (string.Equals(message.Reason, "network-test", StringComparison.Ordinal))
            {
                this.lastNetworkTestReceived = new(
                    true,
                    true,
                    "received test destack",
                    DateTime.UtcNow);
            }
#endif
        }
        catch (JsonException)
        {
        }
    }

    private void SendDescription(string type, string target, RTCSessionDescriptionInit description)
    {
        var payload = JsonSerializer.SerializeToElement(
            new DescriptionPayload(description.type.ToString(), description.sdp),
            JsonOptions);
        this.SendPeerSignal(type, target, payload);
    }

    private void SendPeerSignal(string type, string target, JsonElement payload)
    {
        if (!sendSignal(new PeerSignalWire(type, ProtocolVersion, localSender, target, payload)))
        {
            this.status = "signal queue full";
        }
    }

    private static RTCSessionDescriptionInit DecodeDescription(JsonElement payload, RTCSdpType expectedType)
    {
        var description = payload.Deserialize<DescriptionPayload>(JsonOptions)
            ?? throw new JsonException("missing peer description");
        var type = Enum.TryParse<RTCSdpType>(description.Type, ignoreCase: true, out var parsed)
            ? parsed
            : expectedType;
        if (type != expectedType || string.IsNullOrWhiteSpace(description.Sdp))
        {
            throw new JsonException("invalid peer description");
        }

        return new RTCSessionDescriptionInit { type = type, sdp = description.Sdp };
    }

    private sealed class PeerConnection(string sender, RTCPeerConnection peer) : IDisposable
    {
        public string Sender { get; } = sender;
        public RTCPeerConnection Peer { get; } = peer;
        public RTCDataChannel? Channel { get; set; }
        public bool OfferSent { get; set; }
        public string State { get; set; } = "new";

        public void Dispose()
        {
            this.Channel?.close();
            this.Peer.close();
            this.Peer.Dispose();
        }
    }

    private sealed record DescriptionPayload(string Type, string Sdp);

    private sealed record IcePayload(string Candidate, string? SdpMid, ushort SdpMLineIndex, string? UsernameFragment);

    private sealed record PeerSignalWire(string Type, int ProtocolVersion, string Sender, string Target, JsonElement Payload);

    private sealed record MovementIntentWire(
        string Kind,
        int ProtocolVersion,
        string Sender,
        long Seq,
        long SentMs,
        int TtlMs,
        string ActorKey,
        string Intent,
        float BiasX,
        float BiasZ,
        float Strength,
        long ExpiresMs,
        string Reason);
}
