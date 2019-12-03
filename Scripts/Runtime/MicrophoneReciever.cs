using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;

namespace ICKX.VoiceChat {

	public class MicrophoneReciever : MonoBehaviour {

		public delegate void OnRecieveMicDataEvent (float[] readOnlyData, int length, int samplingFrequency);

		private const int MicLengthSeconds = 1;

		[SerializeField]
		private float _MicThreshold = 0.05f;
		[SerializeField]
		private int _MicSuspendFrame = 10;
		[SerializeField]
		private int _SamplingFrequency = 24000;
		[SerializeField]
		private int _ProcessBufferSize = 512;

		private float[] _ProcessBuffer;
		private float[] _MicrophoneBuffer;
		private float[] _MicAverageLog;

		private AudioClip _MicrophoneClip = null;

		private int _HeadPos = 0;

		public int SamplingFrequency { get { return _SamplingFrequency; } }

		public event OnRecieveMicDataEvent OnUpdateMicData = null;

		IEnumerator Start ()
        {
            while (Microphone.devices == null || Microphone.devices.Length == 0)
            {
                Debug.Log($"Microphone null");
                yield return new WaitForSeconds(1.0f);
            }

            _MicrophoneClip = Microphone.Start (null, true, 1, _SamplingFrequency);

			//44100x1sで880, 22050x1sで440ほどPositionが移動する.ので50Hzぐらいで更新している様子
			//フレーム落ちで2倍の量移動することがあるので、それでも記録できる程度に
			_ProcessBuffer = new float[_ProcessBufferSize];
			_MicrophoneBuffer = new float[SamplingFrequency * MicLengthSeconds];
			_MicAverageLog = new float[_MicSuspendFrame];

            yield return new WaitForSeconds(1.0f);


            foreach (var dev in Microphone.devices)
            {
                Microphone.GetDeviceCaps(dev, out int min, out int max);
                Debug.Log($"Microphone {dev}, min={min} max={max} IsRecording={Microphone.IsRecording(dev)}");
            }
        }

        void Update ()
        {
            if (_MicrophoneClip == null) return;
			
			var position = Microphone.GetPosition(null);
			if (position < 0 || _HeadPos == position)
			{
				return;
			}

			_MicrophoneClip.GetData(_MicrophoneBuffer, 0);
			while (GetDataLength(_MicrophoneBuffer.Length, _HeadPos, position) > _ProcessBuffer.Length)
			{
				var remain = _MicrophoneBuffer.Length - _HeadPos;
				if (remain < _ProcessBuffer.Length)
				{
					System.Array.Copy(_MicrophoneBuffer, _HeadPos, _ProcessBuffer, 0, remain);
					System.Array.Copy(_MicrophoneBuffer, 0, _ProcessBuffer, remain, _ProcessBuffer.Length - remain);
				}
				else
				{
					System.Array.Copy(_MicrophoneBuffer, _HeadPos, _ProcessBuffer, 0, _ProcessBuffer.Length);
				}

				float ave = 0.0f;
				for (int i = 0; i < _ProcessBuffer.Length; i += 5)
				{
					ave += Mathf.Abs(_ProcessBuffer[i]);
				}
				ave /= (_ProcessBuffer.Length / 5);

				for (int i = _MicAverageLog.Length - 1; i > 0; i--)
				{
					_MicAverageLog[i] = _MicAverageLog[i - 1];
				}
				_MicAverageLog[0] = ave;

				//Debug.Log("Mic Ave Value : " + ave);
				bool isAny = false;
				for (int i = 0; i < _MicAverageLog.Length; i++)
				{
					if (_MicAverageLog[i] > _MicThreshold)
					{
						isAny = true;
					}
				}

				if (isAny)
				{
					OnUpdateMicData?.Invoke(_ProcessBuffer, _ProcessBuffer.Length, _SamplingFrequency);
				}

				_HeadPos += _ProcessBuffer.Length;
				if (_HeadPos > _MicrophoneBuffer.Length)
				{
					_HeadPos -= _MicrophoneBuffer.Length;
				}
			}
		}

		public float GetRMS()
		{
			float sum = 0.0f;
			foreach (var sample in _ProcessBuffer)
			{
				sum += sample * sample;
			}
			return Mathf.Sqrt(sum / _ProcessBuffer.Length);
		}

		static int GetDataLength (int bufferLength, int head, int tail) {
			if (head < tail) {
				return tail - head;
			} else {
				return bufferLength - head + tail;
			}
		}
	}
}