using System;
using System.Net;
using System.Threading;
using System.Diagnostics;
using System.Net.Sockets;
using System.Collections.Generic;

#if !__NOIPENDPOINT__
using NetEndPoint = System.Net.IPEndPoint;
#endif

namespace Lidgren.Network
{
	public partial class NetPeer
	{
		private NetPeerStatus m_status;
		private Thread m_networkThread;
		private Socket m_socket;
		internal byte[] m_sendBuffer;
		internal byte[] m_receiveBuffer;
		internal NetIncomingMessage m_readHelperMessage;
		private EndPoint m_senderRemote;
		private object m_initializeLock = new object();
		private uint m_frameCounter;
		private double m_lastHeartbeat;
		private double m_lastSocketBind = float.MinValue;
		private NetUPnP m_upnp;
		internal bool m_needFlushSendQueue;

		internal readonly NetPeerConfiguration m_configuration;
		private readonly NetQueue<NetIncomingMessage> m_releasedIncomingMessages;
		internal readonly NetQueue<NetTuple<NetEndPoint, NetOutgoingMessage>> m_unsentUnconnectedMessages;

		internal Dictionary<NetEndPoint, NetConnection> m_handshakes;

		internal readonly NetPeerStatistics m_statistics;
		internal long m_uniqueIdentifier;
		internal bool m_executeFlushSendQueue;

		private AutoResetEvent m_messageReceivedEvent;
		private List<NetTuple<SynchronizationContext, SendOrPostCallback>> m_receiveCallbacks;

		/// <summary>
		/// Gets the socket, if Start() has been called
		/// </summary>
		public Socket Socket { get { return m_socket; } }

		/// <summary>
		/// Call this to register a callback for when a new message arrives
		/// </summary>
		public void RegisterReceivedCallback(SendOrPostCallback callback, SynchronizationContext syncContext = null)
		{
			if (syncContext == null)
				syncContext = SynchronizationContext.Current;
			if (syncContext == null)
				throw new NetException("Need a SynchronizationContext to register callback on correct thread!");
			if (m_receiveCallbacks == null)
				m_receiveCallbacks = new List<NetTuple<SynchronizationContext, SendOrPostCallback>>();
			m_receiveCallbacks.Add(new NetTuple<SynchronizationContext, SendOrPostCallback>(syncContext, callback));
		}

		/// <summary>
		/// Call this to unregister a callback, but remember to do it in the same synchronization context!
		/// </summary>
		public void UnregisterReceivedCallback(SendOrPostCallback callback)
		{
			if (m_receiveCallbacks == null)
				return;

			// remove all callbacks regardless of sync context
			m_receiveCallbacks.RemoveAll(tuple => tuple.Item2.Equals(callback));

			if (m_receiveCallbacks.Count < 1)
				m_receiveCallbacks = null;
		}

		internal void ReleaseMessage(NetIncomingMessage msg)
		{
			NetException.Assert(msg.m_incomingMessageType != NetIncomingMessageType.Error);

			if (msg.m_isFragment)
			{
				HandleReleasedFragment(msg);
				return;
			}

			m_releasedIncomingMessages.Enqueue(msg);

			if (m_messageReceivedEvent != null)
				m_messageReceivedEvent.Set();

			if (m_receiveCallbacks != null)
			{
				foreach (var tuple in m_receiveCallbacks)
				{
					try
					{
						tuple.Item1.Post(tuple.Item2, this);
					}
					catch (Exception ex)
					{
						LogWarning("Receive callback exception:" + ex);
					}
				}
			}
		}

		private void BindSocket(bool reBind)
		{
			double now = NetTime.Now;
			if (now - m_lastSocketBind < 1.0)
			{
				LogDebug("Suppressed socket rebind; last bound " + (now - m_lastSocketBind) + " seconds ago");
				return; // only allow rebind once every second
			}
			m_lastSocketBind = now;

			if (m_socket == null)
				m_socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

			if (reBind)
				m_socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, (int)1);

			m_socket.ReceiveBufferSize = m_configuration.ReceiveBufferSize;
			m_socket.SendBufferSize = m_configuration.SendBufferSize;
			m_socket.Blocking = false;

