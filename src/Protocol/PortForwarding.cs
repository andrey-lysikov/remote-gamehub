//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using RemoteGameHub.App;

namespace RemoteGameHub.Protocol;

// Asks the router to forward the streaming ports from the internet, over UPnP. Off unless the
// configuration says otherwise; the mappings carry a lease and are renewed, so nothing is left.
internal sealed class PortForwarding : IAsyncDisposable
{
    private static readonly IPEndPoint SsdpGroup = new(IPAddress.Parse("239.255.255.250"), 1900);

    // Both spellings of the service; routers publish one or the other, never both.
    private static readonly string[] ServiceTypes =
    {
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
    };

    // Long enough that a renewal missed once does not drop the stream, short enough that a
    // server which was killed rather than stopped stops being forwarded within the hour.
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(45);

    private static readonly TimeSpan RenewEvery = TimeSpan.FromMinutes(20);

    // How often the search for a router is tried again after it found none. Shorter than the
    // renewal: not being forwarded at all is more urgent than a mapping that is merely due.
    private static readonly TimeSpan SearchRetryEvery = TimeSpan.FromMinutes(2);

    private readonly AppConfig _config;
    private readonly CancellationTokenSource _stopping = new();

    private readonly List<(int Port, string Protocol)> _mapped = new();
    private string? _controlUrl;
    private string? _serviceType;
    private string? _localAddress;
    private Task? _renewing;
    private bool _announced;
    private bool _searchFailedOnce;

    internal PortForwarding(AppConfig config) => _config = config;

    private (int Port, string Protocol)[] Wanted => new[]
    {
        (_config.HttpPort, "TCP"),
        (_config.HttpsPort, "TCP"),
        (_config.RtspPort, "TCP"),
        (_config.VideoPort, "UDP"),
        (_config.ControlPort, "UDP"),
        (_config.AudioPort, "UDP"),
    };

