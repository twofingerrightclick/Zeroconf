﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace Zeroconf
{
    class NetworkInterface : INetworkInterface
    {
        //string _SiteLocalBroadcastAddress = "FF05:0:0:0:0:0:0:FB"; // site local ipv6

        string _SiteLocalBroadcastAddress =   "FF02::FB";
        public async Task NetworkRequestAsync(byte[] requestBytes,
                                              TimeSpan scanTime,
                                              int retries,
                                              int retryDelayMilliseconds,
                                              Action<IPAddress, byte[]> onResponse,
                                              CancellationToken cancellationToken,
                                              IEnumerable<System.Net.NetworkInformation.NetworkInterface> netInterfacesToSendRequestOn = null)
        {
            // populate list with all adapters if none specified
            if(netInterfacesToSendRequestOn == null || !netInterfacesToSendRequestOn.Any())
            {
                netInterfacesToSendRequestOn = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            }
                                    
            var tasks = netInterfacesToSendRequestOn
                              .Select(inter =>
                                      NetworkRequestAsync(requestBytes, scanTime, retries, retryDelayMilliseconds, onResponse, inter, cancellationToken))
                              .ToList();

            await Task.WhenAll(tasks)
                      .ConfigureAwait(false);

        }


        async Task NetworkRequestAsync(byte[] requestBytes,
                                              TimeSpan scanTime,
                                              int retries,
                                              int retryDelayMilliseconds,
                                              Action<IPAddress, byte[]> onResponse,
                                              System.Net.NetworkInformation.NetworkInterface adapter,
                                              CancellationToken cancellationToken)
        {
            // http://stackoverflow.com/questions/2192548/specifying-what-network-interface-an-udp-multicast-should-go-to-in-net

            // Xamarin doesn't support this
            //if (!adapter.GetIPProperties().MulticastAddresses.Any())
            //    return; // most of VPN adapters will be skipped

            if (!adapter.SupportsMulticast)
                return; // multicast is meaningless for this type of connection

            if (OperationalStatus.Up != adapter.OperationalStatus)
                return; // this adapter is off or not connected

            /*if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                return; // strip out loopback addresses*/

            var p = adapter.GetIPProperties().GetIPv6Properties();
            if (null == p)
                return; // IPv6 is not configured on this adapter

            var ipv6Address = adapter.GetIPProperties().UnicastAddresses
                                    .FirstOrDefault(ua => ua.Address.AddressFamily == AddressFamily.InterNetworkV6)?.Address;

            if (ipv6Address == null)
                return; // could not find an IPv4 address for this adapter

            var ifaceIndex = p.Index;

            Debug.WriteLine($"Scanning on iface {adapter.Name}, idx {ifaceIndex}, IP: {ipv6Address}");


            using (var client = new UdpClient(AddressFamily.InterNetworkV6))
            {
                for (var i = 0; i < retries; i++)
                {
                    try
                    {
                        var socket = client.Client;

                        if (socket.IsBound) continue;

                        var interfaceIPNetworkOrder= (int)IPAddress.HostToNetworkOrder(ifaceIndex);
                        var interfaceIP = IPAddress.NetworkToHostOrder((int)IPAddress.HostToNetworkOrder(ifaceIndex));

                        socket.SetSocketOption(SocketOptionLevel.IPv6,
                                                     SocketOptionName.MulticastInterface,
                                                     interfaceIP);



                        client.ExclusiveAddressUse = false;
                        socket.SetSocketOption(SocketOptionLevel.Socket,
                                                      SocketOptionName.ReuseAddress,
                                                      true);
                        socket.SetSocketOption(SocketOptionLevel.Socket,
                                                      SocketOptionName.ReceiveTimeout,
                                                      (int)scanTime.TotalMilliseconds);
                        client.ExclusiveAddressUse = false;


                        var localEp = new IPEndPoint(IPAddress.IPv6Any, 5353);
                        //var localEp = new IPEndPoint(IPAddress.Any, 5353);

                        Debug.WriteLine($"Attempting to bind to {localEp} on adapter {adapter.Name}");
                        socket.Bind(localEp);
                        Debug.WriteLine($"Bound to {localEp}");

                        //var multicastAddress = IPAddress.Parse("224.0.0.251");
                        var multicastAddress = IPAddress.Parse(_SiteLocalBroadcastAddress);
                        var multOpt = new IPv6MulticastOption(multicastAddress, ifaceIndex);
                        socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, multOpt);


                        Debug.WriteLine($"Bound to multicast address, {_SiteLocalBroadcastAddress}");


                        // Start a receive loop
                        var shouldCancel = false;
                        var recTask = Task.Run(async
                                               () =>
                                               {
                                                   try
                                                   {
                                                       while (!Volatile.Read(ref shouldCancel))
                                                       {
                                                           var res = await client.ReceiveAsync()
                                                                                 .ConfigureAwait(false);

                                                           onResponse(res.RemoteEndPoint.Address, res.Buffer);
                                                       }
                                                   }
                                                   catch when (Volatile.Read(ref shouldCancel))
                                                   {
                                                       // If we're canceling, eat any exceptions that come from here   
                                                   }
                                               }, cancellationToken);

                        //var broadcastEp = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
                        var broadcastEp = new IPEndPoint(IPAddress.Parse(_SiteLocalBroadcastAddress), 5353);

                        Debug.WriteLine($"About to send on iface {adapter.Name}");
                        await client.SendAsync(requestBytes, requestBytes.Length, broadcastEp)
                                    .ConfigureAwait(false);

                        Debug.WriteLine($"Sent mDNS query on iface {adapter.Name}");


                        // wait for responses
                        await Task.Delay(scanTime, cancellationToken)
                                  .ConfigureAwait(false);

                        Volatile.Write(ref shouldCancel, true);

                        ((IDisposable)client).Dispose();

                        Debug.WriteLine("Done Scanning");


                        await recTask.ConfigureAwait(false);

                        return;
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine($"Execption with network request, IP {ipv6Address}\n: {e}");
                        if (i + 1 >= retries) // last one, pass underlying out
                        {
                            // Ensure all inner info is captured                            
                            ExceptionDispatchInfo.Capture(e).Throw();
                            throw;
                        }
                    }

                    await Task.Delay(retryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public Task ListenForAnnouncementsAsync(Action<AdapterInformation, string, byte[]> callback, CancellationToken cancellationToken)
        {
            return Task.WhenAll(System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                                     // .Where(a => a.GetIPProperties().MulticastAddresses.Any()) // Xamarin doesn't support this
                                      .Where(a => a.SupportsMulticast)
                                      .Where(a => a.OperationalStatus == OperationalStatus.Up)
                                      .Where(a => a.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                                      .Where(a => a.GetIPProperties().GetIPv6Properties() != null)
                                      .Where(a => a.GetIPProperties().UnicastAddresses.Any(ua => ua.Address.AddressFamily == AddressFamily.InterNetworkV6))
                                      .Select(inter => ListenForAnnouncementsAsync(inter, callback, cancellationToken)));
        }

        Task ListenForAnnouncementsAsync(System.Net.NetworkInformation.NetworkInterface adapter, Action<AdapterInformation, string, byte[]> callback, CancellationToken cancellationToken)
        {
            return Task.Factory.StartNew(async () =>
            {
                var ipv6Address = adapter.GetIPProperties().UnicastAddresses
                                         .First(ua => ua.Address.AddressFamily == AddressFamily.InterNetworkV6)?.Address;

                if (ipv6Address == null)
                    return;

                var ifaceIndex = adapter.GetIPProperties().GetIPv6Properties()?.Index;
                if (ifaceIndex == null)
                    return;

                Debug.WriteLine($"Scanning on iface {adapter.Name}, idx {ifaceIndex}, IP: {ipv6Address}");

                using (var client = new UdpClient(AddressFamily.InterNetworkV6))
                {
                    var socket = client.Client;
                    socket.SetSocketOption(SocketOptionLevel.IPv6,
                                           SocketOptionName.MulticastInterface,
                                           IPAddress.HostToNetworkOrder(ifaceIndex.Value));

                    socket.SetSocketOption(SocketOptionLevel.Socket,
                                           SocketOptionName.ReuseAddress,
                                           true);
                    client.ExclusiveAddressUse = false;


                    var localEp = new IPEndPoint(IPAddress.IPv6Any, 5353);
                    socket.Bind(localEp);

                    //var multicastAddress = IPAddress.Parse("224.0.0.251");
                    var multicastAddress = IPAddress.Parse(_SiteLocalBroadcastAddress);
                    var multOpt = new IPv6MulticastOption(multicastAddress, ifaceIndex.Value);
                    socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, multOpt);


                    cancellationToken.Register((() =>
                                                {
                                                    ((IDisposable)client).Dispose();
                                                }));
                        

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        try
                        {
                            var packet = await client.ReceiveAsync()
                                                 .ConfigureAwait(false);
                            try
                            {
                                callback(new AdapterInformation(ipv6Address.ToString(), adapter.Name), packet.RemoteEndPoint.Address.ToString(), packet.Buffer);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Callback threw an exception: {ex}");
                            }
                        }
                        catch when (cancellationToken.IsCancellationRequested)
                        {
                            // eat any exceptions if we've been cancelled
                        }
                    }


                    Debug.WriteLine($"Done listening for mDNS packets on {adapter.Name}, idx {ifaceIndex}, IP: {ipv6Address}.");

                    cancellationToken.ThrowIfCancellationRequested();
                }
            }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        }
    }
}