			var ep = (EndPoint)new NetEndPoint(m_configuration.LocalAddress, reBind ? m_listenPort : m_configuration.Port);
			m_socket.Bind(ep);

			// try catch only works on linux not osx
			try
			{
				// this is not supported in mono / mac or linux yet.
				if(Environment.OSVersion.Platform != PlatformID.Unix)
				{
					const uint IOC_IN = 0x80000000;
					const uint IOC_VENDOR = 0x18000000;
					uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;
					m_socket.IOControl((int)SIO_UDP_CONNRESET, new byte[] { Convert.ToByte(false) }, null);
				}
                else
                {
                    LogDebug("Platform doesn't support SIO_UDP_CONNRESET");
                }
			}
			catch (System.Exception e)
			{
                LogDebug("Platform doesn't support SIO_UDP_CONNRESET");
				// this will be thrown on linux but not mac if it doesn't exist.
				// ignore; SIO_UDP_CONNRESET not supported on this platform
			}

			var boundEp = m_socket.LocalEndPoint as NetEndPoint;
			LogDebug("Socket bound to " + boundEp + ": " + m_socket.IsBound);
			m_listenPort = boundEp.Port;
		}

		private void InitializeNetwork()
		{
			lock (m_initializeLock)
			{
				m_configuration.Lock();

				if (m_status == NetPeerStatus.Running)
					return;

				if (m_configuration.m_enableUPnP)
					m_upnp = new NetUPnP(this);

				InitializePools();

				m_releasedIncomingMessages.Clear();
				m_unsentUnconnectedMessages.Clear();
				m_handshakes.Clear();

				// bind to socket
				BindSocket(false);

				m_receiveBuffer = new byte[m_configuration.ReceiveBufferSize];
				m_sendBuffer = new byte[m_configuration.SendBufferSize];
				m_readHelperMessage = new NetIncomingMessage(NetIncomingMessageType.Error);
				m_readHelperMessage.m_data = m_receiveBuffer;

				byte[] macBytes = NetUtility.GetMacAddressBytes();

				var boundEp = m_socket.LocalEndPoint as NetEndPoint;
				byte[] epBytes = BitConverter.GetBytes(boundEp.GetHashCode());
				byte[] combined = new byte[epBytes.Length + macBytes.Length];
				Array.Copy(epBytes, 0, combined, 0, epBytes.Length);
				Array.Copy(macBytes, 0, combined, epBytes.Length, macBytes.Length);
				m_uniqueIdentifier = BitConverter.ToInt64(NetUtility.ComputeSHAHash(combined), 0);

				m_status = NetPeerStatus.Running;
			}
		}

		private void NetworkLoop()
		{
			VerifyNetworkThread();

			LogDebug("Network thread started");

			//
			// Network loop
			//
			do
			{
				try
				{
					Heartbeat();
				}
				catch (Exception ex)
				{
					LogWarning(ex.ToString());
				}
			} while (m_status == NetPeerStatus.Running);

			//
			// perform shutdown
			//
			ExecutePeerShutdown();
		}

		private void ExecutePeerShutdown()
		{
			VerifyNetworkThread();

			LogDebug("Shutting down...");

			// disconnect and make one final heartbeat
			var list = new List<NetConnection>(m_handshakes.Count + m_connections.Count);
			lock (m_connections)
			{
				foreach (var conn in m_connections)
					if (conn != null)
						list.Add(conn);
			}

			lock (m_handshakes)
			{
				foreach (var hs in m_handshakes.Values)
					if (hs != null && list.Contains(hs) == false)
						list.Add(hs);
			}

			// shut down connections
			foreach (NetConnection conn in list)
				conn.Shutdown(m_shutdownReason);

			FlushDelayedPackets();

			// one final heartbeat, will send stuff and do disconnect
			Heartbeat();

			NetUtility.Sleep(10);

			lock (m_initializeLock)
			{
				try
				{
					if (m_socket != null)
					{
						try
						{
							m_socket.Shutdown(SocketShutdown.Receive);
						}
						catch(Exception ex)
						{
							LogDebug("Socket.Shutdown exception: " + ex.ToString());
						}

						try
						{
							m_socket.Close(2); // 2 seconds timeout
						}
						catch (Exception ex)
						{
							LogDebug("Socket.Close exception: " + ex.ToString());
						}
					}
				}
				finally
				{
					m_socket = null;
					m_status = NetPeerStatus.NotRunning;
					LogDebug("Shutdown complete");

					// wake up any threads waiting for server shutdown
					if (m_messageReceivedEvent != null)
						m_messageReceivedEvent.Set();
				}

				m_lastSocketBind = float.MinValue;
				m_receiveBuffer = null;
				m_sendBuffer = null;
				m_unsentUnconnectedMessages.Clear();
				m_connections.Clear();
				m_connectionLookup.Clear();
				m_handshakes.Clear();
			}

			return;
		}

