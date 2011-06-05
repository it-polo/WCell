using System;
using WCell.Util.Logging;
using WCell.Constants;
using WCell.Constants.Login;
using WCell.Core;
using WCell.Core.Network;
using WCell.RealmServer.Chat;
using WCell.RealmServer.Database;
using WCell.RealmServer.Entities;
using WCell.RealmServer.Global;
using WCell.RealmServer.Res;
using WCell.Util.Graphics;
using WCell.Util.Threading;
using WCell.RealmServer.Network;
using WCell.Util;

namespace WCell.RealmServer.Handlers
{
	public static class LoginHandler
	{
		/// <summary>
		/// Triggered after an Account logs into the Realm-server
		/// </summary>
		public static event Action<RealmAccount> AccountLogin;

		/// <summary>
		/// Triggered before a client disconnects
		/// </summary>
		public static event Func<IRealmClient, CharacterRecord, CharacterRecord> BeforeLogin;

		/// <summary>
		/// Triggered before a client disconnects
		/// </summary>
		public static event Action<IRealmClient> ClientDisconnected;

		private static readonly Logger log = LogManager.GetCurrentClassLogger();

		#region Authentication
		/// <summary>
		/// Handles an incoming auth session request.
		/// </summary>
		/// <param name="client">the Session the incoming packet belongs to</param>
		/// <param name="packet">the full packet</param>
		[ClientPacketHandler(RealmServerOpCode.CMSG_AUTH_SESSION, IsGamePacket = false, RequiresLogin = false)]
		public static void AuthSessionRequest(IRealmClient client, RealmPacketIn packet)
		{
			if (client.ActiveCharacter != null || client.Account != null)
			{
				// Already logged in
				SendAuthSessionErrorReply(client, LoginErrorCode.AUTH_ALREADY_ONLINE);
				client.Disconnect();
			}
			else if (!client.IsEncrypted)
			{
				var builtNumberClient = packet.ReadUInt32();
				var new302 = packet.ReadUInt32(); // NEW 0.0.2.8970
				var accName = packet.ReadCString();
				var unk322 = packet.ReadUInt32();

				client.ClientSeed = packet.ReadUInt32();

				var unk1 = packet.ReadUInt32(); // 3.3.5a
				var unk2 = packet.ReadUInt32(); // 3.3.5a
				var unk3 = packet.ReadUInt32(); // 3.3.5a
				var unk4 = packet.ReadUInt64();

				client.ClientDigest = packet.ReadBigInteger(20);

#if DEBUG
				log.Debug("builtNumberClient:{0} new302:{1} accName:{2} unk322:{3} client.ClientSeed:{4} unk4:{5} ClientDigest:{6}",
					builtNumberClient,
					new302, accName, unk322, client.ClientSeed, unk4, client.ClientDigest);
#endif

				var decompressedDataLength = packet.ReadInt32();
				var compressedData = packet.ReadBytes(packet.RemainingLength);
				client.Addons = new byte[decompressedDataLength];
				Compression.DecompressZLib(compressedData, client.Addons);

				var acctLoadTask = Message.Obtain(() => RealmAccount.InitializeAccount(client, accName));
				RealmServer.IOQueue.AddMessage(acctLoadTask);
			}
		}

		/// <summary>
		/// Send packet generated by the server to initialize authentification
		/// </summary>
		/// <param name="client">the client to send to</param>
		public static void SendAuthChallenge(IRealmClient client)
		{
			using (var packet = new RealmPacketOut(RealmServerOpCode.SMSG_AUTH_CHALLENGE))
			{
				packet.Write(1); // 1...31
				packet.Write(RealmServer.Instance.AuthSeed);

				// new 3.2.2 random data
				packet.WriteUInt(0xF3539DA3);
				packet.WriteUInt(0x6E8547B9);
				packet.WriteUInt(0x9A6AA2F8);
				packet.WriteUInt(0xA4F170F4);
				packet.WriteUInt(0xF3539DA3);
				packet.WriteUInt(0x6E8547B9);
				packet.WriteUInt(0x9A6AA2F8);
				packet.WriteUInt(0xA4F170F4);

				client.Send(packet);
			}
		}

		/// <summary>
		/// Sends an auth session error response to the client.
		/// </summary>
		/// <param name="client">the client to send to</param>
		/// <param name="error">the error code</param>
		public static void SendAuthSessionErrorReply(IPacketReceiver client, LoginErrorCode error)
		{
			using (var packet = new RealmPacketOut(RealmServerOpCode.SMSG_AUTH_RESPONSE, 1))
			{
				packet.WriteByte((byte)error);

				client.Send(packet);
			}
		}

		/// <summary>
		/// Sends an auth session success response to the client.
		/// </summary>
		/// <param name="client">the client to send to</param>
		public static void InviteToRealm(IRealmClient client)
		{
			var evt = AccountLogin;
			if (evt != null)
			{
				evt(client.Account);
			}
			SendAuthSuccessful(client);
		}

		public static void NotifyLogout(IRealmClient client)
		{
			var evt = ClientDisconnected;
			if (evt != null)
			{
				evt(client);
			}
		}

