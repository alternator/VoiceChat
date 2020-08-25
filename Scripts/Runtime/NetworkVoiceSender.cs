using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Networking.Transport;
using UnityEngine;
using ICKX;
using ICKX.Radome;
using Unity.Collections;
using Unity.Jobs;

namespace ICKX.VoiceChat
{
	public enum VoiceMode
	{
		Default = 0,
		DirectionOnly,
		Virtual3D,
		End,
	}

	public class NetworkVoiceSender : SingletonBehaviour<NetworkVoiceSender>
	{

		//重複するなら書き換える
		public static byte VoiceSenderPacketType = 250;
		public static byte VoiceUpdatePacketType = 251;

		[SerializeField]
		private MicrophoneReciever _MicrophoneReciever;
		[SerializeField]
		private QosType _QosType = QosType.Unreliable;
		[SerializeField]
		private VoiceMode _SendVoiceMode;

		[SerializeField]
		private int _Bitrate = 96000;

		[Range(0.0f, 1.0f)]
		[SerializeField]
		private float _MaxVolume = 1.0f;

		[Tooltip("Virtual3Dモードのみ")]
		[SerializeField]
		private float _MaxDistance = 10.0f;

		public NativeList<ushort> TargetPlayerList;

		public MicrophoneReciever MicrophoneReciever { get { return _MicrophoneReciever; } set { _MicrophoneReciever = value; } }
		public VoiceMode SendVoiceMode { get { return _SendVoiceMode; } set { _SendVoiceMode = value; } }
		public QosType QosType { get { return _QosType; } set { _QosType = value; } }

		public Transform CacheTransform { get; private set; }

		private DataStreamWriter _SendVoicePacket;
		private UnityOpus.Encoder _Encoder;
		private byte[] _EncodeBuffer = new byte[ushort.MaxValue];

		private const int MaxSendVoiceTarget = 4;
		private const float UpdateInterval = 0.05f;
		private const float UpdatePosThreashold = 0.05f;
		private float _PrevUpdateTime;
		private Vector3 _PrevPos;
		
		protected override void Initialize()
		{
			base.Initialize();
			CacheTransform = transform;
			TargetPlayerList = new NativeList<ushort>(Allocator.Persistent);

			_SendVoicePacket = new DataStreamWriter(NetworkParameterConstants.MTU, Allocator.Persistent);

			UnityOpus.SamplingFrequency frequency;
			switch (MicrophoneReciever.SamplingFrequency)
			{
				case 48000: frequency = UnityOpus.SamplingFrequency.Frequency_48000; break;
				case 24000: frequency = UnityOpus.SamplingFrequency.Frequency_24000; break;
				case 16000: frequency = UnityOpus.SamplingFrequency.Frequency_16000; break;
				case 12000: frequency = UnityOpus.SamplingFrequency.Frequency_12000; break;
				case 8000: frequency = UnityOpus.SamplingFrequency.Frequency_8000; break;
				default: throw new NotSupportedException($"Frequency_{MicrophoneReciever.SamplingFrequency} is not supported");
			}

			_Encoder = new UnityOpus.Encoder(
				frequency, UnityOpus.NumChannels.Mono, UnityOpus.OpusApplication.VoIP)
			{
				Bitrate = _Bitrate,
				Complexity = 10,
				Signal = UnityOpus.OpusSignal.Voice
			};
			Debug.LogError($"Initialize");

		}

		void OnEnable()
		{
			MicrophoneReciever.OnUpdateMicData += OnUpdateMicData;
		}

		void OnDisable()
		{
			MicrophoneReciever.OnUpdateMicData -= OnUpdateMicData;
		}

		private void OnDestroy()
		{
			TargetPlayerList.Dispose();
			_SendVoicePacket.Dispose();
		}

		private void Update()
		{
			if(_SendVoiceMode != VoiceMode.Default
				&& Time.realtimeSinceStartup - _PrevUpdateTime > UpdateInterval
				&& Vector3.Distance(_PrevPos, CacheTransform.position) > UpdatePosThreashold)
			{
				SendUpdatePacket();
			}
		}

		private void SendUpdatePacket ()
		{
			Vector3 senderPosition = CacheTransform.position;
			_SendVoicePacket.Clear();
			_SendVoicePacket.Write(VoiceUpdatePacketType);
			_SendVoicePacket.Write(GamePacketManager.PlayerId);
			_SendVoicePacket.Write(GamePacketManager.CurrentUnixTime);
			_SendVoicePacket.Write(senderPosition);

			GamePacketManager.Brodcast( _SendVoicePacket, QosType.Unreliable);
		}

