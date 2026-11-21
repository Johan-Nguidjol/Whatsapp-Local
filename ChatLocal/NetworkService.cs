using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ChatLocal
{
    public class Packet
    {
        public string T { get; set; }        // hello, bye, msg, status, sview, sdel, vonce, call_req, call_accept, call_reject, call_end
        public string Id { get; set; }
        public string From { get; set; }
        public string FromName { get; set; }
        public int Port { get; set; }
        public string Text { get; set; }
        public bool ViewOnce { get; set; }
        public int EphemeralSeconds { get; set; }
        public string Color { get; set; }
        public string Kind { get; set; }     // "text", "image", "voice", "file", "sticker", "call"
        public string Data { get; set; }     // image JPEG ou audio WAV en base64
        public string AttachmentName { get; set; }
        public string VoiceDuration { get; set; }
        public string CallType { get; set; } // "audio" ou "video"
        public string CallId { get; set; }
        public string CommunityId { get; set; }
        public int MediaPort { get; set; }
        public long Ticks { get; set; }
    }

    public class NetworkService : IDisposable
    {
        public const int DiscoveryPort = 45454;

        private readonly string _myId;
        private readonly string _myName;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private TcpListener _listener;
        private UdpClient _udpRecv;

        public int TcpPort { get; private set; }

        public event Action<Packet, string> PeerSeen;        // paquet hello/bye + IP
        public event Action<Packet, string> PacketReceived;  // message / statut + IP

        public NetworkService(string myId, string myName)
        {
            _myId = myId;
            _myName = myName;
        }

        public void Start()
        {
            // 1) Écoute TCP sur un port libre choisi automatiquement
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();
            TcpPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoop);

            // 2) Écoute UDP (découverte). ReuseAddress => plusieurs fenêtres sur le même PC
            _udpRecv = new UdpClient(AddressFamily.InterNetwork);
            _udpRecv.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpRecv.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _ = Task.Run(UdpReceiveLoop);

            // 3) Annonce régulière "je suis là"
            _ = Task.Run(BroadcastLoop);
        }

        // ---------- Découverte (UDP) ----------

        private async Task BroadcastLoop()
        {
            using var sender = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Packet
                    {
                        T = "hello",
                        From = _myId,
                        FromName = _myName,
                        Port = TcpPort
                    }));

                    foreach (var addr in GetBroadcastAddresses())
                    {
                        try { await sender.SendAsync(data, data.Length, new IPEndPoint(addr, DiscoveryPort)); }
                        catch { }
                    }
                }
                catch { }

                try { await Task.Delay(3000, _cts.Token); }
                catch { break; }
            }
        }

        private async Task UdpReceiveLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var res = await _udpRecv.ReceiveAsync(_cts.Token);
                    var p = JsonSerializer.Deserialize<Packet>(Encoding.UTF8.GetString(res.Buffer));
                    if (p == null || p.From == _myId) continue;
                    if (p.T == "hello" || p.T == "bye")
                        PeerSeen?.Invoke(p, res.RemoteEndPoint.Address.ToString());
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch
                {
                    if (_cts.IsCancellationRequested) break;
                    await Task.Delay(200);
                }
            }
        }

        private static List<IPAddress> GetBroadcastAddresses()
        {
            var list = new List<IPAddress> { IPAddress.Broadcast };
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    var b = new byte[4];
                    for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                    var addr = new IPAddress(b);
                    if (!list.Contains(addr)) list.Add(addr);
                }
            }
            return list;
        }

        // ---------- Messages (TCP) ----------

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => HandleClient(client));
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch
                {
                    if (_cts.IsCancellationRequested) break;
                    await Task.Delay(100);
                }
            }
        }

        private async Task HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                {
                    string ip = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);

                    string line = await reader.ReadLineAsync().WaitAsync(UploadLimits.TransferTimeout);
                    if (!string.IsNullOrEmpty(line))
                    {
                        var p = JsonSerializer.Deserialize<Packet>(line);
                        if (p != null && p.From != _myId)
                            PacketReceived?.Invoke(p, ip);
                    }

                    // Accusé de réception : l'expéditeur sait que c'est bien arrivé
                    var ack = Encoding.UTF8.GetBytes("OK\n");
                    await stream.WriteAsync(ack, 0, ack.Length);
                    await stream.FlushAsync();
                }
            }
            catch { }
        }

        public async Task<bool> SendAsync(string ip, int port, Packet p)
        {
            try
            {
                using var client = new TcpClient();
                using (var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                    await client.ConnectAsync(ip, port, connectCts.Token);
                using var cts = new CancellationTokenSource(UploadLimits.TransferTimeout);

                var stream = client.GetStream();
                var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(p) + "\n");
                await stream.WriteAsync(data, 0, data.Length, cts.Token);
                await stream.FlushAsync(cts.Token);

                using var reader = new StreamReader(stream, Encoding.UTF8, false, 256, true);
                string ack = await reader.ReadLineAsync().WaitAsync(UploadLimits.TransferTimeout);
                return ack == "OK";
            }
            catch
            {
                return false;
            }
        }

        // ---------- Arrêt ----------

        private void SendBye()
        {
            using var u = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Packet
            {
                T = "bye",
                From = _myId,
                FromName = _myName,
                Port = TcpPort
            }));
            foreach (var a in GetBroadcastAddresses())
            {
                try { u.Send(data, data.Length, new IPEndPoint(a, DiscoveryPort)); }
                catch { }
            }
        }

        public void Dispose()
        {
            try { SendBye(); } catch { }
            _cts.Cancel();
            try { _listener?.Stop(); } catch { }
            try { _udpRecv?.Close(); } catch { }
        }
    }
}
