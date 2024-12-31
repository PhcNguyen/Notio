﻿using Notio.Common.Connection;
using Notio.Logging;
using Notio.Network.Protocols;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Notio.Network.Listeners;

public abstract class Listener(int port, IProtocol protocol) : TcpListener(IPAddress.Any, port), IListener
{
    private readonly int _port = port;
    private readonly IProtocol _protocol = protocol;

    public void BeginListening(CancellationToken cancellationToken)
    {
        Task.Run(async () =>
        {
            try
            {
                base.Start();
            }
            catch (SocketException ex)
            {
                NotioLog.Instance.Error($"Could not start {_protocol} on port {_port}", ex);
                Environment.Exit(1);
                return;
            }

            NotioLog.Instance.Info($"{_protocol} is online on port {_port}");

            while (!cancellationToken.IsCancellationRequested)
            {
                var connection = await CreateConnection(cancellationToken);

                _protocol.OnAccept(connection);
            }
        }, cancellationToken);
    }

    public void EndListening() => base.Stop();

    private async Task<IConnection> CreateConnection(CancellationToken cancellationToken)
    {
        Socket socket = await AcceptSocketAsync(cancellationToken).ConfigureAwait(false);

        Connection connection = new(socket);

        connection.OnCloseEvent += OnConnectionClose!;
        connection.OnProcessEvent += _protocol.ProcessMessage!;
        connection.OnPostProcessEvent += _protocol.PostProcessMessage!;
        return connection;
    }

    private void OnConnectionClose(object? sender, IConnectionEventArgs args)
    {
        // De-subscribe to this event first.
        args.Connection.OnCloseEvent -= OnConnectionClose!;
        args.Connection.OnReceiveEvent -= _protocol.ProcessMessage!;
        args.Connection.OnPostProcessEvent -= _protocol.PostProcessMessage!;
    }
}