		//Updateのタイミングで呼ばれる
		private void OnUpdateMicData(float[] readOnlyData, int rawLength, int samplingFrequency)
		{
			if (GamePacketManager.NetworkManager == null || GamePacketManager.NetworkManager.NetworkState != NetworkConnection.State.Connected)
			{
				return;
			}

			Vector3 senderPosition = CacheTransform.position;

			int dataSize = _Encoder.Encode(readOnlyData, rawLength, _EncodeBuffer);

			//Debug.LogWarning($"dataSize {dataSize}, rawLength {rawLength}");
			if (dataSize > 1024 || dataSize <= 0)
			{
				Debug.LogWarning("Frame Drop and MicData Lost.");
				Debug.LogWarning(string.Join(",", new System.ArraySegment<float>(readOnlyData, 0, rawLength)));
				return;
			}

			_SendVoicePacket.Clear();
			_SendVoicePacket.Write(VoiceSenderPacketType);
			_SendVoicePacket.Write((byte)SendVoiceMode);
			_SendVoicePacket.Write(GamePacketManager.PlayerId);
			_SendVoicePacket.Write((ushort)_MicrophoneReciever.SamplingFrequency);
			_SendVoicePacket.Write(_MaxVolume);

			switch (SendVoiceMode)
			{
				case VoiceMode.Default:
					break;
				case VoiceMode.DirectionOnly:
					_SendVoicePacket.Write(senderPosition);
					break;
				case VoiceMode.Virtual3D:
					_SendVoicePacket.Write(senderPosition);
					_SendVoicePacket.Write(_MaxDistance);
					break;
			}
			_SendVoicePacket.Write(GamePacketManager.CurrentUnixTime);
			_SendVoicePacket.Write((ushort)rawLength);
			_SendVoicePacket.Write((ushort)dataSize);
			_SendVoicePacket.Write(_EncodeBuffer, dataSize);

			//Debug.Log($"{rawLength} {dataSize} {GamePacketManager.CurrentUnixTime}");

			if(SendVoiceMode != VoiceMode.Virtual3D)
			{
				if (TargetPlayerList.Length == 0)
				{
					GamePacketManager.Brodcast(_SendVoicePacket, _QosType, true);
				}
				else
				{
					GamePacketManager.Multicast(TargetPlayerList, _SendVoicePacket, _QosType);
				}
			}
			else
			{
				//GamePacketManager.Brodcast(_SendVoicePacket, _QosType, true);
				//return;

				TargetPlayerList.Clear();

				//対象範囲内のみ送信
				foreach (var sourceA in NetworkVoiceReciever.Instance.NetworkVoiceSourceTable.Values)
				{
					//Debug.Log(TargetPlayerList.Length + " : PlayerId " + sourceA.PlayerId);
					if (sourceA.PlayerId == GamePacketManager.PlayerId) continue;

					if (TargetPlayerList.Length == 0)
					{
						var distanceA = Vector3.SqrMagnitude(sourceA.CacheTransform.position - CacheTransform.position);
						if (distanceA < _MaxDistance * _MaxDistance)
						{
							TargetPlayerList.Add(sourceA.PlayerId);
						}
					}
					else
					{
						//最寄りのMaxSendVoiceTargetの数まで選んで音声を送信する
						for (int i = TargetPlayerList.Length - 1; i >= 0; i--)
						{
							ushort playerIdB = TargetPlayerList[i];
							if (playerIdB == sourceA.PlayerId) continue;

							var distanceA = Vector3.SqrMagnitude(sourceA.CacheTransform.position - CacheTransform.position);
							if (distanceA > _MaxDistance * _MaxDistance) break;

							if (NetworkVoiceReciever.Instance.NetworkVoiceSourceTable.TryGetValue(playerIdB, out var sourceB))
							{
								var distanceB = Vector3.SqrMagnitude(sourceB.CacheTransform.position - CacheTransform.position);

								//遠ければ並び替え終了
								if (distanceA > distanceB)
								{
									//最後の要素かどうか
									if (i == TargetPlayerList.Length - 1 && i < MaxSendVoiceTarget - 1)
									{
										TargetPlayerList.Add(sourceA.PlayerId);
									}
									break;
								}
								else
								{
									if (i == TargetPlayerList.Length - 1 && i < MaxSendVoiceTarget - 1)
									{
										TargetPlayerList.Add(sourceB.PlayerId);
									}
									else if (i < TargetPlayerList.Length - 1)
									{
										TargetPlayerList[i + 1] = TargetPlayerList[i];
									}
									TargetPlayerList[i] = sourceA.PlayerId;
								}
							}
						}
					}
				}

				if(TargetPlayerList.Length > 0)
				{
					GamePacketManager.Multicast(TargetPlayerList, _SendVoicePacket, _QosType);
					//Debug.Log($"NetworkVoiceSourceTable {TargetPlayerList.Length}");
				}
			}
		}
	}
}
