using System;
using System.Collections.Generic;
using TakXr.Core;
using TakXr.Cot;
using TakXr.Map;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// ATAK Contacts &amp; GeoChat (`b-t-f`) spatial panel:
    /// - CONTACTS tab: lists live team members and units with team color badge,
    ///   distance/bearing from viewer, GoTo, Follow, and Direct Chat buttons.
    /// - GEOCHAT tab: displays incoming/outgoing `b-t-f` chat history, room selector
    ///   (All Chat Rooms or direct callsign), 1-tap tactical quick replies, and
    ///   custom keyboard message entry via <see cref="XrKeyboardPanel"/>.
    /// </summary>
    [DefaultExecutionOrder(60)]
    public class XrChatContactsPanel : MonoBehaviour
    {
        const float PanelW = 1.08f;
        const float PanelH = 0.78f;
        const int ContactsPerPage = 6;
        const int MessagesShown = 7;

        static readonly string[] QuickMessages =
        {
            "ROGER / WILCO",
            "POSREP - ALL CLEAR",
            "HOLD POSITION",
            "MOVING TO OBJECTIVE",
            "EYES ON TARGET",
            "RTB / COMPLETE",
        };

        AppConfig _config;
        CotFeedClient _feed;
        CotLayerController _cotLayer;
        TakDirectHub _direct;
        XrFollowController _follow;
        XrWorldRoot _world;
        XrKeyboardPanel _keyboard;
        Transform _cam;
        Action<string> _flash;

        Transform _root;
        Transform _contentRoot;
        bool _chatTab; // false = Contacts, true = GeoChat
        string _activeChatroom = "All Chat Rooms";
        string _targetUid = "All Chat Rooms";
        int _contactPage;
        int _unreadCount;

        readonly List<(Transform root, Action onClick)> _hitTargets = new List<(Transform, Action)>();
        readonly bool[] _wasGrabbing = new bool[2];
        readonly bool[] _blockedByMe = new bool[2];
        float _nextClickTime;

        public bool IsVisible => _root != null && _root.gameObject.activeSelf;
        public int UnreadCount => _unreadCount;

        public static XrChatContactsPanel Create()
        {
            var go = new GameObject("XrChatContactsPanel");
            return go.AddComponent<XrChatContactsPanel>();
        }

        public void Configure(
            AppConfig config,
            CotFeedClient feed,
            CotLayerController cotLayer,
            TakDirectHub direct,
            XrFollowController follow,
            XrWorldRoot world,
            XrKeyboardPanel keyboard,
            Transform cam,
            Action<string> flash)
        {
            _config = config;
            if (_feed != null) _feed.ChatReceived -= OnChatReceived;
            _feed = feed;
            if (_feed != null) _feed.ChatReceived += OnChatReceived;
            _cotLayer = cotLayer;
            _direct = direct;
            _follow = follow;
            _world = world;
            _keyboard = keyboard;
            _cam = cam;
            _flash = flash;
        }

        void OnDestroy()
        {
            if (_feed != null) _feed.ChatReceived -= OnChatReceived;
        }

        void OnChatReceived(NormalizedCot chatCot)
        {
            if (chatCot == null) return;
            string sender = chatCot.detail?.chat?.senderCallsign ?? chatCot.Callsign ?? "TAK";
            string msg = chatCot.detail?.remarks ?? "";
            if (!IsVisible) _unreadCount++;
            _flash?.Invoke($"CHAT [{sender}]: {msg}");
            if (IsVisible && _chatTab) RebuildUi();
        }

        void Awake()
        {
            _root = new GameObject("Root").transform;
            _root.SetParent(transform, false);
            _root.gameObject.SetActive(false);

            Quad("Bg", _root, new Vector3(0f, 0f, 0.01f), new Vector2(PanelW, PanelH),
                new Color(0.04f, 0.08f, 0.14f, 0.95f), 2990);
            // Subtle cyan top accent bar
            Quad("TopBar", _root, new Vector3(0f, PanelH / 2f - 0.008f, 0.005f),
                new Vector2(PanelW, 0.014f), new Color(0.15f, 0.78f, 0.98f, 0.95f), 2995);

            _contentRoot = new GameObject("Content").transform;
            _contentRoot.SetParent(_root, false);
        }

        public void ShowContacts(Transform cam = null)
        {
            if (cam != null) _cam = cam;
            _chatTab = false;
            _root.gameObject.SetActive(true);
            PlaceFacing();
            RebuildUi();
        }

        public void ShowChat(Transform cam = null, string chatroom = null, string targetUid = null)
        {
            if (cam != null) _cam = cam;
            _chatTab = true;
            _unreadCount = 0;
            if (!string.IsNullOrEmpty(chatroom)) _activeChatroom = chatroom;
            if (!string.IsNullOrEmpty(targetUid)) _targetUid = targetUid;
            _root.gameObject.SetActive(true);
            PlaceFacing();
            RebuildUi();
        }

        public void Toggle(Transform cam = null, bool openChat = false)
        {
            if (IsVisible) { Hide(); return; }
            if (openChat) ShowChat(cam);
            else ShowContacts(cam);
        }

        public void Hide()
        {
            if (_root != null) _root.gameObject.SetActive(false);
            for (int h = 0; h < 2; h++)
            {
                if (_blockedByMe[h]) XrHandPinchInput.SetUiBlocking(h, false);
                _blockedByMe[h] = false;
            }
        }

        void PlaceFacing()
        {
            if (_cam == null && Camera.main != null) _cam = Camera.main.transform;
            if (_cam == null) return;
            var camPos = _cam.position;
            var flat = _cam.forward;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
            flat.Normalize();
            _root.position = camPos + flat * 1.65f + Vector3.up * -0.02f;
            _root.rotation = XrUiFacing.RotationFacingUser(_root.position, camPos);
        }

        void RebuildUi()
        {
            foreach (Transform child in _contentRoot) Destroy(child.gameObject);
            _hitTargets.Clear();

            // Header + Tab Bar
            Text("Title", _contentRoot, new Vector3(-PanelW / 2f + 0.045f, PanelH / 2f - 0.048f, -0.01f),
                _chatTab ? $"GEOCHAT  ·  {_activeChatroom}" : "TACTICAL CONTACTS & TEAM",
                0.0088f, new Color(0.88f, 0.96f, 1f, 1f), TextAnchor.MiddleLeft, TextAlignment.Left);

            AddButton("TabContacts", new Vector3(0.11f, PanelH / 2f - 0.048f, -0.01f),
                new Vector2(0.19f, 0.048f), "Contacts", !_chatTab, () =>
                {
                    _chatTab = false;
                    RebuildUi();
                });

            string chatLbl = _unreadCount > 0 ? $"GeoChat ({_unreadCount})" : "GeoChat";
            AddButton("TabChat", new Vector3(0.32f, PanelH / 2f - 0.048f, -0.01f),
                new Vector2(0.19f, 0.048f), chatLbl, _chatTab, () =>
                {
                    _chatTab = true;
                    _unreadCount = 0;
                    RebuildUi();
                });

            AddButton("Close", new Vector3(PanelW / 2f - 0.055f, PanelH / 2f - 0.048f, -0.01f),
                new Vector2(0.068f, 0.048f), "X", false, Hide);

            if (_chatTab) BuildChatView();
            else BuildContactsView();
        }

        void BuildContactsView()
        {
            var list = CollectContacts();
            int totalPages = Mathf.Max(1, Mathf.CeilToInt(list.Count / (float)ContactsPerPage));
            _contactPage = Mathf.Clamp(_contactPage, 0, totalPages - 1);

            float y = PanelH / 2f - 0.115f;
            Text("Sub", _contentRoot, new Vector3(-PanelW / 2f + 0.045f, y, -0.01f),
                $"Active TAK Contacts: {list.Count}   (Page {_contactPage + 1}/{totalPages})",
                0.0068f, new Color(0.65f, 0.82f, 0.95f, 0.95f), TextAnchor.MiddleLeft, TextAlignment.Left);

            AddButton("AllChatBtn", new Vector3(0.32f, y, -0.01f), new Vector2(0.32f, 0.044f),
                "Broadcast GeoChat (All)", true, () =>
                {
                    _activeChatroom = "All Chat Rooms";
                    _targetUid = "All Chat Rooms";
                    _chatTab = true;
                    RebuildUi();
                });

            y -= 0.062f;

            if (list.Count == 0)
            {
                Text("Empty", _contentRoot, new Vector3(0f, -0.02f, -0.01f),
                    "No active team or unit contacts in feed yet.\nConnect to a TAK server or spawn demo units in Overlays.",
                    0.0075f, new Color(0.75f, 0.85f, 0.95f, 0.85f));
                return;
            }

            int start = _contactPage * ContactsPerPage;
            int end = Mathf.Min(list.Count, start + ContactsPerPage);
            for (int i = start; i < end; i++)
            {
                var c = list[i];
                Quad("Row_" + i, _contentRoot, new Vector3(0f, y, 0.002f),
                    new Vector2(PanelW - 0.07f, 0.068f), new Color(0.07f, 0.13f, 0.21f, 0.92f), 2996);

                // Team/affiliation color badge
                var dotCol = CotMarkerView.GetAffiliationOrTeamColor(c);
                Quad("Badge_" + i, _contentRoot, new Vector3(-PanelW / 2f + 0.065f, y, -0.004f),
                    new Vector2(0.028f, 0.042f), dotCol, 3001);

                string teamStr = !string.IsNullOrEmpty(c.detail?.team?.name)
                    ? $"[{c.detail.team.name}] "
                    : "";
                string distBrg = FormatDistanceBearing(c);
                Text("Cs_" + i, _contentRoot, new Vector3(-PanelW / 2f + 0.095f, y + 0.011f, -0.01f),
                    $"{c.Callsign}", 0.0076f, Color.white, TextAnchor.MiddleLeft, TextAlignment.Left);
                Text("Meta_" + i, _contentRoot, new Vector3(-PanelW / 2f + 0.095f, y - 0.014f, -0.01f),
                    $"{teamStr}{c.type}  ·  {distBrg}", 0.0058f,
                    new Color(0.68f, 0.84f, 0.96f, 0.92f), TextAnchor.MiddleLeft, TextAlignment.Left);

                var captured = c;
                AddButton("GoTo_" + i, new Vector3(0.08f, y, -0.01f), new Vector2(0.13f, 0.048f),
                    "Go To", false, () => GoToContact(captured));
                bool isFollowing = _follow != null && _follow.FollowUid == captured.uid;
                AddButton("Fol_" + i, new Vector3(0.23f, y, -0.01f), new Vector2(0.14f, 0.048f),
                    isFollowing ? "Unfollow" : "Follow", isFollowing, () =>
                    {
                        _follow?.SetFollow(isFollowing ? null : captured.uid);
                        RebuildUi();
                    });
                AddButton("Chat_" + i, new Vector3(0.39f, y, -0.01f), new Vector2(0.16f, 0.048f),
                    "Message", true, () =>
                    {
                        _activeChatroom = captured.Callsign ?? captured.uid;
                        _targetUid = captured.uid;
                        _chatTab = true;
                        RebuildUi();
                    });

                y -= 0.078f;
            }

            // Pagination controls
            float footY = -PanelH / 2f + 0.052f;
            AddButton("Prev", new Vector3(-0.18f, footY, -0.01f), new Vector2(0.18f, 0.048f),
                "< Prev", false, () => { _contactPage = Mathf.Max(0, _contactPage - 1); RebuildUi(); });
            AddButton("Next", new Vector3(0.18f, footY, -0.01f), new Vector2(0.18f, 0.048f),
                "Next >", false, () => { _contactPage = Mathf.Min(totalPages - 1, _contactPage + 1); RebuildUi(); });
        }

        void BuildChatView()
        {
            float y = PanelH / 2f - 0.115f;
            Text("RoomInfo", _contentRoot, new Vector3(-PanelW / 2f + 0.045f, y, -0.01f),
                $"Room: {_activeChatroom}   (CoT b-t-f over TAK TLS)",
                0.0066f, new Color(0.65f, 0.85f, 1f, 0.95f), TextAnchor.MiddleLeft, TextAlignment.Left);

            AddButton("SwitchAll", new Vector3(0.16f, y, -0.01f), new Vector2(0.20f, 0.044f),
                "All Rooms", _activeChatroom == "All Chat Rooms", () =>
                {
                    _activeChatroom = "All Chat Rooms";
                    _targetUid = "All Chat Rooms";
                    RebuildUi();
                });

            AddButton("TypeCustom", new Vector3(0.39f, y, -0.01f), new Vector2(0.22f, 0.044f),
                "Keyboard Msg...", true, OpenCustomMessageKeyboard);

            // Message log box
            float logH = 0.31f;
            float logCenterY = 0.06f;
            Quad("LogBg", _contentRoot, new Vector3(0f, logCenterY, 0.003f),
                new Vector2(PanelW - 0.07f, logH), new Color(0.02f, 0.05f, 0.09f, 0.96f), 2996);

            var history = _feed != null ? _feed.ChatHistory : null;
            if (history == null || history.Count == 0)
            {
                Text("NoMsg", _contentRoot, new Vector3(0f, logCenterY, -0.01f),
                    "No GeoChat messages yet. Tap a Quick Tactical Preset below or 'Keyboard Msg...' to transmit.",
                    0.0066f, new Color(0.65f, 0.78f, 0.90f, 0.85f));
            }
            else
            {
                int start = Mathf.Max(0, history.Count - MessagesShown);
                float msgY = logCenterY + logH / 2f - 0.03f;
                for (int i = start; i < history.Count; i++)
                {
                    var m = history[i];
                    string sender = m.detail?.chat?.senderCallsign ?? m.Callsign ?? "TAK";
                    string room = m.detail?.chat?.chatroom ?? "All";
                    string txt = m.detail?.remarks ?? "";
                    if (txt.Length > 58) txt = txt.Substring(0, 58) + "…";
                    bool isMe = string.Equals(sender, TakIdentity.Callsign, StringComparison.OrdinalIgnoreCase);
                    Color lineCol = isMe
                        ? new Color(0.45f, 0.95f, 0.65f, 0.98f)
                        : new Color(0.92f, 0.96f, 1f, 0.98f);
                    Text("Msg_" + i, _contentRoot, new Vector3(-PanelW / 2f + 0.06f, msgY, -0.01f),
                        $"[{room}] {sender}: {txt}", 0.0062f, lineCol,
                        TextAnchor.MiddleLeft, TextAlignment.Left);
                    msgY -= 0.040f;
                }
            }

            // Quick tactical reply presets (2 rows x 3 cols)
            float qTop = -0.145f;
            Text("QuickHdr", _contentRoot, new Vector3(-PanelW / 2f + 0.045f, qTop, -0.01f),
                "QUICK TACTICAL TRANSMIT (1-PINCH SEND):", 0.0062f,
                new Color(0.70f, 0.88f, 1f, 0.92f), TextAnchor.MiddleLeft, TextAlignment.Left);

            float rowY = qTop - 0.052f;
            const int cols = 3;
            float colW = 0.31f;
            float pitchX = 0.33f;
            for (int i = 0; i < QuickMessages.Length; i++)
            {
                int r = i / cols;
                int c = i % cols;
                float x = (c - 1) * pitchX;
                float yy = rowY - r * 0.064f;
                string preset = QuickMessages[i];
                AddButton("Preset_" + i, new Vector3(x, yy, -0.01f),
                    new Vector2(colW, 0.052f), preset, false, () => SendGeoChatMessage(preset));
            }
        }

        void OpenCustomMessageKeyboard()
        {
            if (_keyboard == null)
            {
                _flash?.Invoke("Keyboard unavailable");
                return;
            }
            _keyboard.Open(_cam, $"GEOCHAT TO {_activeChatroom}", "", text =>
            {
                if (!string.IsNullOrWhiteSpace(text))
                    SendGeoChatMessage(text.Trim());
            }, 64);
        }

        public void SendGeoChatMessage(string messageText)
        {
            if (string.IsNullOrWhiteSpace(messageText)) return;
            var now = DateTime.UtcNow;
            string msgId = Guid.NewGuid().ToString();
            string senderUid = TakIdentity.ClientUid;
            string senderCs = TakIdentity.Callsign;

            double lat = _config != null ? _config.originLat : 0.0;
            double lon = _config != null ? _config.originLon : 0.0;
            float hae = _config != null ? (float)_config.originAlt : 0f;

            var cot = new NormalizedCot
            {
                uid = $"GeoChat.{senderUid}.{_activeChatroom}.{msgId}",
                type = "b-t-f",
                how = "h-g-i-g-o",
                time = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                start = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                stale = now.AddHours(2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                point = new CotPoint { lat = lat, lon = lon, hae = hae, ce = 9999999f, le = 9999999f },
                contact = new CotContact { callsign = senderCs },
                detail = new CotDetail
                {
                    remarks = messageText,
                    chat = new CotChat
                    {
                        id = _targetUid,
                        chatroom = _activeChatroom,
                        senderCallsign = senderCs,
                        messageId = msgId,
                        parent = "RootContactGroup",
                        groupOwner = "false",
                    },
                },
            };

            string xml = CotXmlBuilder.Build(cot);
            bool sent = _direct != null && _direct.SendCot(xml);
            _feed?.UpsertDirect(cot);
            _flash?.Invoke($"GeoChat → {_activeChatroom}: \"{messageText}\"\n{(sent ? "Sent over TAK TLS" : "Saved locally (TAK stream offline)")}");
            if (IsVisible && _chatTab) RebuildUi();
        }

        List<NormalizedCot> CollectContacts()
        {
            var result = new List<NormalizedCot>();
            if (_feed == null) return result;
            foreach (var c in _feed.Cots.Values)
            {
                if (c == null || string.IsNullOrEmpty(c.uid) || c.IsChatMessage) continue;
                var kind = CotClassifier.Classify(c);
                bool isContact = kind == MarkerKind.TeamMember ||
                                 kind == MarkerKind.Observer ||
                                 kind == MarkerKind.AircraftFixed ||
                                 kind == MarkerKind.AircraftRotary ||
                                 (!string.IsNullOrEmpty(c.type) && c.type.StartsWith("a-"));
                if (isContact) result.Add(c);
            }
            result.Sort((a, b) => string.Compare(a.Callsign, b.Callsign, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        string FormatDistanceBearing(NormalizedCot c)
        {
            if (c?.point == null || _cotLayer == null || _cam == null || _world == null) return "";
            var enu = GeoMath.GeodeticToEnu(new GeoMath.Geodetic(c.point.lat, c.point.lon, 0), _cotLayer.Origin);
            var localPos = GeoMath.EnuToUnity(enu);
            var viewerLocal = _world.Root.InverseTransformPoint(_cam.position);
            float dx = localPos.x - viewerLocal.x;
            float dz = localPos.z - viewerLocal.z;
            float distM = Mathf.Sqrt(dx * dx + dz * dz);
            float brg = (Mathf.Atan2(dx, dz) * Mathf.Rad2Deg + 360f) % 360f;
            string dStr = distM >= 1000f ? $"{distM / 1000f:0.0} km" : $"{Mathf.RoundToInt(distM)} m";
            return $"{dStr} @ {brg:000}°";
        }

        void GoToContact(NormalizedCot c)
        {
            if (c == null || _cotLayer == null || _world == null || _cam == null) return;
            if (_cotLayer.TryGetMarkerWorldPos(c.uid, out var pos))
            {
                var flat = _cam.forward; flat.y = 0f;
                if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
                flat.Normalize();
                var desired = _cam.position + flat * 160f + Vector3.down * 50f;
                _world.Root.position += desired - pos;
                _flash?.Invoke($"Jumped to {c.Callsign}");
                Hide();
            }
        }

        void Update()
        {
            if (!IsVisible) return;
            for (int h = 0; h < 2; h++)
            {
                bool aimOk = XrHandPinchInput.TryGetAim(h, out var origin, out var fwd);
                bool grabbing = aimOk && XrHandPinchInput.IsGrabbing(h);
                bool rising = grabbing && !_wasGrabbing[h];
                _wasGrabbing[h] = grabbing;

                bool pointingAtUi = false;
                if (aimOk)
                {
                    var hits = Physics.RaycastAll(origin, fwd, 8f, ~0, QueryTriggerInteraction.Collide);
                    Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                    foreach (var hit in hits)
                    {
                        foreach (var (root, onClick) in _hitTargets)
                        {
                            if (root == null) continue;
                            if (hit.transform != root && !hit.transform.IsChildOf(root)) continue;
                            pointingAtUi = true;
                            if (rising && Time.unscaledTime >= _nextClickTime)
                            {
                                _nextClickTime = Time.unscaledTime + 0.28f;
                                onClick?.Invoke();
                            }
                            break;
                        }
                        if (pointingAtUi) break;
                    }
                }

                bool block = pointingAtUi && grabbing;
                if (block)
                {
                    XrHandPinchInput.SetUiBlocking(h, true);
                    _blockedByMe[h] = true;
                }
                else if (_blockedByMe[h])
                {
                    XrHandPinchInput.SetUiBlocking(h, false);
                    _blockedByMe[h] = false;
                }
                if (!IsVisible) return;
            }
        }

        void AddButton(string name, Vector3 pos, Vector2 size, string label, bool active, Action onClick)
        {
            var root = new GameObject("Btn_" + name).transform;
            root.SetParent(_contentRoot, false);
            root.localPosition = pos;
            Color bg = active
                ? new Color(0.14f, 0.46f, 0.72f, 0.97f)
                : new Color(0.10f, 0.19f, 0.30f, 0.95f);
            Quad("Bg", root, Vector3.zero, size, bg, 3000);
            Text("Lbl", root, new Vector3(0f, 0f, -0.005f), label, 0.0062f, Color.white);
            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(size.x + 0.01f, size.y + 0.01f, 0.035f);
            col.isTrigger = true;
            _hitTargets.Add((root, onClick));
        }

        static void Quad(string name, Transform parent, Vector3 pos, Vector2 size, Color color, int queue)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            Destroy(go.GetComponent<Collider>());
            var r = go.GetComponent<Renderer>();
            var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            r.sharedMaterial = new Material(sh);
            if (r.sharedMaterial.HasProperty("_Color")) r.sharedMaterial.SetColor("_Color", color);
            if (r.sharedMaterial.HasProperty("_BaseColor")) r.sharedMaterial.SetColor("_BaseColor", color);
            r.sharedMaterial.renderQueue = queue;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        static TextMesh Text(string name, Transform parent, Vector3 pos, string msg, float size, Color color,
            TextAnchor anchor = TextAnchor.MiddleCenter,
            TextAlignment alignment = TextAlignment.Center)
        {
            return XrText.Make(name, parent, pos, msg, size, color, anchor, alignment);
        }
    }
}
