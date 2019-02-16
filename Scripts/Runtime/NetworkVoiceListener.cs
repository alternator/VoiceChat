using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using ICKX;
using ICKX.Radome;

namespace ICKX.VoiceChat {

	//TODO
	//VoiceMode.Virtual3D以外は複数のAudioSource使わないで、
	//Lister側で単純に合成して1つのAudioSourceから再生するほうが低コスト？
	public class NetworkVoiceListener : SingletonBehaviour<NetworkVoiceListener> {

		public Transform CacheTransform { get; private set; }

		protected override void Initialize () {
			base.Initialize ();
			CacheTransform = transform;
		}
	}
}