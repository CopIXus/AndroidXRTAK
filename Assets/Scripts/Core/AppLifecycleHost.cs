using System.Collections;
using TakXr.Cot;
using TakXr.UI;
using UnityEngine;

namespace TakXr.Core
{
    /// <summary>
    /// Pause/resume reconnect for Direct + Marti + self presence when the
    /// headset app loses/gains focus (Android XR common).
    /// </summary>
    public class AppLifecycleHost : MonoBehaviour
    {
        TakDirectHub _direct;
        TakLayersService _layers;
        SelfPresence _self;
        XrChromeHud _chrome;
        bool _wasPaused;
        bool _reconnectBusy;

        public void Configure(
            TakDirectHub direct,
            TakLayersService layers,
            SelfPresence self,
            XrChromeHud chrome)
        {
            _direct = direct;
            _layers = layers;
            _self = self;
            _chrome = chrome;
        }

        void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus)
            {
                _wasPaused = true;
                TakXrStateStore.Capture();
                _self?.Pause();
                // Soft-stop reads so sockets don't sit half-open across suspend.
                _direct?.StopClient();
            }
            else if (_wasPaused)
            {
                _wasPaused = false;
                if (!_reconnectBusy) StartCoroutine(ReconnectAfterResume());
            }
        }

        void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus && _wasPaused && !_reconnectBusy)
            {
                _wasPaused = false;
                StartCoroutine(ReconnectAfterResume());
            }
        }

        IEnumerator ReconnectAfterResume()
        {
            _reconnectBusy = true;
            _chrome?.FlashStatus("Reconnecting…");
            _layers?.RebindMartiHost();
            if (_direct != null)
                yield return _direct.RestartClientRoutine();
            else
                yield return new WaitForSecondsRealtime(0.4f);
            _self?.Resume();
            _self?.PublishOnce();
            _chrome?.FlashStatus("Resumed");
            _reconnectBusy = false;
        }
    }
}
