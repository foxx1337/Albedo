using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace SsdpScan
{
    class Program
    {
        static void Main(string[] args)
        {
            int timeout = 60;

            var sockets = CreateV4SocketsPerInterface();

            var multicastResults =
                SendMulticastsAsync(sockets.Select(s => s.unicast).ToList()).Result;

            var receiveTasks = new Dictionary<Task, Tuple<Socket, ArraySegment<byte>>>();
            foreach (var pair in sockets)
            {
                foreach (var socket in new Socket[] {pair.multicast, pair.unicast})
                {
                    var buffer = new ArraySegment<byte>(new byte[1024]);
                    var task = socket.ReceiveFromAsync(buffer, SocketFlags.None, socket.LocalEndPoint);
                    receiveTasks[task] = new Tuple<Socket, ArraySegment<byte>>(socket, buffer);
                }
            }

            var lapse = Task.Delay(1000);
            receiveTasks[lapse] = null;

            int second = 0;
            while (second < timeout)
            {
                var completed = Task.WhenAny(receiveTasks.Keys).Result;
                if (completed == lapse)
                {
                    Console.WriteLine($"{second}/{timeout}...");
                    second++;
                    receiveTasks.Remove(lapse);
                    lapse = Task.Delay(1000);
                    receiveTasks[lapse] = null;
                }
                else
                {
                    var receiveTask = (Task<SocketReceiveFromResult>) completed;
                    var received = receiveTask.Result;
                    var socketInfo = receiveTasks[receiveTask];
                    Socket socket = socketInfo.Item1;
                    ArraySegment<byte> buffer = socketInfo.Item2;

                    string payload = Encoding.UTF8.GetString(buffer.Array, 0, received.ReceivedBytes);
                    Console.WriteLine($"{received.RemoteEndPoint} -> {socket.LocalEndPoint}: {payload}");

                    receiveTasks.Remove(receiveTask);
                    var task = socket.ReceiveFromAsync(buffer, SocketFlags.None, socket.LocalEndPoint);
                    receiveTasks[task] = socketInfo;
                }
            }

            Console.WriteLine("Done, press any key to exit...");
            Console.ReadKey();
        }

        private static List<(Socket unicast, Socket multicast)> CreateV4SocketsPerInterface() =>
            NetworkInterface.GetAllNetworkInterfaces()
                .Where(iface => iface.SupportsMulticast)
                .Select(iface => iface.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(addr => addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    ?.Address
                )
                .Where(address => address != null)
                .Select(CreateSsdpSocketPair)
                .Where(pair => pair.multicast != null)
                .ToList();

        private const string MulticastIp = "239.255.255.250";
        private static readonly IPAddress MulticastAddress = IPAddress.Parse(MulticastIp);
        private const int MulticastPort = 1900;
        private static readonly IPEndPoint MulticastEndpoint = new IPEndPoint(MulticastAddress, MulticastPort);
        private const int UnicastPort = 19000;

        private static readonly string SsdpDiscoveryMessage = "M-SEARCH * HTTP/1.1\r\n" +
                                                              $"HOST: {MulticastIp}:{MulticastPort}\r\n" +
                                                              "ST: upnp:rootdevice\r\n" +
                                                              "MAN: \"ssdp:discover\"\r\n" +
                                                              "MX: 1\r\n\r\n";

        private static readonly ArraySegment<byte> SsdpDiscoveryMessageBytes =
            new ArraySegment<byte>(Encoding.UTF8.GetBytes(SsdpDiscoveryMessage));

        private static async Task<int[]> SendMulticastsAsync(List<Socket> sockets) =>
            await Task.WhenAll(sockets
                .Select(socket =>
                    socket.SendToAsync(
                        SsdpDiscoveryMessageBytes,
                        SocketFlags.None,
                        MulticastEndpoint))
                .ToArray());

        private static (Socket unicast, Socket multicast) CreateSsdpSocketPair(IPAddress address)
        {
            try
            {
                // The unicast socket binds to a non-Windows SSDP Discovery service port in order to receive unicasts.
                Socket unicastSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                unicastSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                unicastSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, false);
                unicastSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    new MulticastOption(MulticastAddress, address));
                unicastSocket.Bind(new IPEndPoint(address, UnicastPort));

                // The multicast socket most probably can't receive unicast message thanks to Windows SSDP Discovery.
                Socket multicastSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                multicastSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                multicastSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, false);
                multicastSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    new MulticastOption(MulticastAddress, address));
                multicastSocket.Bind(new IPEndPoint(address, MulticastPort));

                return (unicast: unicastSocket, multicast: multicastSocket);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed binding sockets on {address} - {e}.");
                Console.WriteLine("This is normal and the interface will simply be ignored for SSDP discovery.");
                return (null, null);
            }
        }
    }
}
