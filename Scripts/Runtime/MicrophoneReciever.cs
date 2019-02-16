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
		private int _SamplingFrequency = 22050;
		[SerializeField]
		private int _ProcessBufferSize = 1024;

		private float[] _ProcessBuffer;
		private float[] _SubProcessBuffer;
		private AudioClip _MicrophoneClip;

		private float[] _MicAverageLog; 

		private int _PrevPosition;

		public int SamplingFrequency { get { return _SamplingFrequency; } }

		public event OnRecieveMicDataEvent OnUpdateMicData = null;

		void Start () {

			_MicrophoneClip = Microphone.Start (null, true, 1, _SamplingFrequency);

			//44100x1sで880, 22050x1sで440ほどPositionが移動する.ので50Hzぐらいで更新している様子
			//フレーム落ちで2倍の量移動することがあるので、それでも記録できる程度に
			_ProcessBuffer = new float[_ProcessBufferSize];
			_SubProcessBuffer = new float[_ProcessBufferSize];
			_MicAverageLog = new float[_MicSuspendFrame];
		}

		void Update () {
			var position = Microphone.GetPosition (null);
			if (position < 0 || _PrevPosition > SamplingFrequency || position > SamplingFrequency) {
				_PrevPosition = position;
				return;
			}
			if (Mathf.Abs (_PrevPosition - position) > _SamplingFrequency / 30) {
				_PrevPosition = position;
				return;
			}

			int length = 0;
			if (position == _PrevPosition) {
				//何もしない
				return;
			} else if (position > _PrevPosition) {
				length = position - _PrevPosition;
				if (length > _ProcessBuffer.Length) {
					Debug.Log ("Resize _ProcessBuffer : " + length);
					System.Array.Resize (ref _ProcessBuffer, length);
					//length = _ProcessBuffer.Length;
					//_PrevPosition = position - _ProcessBuffer.Length;
				}
				_MicrophoneClip.GetData (_ProcessBuffer, _PrevPosition);
			} else {
				length = position + (SamplingFrequency - _PrevPosition);
				if (length > _ProcessBuffer.Length) {
					Debug.Log ("Resize _ProcessBuffer : " + length);
					System.Array.Resize (ref _ProcessBuffer, length);
					//length = _ProcessBuffer.Length;
					//_PrevPosition = position - _ProcessBuffer.Length;
					//if(_PrevPosition < 0)_PrevPosition += SamplingFrequency;
				}
				//未処理のデータが_clipの後半に格納されているので、分割して読み込む
				_MicrophoneClip.GetData (_ProcessBuffer, _PrevPosition);
				if (_PrevPosition > position) {
					_MicrophoneClip.GetData (_SubProcessBuffer, 0);
					System.Array.Copy (_SubProcessBuffer, 0, _ProcessBuffer, (SamplingFrequency - _PrevPosition), position);
				}
			}

			float ave = 0.0f;
			for (int i=0;i<_ProcessBuffer.Length;i++) {
				ave += Mathf.Abs( _ProcessBuffer[i]);
			}
			ave /= _ProcessBuffer.Length;

			for (int i = _MicAverageLog.Length - 1; i > 0; i--) {
				_MicAverageLog[i] = _MicAverageLog[i-1];
			}
			_MicAverageLog[0] = ave;

			if (_MicAverageLog.Any(a=>a > _MicThreshold)) {
				OnUpdateMicData?.Invoke (_ProcessBuffer, length, _SamplingFrequency);
			}

			_PrevPosition = position;
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