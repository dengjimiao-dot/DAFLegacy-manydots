using System.Net;
using System.Net.Sockets;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// A passive framed-packet collector for the still-unmapped game channel.
/// It never fabricates gameplay replies and therefore cannot corrupt client state.
/// </summary>
public sealed class ProbeService(
    ServerOptions options,
    RuntimeState runtime,
    ILogger<ProbeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.GameProbe.Enabled)
        {
            return;
        }

        var listener = new TcpListener(IPAddress.Parse(options.GameProbe.Host), options.GameProbe.Port);
        listener.Start();
        logger.LogInformation("Game packet probe ready at {Host}:{Port}", options.GameProbe.Host, options.GameProbe.Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = CaptureAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task CaptureAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var session = runtime.Add("game-probe", client.Client.RemoteEndPoint?.ToString() ?? "unknown");
            try
            {
                using var stream = client.GetStream();
                while (await PacketFrame.ReadAsync(stream, options.MaximumPacketLength, cancellationToken) is { } packet)
                {
                    runtime.CountPacket(session);
                    session.LastProtocolId = packet.ProtocolId;
                    logger.LogInformation(
                        "PROBE {SessionId} protocol={Protocol} length={Length} crc={Crc:X8} valid={Valid} body={Body}",
                        session.Id, packet.ProtocolId, packet.TotalLength, packet.DeclaredCrc32,
                        packet.HasValidCrc32,
                        Convert.ToHexString(packet.Body.AsSpan(0, Math.Min(packet.Body.Length, options.HexDumpLimit))));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Game probe session {SessionId} ended.", session.Id);
            }
            finally
            {
                runtime.Remove(session.Id);
            }
        }
    }
}