		private void Heartbeat()
		{
			VerifyNetworkThread();

			double now = NetTime.Now;
			double delta = now - m_lastHeartbeat;

			int maxCHBpS = 1250 - m_connections.Count;
			if (maxCHBpS < 250)
				maxCHBpS = 250;
			if (delta > (1.0 / (double)maxCHBpS) || delta < 0.0) // max connection heartbeats/second max
			{
				m_frameCounter++;
				m_lastHeartbeat = now;

				// do handshake heartbeats
				if ((m_frameCounter % 3) == 0)
				{
					foreach (var kvp in m_handshakes)
					{
						NetConnection conn = kvp.Value as NetConnection;
#if DEBUG
						// sanity check
						if (kvp.Key != kvp.Key)
							LogWarning("Sanity fail! Connection in handshake list under wrong key!");
#endif
						conn.UnconnectedHeartbeat(now);
						if (conn.m_status == NetConnectionStatus.Connected || conn.m_status == NetConnectionStatus.Disconnected)
						{
#if DEBUG
							// sanity check
							if (conn.m_status == NetConnectionStatus.Disconnected && m_handshakes.ContainsKey(conn.RemoteEndPoint))
							{
								LogWarning("Sanity fail! Handshakes list contained disconnected connection!");
								m_handshakes.Remove(conn.RemoteEndPoint);
							}
#endif
							break; // collection has been modified
						}
					}
				}

#if DEBUG
				SendDelayedPackets();
#endif

				// update m_executeFlushSendQueue
				if (m_configuration.m_autoFlushSendQueue && m_needFlushSendQueue == true)
				{
					m_executeFlushSendQueue = true;
					m_needFlushSendQueue = false; // a race condition to this variable will simply result in a single superfluous call to FlushSendQueue()
				}

				// do connection heartbeats
				lock (m_connections)
				{
					for (int i = m_connections.Count - 1; i >= 0; i--)
					{
						var conn = m_connections[i];
						conn.Heartbeat(now, m_frameCounter);
						if (conn.m_status == NetConnectionStatus.Disconnected)
						{
							//
							// remove connection
							//
							m_connections.RemoveAt(i);
							m_connectionLookup.Remove(conn.RemoteEndPoint);
						}
					}
				}
				m_executeFlushSendQueue = false;

				// send unsent unconnected messages
				NetTuple<NetEndPoint, NetOutgoingMessage> unsent;
				while (m_unsentUnconnectedMessages.TryDequeue(out unsent))
				{
					NetOutgoingMessage om = unsent.Item2;

					int len = om.Encode(m_sendBuffer, 0, 0);

					Interlocked.Decrement(ref om.m_recyclingCount);
					if (om.m_recyclingCount <= 0)
						Recycle(om);

					bool connReset;
					SendPacket(len, unsent.Item1, 1, out connReset);
				}
			}

            if (m_upnp != null)
                m_upnp.CheckForDiscoveryTimeout();

			//
			// read from socket
			//
			if (m_socket == null)
				return;

			if (!m_socket.Poll(1000, SelectMode.SelectRead)) // wait up to 1 ms for data to arrive
				return;

			//if (m_socket == null || m_socket.Available < 1)
			//	return;

			// update now
			now = NetTime.Now;

			do
			{
				int bytesReceived = 0;
				try
				{
					bytesReceived = m_socket.ReceiveFrom(m_receiveBuffer, 0, m_receiveBuffer.Length, SocketFlags.None, ref m_senderRemote);
				}
				catch (SocketException sx)
				{
					switch (sx.SocketErrorCode)
					{
						case SocketError.ConnectionReset:
							// connection reset by peer, aka connection forcibly closed aka "ICMP port unreachable"
							// we should shut down the connection; but m_senderRemote seemingly cannot be trusted, so which connection should we shut down?!
							// So, what to do?
							LogWarning("ConnectionReset");
							return;

						case SocketError.NotConnected:
							// socket is unbound; try to rebind it (happens on mobile when process goes to sleep)
							BindSocket(true);
							return;

					    case SocketError.AccessDenied:					    case SocketError.AddressAlreadyInUse:					    case SocketError.AddressFamilyNotSupported:					    case SocketError.AddressNotAvailable:					    case SocketError.AlreadyInProgress:					    case SocketError.ConnectionAborted:					    case SocketError.ConnectionRefused:					    case SocketError.DestinationAddressRequired:					    case SocketError.Disconnecting:					    case SocketError.Fault:					    case SocketError.HostDown:					    case SocketError.HostNotFound:					    case SocketError.HostUnreachable:					    case SocketError.InProgress:					    case SocketError.Interrupted:					    case SocketError.InvalidArgument:					    case SocketError.IOPending:					    case SocketError.IsConnected:					    case SocketError.MessageSize:					    case SocketError.NetworkDown:					    case SocketError.NetworkReset:					    case SocketError.NetworkUnreachable:					    case SocketError.NoBufferSpaceAvailable:					    case SocketError.NoData:					    case SocketError.NoRecovery:					    case SocketError.NotInitialized:					    case SocketError.NotSocket:					    case SocketError.OperationAborted:					    case SocketError.OperationNotSupported:					    case SocketError.ProcessLimit:					    case SocketError.ProtocolFamilyNotSupported:					    case SocketError.ProtocolNotSupported:					    case SocketError.ProtocolOption:					    case SocketError.ProtocolType:					    case SocketError.Shutdown:					    case SocketError.SocketError:					    case SocketError.SocketNotSupported:					    case SocketError.Success:					    case SocketError.SystemNotReady:					    case SocketError.TimedOut:					    case SocketError.TooManyOpenSockets:					    case SocketError.TryAgain:					    case SocketError.TypeNotFound:					    case SocketError.VersionNotSupported:					    case SocketError.WouldBlock:					    default:
							LogWarning("Socket exception: " + sx.ToString());
							return;
					}
				}

				if (bytesReceived < NetConstants.HeaderByteSize)
					return;

				//LogVerbose("Received " + bytesReceived + " bytes");

				var ipsender = (NetEndPoint)m_senderRemote;

				if (m_upnp != null && now < m_upnp.m_discoveryResponseDeadline && bytesReceived > 32)
				{
					// is this an UPnP response?
					string resp = System.Text.Encoding.UTF8.GetString(m_receiveBuffer, 0, bytesReceived);
					if (resp.Contains("upnp:rootdevice") || resp.Contains("UPnP/1.0"))
					{
						try
						{
							resp = resp.Substring(resp.ToLower().IndexOf("location:") + 9);
							resp = resp.Substring(0, resp.IndexOf("\r")).Trim();
							m_upnp.ExtractServiceUrl(resp);
							return;
						}
						catch (Exception ex)
						{
							LogDebug("Failed to parse UPnP response: " + ex.ToString());

							// don't try to parse this packet further
							return;
						}
					}
				}

				NetConnection sender = null;
				m_connectionLookup.TryGetValue(ipsender, out sender);

				//
				// parse packet into messages
				//
				int numMessages = 0;
				int numFragments = 0;
				int ptr = 0;
				while ((bytesReceived - ptr) >= NetConstants.HeaderByteSize)
				{
					// decode header
					//  8 bits - NetMessageType
					//  1 bit  - Fragment?
					// 15 bits - Sequence number
					// 16 bits - Payload length in bits

					numMessages++;

					NetMessageType tp = (NetMessageType)m_receiveBuffer[ptr++];

					byte low = m_receiveBuffer[ptr++];
					byte high = m_receiveBuffer[ptr++];

					bool isFragment = ((low & 1) == 1);
					ushort sequenceNumber = (ushort)((low >> 1) | (((int)high) << 7));

					if (isFragment)
						numFragments++;

					ushort payloadBitLength = (ushort)(m_receiveBuffer[ptr++] | (m_receiveBuffer[ptr++] << 8));
					int payloadByteLength = NetUtility.BytesToHoldBits(payloadBitLength);

					if (bytesReceived - ptr < payloadByteLength)
					{
						LogWarning("Malformed packet; stated payload length " + payloadByteLength + ", remaining bytes " + (bytesReceived - ptr));
						return;
					}

					if (tp >= NetMessageType.Unused1 && tp <= NetMessageType.Unused29)
					{
						ThrowOrLog("Unexpected NetMessageType: " + tp);
						return;
					}

					try
					{
						if (tp >= NetMessageType.LibraryError)
						{
							if (sender != null)
								sender.ReceivedLibraryMessage(tp, ptr, payloadByteLength);
							else
								ReceivedUnconnectedLibraryMessage(now, ipsender, tp, ptr, payloadByteLength);
						}
						else
						{
							if (sender == null && !m_configuration.IsMessageTypeEnabled(NetIncomingMessageType.UnconnectedData))
								return; // dropping unconnected message since it's not enabled

							NetIncomingMessage msg = CreateIncomingMessage(NetIncomingMessageType.Data, payloadByteLength);
							msg.m_isFragment = isFragment;
							msg.m_receiveTime = now;
							msg.m_sequenceNumber = sequenceNumber;
							msg.m_receivedMessageType = tp;
							msg.m_senderConnection = sender;
							msg.m_senderEndPoint = ipsender;
							msg.m_bitLength = payloadBitLength;

							Buffer.BlockCopy(m_receiveBuffer, ptr, msg.m_data, 0, payloadByteLength);
							if (sender != null)
							{
								if (tp == NetMessageType.Unconnected)
								{
									// We're connected; but we can still send unconnected messages to this peer
									msg.m_incomingMessageType = NetIncomingMessageType.UnconnectedData;
									ReleaseMessage(msg);
								}
								else
								{
									// connected application (non-library) message
									sender.ReceivedMessage(msg);
								}
							}
							else
							{
								// at this point we know the message type is enabled
								// unconnected application (non-library) message
								msg.m_incomingMessageType = NetIncomingMessageType.UnconnectedData;
								ReleaseMessage(msg);
							}
						}
					}
					catch (Exception ex)
					{
						LogError("Packet parsing error: " + ex.Message + " from " + ipsender);
					}
					ptr += payloadByteLength;
				}

				m_statistics.PacketReceived(bytesReceived, numMessages, numFragments);
				if (sender != null)
					sender.m_statistics.PacketReceived(bytesReceived, numMessages, numFragments);

			} while (m_socket.Available > 0);
		}

