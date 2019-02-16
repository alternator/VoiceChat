using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ICKX.VoiceChat {

	public class MicrophoneVisualizer : MonoBehaviour {
		[SerializeField]
		MicrophoneReciever reciever = null;

		Transform[] gauge;

		void Start () {
			reciever.OnUpdateMicData += OnUpdateMicData;
			gauge = new Transform[reciever.SamplingFrequency / 50];
			for (int i = 0; i < gauge.Length; i++) {
				gauge[i] = GameObject.CreatePrimitive (PrimitiveType.Quad).transform;
				gauge[i].transform.position = Vector3.right * i;
				gauge[i].transform.SetParent (transform);
			}
		}

		int _PrevPosition = 0;

		private void OnUpdateMicData (float[] readOnlyData, int length, int samplingFrequency) {
			for (int i = 0; i < readOnlyData.Length / 50; i++) {
				//gauge[i].transform.localScale = new Vector3 (1.0f, microphoneBuffer[(microphoneBuffer.Length / gauge.Length) * i] * 100.0f, 1.0f);
				if ((_PrevPosition / 50 + i) < gauge.Length) {
					gauge[_PrevPosition / 50 + i].transform.localScale = new Vector3 (1.0f, readOnlyData[i * 50] * 1000.0f, 1.0f);
				} else {
					gauge[(_PrevPosition / 50 + i) - gauge.Length].transform.localScale = new Vector3 (1.0f, readOnlyData[i * 50] * 1000.0f, 1.0f);
				}
			}
			_PrevPosition += length;
			if (_PrevPosition > reciever.SamplingFrequency) _PrevPosition -= reciever.SamplingFrequency;
		}
	}
}