using System.Net; using System.Net.Sockets;
// Does a SocketAddress filled in by ReceiveFrom compare Equal to IPEndPoint.Serialize() of the sender? (NT.2 validation design)
var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
var cached = ((IPEndPoint)client.LocalEndPoint).Serialize();
var recv = new SocketAddress(AddressFamily.InterNetwork);
var buf = new byte[64];
// dirty the receive address first, as a reused buffer would be after a packet from another sender
var other = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); other.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
other.SendTo(new byte[] { 9 }, server.LocalEndPoint); server.ReceiveFrom(buf, SocketFlags.None, recv);
Console.WriteLine($"after other sender: Equals(cached)={recv.Equals(cached)}  (expect False)");
client.SendTo(new byte[] { 1, 2, 3 }, server.LocalEndPoint);
int n = server.ReceiveFrom(buf, SocketFlags.None, recv);
Console.WriteLine($"received {n} bytes; recv.Size={recv.Size} cached.Size={cached.Size}  Equals={recv.Equals(cached)}  hashEqual={recv.GetHashCode() == cached.GetHashCode()}");
Console.WriteLine("recv   bytes: " + Convert.ToHexString(recv.Buffer.Span[..recv.Size]));
Console.WriteLine("cached bytes: " + Convert.ToHexString(cached.Buffer.Span[..cached.Size]));
// IPv6 dual-mode socket case (if a host ever binds IPv6Any dual mode, v4 clients arrive as v4-mapped v6)
var s6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp); s6.DualMode = true; s6.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
var r6 = new SocketAddress(AddressFamily.InterNetworkV6);
client.SendTo(new byte[] { 1 }, new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)s6.LocalEndPoint).Port)); s6.ReceiveFrom(buf, SocketFlags.None, r6);
Console.WriteLine($"dual-mode v6 listener: recv.Size={r6.Size} Equals(cached v4)={r6.Equals(cached)}");
