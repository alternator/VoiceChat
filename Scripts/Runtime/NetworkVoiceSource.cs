using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using ICKX.Radome;
using Unity.Networking.Transport;
using Unity.Jobs;
using Unity.Collections;
using Unity.Networking.Transport.LowLevel.Unsafe;
using System.Linq;
using System.Threading.Tasks;

namespace ICKX.VoiceChat {

	//	[RequireComponent(typeof(AudioSource))]
	public class NetworkVoiceSource : MonoBehaviour {

		public ushort PlayerId { get; private set; }

		private float[] _RecieveVoiceBuffer;
		private float[] _FilterVoiceBuffer;
		private float[] _CopyBuffer;
		private int _RecieveVoiceBufferLastPos;
		private int _FilterVoiceBufferLastPos;

		public VoiceMode Mode { get; private set; } = VoiceMode.End;

		public ushort SamplingFrequency { get; private set; }
		public byte BitDepthCompressionLevel { get; private set; }
		public Transform CacheTransform { get; private set; }
		public AudioSource CacheAudioSource { get; private set; }

		private List<DecommpressData> dataList;
		private Task task = null;
		private bool isWriting = false;
		private int outputSampleRate = 0;
		private float listerAngleSin;

		public void Initialize (ushort playerId) {
			CacheTransform = transform;
			CacheAudioSource = gameObject.AddComponent<AudioSource> ();
			CacheAudioSource.loop = true;

			_RecieveVoiceBuffer = new float[2048];
			_FilterVoiceBuffer = new float[4096];
			_CopyBuffer = new float[1024];
			
			dataList = new List<DecommpressData> ();
			DontDestroyOnLoad (gameObject);
		}

		private void OnDestroy () {
			for (int i = 0; i < dataList.Count; i++) {
				if(dataList[i].StreamCopy.IsCreated) {
					dataList[i].StreamCopy.Dispose ();
				}
			}
		}

		//updateの前に呼ばれる
		internal void OnRecievePacket (VoiceMode mode, ushort dataCount
				, ushort samplingFrequency, byte compressionLevel, DataStreamReader stream, DataStreamReader.Context ctx) {

			if (SamplingFrequency != samplingFrequency) {
				SamplingFrequency = samplingFrequency;
				_RecieveVoiceBufferLastPos = 0;
			}
			if (BitDepthCompressionLevel != compressionLevel) {
				BitDepthCompressionLevel = compressionLevel;
				_RecieveVoiceBufferLastPos = 0;
			}

			if (Mode != mode) {
				switch (mode) {
					case VoiceMode.Default:
						CacheAudioSource.spatialBlend = 0.0f;
						break;
					case VoiceMode.DirectionOnly:
						CacheAudioSource.spatialBlend = 0.0f;
						break;
					case VoiceMode.Virtual3D:
						throw new System.NotFiniteNumberException ();
						//CacheAudioSource.spatialBlend = 1.0f;
						//break;
				}
				if (!CacheAudioSource.isPlaying) CacheAudioSource.Play ();
				Mode = mode;
			}

			var streamCopy = new DataStreamWriter (stream.Length, Allocator.Persistent);
			unsafe {
				streamCopy.WriteBytes (stream.GetUnsafeReadOnlyPtr (), stream.Length);
			}
			var data = new DecommpressData () {
				StreamCopy = streamCopy,
				Ctx = ctx,
				DataCount = dataCount,
			};
			dataList.Add (data);
		}

		struct DecommpressData {
			public DataStreamWriter StreamCopy;
			public DataStreamReader.Context Ctx;
			public ushort DataCount;
		}
		
