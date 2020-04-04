﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NLog;
using Shadowsocks.Model;

namespace Shadowsocks.Controller
{
    public interface IDatagramService
    {
        [Obsolete]
        bool Handle(byte[] firstPacket, int length, Socket socket, object state);

        public abstract bool Handle(CachedNetworkStream stream, object state);

        void Stop();
    }

    public abstract class DatagramService : IDatagramService
    {
        [Obsolete]
        public abstract bool Handle(byte[] firstPacket, int length, Socket socket, object state);

        public abstract bool Handle(CachedNetworkStream stream, object state);

        public virtual void Stop() { }
    }

    public class UDPListener
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();

        public class UDPState
        {
            public UDPState(Socket s)
            {
                socket = s;
                remoteEndPoint = new IPEndPoint(s.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
            }
            public Socket socket;
            public byte[] buffer = new byte[4096];
            public EndPoint remoteEndPoint;
        }

        Configuration _config;
        bool _shareOverLAN;
        Socket _udpSocket;
        IEnumerable<IDatagramService> _services;

        public UDPListener(Configuration config, IEnumerable<IDatagramService> services)
        {
            this._config = config;
            this._shareOverLAN = _config.shareOverLan;

            this._services = services;
        }

        private bool CheckIfPortInUse(int port)
        {
            IPGlobalProperties ipProperties = IPGlobalProperties.GetIPGlobalProperties();
            return ipProperties.GetActiveUdpListeners().Any(endPoint => endPoint.Port == port);
        }

        public void Start()
        {
            if (CheckIfPortInUse(this._config.localPort))
                throw new Exception(I18N.GetString("Port {0} already in use", this._config.localPort));

            // Create a TCP/IP socket.
            _udpSocket = new Socket(_config.isIPv6Enabled ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            IPEndPoint localEndPoint = null;
            localEndPoint = _shareOverLAN
                ? new IPEndPoint(_config.isIPv6Enabled ? IPAddress.IPv6Any : IPAddress.Any, this._config.localPort)
                : new IPEndPoint(_config.isIPv6Enabled ? IPAddress.IPv6Loopback : IPAddress.Loopback, this._config.localPort);

            // Bind the socket to the local endpoint and listen for incoming connections.
            _udpSocket.Bind(localEndPoint);

            // Start an asynchronous socket to listen for connections.
            logger.Info($"Shadowsocks started UDP ({UpdateChecker.Version})");
            logger.Debug(Encryption.EncryptorFactory.DumpRegisteredEncryptor());
            UDPState udpState = new UDPState(_udpSocket);
            _udpSocket.BeginReceiveFrom(udpState.buffer, 0, udpState.buffer.Length, 0, ref udpState.remoteEndPoint, new AsyncCallback(RecvFromCallback), udpState);

        }

        public void Stop()
        {
            _udpSocket?.Close();
            foreach (var s in _services)
            {
                s.Stop();
            }
        }

        public void RecvFromCallback(IAsyncResult ar)
        {
            UDPState state = (UDPState)ar.AsyncState;
            var socket = state.socket;
            try
            {
                int bytesRead = socket.EndReceiveFrom(ar, ref state.remoteEndPoint);
                foreach (IDatagramService service in _services)
                {
                    if (service.Handle(state.buffer, bytesRead, socket, state))
                    {
                        break;
                    }
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                logger.Debug(ex);
            }
            finally
            {
                try
                {
                    socket.BeginReceiveFrom(state.buffer, 0, state.buffer.Length, 0, ref state.remoteEndPoint, new AsyncCallback(RecvFromCallback), state);
                }
                catch (ObjectDisposedException)
                {
                    // do nothing
                }
                catch (Exception)
                {
                }
            }
        }
    }
}