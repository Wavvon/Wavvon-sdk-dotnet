using SIPSorcery.Net;

namespace Wavvon.Sdk;

/// <summary>
/// The voice transport (voice-transport-v3.md) and the only WebRTC code in the SDK: one data
/// channel, negotiated on stream 0, unordered, no retransmission, over SIPSorcery. The hub is
/// ICE-lite and answers with its host candidates, so no STUN or TURN server is configured.
/// </summary>
/// <remarks>
/// ponytail: SIPSorcery sends every message reliable and ordered whatever the channel asks, and its
/// SCTP has no FORWARD-TSN, so a lost uplink packet is retransmitted rather than dropped. Fine on a
/// clean path; on a lossy one it adds latency. Revisit if SIPSorcery gains partial reliability.
/// </remarks>
internal sealed class VoiceLink : IDisposable
{
    /// <summary>A packet that still fits one SCTP chunk in a 1280-byte path MTU; a split one is lost whole if any piece is.</summary>
    public const int MaxPacket = 1200;

    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);

    private readonly RTCPeerConnection _pc;
    private readonly RTCDataChannel _dc;
    private int _closed;

    private VoiceLink(RTCPeerConnection pc, RTCDataChannel dc, string sdp)
    {
        _pc = pc;
        _dc = dc;
        OfferSdp = sdp;
    }

    public string OfferSdp { get; }

    public static async Task<VoiceLink> OfferAsync()
    {
        var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = [] });
        try
        {
            var dc = await pc.createDataChannel("voice", new RTCDataChannelInit { negotiated = true, id = 0, ordered = false, maxRetransmits = 0 }).ConfigureAwait(false);
            // The hub takes no trickled candidates: the offer must carry them all.
            var offer = pc.createOffer(new RTCOfferOptions { X_WaitForIceGatheringToComplete = true });
            await pc.setLocalDescription(offer).ConfigureAwait(false);
            return new VoiceLink(pc, dc, offer.sdp);
        }
        catch (Exception e)
        {
            pc.close();
            throw new VoiceException(VoiceErrorKind.Transport, $"could not build the WebRTC offer: {e.Message}", e);
        }
    }

    /// <summary>Applies the hub's answer and waits for the channel to open. <paramref name="onClose"/> fires once, never after <see cref="Dispose"/>.</summary>
    public async Task AcceptAsync(string answerSdp, Action<byte[]> onMessage, Action onClose)
    {
        void Ended()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                onClose();
            }
        }
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dc.onopen += () => opened.TrySetResult();
        _dc.onmessage += (_, protocol, data) =>
        {
            if (protocol == DataChannelPayloadProtocols.WebRTC_Binary)
            {
                onMessage(data);
            }
        };
        _dc.onclose += Ended;
        _pc.onconnectionstatechange += state =>
        {
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
            {
                opened.TrySetException(new InvalidOperationException($"peer connection {state}"));
                Ended();
            }
        };
        try
        {
            var result = _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answerSdp });
            if (result != SetDescriptionResultEnum.OK)
            {
                throw new InvalidOperationException($"the answer was refused: {result}");
            }
            if (_dc.readyState == RTCDataChannelState.open)
            {
                opened.TrySetResult();
            }
            await opened.Task.WaitAsync(OpenTimeout).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Dispose();
            throw new VoiceException(VoiceErrorKind.Transport, $"voice data channel did not open: {e.Message}", e);
        }
    }

    public void Send(byte[] packet)
    {
        if (_dc.readyState == RTCDataChannelState.open)
        {
            _dc.send(packet);
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _closed, 1);
        _pc.close();
    }
}
