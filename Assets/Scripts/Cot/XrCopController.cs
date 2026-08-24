using TakXr.Locomotion;
using TakXr.UI;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.Cot
{
    /// <summary>
    /// CoT select → radial menu → Details / Video / Follow / R&amp;B / Delete.
    /// Trigger click uses controller aim, with camera-forward fallback when pose
    /// aim is unavailable.
    /// </summary>
    public class XrCopController : MonoBehaviour
    {
        [SerializeField] CotLayerController cotLayer;
        [SerializeField] CotFeedClient feed;
        [SerializeField] Transform cameraTransform;
        [SerializeField] XrWorldLocomotion locomotion;
        [SerializeField] XrInfoPanel infoPanel;
        [SerializeField] XrVideoPanel videoPanel;
        [SerializeField] XrFollowController follow;
        [SerializeField] XrRadialMenu radialMenu;

        float _nextSelectTime;
        readonly bool[] _wasGrabbing = new bool[2];
        readonly float[] _grabStart = new float[2];
        readonly float[] _grabMoved = new float[2];
        readonly Vector3[] _grabLast = new Vector3[2];
        string _lastSelectedUid;

        /// <summary>Last CoT opened via select (for Tools → Follow).</summary>
        public string LastSelectedUid => _lastSelectedUid;

        public void Configure(
            CotLayerController layer,
            CotFeedClient feedClient,
            Transform cam,
            XrWorldLocomotion loco,
            XrInfoPanel info,
            XrVideoPanel video,
            XrFollowController followCtrl,
            XrRadialMenu radial = null)
        {
            cotLayer = layer;
            feed = feedClient;
            cameraTransform = cam;
            locomotion = loco;
            infoPanel = info;
            videoPanel = video;
            follow = followCtrl;
            radialMenu = radial;

            if (infoPanel != null)
            {
                infoPanel.FollowToggled += OnFollowToggled;
                infoPanel.VideoRequested += OnVideoRequested;
                infoPanel.GoToRequested += OnGoToRequested;
            }
            if (feed != null)
                feed.Changed += OnFeedChanged;
        }

        void OnDestroy()
        {
            if (infoPanel != null)
            {
                infoPanel.FollowToggled -= OnFollowToggled;
                infoPanel.VideoRequested -= OnVideoRequested;
                infoPanel.GoToRequested -= OnGoToRequested;
            }
            if (feed != null) feed.Changed -= OnFeedChanged;
        }

        void Update()
        {
            if (cameraTransform == null && Camera.main != null)
                cameraTransform = Camera.main.transform;

            infoPanel?.PlaceFacing(cameraTransform);
            videoPanel?.PlaceFacing(cameraTransform);

            PollSelectGestures();
        }

        void PollSelectGestures()
        {
            // Draw / measure / elevation tools own pinches while armed.
            if (XrDrawTool.IsArmed || XrRangeMeasureTool.IsArmed || XrElevationTool.IsArmed)
            {
                for (int h = 0; h < 2; h++) _wasGrabbing[h] = XrHandPinchInput.IsGrabbing(h);
                return;
            }

            for (int h = 0; h < 2; h++)
            {
                bool tracked = XrHandPinchInput.TryGetPose(h, out var tip, out _);
                // Allow trigger select even if pose briefly drops — use camera as tip.
                bool grabbing = XrHandPinchInput.IsGrabbing(h) && !XrHandPinchInput.IsUiBlocking(h);
                if (!tracked && grabbing && cameraTransform != null)
                    tip = cameraTransform.position + cameraTransform.forward * 0.3f;

                bool rising = grabbing && !_wasGrabbing[h];

                if (grabbing)
                {
                    if (rising)
                    {
                        _grabStart[h] = Time.unscaledTime;
                        _grabMoved[h] = 0f;
                        _grabLast[h] = tip;
                        // Trigger click: select if aim (or gaze) hits a CoT/panel.
                        if (Time.unscaledTime >= _nextSelectTime && TrySelectHand(h, tip, requireHit: true))
                            _nextSelectTime = Time.unscaledTime + 0.45f;
                    }
                    else if (tracked)
                    {
                        _grabMoved[h] += Vector3.Distance(tip, _grabLast[h]);
                        _grabLast[h] = tip;
                    }
                }
                else if (_wasGrabbing[h])
                {
                    float elapsed = Time.unscaledTime - _grabStart[h];
                    if (elapsed < 0.4f && _grabMoved[h] < 0.05f &&
                        Time.unscaledTime >= _nextSelectTime)
                    {
                        TrySelectHand(h, tip, requireHit: false);
                        _nextSelectTime = Time.unscaledTime + 0.35f;
                    }
                }

                _wasGrabbing[h] = grabbing;
            }
        }

        bool TrySelectHand(int handIndex, Vector3 tip, bool requireHit)
        {
            if (!TryBuildSelectRay(handIndex, tip, out var ray))
                return false;

            if (infoPanel != null && infoPanel.IsVisible)
            {
                if (infoPanel.HandleProximitySelect(tip)) return true;
                if (infoPanel.HandleRaySelect(ray)) return true;
            }
            if (videoPanel != null && videoPanel.IsVisible && videoPanel.HandleRaySelect(ray))
                return true;
            // Radial menu consumes selects aimed at it (its slices fire from here on
            // the gaze-fallback path; the hand-aim path shares a click debounce).
            if (radialMenu != null && radialMenu.IsVisible && radialMenu.HandleRaySelect(ray))
                return true;

            // Layers panel handles its own clicks — don't select CoTs through it.
            if (Physics.Raycast(ray, out var uiHit, 8f, ~0, QueryTriggerInteraction.Collide) &&
                uiHit.transform.GetComponentInParent<XrLayersPanel>() != null)
                return true;

            if (cotLayer != null && cotLayer.TryRaycastSelect(ray, 8000f, out var uid))
            {
                OpenCot(uid);
                return true;
            }

            // Soft sphere pick — helps when markers are small on the terrain.
            if (cotLayer != null && cotLayer.TrySphereSelect(ray, 8000f, 25f, out uid))
            {
                OpenCot(uid);
                return true;
            }

            if (requireHit) return false;

            if (Physics.Raycast(ray, out var hit, 8f, ~0, QueryTriggerInteraction.Collide))
            {
                if (hit.transform.name.StartsWith("Btn_") ||
                    hit.transform.GetComponentInParent<XrChromeHud>() != null)
                    return false;
            }
            infoPanel?.Hide();
            radialMenu?.Hide();
            return false;
        }

        bool TryBuildSelectRay(int handIndex, Vector3 tip, out Ray ray)
        {
            if (XrHandPinchInput.TryGetAim(handIndex, out var origin, out var forward))
            {
                ray = new Ray(origin, forward);
                return true;
            }
            if (cameraTransform != null)
            {
                // Gaze + trigger fallback when controller aim pose is missing.
                ray = new Ray(cameraTransform.position, cameraTransform.forward);
                return true;
            }
            ray = new Ray(tip, Vector3.forward);
            return false;
        }

        void OpenCot(string uid)
        {
            if (feed == null || !feed.Cots.TryGetValue(uid, out var cot) || cot == null)
                return;
            _lastSelectedUid = uid;
            // ATAK-style: tap opens the radial coin menu; Details slice routes to the
            // info panel. Fall back to the panel when no radial menu is wired.
            if (radialMenu != null)
            {
                infoPanel?.Hide();
                radialMenu.Open(cot);
            }
            else
            {
                bool following = follow != null && follow.FollowUid == uid;
                infoPanel?.Show(cot, following, cameraTransform);
            }
        }

        void OnFollowToggled(string uid, bool followOn)
        {
            follow?.SetFollow(followOn ? uid : null);
            infoPanel?.SetFollowing(followOn && follow?.FollowUid == uid);
        }

        void OnVideoRequested(NormalizedCot cot)
        {
            videoPanel?.Show(cot, cameraTransform);
        }

        void OnGoToRequested(NormalizedCot cot)
        {
            if (cot?.point == null || locomotion == null) return;
            if (cotLayer != null && cotLayer.TryGetMarkerWorldPos(cot.uid, out var pos))
                locomotion.FrameWorldPoint(pos);
            else
                locomotion.ResetView();
        }

        void OnFeedChanged()
        {
            if (infoPanel == null || !infoPanel.IsVisible || feed == null) return;
            var uid = infoPanel.CurrentUid;
            if (uid != null && feed.Cots.TryGetValue(uid, out var cot))
                infoPanel.Refresh(cot);
        }
    }
}