		public static void SendAuthSuccessful(IRealmClient client)
		{
			using (var packet = new RealmPacketOut(RealmServerOpCode.SMSG_AUTH_RESPONSE, 11))
			{
				packet.WriteByte((byte)LoginErrorCode.AUTH_OK);

				//BillingTimeRemaining
				packet.Write(0);

				packet.Write((byte)0x02);// BillingPlan Flags
				// 0x2, 0x4, 0x10 mutually exclusive. Order of Precedence: 0x2 > 0x4 > 0x10
				// 0x2 -> No Time left?
				// 0x20
				// 0x8

				// BillingTimeRested
				packet.Write(0);
				packet.Write((byte)client.Account.ClientId);

				client.Send(packet);
			}

			ClientAddonHandler.SendAddOnInfoPacket(client);

			RealmServer.Instance.OnClientAccepted(null, null);
		}

		/// <summary>
		/// Sends the number of currently queued clients.
		/// </summary>
		/// <param name="client">the client to send to</param>
		public static void SendAuthQueueStatus(IRealmClient client)
		{
			using (var packet = new RealmPacketOut(RealmServerOpCode.SMSG_AUTH_RESPONSE))
			{
				packet.WriteByte((byte)LoginErrorCode.AUTH_WAIT_QUEUE);
				packet.Write(AuthQueue.QueuedClients + 1);

				client.Send(packet);
			}
		}
		#endregion

		#region Login
		/// <summary>
		/// Handles an incoming player login request.
		/// </summary>
		/// <param name="client">the Session the incoming packet belongs to</param>
		/// <param name="packet">the full packet</param>        
		[ClientPacketHandler(RealmServerOpCode.CMSG_PLAYER_LOGIN, IsGamePacket = false, RequiresLogin = false)]
		public static void PlayerLoginRequest(IRealmClient client, RealmPacketIn packet)
		{
			if (client.Account == null || client.ActiveCharacter != null)
			{
				return;
			}

			var charLowId = packet.ReadEntityId().Low;

			try
			{
				var chr = World.GetCharacter(charLowId);

				if (chr != null)
				{
					if (!chr.IsLoggingOut)
					{
						// trying to connect to an already connected Character
						log.Error(WCell_RealmServer.CharacterAlreadyConnected, charLowId, client.Account.Name);

						SendCharacterLoginFail(client, LoginErrorCode.CHAR_LOGIN_DUPLICATE_CHARACTER);
					}
					else
					{
						chr.Map.AddMessage(new Message(() =>
						{
							if (!chr.IsInContext)
							{
								// Character was removed in the meantime -> Login again
								// enqueue task in IO-Queue to sync with Character.Save()
								RealmServer.IOQueue.AddMessage(
									new Message(() => LoginCharacter(client, charLowId)));
							}
							else
							{
								// reconnect Client with a logging out Character
								chr.ReconnectCharacter(client);
							}
						}));
					}
				}
				else
				{
					LoginCharacter(client, charLowId);
				}
			}
			catch (Exception e)
			{
				log.ErrorException(e);
				SendCharacterLoginFail(client, LoginErrorCode.CHAR_LOGIN_FAILED);
			}
		}

		/// <summary>
		/// Checks whether the client is allowed to login and -if so- logs it in
		/// </summary>
		/// <remarks>Executed in IO-Context.</remarks>
		/// <param name="client"></param>
		/// <param name="charLowId"></param>
		private static void LoginCharacter(IRealmClient client, uint charLowId)
		{
			var acc = client.Account;
			if (acc == null)
			{
				return;
			}

			var record = client.Account.GetCharacterRecord(charLowId);

			if (record == null)
			{
				log.Error(String.Format(WCell_RealmServer.CharacterNotFound, charLowId, acc.Name));

				SendCharacterLoginFail(client, LoginErrorCode.CHAR_LOGIN_NO_CHARACTER);
			}
			else if (record.CharacterFlags.HasAnyFlag(CharEnumFlags.NeedsRename | CharEnumFlags.LockedForBilling))
			{
				// TODO: Check in Char Enum?
				SendCharacterLoginFail(client, LoginErrorCode.AUTH_BILLING_EXPIRED);
			}
			else if (client.ActiveCharacter == null)
			{
				Character chr = null;
				try
				{
					var evt = BeforeLogin;
					if (evt != null)
					{
						record = evt(client, record);
						if (record == null)
						{
							throw new ArgumentNullException("record", "BeforeLogin returned null");
						}
					}
					chr = record.CreateCharacter();
					chr.Create(acc, record, client);
					chr.LoadAndLogin();

					var message = String.Format("Welcome to " + RealmServer.FormattedTitle);

					//chr.SendSystemMessage(message);
					MiscHandler.SendMotd(client, message);

					if (CharacterHandler.NotifyPlayerStatus)
					{
						World.Broadcast("{0} is now " + ChatUtility.Colorize("Online", Color.Green) + ".", chr.Name);
					}
				}
				catch (Exception ex)
				{
					LogUtil.ErrorException(ex, "Failed to load Character from Record: " + record);
					if (chr != null)
					{
						// Force client to relog
						chr.Dispose();
						client.Disconnect();
					}
				}
			}
		}

		/// <summary>
		/// Sends a "character login failed" error message to the client.
		/// </summary>
		/// <param name="client">the client to send to</param>
		/// <param name="error">the actual login error</param>
		public static void SendCharacterLoginFail(IPacketReceiver client, LoginErrorCode error)
		{
			using (var packet = new RealmPacketOut(RealmServerOpCode.SMSG_CHARACTER_LOGIN_FAILED, 1))
			{
				packet.WriteByte((byte)error);

				client.Send(packet);
			}
		}
		#endregion
	}
}