		private Task DecommpressJob (List<DecommpressData> dataList) {
			return Task.Run (() => {
				foreach (var data in dataList) {
					if (!data.StreamCopy.IsCreated) continue;

					if (_RecieveVoiceBufferLastPos + data.DataCount + 1024 > _RecieveVoiceBuffer.Length) {
						System.Array.Resize (ref _RecieveVoiceBuffer, _RecieveVoiceBuffer.Length * 2);
					}

					var reader = new DataStreamReader (data.StreamCopy, 0, data.StreamCopy.Length);
					var ctx = data.Ctx;
					switch (BitDepthCompressionLevel) {
						case 0:
							for (int i = 0; i < data.DataCount; i++) {
								_RecieveVoiceBufferLastPos++;
								_RecieveVoiceBuffer[_RecieveVoiceBufferLastPos] = (reader.ReadFloat (ref ctx));
							}
							break;
						case 1:
							float invShort = 1.0f / (short.MaxValue - 1);
							for (int i = 0; i < data.DataCount; i++) {
								_RecieveVoiceBufferLastPos++;
								_RecieveVoiceBuffer[_RecieveVoiceBufferLastPos] 
									= (MuLawCompression.InvMuLaw (reader.ReadShort (ref ctx), short.MaxValue - 1, invShort));
							}
							break;
						case 2:
							float invByte = 1.0f / 127;
							for (int i = 0; i < data.DataCount; i++) {
								_RecieveVoiceBufferLastPos++;
								_RecieveVoiceBuffer[_RecieveVoiceBufferLastPos]
									= (MuLawCompression.InvMuLaw (((short)reader.ReadByte (ref ctx) - 127), 127, invByte));
							}
							break;
						default:
							throw new System.NotImplementedException ();
					}
				}
			});
		}

		private void Update () {
			outputSampleRate = AudioSettings.outputSampleRate;
			task = null;
			if (dataList.Count > 0) {
				task = DecommpressJob (dataList);
			}
		}

		private void LateUpdate () {
			if (task != null) {
				task.Wait ();

				var listerTrans = NetworkVoiceListener.Instance.CacheTransform;
				var localPos = listerTrans.InverseTransformPoint (CacheTransform.position);
				localPos.Normalize ();
				listerAngleSin = localPos.x;

				for (int i = 0; i < dataList.Count; i++) {
					dataList[i].StreamCopy.Dispose ();
				}
				dataList.Clear ();

				isWriting = true;
				if (_FilterVoiceBuffer.Length <= _RecieveVoiceBufferLastPos + _FilterVoiceBufferLastPos) {
					System.Array.Resize (ref _FilterVoiceBuffer, _FilterVoiceBuffer.Length * 2);
				}
				System.Array.Copy (_RecieveVoiceBuffer, 0, _FilterVoiceBuffer, _FilterVoiceBufferLastPos, _RecieveVoiceBufferLastPos);
				_FilterVoiceBufferLastPos += _RecieveVoiceBufferLastPos;
				_RecieveVoiceBufferLastPos = 0;
				isWriting = false;
			}
		}

		//VoiceState.DirectionOnlyなら左右の音量のシミュレーションを独自に行う
		private void OnAudioFilterRead (float[] data, int channels) {
			//if (Mode != VoiceMode.DirectionOnly) return;
			if(channels > 2) {
				throw new System.NotSupportedException ();
			}

			if (isWriting || _FilterVoiceBufferLastPos == 0) {
				return;
			}

			int dataSize = data.Length / channels;
			float sampleRate = (float)SamplingFrequency / outputSampleRate;
			int useRecieveVoiceDataSize = (int)((dataSize - 1) * sampleRate) + 1;

			for (int i = 0; i < dataSize; i++) {
				if(channels == 1) {
					if ((int)(i * sampleRate) < _FilterVoiceBufferLastPos) {
						data[i] = _FilterVoiceBuffer[(int)(i * sampleRate)];
					} else {
						data[i] = 0.0f;
					}
				} else {
					if ((int)(i * sampleRate) < _FilterVoiceBufferLastPos) {
						data[i * 2] = _FilterVoiceBuffer[(int)(i * sampleRate)];
					} else {
						data[i * 2] = 0.0f;
					}
					data[i * 2 + 1] = data[i * 2];
					if (Mode == VoiceMode.DirectionOnly) {
						data[i * 2] *= Mathf.Clamp01 (1.0f - listerAngleSin);
						data[i * 2 + 1] *= Mathf.Clamp01 (1.0f + listerAngleSin);
					}
				}
			}

			if (_CopyBuffer.Length < useRecieveVoiceDataSize) {
				System.Array.Resize (ref _CopyBuffer, _CopyBuffer.Length * 2);
			}

			for (int i = 0; i < _FilterVoiceBufferLastPos; i += useRecieveVoiceDataSize) {
				System.Array.Copy (_FilterVoiceBuffer, useRecieveVoiceDataSize + i, _CopyBuffer, 0, useRecieveVoiceDataSize);
				System.Array.Copy (_CopyBuffer, 0, _FilterVoiceBuffer, i, useRecieveVoiceDataSize);
			}

			_FilterVoiceBufferLastPos -= useRecieveVoiceDataSize;
			if (_FilterVoiceBufferLastPos < 0) _FilterVoiceBufferLastPos = 0;
		}
	}
}