		/// <summary>
		/// If NetPeerConfiguration.AutoFlushSendQueue() is false; you need to call this to send all messages queued using SendMessage()
		/// </summary>
		public void FlushSendQueue()
		{
			m_executeFlushSendQueue = true;
		}

		internal void HandleIncomingDiscoveryRequest(double now, NetEndPoint senderEndPoint, int ptr, int payloadByteLength)
		{
			if (m_configuration.IsMessageTypeEnabled(NetIncomingMessageType.DiscoveryRequest))
			{
				NetIncomingMessage dm = CreateIncomingMessage(NetIncomingMessageType.DiscoveryRequest, payloadByteLength);
				if (payloadByteLength > 0)
					Buffer.BlockCopy(m_receiveBuffer, ptr, dm.m_data, 0, payloadByteLength);
				dm.m_receiveTime = now;
				dm.m_bitLength = payloadByteLength * 8;
				dm.m_senderEndPoint = senderEndPoint;
				ReleaseMessage(dm);
			}
		}

		internal void HandleIncomingDiscoveryResponse(double now, NetEndPoint senderEndPoint, int ptr, int payloadByteLength)
		{
			if (m_configuration.IsMessageTypeEnabled(NetIncomingMessageType.DiscoveryResponse))
			{
				NetIncomingMessage dr = CreateIncomingMessage(NetIncomingMessageType.DiscoveryResponse, payloadByteLength);
				if (payloadByteLength > 0)
					Buffer.BlockCopy(m_receiveBuffer, ptr, dr.m_data, 0, payloadByteLength);
				dr.m_receiveTime = now;
				dr.m_bitLength = payloadByteLength * 8;
				dr.m_senderEndPoint = senderEndPoint;
				ReleaseMessage(dr);
			}
		}

