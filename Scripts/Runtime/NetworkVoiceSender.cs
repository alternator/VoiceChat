using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Networking.Transport;
using UnityEngine;
using ICKX;
using ICKX.Radome;
using Unity.Collections;
using Unity.Jobs;

namespace ICKX.VoiceChat {

	public static class MuLawCompression {

		/// <summary>
		/// μ-Lawで圧縮
		/// </summary>
		/// <param name="val">対象の値</param>
		/// <param name="mu">値の範囲</param>
		/// <param name="alpha"> 1.0f / Mathf.Log (mu + 1) * mu を事前に計算しておいて代入</param>
		/// <returns></returns>
		public static float MuLaw (float value, float mu, float alpha) {
			float signVal = Mathf.Sign (value);
			float absVal = Mathf.Abs (value);
			float n = signVal * Mathf.Log (1 + mu * absVal) * alpha;
			return n;
		}

		/// <summary>
		/// μ-Lawを解凍
		/// </summary>
		/// <param name="val">対象の値</param>
		/// <param name="mu">値の範囲</param>
		/// <param name="invMu"> 1.0f / mu を事前に計算しておいて代入</param>
		/// <returns></returns>
		public static float InvMuLaw (float val, float mu, float invMu) {
			float sign = Mathf.Sign (val);
			float absVal = Mathf.Abs (val);
			float f = absVal * invMu;
			float s = sign * invMu * (Mathf.Pow (1 + mu, f) - 1);
			return s;
		}
	}

	public enum VoiceMode {
		Default = 0,
		DirectionOnly,
		Virtual3D,
		End,
	}

	public class NetworkVoiceSender : SingletonBehaviour<NetworkVoiceSender> {

		//重複するなら書き換える
		public static byte VoiceSenderPacketType = 250;

		[SerializeField]
		private MicrophoneReciever _MicrophoneReciever;

		[SerializeField]
		private VoiceMode _SendVoiceMode;

		[Tooltip ("ビット深度を何分の1まで圧縮するかどうか")]
		[Range (0, 2)]
		[SerializeField]
		private int _BitDepthCompressionLevel = 2;
		[Range (0.0f, 1.0f)]
		[SerializeField]
		private float _MaxVolume = 1.0f;

		public NativeList<ushort> TargetPlayerList { get; set; }

		public MicrophoneReciever MicrophoneReciever { get { return _MicrophoneReciever; } set { _MicrophoneReciever = value; } }
		public VoiceMode SendVoiceMode { get { return _SendVoiceMode; } set { _SendVoiceMode = value; } }
		public int BitDepthCompressionLevel { get { return _BitDepthCompressionLevel; } set { _BitDepthCompressionLevel = value; } }

		public Transform cacheTransform { get; private set; }

		private NativeArray<float> rawVoiceData;
		private DataStreamWriter sendVoicePacket;

		private JobHandle compressJobHandle;

		protected override void Initialize () {
			base.Initialize ();
			cacheTransform = transform;
			TargetPlayerList = new NativeList<ushort> (Allocator.Persistent);
		}

		void OnEnable () {
			MicrophoneReciever.OnUpdateMicData += OnUpdateMicData;
		}

		void OnDisable () {
			MicrophoneReciever.OnUpdateMicData -= OnUpdateMicData;
		}

		private void OnDestroy () {
			TargetPlayerList.Dispose ();
		}