    internal void Start()
    {
        if (!_config.Upnp) return;

        _renewing = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                if (_controlUrl is null)
                {
                    if (!await FindRouterAsync())
                    {
                        var message =
                            "No router answered the search for one that forwards ports, so the\n" +
                            "streaming ports are reachable on this network only. Either the router\n" +
                            "does not speak UPnP or it has it switched off — which many do by\n" +
                            "default, and for good reason. Forward the ports by hand in the router\n" +
                            "if you meant to play from outside: " +
                            string.Join(", ", Wanted.Select(w => $"{w.Port}/{w.Protocol}")) +
                            $"\nTried again every {SearchRetryEvery.TotalMinutes:0} minute(s).";

                        // Said once at a level worth noticing; a router that stays off is not news
                        // the second time, and this may retry for as long as the server runs.
                        if (_searchFailedOnce) Log.Info(message); else Log.Warn(message);
                        _searchFailedOnce = true;

                        try
                        {
                            await Task.Delay(SearchRetryEvery, _stopping.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }

                        continue;
                    }

                    // Before anything is asked for: a mapping to an address that is itself behind
                    // the provider's translation forwards nothing, and the log should say so once.
                    await ReportExternalAddressAsync();
                }

                await MapAllAsync();

                try
                {
                    await Task.Delay(RenewEvery, _stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        catch (Exception error)
        {
            Log.Warn($"port forwarding stopped: {error.Message}. The streaming ports are " +
                     "reachable on this network only.");
        }
    }

    // The multicast search, then the router's description, which names the address to send the
    // requests to. From every address this machine has: the routing table picked a virtual one.
    private async Task<bool> FindRouterAsync()
    {
        var addresses = LocalAddresses();
        if (addresses.Count == 0) return false;

        Log.Info($"looking for a router that forwards ports, from {addresses.Count} address(es): " +
                 string.Join(", ", addresses));

        foreach (var address in addresses)
        {
            if (_stopping.IsCancellationRequested) return false;
            if (await SearchFromAsync(address)) return true;
        }

        return false;
    }

    // One address's search. Every device type is asked for, because routers answer to the name
    // they were written to and the two versions of the profile are not interchangeable.
    private async Task<bool> SearchFromAsync(IPAddress address)
    {
        string[] targets =
        {
            "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
            "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
            "upnp:rootdevice",
        };

        try
        {
            using var socket = new UdpClient(new IPEndPoint(address, 0));

            // Two hops, as the reference asks for: one is enough for a router on the same wire,
            // and two survives the switch some networks put between.
            socket.Client.SetSocketOption(SocketOptionLevel.IP,
                SocketOptionName.MulticastTimeToLive, 2);

            foreach (var target in targets)
            {
                var search = Encoding.ASCII.GetBytes(
                    "M-SEARCH * HTTP/1.1\r\n" +
                    $"HOST: {SsdpGroup}\r\n" +
                    "MAN: \"ssdp:discover\"\r\n" +
                    "MX: 2\r\n" +
                    $"ST: {target}\r\n" +
                    "\r\n");

                await socket.SendAsync(search, search.Length, SsdpGroup);
            }

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && !_stopping.IsCancellationRequested)
            {
                var left = deadline - DateTime.UtcNow;
                var receiving = socket.ReceiveAsync(_stopping.Token).AsTask();

                if (await Task.WhenAny(receiving, Task.Delay(left, _stopping.Token)) != receiving)
                    break;

                var answer = await receiving;
                var text = Encoding.ASCII.GetString(answer.Buffer);
                var location = Header(text, "LOCATION");
                if (location is null) continue;

                // The address the search went out of is the address to forward to: it is the one
                // on the router's own network, whatever else this machine has.
                _localAddress = address.ToString();

                if (await ReadDescriptionAsync(location)) return true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log.Info($"the search from {address} failed: {error.Message}");
        }

        return false;
    }

    // Every IPv4 address this machine holds on an interface that is up and is not the loopback.
    private static List<IPAddress> LocalAddresses()
    {
        var found = new List<IPAddress>();

        try
        {
            foreach (var card in System.Net.NetworkInformation.NetworkInterface
                         .GetAllNetworkInterfaces())
            {
                if (card.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;

                if (card.NetworkInterfaceType ==
                    System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var entry in card.GetIPProperties().UnicastAddresses)
                {
                    if (entry.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(entry.Address)) continue;

                    found.Add(entry.Address);
                }
            }
        }
        catch (Exception error)
        {
            Log.Info($"this machine's addresses could not be listed: {error.Message}");
        }

        return found;
    }

    private async Task<bool> ReadDescriptionAsync(string location)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var description = await http.GetStringAsync(location, _stopping.Token);

            foreach (var service in ServiceTypes)
            {
                // The description is XML, but the shape routers emit varies enough that a path
                // expression is more brittle than a search: find the service, then the control URL.
                var at = description.IndexOf(service, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;

                var control = Regex.Match(description[at..], @"<controlURL>\s*([^<]+?)\s*</controlURL>",
                                          RegexOptions.IgnoreCase);
                if (!control.Success) continue;

                _controlUrl = new Uri(new Uri(location), control.Groups[1].Value).ToString();
                _serviceType = service;

                Log.Info($"a router that forwards ports answered at {location}");
                return true;
            }
        }
        catch (Exception error)
        {
            Log.Info($"the router's description at {location} could not be read: {error.Message}");
        }

        return false;
    }

    private async Task MapAllAsync()
    {
        var opened = new List<string>();

        foreach (var (port, protocol) in Wanted)
        {
            if (await MapAsync(port, protocol))
            {
                opened.Add($"{port}/{protocol}");

                lock (_mapped)
                {
                    if (!_mapped.Contains((port, protocol))) _mapped.Add((port, protocol));
                }
            }
        }

        if (opened.Count == 0) return;

        // Said as a warning the first time, because it is the one state of this server that
        // changes who can reach it; the renewals that follow repeat it at Info.
        var announcement =
            $"The router is forwarding {opened.Count} port(s) from the internet to this machine:\n" +
            string.Join(", ", opened) + "\n" +
            "Anything that finds them can pair with this server and use this screen and keyboard.\n" +
            "That is what [Network] Upnp = true asks for. Set it to false to stop.";

        if (_announced) Log.Info(announcement);
        else Log.Warn(announcement);

        _announced = true;
    }

    // The two refusals worth acting on rather than reporting. 725 is a router that keeps mappings
    // for ever or not at all; 718 is one of ours from a previous run still in the table.
    private const int ErrorOnlyPermanentLeases = 725;
    private const int ErrorConflictingMapping = 718;

    private async Task<bool> MapAsync(int port, string protocol)
    {
        var result = await AddAsync(port, protocol, (int)Lease.TotalSeconds);
        if (result.Ok) return true;

        if (result.Error == ErrorOnlyPermanentLeases)
        {
            // Asked again for a mapping with no end. It is then removed when this server stops,
            // and the reference does exactly this for the same routers.
            Log.Info($"the router keeps only permanent mappings; {port}/{protocol} is asked for " +
                     "without a lease and is removed when this server stops");

            result = await AddAsync(port, protocol, 0);
            if (result.Ok) return true;
        }

        if (result.Error == ErrorConflictingMapping)
        {
            // Almost always this server's own mapping from a run that did not stop cleanly.
            Log.Info($"{port}/{protocol} is already forwarded to something; " +
                     "the old mapping is being removed and asked for again");

            await DeleteAsync(port, protocol, CancellationToken.None);
            result = await AddAsync(port, protocol, (int)Lease.TotalSeconds);
            if (result.Ok) return true;
        }

        Log.Info($"the router refused to forward {port}/{protocol}" +
                 (result.Error > 0 ? $": error {result.Error} {result.Description}".TrimEnd()
                                   : $": {result.Description}"));
        return false;
    }

    private async Task<CallResult> AddAsync(int port, string protocol, int leaseSeconds)
    {
        var body =
            $"<u:AddPortMapping xmlns:u=\"{_serviceType}\">" +
            "<NewRemoteHost></NewRemoteHost>" +
            $"<NewExternalPort>{port}</NewExternalPort>" +
            $"<NewProtocol>{protocol}</NewProtocol>" +
            $"<NewInternalPort>{port}</NewInternalPort>" +
            $"<NewInternalClient>{_localAddress}</NewInternalClient>" +
            "<NewEnabled>1</NewEnabled>" +
            $"<NewPortMappingDescription>{AppParameters.Identity.DisplayName}</NewPortMappingDescription>" +
            $"<NewLeaseDuration>{leaseSeconds}</NewLeaseDuration>" +
            "</u:AddPortMapping>";

        return await CallAsync("AddPortMapping", body, _stopping.Token);
    }

    private async Task<CallResult> DeleteAsync(int port, string protocol, CancellationToken cancel)
    {
        var body =
            $"<u:DeletePortMapping xmlns:u=\"{_serviceType}\">" +
            "<NewRemoteHost></NewRemoteHost>" +
            $"<NewExternalPort>{port}</NewExternalPort>" +
            $"<NewProtocol>{protocol}</NewProtocol>" +
            "</u:DeletePortMapping>";

        return await CallAsync("DeletePortMapping", body, cancel);
    }

    // What one call came back as: whether it worked, and the router's own number and words when
    // it did not. The number is what decides whether to ask differently or to give up.
    private readonly record struct CallResult(bool Ok, int Error, string Description);

    private async Task<CallResult> CallAsync(string action, string body, CancellationToken cancel)
    {
        if (_controlUrl is null || _serviceType is null)
            return new CallResult(false, 0, "no router was found");

        var envelope =
            "<?xml version=\"1.0\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            $"<s:Body>{body}</s:Body></s:Envelope>";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var content = new StringContent(envelope, Encoding.UTF8, "text/xml");
            content.Headers.Add("SOAPAction", $"\"{_serviceType}#{action}\"");

            using var answer = await http.PostAsync(_controlUrl, content, cancel);
            var text = await answer.Content.ReadAsStringAsync(cancel);

            if (answer.IsSuccessStatusCode) return new CallResult(true, 0, text);

            // A refusal is an HTTP 500 carrying a UPnP fault. The number in it is the whole of the
            // diagnosis — "500" alone said nothing and was why this was hard to see.
            var code = Regex.Match(text, @"<errorCode>\s*(\d+)\s*</errorCode>",
                                   RegexOptions.IgnoreCase);
            var said = Regex.Match(text, @"<errorDescription>\s*([^<]*?)\s*</errorDescription>",
                                   RegexOptions.IgnoreCase);

            return new CallResult(false,
                code.Success ? int.Parse(code.Groups[1].Value) : 0,
                said.Success ? said.Groups[1].Value : $"HTTP {(int)answer.StatusCode}");
        }
        catch (Exception error)
        {
            return new CallResult(false, 0, error.Message);
        }
    }

    // The address the world would reach this machine at. Asked once: a mapping to an address that
    // is itself behind the provider's translation forwards nothing and reports no fault either.
    private async Task ReportExternalAddressAsync()
    {
        var body = $"<u:GetExternalIPAddress xmlns:u=\"{_serviceType}\"></u:GetExternalIPAddress>";
        var result = await CallAsync("GetExternalIPAddress", body, _stopping.Token);

        if (!result.Ok)
        {
            Log.Info($"the router would not say its external address: {result.Description}");
            return;
        }

        var found = Regex.Match(result.Description,
                                @"<NewExternalIPAddress>\s*([^<]*?)\s*</NewExternalIPAddress>",
                                RegexOptions.IgnoreCase);

        if (!found.Success || !IPAddress.TryParse(found.Groups[1].Value, out var external))
        {
            Log.Info("the router did not name an external address; it may not be connected");
            return;
        }

        if (IsPrivate(external))
        {
            Log.Warn(
                $"The router's own address on the internet side is {external}, which is a private\n" +
                "one: this connection is behind the provider's translation as well, and no port\n" +
                "forwarded here can be reached from outside. Nothing this server does can change\n" +
                "that — a public address from the provider, or a relay, is what would.");
            return;
        }

        Log.Info($"the router's address on the internet side is {external}");
    }

    // The ranges that are never reachable from outside: RFC 1918, the provider-grade range of
    // RFC 6598, and link-local.
    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;

        return bytes[0] switch
        {
            10 => true,
            127 => true,
            169 when bytes[1] == 254 => true,
            172 when bytes[1] >= 16 && bytes[1] <= 31 => true,
            192 when bytes[1] == 168 => true,
            100 when bytes[1] >= 64 && bytes[1] <= 127 => true,
            _ => false,
        };
    }

    private static string? Header(string message, string name)
    {
        foreach (var line in message.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;

            if (line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }

        return null;
    }

    // Removes what was opened. A lease would expire on its own eventually, but "eventually" is
    // three quarters of an hour during which a machine that is switched off is still forwarded.
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        (int, string)[] mapped;
        lock (_mapped) mapped = _mapped.ToArray();

        // The stopping token is already cancelled, so these must not use it; they rely on the
        // client timeout. A router that does not answer costs a second, and the lease cleans up.
        foreach (var (port, protocol) in mapped)
            await DeleteAsync(port, protocol, CancellationToken.None);

        if (mapped.Length > 0) Log.Event($"{mapped.Length} forwarded port(s) were closed");

        _stopping.Dispose();
    }
}