		private void ReceivedUnconnectedLibraryMessage(double now, NetEndPoint senderEndPoint, NetMessageType tp, int ptr, int payloadByteLength)
		{
			NetConnection shake;
			if (m_handshakes.TryGetValue(senderEndPoint, out shake))
			{
				shake.ReceivedHandshake(now, tp, ptr, payloadByteLength);
				return;
			}

			//
			// Library message from a completely unknown sender; lets just accept Connect
			//
			switch (tp)
			{
				case NetMessageType.Discovery:
					HandleIncomingDiscoveryRequest(now, senderEndPoint, ptr, payloadByteLength);
					return;
				case NetMessageType.DiscoveryResponse:
					HandleIncomingDiscoveryResponse(now, senderEndPoint, ptr, payloadByteLength);
					return;
				case NetMessageType.NatIntroduction:
					if (m_configuration.IsMessageTypeEnabled(NetIncomingMessageType.NatIntroductionSuccess))
						HandleNatIntroduction(ptr);
					return;
				case NetMessageType.NatPunchMessage:
					if (m_configuration.IsMessageTypeEnabled(NetIncomingMessageType.NatIntroductionSuccess))
						HandleNatPunch(ptr, senderEndPoint);
					return;
				case NetMessageType.NatIntroductionConfirmRequest:
					if (m_configuration.IsMessageTypeEnabled(NetIncomingMessageType.NatIntroductionSuccess))
						HandleNatPunchConfirmRequest(ptr, senderEndPoint);
					return;
				case NetMessageType.NatIntroductionConfirmed:
					if (m_configuration.IsMessageTypeEnabled(NetIncomingMessageType.NatIntroductionSuccess))
						HandleNatPunchConfirmed(ptr, senderEndPoint);
					return;
				case NetMessageType.ConnectResponse:

					lock (m_handshakes)
					{
						foreach (var hs in m_handshakes)
						{
							if (hs.Key.Address.Equals(senderEndPoint.Address))
							{
								if (hs.Value.m_connectionInitiator)
								{
									//
									// We are currently trying to connection to XX.XX.XX.XX:Y
									// ... but we just received a ConnectResponse from XX.XX.XX.XX:Z
									// Lets just assume the router decided to use this port instead
									//
									var hsconn = hs.Value;
									m_connectionLookup.Remove(hs.Key);
									m_handshakes.Remove(hs.Key);

									LogDebug("Detected host port change; rerouting connection to " + senderEndPoint);
									hsconn.MutateEndPoint(senderEndPoint);

									m_connectionLookup.Add(senderEndPoint, hsconn);
									m_handshakes.Add(senderEndPoint, hsconn);

									hsconn.ReceivedHandshake(now, tp, ptr, payloadByteLength);
									return;
								}
							}
						}
					}

					LogWarning("Received unhandled library message " + tp + " from " + senderEndPoint);
					return;
				case NetMessageType.Connect:
					if (m_configuration.AcceptIncomingConnections == false)
					{
						LogWarning("Received Connect, but we're not accepting incoming connections!");
						return;
					}
					// handle connect
					// It's someone wanting to shake hands with us!

					int reservedSlots = m_handshakes.Count + m_connections.Count;
					if (reservedSlots >= m_configuration.m_maximumConnections)
					{
						// server full
						NetOutgoingMessage full = CreateMessage("Server full");
						full.m_messageType = NetMessageType.Disconnect;
						SendLibrary(full, senderEndPoint);
						return;
					}

					// Ok, start handshake!
					NetConnection conn = new NetConnection(this, senderEndPoint);
					conn.m_status = NetConnectionStatus.ReceivedInitiation;
					m_handshakes.Add(senderEndPoint, conn);
					conn.ReceivedHandshake(now, tp, ptr, payloadByteLength);
					return;

				case NetMessageType.Disconnect:
					// this is probably ok
					LogVerbose("Received Disconnect from unconnected source: " + senderEndPoint);
					return;
			    case NetMessageType.Unconnected:			    case NetMessageType.UserUnreliable:			    case NetMessageType.UserSequenced1:			    case NetMessageType.UserSequenced2:			    case NetMessageType.UserSequenced3:			    case NetMessageType.UserSequenced4:			    case NetMessageType.UserSequenced5:			    case NetMessageType.UserSequenced6:			    case NetMessageType.UserSequenced7:			    case NetMessageType.UserSequenced8:			    case NetMessageType.UserSequenced9:			    case NetMessageType.UserSequenced10:			    case NetMessageType.UserSequenced11:			    case NetMessageType.UserSequenced12:			    case NetMessageType.UserSequenced13:			    case NetMessageType.UserSequenced14:			    case NetMessageType.UserSequenced15:			    case NetMessageType.UserSequenced16:			    case NetMessageType.UserSequenced17:			    case NetMessageType.UserSequenced18:			    case NetMessageType.UserSequenced19:			    case NetMessageType.UserSequenced20:			    case NetMessageType.UserSequenced21:			    case NetMessageType.UserSequenced22:			    case NetMessageType.UserSequenced23:			    case NetMessageType.UserSequenced24:			    case NetMessageType.UserSequenced25:			    case NetMessageType.UserSequenced26:			    case NetMessageType.UserSequenced27:			    case NetMessageType.UserSequenced28:			    case NetMessageType.UserSequenced29:			    case NetMessageType.UserSequenced30:			    case NetMessageType.UserSequenced31:			    case NetMessageType.UserSequenced32:			    case NetMessageType.UserReliableUnordered:			    case NetMessageType.UserReliableSequenced1:			    case NetMessageType.UserReliableSequenced2:			    case NetMessageType.UserReliableSequenced3:			    case NetMessageType.UserReliableSequenced4:			    case NetMessageType.UserReliableSequenced5:			    case NetMessageType.UserReliableSequenced6:			    case NetMessageType.UserReliableSequenced7:			    case NetMessageType.UserReliableSequenced8:			    case NetMessageType.UserReliableSequenced9:			    case NetMessageType.UserReliableSequenced10:			    case NetMessageType.UserReliableSequenced11:			    case NetMessageType.UserReliableSequenced12:			    case NetMessageType.UserReliableSequenced13:			    case NetMessageType.UserReliableSequenced14:			    case NetMessageType.UserReliableSequenced15:			    case NetMessageType.UserReliableSequenced16:			    case NetMessageType.UserReliableSequenced17:			    case NetMessageType.UserReliableSequenced18:			    case NetMessageType.UserReliableSequenced19:			    case NetMessageType.UserReliableSequenced20:			    case NetMessageType.UserReliableSequenced21:			    case NetMessageType.UserReliableSequenced22:			    case NetMessageType.UserReliableSequenced23:			    case NetMessageType.UserReliableSequenced24:			    case NetMessageType.UserReliableSequenced25:			    case NetMessageType.UserReliableSequenced26:			    case NetMessageType.UserReliableSequenced27:			    case NetMessageType.UserReliableSequenced28:			    case NetMessageType.UserReliableSequenced29:			    case NetMessageType.UserReliableSequenced30:			    case NetMessageType.UserReliableSequenced31:			    case NetMessageType.UserReliableSequenced32:			    case NetMessageType.UserReliableOrdered1:			    case NetMessageType.UserReliableOrdered2:			    case NetMessageType.UserReliableOrdered3:			    case NetMessageType.UserReliableOrdered4:			    case NetMessageType.UserReliableOrdered5:			    case NetMessageType.UserReliableOrdered6:			    case NetMessageType.UserReliableOrdered7:			    case NetMessageType.UserReliableOrdered8:			    case NetMessageType.UserReliableOrdered9:			    case NetMessageType.UserReliableOrdered10:			    case NetMessageType.UserReliableOrdered11:			    case NetMessageType.UserReliableOrdered12:			    case NetMessageType.UserReliableOrdered13:			    case NetMessageType.UserReliableOrdered14:			    case NetMessageType.UserReliableOrdered15:			    case NetMessageType.UserReliableOrdered16:			    case NetMessageType.UserReliableOrdered17:			    case NetMessageType.UserReliableOrdered18:			    case NetMessageType.UserReliableOrdered19:			    case NetMessageType.UserReliableOrdered20:			    case NetMessageType.UserReliableOrdered21:			    case NetMessageType.UserReliableOrdered22:			    case NetMessageType.UserReliableOrdered23:			    case NetMessageType.UserReliableOrdered24:			    case NetMessageType.UserReliableOrdered25:			    case NetMessageType.UserReliableOrdered26:			    case NetMessageType.UserReliableOrdered27:			    case NetMessageType.UserReliableOrdered28:			    case NetMessageType.UserReliableOrdered29:			    case NetMessageType.UserReliableOrdered30:			    case NetMessageType.UserReliableOrdered31:			    case NetMessageType.UserReliableOrdered32:			    case NetMessageType.Unused1:			    case NetMessageType.Unused2:			    case NetMessageType.Unused3:			    case NetMessageType.Unused4:			    case NetMessageType.Unused5:			    case NetMessageType.Unused6:			    case NetMessageType.Unused7:			    case NetMessageType.Unused8:			    case NetMessageType.Unused9:			    case NetMessageType.Unused10:			    case NetMessageType.Unused11:			    case NetMessageType.Unused12:			    case NetMessageType.Unused13:			    case NetMessageType.Unused14:			    case NetMessageType.Unused15:			    case NetMessageType.Unused16:			    case NetMessageType.Unused17:			    case NetMessageType.Unused18:			    case NetMessageType.Unused19:			    case NetMessageType.Unused20:			    case NetMessageType.Unused21:			    case NetMessageType.Unused22:			    case NetMessageType.Unused23:			    case NetMessageType.Unused24:			    case NetMessageType.Unused25:			    case NetMessageType.Unused26:			    case NetMessageType.Unused27:			    case NetMessageType.Unused28:			    case NetMessageType.Unused29:			    case NetMessageType.LibraryError:			    case NetMessageType.Ping:			    case NetMessageType.Pong:			    case NetMessageType.ConnectionEstablished:			    case NetMessageType.Acknowledge:			    case NetMessageType.ExpandMTURequest:			    case NetMessageType.ExpandMTUSuccess:			    default:
					LogWarning("Received unhandled library message " + tp + " from " + senderEndPoint);
					return;
			}
		}