		//Updateのタイミングで呼ばれる
		private void OnUpdateMicData (float[] readOnlyData, int length, int samplingFrequency) {

			if(GamePacketManager.NetworkManager == null || GamePacketManager.NetworkManager.state != NetworkManagerBase.State.Online) {
				return;
			}

			Vector3 senderPosition = cacheTransform.position;

			int packetLen = 15;
			switch (SendVoiceMode) {
				case VoiceMode.Default:
					break;
				case VoiceMode.DirectionOnly:
				case VoiceMode.Virtual3D:
					packetLen += 12;
					break;
			}

			ushort dataLen = (ushort)length;
			int dataSize = (4 / (int)Mathf.Pow (2, _BitDepthCompressionLevel)) * dataLen;

			if (dataSize + packetLen > NetworkParameterConstants.MTU) {
				//Debug.LogWarning ("Voiceデータが大きすぎるため送れないデータがあります \n " +
				//	"CompressionLevelを大きくするか,マイク入力のサンプル数を小さくしてください");
				dataLen = (ushort)(250 * Mathf.Pow (2, _BitDepthCompressionLevel));
				dataSize = 1000;
			}
			packetLen += dataSize;

			sendVoicePacket = new DataStreamWriter (packetLen, Allocator.TempJob);
			sendVoicePacket.Write (VoiceSenderPacketType);
			sendVoicePacket.Write ((byte)SendVoiceMode);
			sendVoicePacket.Write (GamePacketManager.PlayerId);
			sendVoicePacket.Write ((ushort)_MicrophoneReciever.SamplingFrequency);
			sendVoicePacket.Write ((byte)_BitDepthCompressionLevel);
			sendVoicePacket.Write (_MaxVolume);
			switch (SendVoiceMode) {
				case VoiceMode.Default:
					break;
				case VoiceMode.DirectionOnly:
				case VoiceMode.Virtual3D:
					sendVoicePacket.Write (senderPosition);
					break;
			}
			sendVoicePacket.Write (dataLen);

			//Debug.Log ($"{SendVoiceMode} : {length} : {_MaxVolume} : {_BitDepthCompressionLevel}");

			rawVoiceData = new NativeArray<float> (readOnlyData, Allocator.TempJob);

			var compressJob = new CompressJob () {
				bitDepthCompressionLevel = _BitDepthCompressionLevel,
				maxVolume = _MaxVolume,
				rawVoiceData = rawVoiceData,
				rawVoiceDataLength = dataLen,
				sendVoicePacket = sendVoicePacket,
			};
			compressJobHandle = compressJob.Schedule ();
			JobHandle.ScheduleBatchedJobs ();
		}

		
		private void LateUpdate () {
			compressJobHandle.Complete ();

			if (sendVoicePacket.IsCreated && sendVoicePacket.Length != 0) {
				if(TargetPlayerList.Length == 0) {
					//Debug.Log ("sendVoicePacket : " + sendVoicePacket.Length);
					GamePacketManager.Brodcast (sendVoicePacket, QosType.Unreliable, true);
				}else {
					GamePacketManager.Multicast (TargetPlayerList, sendVoicePacket, QosType.Unreliable);
				}
				sendVoicePacket.Dispose ();
				rawVoiceData.Dispose ();
			}
		}

		struct CompressJob : IJob {
			public int bitDepthCompressionLevel;
			public float maxVolume;

			public NativeArray<float> rawVoiceData;
			public int rawVoiceDataLength;
			public DataStreamWriter sendVoicePacket;

			public void Execute () {
				float invMaxVolume = 1.0f / maxVolume;
				switch (bitDepthCompressionLevel) {
					case 0:
						for (int i = 0; i < rawVoiceDataLength; i++) {
							float value = Mathf.Clamp (rawVoiceData[i] * invMaxVolume, -1.0f, 1.0f);
							sendVoicePacket.Write (value);
						}
						break;
					case 1:
						float alphaShort = 1.0f / Mathf.Log (short.MaxValue) * (short.MaxValue - 1);
						for (int i = 0; i < rawVoiceDataLength; i++) {
							float value = Mathf.Clamp (rawVoiceData[i] * invMaxVolume, -1.0f, 1.0f);
							sendVoicePacket.Write ((short)MuLawCompression.MuLaw (value, short.MaxValue - 1, alphaShort));
						}
						break;
					case 2:
						float alphaByte = 1.0f / Mathf.Log (128) * 127;
						for (int i = 0; i < rawVoiceDataLength; i++) {
							float value = Mathf.Clamp (rawVoiceData[i] * invMaxVolume, -1.0f, 1.0f);
							sendVoicePacket.Write ((byte)(MuLawCompression.MuLaw (value, 127, alphaByte) + 127));
						}
						break;
					default:
						throw new System.NotImplementedException ();
				}
			}
		}
	}
}
