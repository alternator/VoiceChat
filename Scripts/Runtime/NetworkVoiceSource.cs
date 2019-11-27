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

namespace ICKX.VoiceChat
{

	//	[RequireComponent(typeof(AudioSource))]
	public class NetworkVoiceSource : MonoBehaviour
	{

		[SerializeField]
		private float _BufferingTime = 0.5f;

		public ushort PlayerId { get; private set; }

		private byte[] _DecodeBuffer;
		private float[] _DecodePcm;

		private float[] _FilterVoiceBuffer;
		private float[] _CopyBuffer;
		private int _FilterVoiceBufferLastPos;

		public VoiceMode Mode { get; private set; } = VoiceMode.End;

		public ushort SamplingFrequency { get; private set; }
		public float MaxVolume { get; private set; }
		public Transform CacheTransform { get; private set; }
		public AudioSource CacheAudioSource { get; private set; }

		private UnityOpus.Decoder _Decoder;
		private int outputSampleRate = 0;
		private float listerAngleSin;

		private bool _IsBuffring = true;

		public void Initialize(ushort playerId)
		{
			CacheTransform = transform;
			CacheAudioSource = gameObject.AddComponent<AudioSource>();
			CacheAudioSource.loop = true;

			_DecodeBuffer = new byte[1024];
			_DecodePcm = new float[2048];
			_FilterVoiceBuffer = new float[4096];
			_CopyBuffer = new float[1024];

			DontDestroyOnLoad(gameObject);
		}

		//updateの前に呼ばれる
		internal unsafe void OnRecievePacket(VoiceMode mode, ushort dataCount
				, ushort samplingFrequency, float maxVolume, DataStreamReader stream, DataStreamReader.Context ctx)
		{

			if (SamplingFrequency != samplingFrequency)
			{
				SamplingFrequency = samplingFrequency;

				if (_Decoder != null) _Decoder.Dispose();

				UnityOpus.SamplingFrequency frequency;
				switch (samplingFrequency)
				{
					case 48000: frequency = UnityOpus.SamplingFrequency.Frequency_48000; break;
					case 24000: frequency = UnityOpus.SamplingFrequency.Frequency_24000; break;
					case 16000: frequency = UnityOpus.SamplingFrequency.Frequency_16000; break;
					case 12000: frequency = UnityOpus.SamplingFrequency.Frequency_12000; break;
					case 8000: frequency = UnityOpus.SamplingFrequency.Frequency_8000; break;
					default: throw new System.NotSupportedException($"Frequency_{samplingFrequency} is not supported");
				}
				_Decoder = new UnityOpus.Decoder(frequency, UnityOpus.NumChannels.Mono);
			}
			if (MaxVolume != maxVolume)
			{
				MaxVolume = maxVolume;
			}

			if (Mode != mode)
			{
				switch (mode)
				{
					case VoiceMode.Default:
						CacheAudioSource.spatialBlend = 0.0f;
						break;
					case VoiceMode.DirectionOnly:
						CacheAudioSource.spatialBlend = 0.0f;
						break;
					case VoiceMode.Virtual3D:
						throw new System.NotFiniteNumberException();
						//CacheAudioSource.spatialBlend = 1.0f;
						//break;
				}
				if (!CacheAudioSource.isPlaying) CacheAudioSource.Play();
				Mode = mode;
			}

			int dataSize = stream.ReadUShort(ref ctx);
			stream.ReadBytesIntoArray(ref ctx, ref _DecodeBuffer, dataSize);
			int size = _Decoder.Decode(_DecodeBuffer, dataSize, _DecodePcm);

			if (size < 0) return;

			lock (_FilterVoiceBuffer)
			{
				while (_FilterVoiceBuffer.Length < _FilterVoiceBufferLastPos + size)
				{
					System.Array.Resize(ref _FilterVoiceBuffer, _FilterVoiceBuffer.Length * 2);
				}
				System.Array.Copy(_DecodePcm, 0, _FilterVoiceBuffer, _FilterVoiceBufferLastPos, size);
				_FilterVoiceBufferLastPos += size;
			}
		}

		private void LateUpdate()
		{
			outputSampleRate = AudioSettings.outputSampleRate;

			var listerTrans = NetworkVoiceListener.Instance.CacheTransform;
			var localPos = listerTrans.InverseTransformPoint(CacheTransform.position);
			localPos.Normalize();
			listerAngleSin = localPos.x;
		}

		//VoiceState.DirectionOnlyなら左右の音量のシミュレーションを独自に行う
		private void OnAudioFilterRead(float[] data, int channels)
		{
			//if (Mode != VoiceMode.DirectionOnly) return;
			if (channels > 2)
			{
				throw new System.NotSupportedException();
			}

			lock (_FilterVoiceBuffer)
			{
				if (_FilterVoiceBufferLastPos == 0)
				{
					_IsBuffring = true;
					return;
				}

				if (_IsBuffring && _FilterVoiceBufferLastPos < _BufferingTime * SamplingFrequency * 0.02f)
				{
					return;
				}
				_IsBuffring = false;

				int dataSize = data.Length / channels;
				float sampleRate = (float)SamplingFrequency / outputSampleRate;
				int useRecieveVoiceDataSize = (int)((dataSize - 1) * sampleRate) + 1;

				for (int i = 0; i < dataSize; i++)
				{
					if (channels == 1)
					{
						if ((int)(i * sampleRate) < _FilterVoiceBufferLastPos)
						{
							data[i] = _FilterVoiceBuffer[(int)(i * sampleRate)];
						}
						else
						{
							data[i] = 0.0f;
						}
					}
					else
					{
						if ((int)(i * sampleRate) < _FilterVoiceBufferLastPos)
						{
							data[i * 2] = _FilterVoiceBuffer[(int)(i * sampleRate)];
						}
						else
						{
							data[i * 2] = 0.0f;
						}
						data[i * 2 + 1] = data[i * 2];
						if (Mode == VoiceMode.DirectionOnly)
						{
							data[i * 2] *= Mathf.Clamp01(1.0f - listerAngleSin);
							data[i * 2 + 1] *= Mathf.Clamp01(1.0f + listerAngleSin);
						}
					}
				}

				while (_CopyBuffer.Length < useRecieveVoiceDataSize)
				{
					System.Array.Resize(ref _CopyBuffer, _CopyBuffer.Length * 2);
				}

				for (int i = 0; i < _FilterVoiceBufferLastPos; i += useRecieveVoiceDataSize)
				{
					if (_FilterVoiceBuffer.Length >= i + useRecieveVoiceDataSize * 2)
					{
						System.Array.Copy(_FilterVoiceBuffer, useRecieveVoiceDataSize + i, _CopyBuffer, 0, useRecieveVoiceDataSize);
						System.Array.Copy(_CopyBuffer, 0, _FilterVoiceBuffer, i, useRecieveVoiceDataSize);
					}
				}

				_FilterVoiceBufferLastPos -= useRecieveVoiceDataSize;
				if (_FilterVoiceBufferLastPos < 0) _FilterVoiceBufferLastPos = 0;
			}
		}
	}
}