		internal void AcceptConnection(NetConnection conn)
		{
			// LogDebug("Accepted connection " + conn);
			conn.InitExpandMTU(NetTime.Now);

			if (m_handshakes.Remove(conn.m_remoteEndPoint) == false)
				LogWarning("AcceptConnection called but m_handshakes did not contain it!");

			lock (m_connections)
			{
				if (m_connections.Contains(conn))
				{
					LogWarning("AcceptConnection called but m_connection already contains it!");
				}
				else
				{
					m_connections.Add(conn);
					m_connectionLookup.Add(conn.m_remoteEndPoint, conn);
				}
			}
		}

		[Conditional("DEBUG")]
		internal void VerifyNetworkThread()
		{
			Thread ct = Thread.CurrentThread;
			if (Thread.CurrentThread != m_networkThread)
				throw new NetException("Executing on wrong thread! Should be library system thread (is " + ct.Name + " mId " + ct.ManagedThreadId + ")");
		}

		internal NetIncomingMessage SetupReadHelperMessage(int ptr, int payloadLength)
		{
			VerifyNetworkThread();

			m_readHelperMessage.m_bitLength = (ptr + payloadLength) * 8;
			m_readHelperMessage.m_readPosition = (ptr * 8);
			return m_readHelperMessage;
		}
	}
}
