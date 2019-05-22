using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using ICKX;
using ICKX.Radome;
using Unity.Networking.Transport;
using Unity.Collections;
using Unity.Jobs;

namespace ICKX.VoiceChat {

//	[RequireComponent (typeof (AudioSource))]
	public class NetworkVoiceReciever : ManagerBase<NetworkVoiceReciever> {

		private Dictionary<ushort, NetworkVoiceSource> _NetworkVoiceSourceList;
		//public AudioSource CacheAudioSource { get; private set; }

		protected override void Initialize () {
			base.Initialize ();
			_NetworkVoiceSourceList = new Dictionary<ushort, NetworkVoiceSource> ();
			//CacheAudioSource = GetComponent<AudioSource> ();
			//CacheAudioSource.loop = true;
			//CacheAudioSource.spatialBlend = 0.0f;
			//if(!CacheAudioSource.isPlaying) CacheAudioSource.Play ();
		}

		void OnEnable () {
			GamePacketManager.OnRecievePacket += OnRecievePacket;
		}

		void OnDisable () {
			GamePacketManager.OnRecievePacket -= OnRecievePacket;
		}

		private void Update () {
			JobHandle.ScheduleBatchedJobs ();
		}

		//updateの前に呼ばれる
		private void OnRecievePacket (ushort senderPlayerId, ulong senderUniqueId, byte type, DataStreamReader stream, DataStreamReader.Context ctx) {
			//Debug.Log ("OnRecievePacket : " + type);
			if (type == NetworkVoiceSender.VoiceSenderPacketType) {
				var mode = (VoiceMode)stream.ReadByte (ref ctx);
				ushort playerId = stream.ReadUShort (ref ctx);
				ushort samplingFrequency = stream.ReadUShort (ref ctx);
				byte bitDepthCompressionLevel = stream.ReadByte (ref ctx);
				float maxVolume = stream.ReadFloat (ref ctx);

				Vector3 senderPosition = default;
				switch (mode) {
					case VoiceMode.Default:
						break;
					case VoiceMode.DirectionOnly:
					case VoiceMode.Virtual3D:
						senderPosition = stream.ReadVector3 (ref ctx);
						break;
				}
				ushort dataCount = stream.ReadUShort (ref ctx);

				if (!_NetworkVoiceSourceList.TryGetValue(playerId, out NetworkVoiceSource source)) {
					source = CreateNetworkVoiceSource (playerId);
					_NetworkVoiceSourceList[playerId] = source;
					Debug.Log ($"CreateNetworkVoiceSource : {playerId} : {bitDepthCompressionLevel}");
				}

				source.CacheTransform.position = senderPosition;
				source.OnRecievePacket (mode, dataCount, samplingFrequency, bitDepthCompressionLevel, maxVolume, stream, ctx);
			}
		}

		private NetworkVoiceSource CreateNetworkVoiceSource (ushort playerId) {
			var source = new GameObject ("NetworkVoiceSource " + playerId).AddComponent<NetworkVoiceSource> ();
			source.Initialize (playerId);
			return source;
		}
	}
}
