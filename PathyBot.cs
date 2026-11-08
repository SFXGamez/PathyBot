// PathyBot - a Gorilla Tag pathfinding bot (BepInEx plugin)
//
// Wandering: explores the walkable floor around the player (including walls it can climb),
// picks a random reachable spot, shortcuts the route, walks it with smooth steering while
// drawing a cyan route line, swings the hands in a planted gait, and turns to face its way.
//
// Climbing: it climbs solid walls (up to 6 m) and also platforms - ledges with no wall under them
// (floating or overhanging) - by walking to just outside the edge, going straight up beside it and
// pulling itself over. It will also drop down off ledges (up to 4 m) to get back to the ground.
//
// Chase (private rooms only): every 5 minutes it picks a random OTHER player in the room,
// announces their name and "I'M COMING FOR YOU", chases them, and when it catches them it
// lunges at them. Players who also run PathyBot get the banner on screen and, for the person
// who was caught, a jumpscare (red flash + sound). Players without the mod just see a gorilla
// run at them and lunge.
//
// Games (private rooms only, the bot is the seeker / caller):
//   Numpad 1  hide & seek: everyone hides for 30s, then the bot searches (sight + hearing) and
//             lunges at whoever it finds; it announces who was found and who is left.
//   Numpad 2  asks the lobby whether to play a game. The bot holds a green snowball (yes, right hand)
//             and a red one (no, left hand); stand next to one to vote. More players at green = it
//             plays; red = it asks about the next game.
//   Numpad 3  red light green light: the bot turns its back on GREEN and watches on RED. Anyone
//             still moving on red is out; the first player to reach the bot wins.
//   Numpad 0  stop the current game.
//   Home      open a text box: type a line, press Enter, and the bot says it (Esc closes it)
//   Page Down random game votes on/off (a vote every 4-8 minutes; a yes starts that game).
//   F12       save a diagnostics file (BepInEx\\PathyBot-diagnostics.txt) describing the game's voice recorder and snowballs.
//   End       mute / unmute the bot's voice. Announcements are spoken with the free Windows voice and
//             sent out through your game mic (private rooms only).
//   Status text (and the snowball balls) only appear for players who also run PathyBot; the held-out
//   hands are visible to everybody, so anyone can still vote by standing next to a hand.
//
// Controls (game window focused):
//   F6      bot on/off  (or left X + right A together in VR)
//   F7      switch which hand transforms the gait drives
//   F8      hand gait on/off
//   F9      route line on/off
//   F10     body turning on/off
//   F11     start a chase now / cancel the current chase
//   Insert  automatic 5-minute chases on/off
//   Delete  block/allow incoming PathyBot banners and scares (works even when the bot is off)
//
// Needs these extra references in the .csproj on top of the earlier ones:
//   Photon3Unity3D.dll and UnityEngine.AudioModule.dll (both in the game's Managed folder).
// The web/JSON modules from the old voice version are no longer needed.
//
// Note: the locomotion class has been renamed between game versions
// (Player -> GTPlayer). Change the lines marked [VERSION] if it doesn't compile.

using System.Collections;
using System.Collections.Generic;
using BepInEx;
using ExitGames.Client.Photon;
using GorillaLocomotion;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.XR;

namespace PathyBot
{
    [BepInPlugin("com.example.pathybot", "PathyBot", "0.8.0")]
    public class PathyBotPlugin : BaseUnityPlugin
    {

        // Settings live in BepInEx\config\com.example.pathybot.cfg (created the first time the game runs).
        public static BepInEx.Configuration.ConfigEntry<bool> CfgSpeak;
        public static BepInEx.Configuration.ConfigEntry<string> CfgWinVoice;
        public static BepInEx.Configuration.ConfigEntry<bool> CfgWallClimb, CfgVoiceSwap, CfgOverlay;
        public static BepInEx.Configuration.ConfigEntry<string> CfgScream;
        public static BepInEx.Configuration.ConfigEntry<string> CfgAiUrl, CfgAiModel, CfgWhisperUrl, CfgWake;

        void Awake()
        {
            CfgSpeak = Config.Bind("Voice", "Speak", true,
                "Let the bot say its announcements out loud (it uses the free Windows voice and plays them through your game mic in private rooms).");
            CfgWinVoice = Config.Bind("Voice", "WindowsVoice", "",
                "Name of an installed Windows voice, for example Microsoft Zira Desktop. Empty = the default Windows voice.");

            CfgOverlay = Config.Bind("Overlay", "ShowOverlay", true,
                "Draw the voice/chat panel (top right) and the floating name tag on your screen.");
            CfgVoiceSwap = Config.Bind("Voice", "SwapMaleVoices", true,
                "Change to a different installed male Windows voice, pitch and volume on every word. Ignored when WindowsVoice is set. Needs two or more male voices installed (Windows has David and Mark by default).");
            CfgWallClimb = Config.Bind("Path", "ClimbWalls", false,
                "Let the path planner (and the chase trail) climb up walls. Off by default. Ledges and platforms still work.");
            CfgScream = Config.Bind("Catch", "ScreamFile", "scream.wav",
                "Sound played (locally and through the game mic) when the bot catches someone. Put the file anywhere under BepInEx\\plugins. A 16-bit .wav always works; mp3/ogg work if ffmpeg is installed.");
            CfgAiUrl = Config.Bind("Talk", "OllamaUrl", "http://127.0.0.1:11434/api/chat",
                "Your local Ollama chat endpoint. Only localhost addresses are accepted.");
            CfgAiModel = Config.Bind("Talk", "OllamaModel", "llama3",
                "The Ollama model name to chat with (see 'ollama list').");
            CfgWhisperUrl = Config.Bind("Talk", "WhisperUrl", "http://127.0.0.1:8080/inference",
                "A local whisper.cpp server that turns speech into text. Only localhost addresses are accepted.");
            CfgWake = Config.Bind("Talk", "WakeWords", "pathy bot,patty bot,pathie bot,pathy but,patty but",
                "The bot only answers speech that contains one of these words (comma separated). Empty = answers anyone nearby.");

            var go = new GameObject("PathyBot");
            go.hideFlags = HideFlags.HideAndDontSave; // keeps the game from cleaning it up
            DontDestroyOnLoad(go);
            go.AddComponent<PathyBotController>();
            Logger.LogInfo("PathyBot loaded. Press F6 to toggle.");
        }
    }

    // One step of a route.
    class Wp
    {
        public Vector3 pos;
        public bool climb;   // true: reached by climbing the wall in front of the previous waypoint
        public Vector3 dir;  // for a climb: horizontal direction pointing into the wall
        public bool platform;    // a climb onto a platform edge: no wall to hold, just go up beside it
        public Vector3 column;   // for a platform climb: the spot on the floor, just outside its edge, to go up from
        public bool phase;       // no way found to walk or climb it: fly straight there through the geometry
    }

    class Banner
    {
        public string text;
        public float start, end;
        public Color color;
    }

    // Runs the speech coroutines on their own component, so the bot's StopAllCoroutines()
    // calls (games, chases, toggling) never cut a sentence off halfway.
    public class CoroutineHost : MonoBehaviour { }

    // Sits next to a player's voice AudioSource and keeps a copy of the samples that pass through it.
    public class VoiceTap : MonoBehaviour
    {
        public readonly object gate = new object();
        public readonly List<float> buf = new List<float>();
        public int rate = 48000;

        void OnAudioFilterRead(float[] data, int channels)
        {
            lock (gate)
            {
                rate = AudioSettings.outputSampleRate;
                if (buf.Count > rate * 20) buf.Clear();
                for (int i = 0; i + channels - 1 < data.Length; i += channels)
                {
                    float m = 0f;
                    for (int c = 0; c < channels; c++) m += data[i + c];
                    buf.Add(m / channels);
                }
            }
        }
    }

    // Result of work done on a background thread (the game must never wait on a program).
    public class AsyncResult
    {
        public volatile bool done;
        public string error;
    }

    [DefaultExecutionOrder(32000)] // run our LateUpdate after the game's, so our changes aren't overwritten
    public class PathyBotController : MonoBehaviour, IOnEventCallback
    {
        // ---- Settings ----
        const bool PrivateLobbyOnly = true;   // turns the bot off if it finds itself in a public room
        const float CellSize = 1f;            // grid resolution (meters)
        const float StepHeight = 0.8f;        // biggest ledge/slope step it will walk up
        const float HopMax = 1.2f;            // tallest low obstacle (step, slab, table) it will hop onto
        const float HopLook = 1.5f;           // how far ahead it watches for those
        const float MaxDrop = 4f;             // biggest drop it will walk off (so it can come back down from a platform)
        const float MinTargetDist = 5f;       // wander target must be at least this far along the route
        const float MaxTargetDist = 20f;      // ...and no farther than this
        const float PauseAtTarget = 1f;       // seconds to wait before picking the next spot
        const float StuckTime = 2.5f;         // seconds without progress before repicking
        const int MaxExpansionsPerFrame = 120;
        const int MaxExpansionsTotal = 3000;

        // Movement feel
        const float MoveSpeed = 5f;           // wandering top speed (m/s)
        const float Accel = 24f;              // ground acceleration (m/s^2)
        const float AirAccel = 5f;            // acceleration while airborne
        const float TurnRate = 540f;          // how fast the heading can change (degrees/second)
        const float FaceTurnRate = 300f;      // how fast the body rotates to face its heading
        const float JumpSpeed = 5.5f;
        const bool StickToGround = true;      // cap upward bounce off slopes (it never pushes down into the floor)
        const float WaypointRadius = 0.7f;
        const float CornerLookahead = 1.5f;   // start easing into the next leg this far from a corner
        const float CornerSlowdown = 0.55f;   // speed multiplier at a sharp (90+ degree) corner

        // Chase
        const float ChaseInterval = 300f;     // a new chase every 5 minutes
        const float AnnounceSeconds = 4.2f;   // name + "I'm coming for you" before it starts running
        const float ChaseSpeed = 7f;
        const float ChaseTimeout = 120f;      // gives up after this long
        const float ChaseReplanInterval = 0.7f;
        const float CatchDist = 1.8f;         // closer than this counts as caught
        const float GoalReach = 1.5f;
        const int MaxExpansionsChase = 6000;
        const float LungeSeconds = 1.4f;
        const float ScareSeconds = 1.6f;
        const float ScareVolume = 0.5f;       // kept moderate on purpose
        const byte EvAnnounce = 171;
        const byte EvScare = 172;
        const byte EvGameMsg = 173;
        const byte EvSnow = 174;
        const string ModTag = "PathyBot1";

        // Climbing
        static readonly bool CanClimb = true;
        static bool WallClimbOn { get { return PathyBotPlugin.CfgWallClimb != null && PathyBotPlugin.CfgWallClimb.Value; } }
        const float MaxClimb = 6f;            // tallest wall it will climb
        const float ClimbReach = 1.0f;        // how close a wall must be to start a climb
        const float ClimbStandoff = 0.45f;
        const float ClimbTouchDist = 0.6f;    // pivot-to-wall distance that counts as touching
        const float ClimbSpeed = 3.5f;
        const float ClimbBaseCost = 4f;       // climbing costs more than walking, so it prefers stairs/slopes
        const float ClimbCostPerMeter = 1.5f;
        const float ClimbTimeout = 9f;
        const int ClimbBudgetPerSearch = 250; // cap on climb checks per search (they are expensive)
        const int PlatformBudgetPerSearch = 160;

        // Route line
        static readonly Color LineColor = new Color(0.25f, 0.95f, 1f, 1f);
        const float LineWidth = 0.028f;
        const float LineLift = 0.15f;         // float the line slightly above the floor
        const bool LineThroughWalls = true;   // draw over geometry so you can always see it

        // Hand gait
        const float ArmStride = 0.86f;         // how far each hand reaches forward/back of the body
        const float ArmLift = 0.46f;         // how high a hand lifts while swinging forward
        const float ArmReach = 0.63f;         // hands sit a little ahead of the body
        const float ArmSpread = 0.25f;        // how far apart the hands are
        const float ArmIdleHeight = 0.98f;    // standing pose: how high the hands hang above the floor
        const float ArmIdleSpread = 0.4f;     // ...and how far out from the body
        const float HandRadius = 0.07f;       // keeps planted hands resting on top of the floor
        // One full gait cycle covers 4 * stride of body travel, so planted hands don't slide on the floor.
        const float ArmCyclesPerMeter = 1f / (4f * ArmStride);

        bool enabledBot;
        Rigidbody rb;
        Transform playerRoot;
        List<Wp> route = new List<Wp>();
        int routeIndex;
        bool planning;
        float resumeAt;
        float lastProgressTime;
        Vector3 lastProgressPos;
        float lastJumpTime;
        float blockedTime;  // how long it has been held back while trying to move
        Vector3 botVel;     // our own smoothed ground velocity, written to the body every physics tick
        float measuredSpeed;   // how fast it is really moving (from position change), for the HUD and jam detection
        Vector3 lastFixedPos;
        int stuckCount;
        float feetOffset;   // how far the player's transform sits above the floor
        bool feetOffsetKnown;
        int lastExplored;
        Vector3 heading;    // smoothed direction the bot is facing/moving
        int climbBudget, platformBudget;

        // Input / debug display
        bool prevKey, rawKey, prevCombo;
        bool prevF7, prevF8, prevF9, prevF10, prevF11, prevIns, prevDel;
        bool prevN0, prevN1, prevN2, prevN3, prevPgDn, prevF12;
        int togglePresses;
        string lastFail = "";

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);

        static bool KeyHeld(int vk) => Application.isFocused && (GetAsyncKeyState(vk) & 0x8000) != 0;

        // True once per key press.
        static bool keysBlocked;   // true while the text box is open, so typing can't trigger hotkeys

        static bool Edge(int vk, ref bool prev)
        {
            bool down = !keysBlocked && KeyHeld(vk);
            bool pressed = down && !prev;
            prev = down;
            return pressed;
        }

        static bool ButtonDown(XRNode node)
        {
            var device = InputDevices.GetDeviceAtXRNode(node);
            return device.isValid
                && device.TryGetFeatureValue(CommonUsages.primaryButton, out bool down)
                && down;
        }

        void Start()
        {
            PhotonNetwork.AddCallbackTarget(this);
            voiceHost = gameObject.AddComponent<CoroutineHost>();
            screamHost = gameObject.AddComponent<CoroutineHost>();
            Debug.Log("[PathyBot] Controller is running. Press F6 (game window focused) to toggle.");
        }

        void OnDestroy()
        {
            PhotonNetwork.RemoveCallbackTarget(this);
            EndClimb();
            EndPhase();
            StopTalk();
            StopScream();
            if (pcTalking) FinishPcTalk(false);
            StopVoice();
            if (voiceAudio != null) Destroy(voiceAudio.gameObject);
            ClearClipCache();
            ShowSnowballs(false);
            ClearRemoteBalls();
            if (redBallMat != null) Destroy(redBallMat);
            if (greenBallMat != null) Destroy(greenBallMat);
            if (pathLine != null) Destroy(pathLine.gameObject);
            if (simMark != null) Destroy(simMark.gameObject);
            if (predL != null) Destroy(predL.gameObject);
            if (predR != null) Destroy(predR.gameObject);
            if (predMatL != null) Destroy(predMatL);
            if (predMatR != null) Destroy(predMatR);
            if (targetRing != null) Destroy(targetRing.gameObject);
            if (lineMat != null) Destroy(lineMat);
            if (flashQuad != null) Destroy(flashQuad);
            if (flashMat != null) Destroy(flashMat);
            if (scareAudio != null) Destroy(scareAudio.gameObject);
        }

        // ---------- HUD ----------

        static void Hud(ref float y, string text)
        {
            GUI.Label(new Rect(10, y, 1100, 25), text);
            y += 22f;
        }

        string ChaseLine()
        {
            if (chasing)
            {
                if (Time.time < chaseGoTime) return "announcing " + chaseName + "...";
                return "chasing " + chaseName + " (" + Mathf.Max(0, Mathf.CeilToInt(chaseDeadline - Time.time)) + "s left)";
            }
            string next = "auto OFF";
            if (autoChase)
            {
                int left = Mathf.Max(0, Mathf.CeilToInt(nextChaseTime - Time.time));
                next = "next chase in " + (left / 60) + ":" + (left % 60).ToString("00");
            }
            return next + (chaseStatus.Length > 0 ? "   | " + chaseStatus : "");
        }

        void OnGUI()
        {
            float y = 10f + barH;
            string state = !enabledBot ? "OFF"
                : climbing ? "climbing"
                : planning ? "planning..."
                : route.Count > 0 && routeIndex < route.Count
                    ? "walking " + routeIndex + "/" + route.Count + "  speed " + measuredSpeed.ToString("0.0") + " m/s  stuck x" + stuckCount
                : "waiting";
            Hud(ref y, "PathyBot: " + state + "   | F6 held: " + rawKey + "   | toggles: " + togglePresses);
            if (enabledBot && lastFail.Length > 0) Hud(ref y, "Last problem: " + lastFail);
            if (enabledBot)
            {
                Hud(ref y, "Hands (F7 = source, F8 = on/off): " + armStatus);
                Hud(ref y, "Route line (F9 = on/off): " + (linesOn ? "on" : "OFF") + "   shader: " + lineStatus);
                Hud(ref y, "Body turning (F10 = on/off): " + (faceOn ? faceStatus : "OFF"));
                Hud(ref y, "Chase (Insert = auto on/off, F11 = start/stop now): " + ChaseLine());
            }
            Hud(ref y, "Incoming PathyBot banners/scares (Delete = block/allow): " + (acceptIncoming ? "allowed" : "BLOCKED"));
            Hud(ref y, "Voice (Home = type something for it to say, End = mute): " + (voiceMuted ? "MUTED" : voiceStatus));
            Hud(ref y, "Path simulation: " + (simOn ? simStatus : "off"));
            Hud(ref y, "Catch scream: " + screamStatus);
            Hud(ref y, "Talk (Page Up = bot listens to players, Numpad 6 = you talk to it, local Ollama): " + (talkOn ? "LISTENING - " : "off - ") + talkStatus);
            if (enabledBot && snowStatus.Length > 0) Hud(ref y, "Vote snowballs: " + snowStatus);
            if (enabledBot) Hud(ref y, "Random game votes (Page Down = on/off): " + AutoGameLine());
            if (enabledBot) Hud(ref y, "Games (Numpad 1 = hide & seek, 2 = ask the lobby, 3 = red light green light, 4 = tag, 5 = unstick, 0 = stop): " + GameLine());

            DrawBanners();
            DrawScareText();
            DrawGameUi();
            DrawTalkUi();
            DrawOverlay();
            DrawChatBox();
            DrawBar();
        }

        // ---------- Main loop ----------

        void Update()
        {
            // Home opens/closes the text box that the bot speaks.
            keysBlocked = chatOpen;
            bool homeDown = KeyHeld(0x24);
            if (homeDown && !prevHome) { chatOpen = !chatOpen; chatText = ""; keysBlocked = chatOpen; }
            prevHome = homeDown;

            // Read keys straight from Windows so it works even if Unity's legacy input is disabled.
            bool keyDown = KeyHeld(0x75); // F6
            bool keyPressed = keyDown && !prevKey;
            prevKey = keyDown;
            rawKey = keyDown;

            if (Edge(0x76, ref prevF7)) { armSource = (armSource + 1) % ArmSources.Length; ResolveArms(); }
            if (Edge(0x77, ref prevF8)) { armsOn = !armsOn; armStatus = armsOn ? "on" : "OFF (F8)"; }
            if (Edge(0x78, ref prevF9)) linesOn = !linesOn;
            if (Edge(0x79, ref prevF10)) faceOn = !faceOn;
            if (Edge(0x7A, ref prevF11) && enabledBot)
            {
                if (chasing) EndChase("cancelled", false);
                else if (gameMode == GameMode.None) TryStartChase();
            }
            if (Edge(0x2D, ref prevIns))
            {
                autoChase = !autoChase;
                if (autoChase) nextChaseTime = Time.time + ChaseInterval;
            }
            if (Edge(0x2E, ref prevDel)) acceptIncoming = !acceptIncoming;
            if (Edge(0x22, ref prevPgDn))                                                           // Page Down
            {
                autoGames = !autoGames;
                if (autoGames) ScheduleNextGame();
            }
            if (Edge(0x7B, ref prevF12)) DumpDiagnostics();   // F12
            if (injecting) HoldTransmit();
            if (Edge(0x21, ref prevPgUp)) ToggleTalk();
            if (Edge(0x66, ref prevN6) && !chatOpen) TogglePcTalk();
            if (Edge(0x65, ref prevN5)) ManualUnstick();
            UpdateTalk();
            if (Edge(0x23, ref prevEnd)) { voiceMuted = !voiceMuted; if (voiceMuted) StopVoice(); }
            if (Edge(0x60, ref prevN0) && gameMode != GameMode.None) StopGame("stopped", true);
            if (Edge(0x61, ref prevN1)) StartHideSeek();
            if (Edge(0x62, ref prevN2)) StartVote();
            if (Edge(0x63, ref prevN3)) StartRedLight();
            if (Edge(0x64, ref prevN4)) StartTag();
            UpdateRemoteBalls();

            // VR toggle: press left X and right A at the same time.
            bool combo = ButtonDown(XRNode.LeftHand) && ButtonDown(XRNode.RightHand);
            bool comboPressed = combo && !prevCombo;
            prevCombo = combo;

            if (keyPressed || comboPressed || toggleRequest)
            {
                toggleRequest = false;
                enabledBot = !enabledBot;
                togglePresses++;
                StopAllCoroutines();
                EndClimb();
                EndPhase();
                route.Clear();
                routeIndex = 0;
                planning = false;
                chasing = false;
                chasePlanning = false;
                gameMode = GameMode.None;
                gameFaceDir = Vector3.zero;
                StopVoice();
                StopScream();
                lastFail = "";
                chaseStatus = "";
                resumeAt = 0f;
                heading = Vector3.zero;
                botVel = Vector3.zero;
                if (enabledBot)
                {
                    if (screamHost != null) screamHost.StartCoroutine(LoadScream());
                    ResolveArms();
                    ResolveFacing();
                    nextChaseTime = Time.time + ChaseInterval;
                    ScheduleNextGame();
                }
                Debug.Log("[PathyBot] " + (enabledBot ? "ON" : "OFF"));
            }

            banners.RemoveAll(b => Time.time >= b.end);
            HideSimMark();
            if (Edge(0x73, ref prevF4)) barOn = !barOn;

            if (!enabledBot) return;

            if (PrivateLobbyOnly && PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null && PhotonNetwork.CurrentRoom.IsVisible)
            {
                enabledBot = false;
                StopAllCoroutines();
                EndClimb();
                EndPhase();
                chasing = false;
                chasePlanning = false;
                planning = false;
                gameMode = GameMode.None;
                gameFaceDir = Vector3.zero;
                StopVoice();
                StopScream();
                lastFail = "";
                Debug.Log("[PathyBot] Public lobby detected - turned off.");
                return;
            }

            if (gameMode != GameMode.None) UpdateGame();
            else if (autoGames && !chasing && Time.time >= nextGameTime)
            {
                StartVote();   // asks the lobby; a "yes" then starts hide and seek or red light green light
                if (gameMode == GameMode.None) nextGameTime = Time.time + 20f; // couldn't start (no players / not private): try again soon
            }

            if (!chasing && autoChase && gameMode == GameMode.None && Time.time >= nextChaseTime) TryStartChase();

            if (chasing) UpdateChase();
            else if (!planning && routeIndex >= route.Count && Time.time >= resumeAt && !GameHolds())
                StartCoroutine(PlanNewPath());
        }

        // ---------- Chase ----------

        bool chasing, chasePlanning;
        bool autoChase = true;
        Vector3 chaseLastPos, chaseVel;
        readonly List<Vector3> targetTrail = new List<Vector3>();   // where the chased player has been (feet level, 3D)
        float nextChaseTime, chaseGoTime, chaseDeadline, nextReplan, lungeUntil;
        int chaseActor, lastChaseActor = -1;
        string chaseName = "";
        string chaseStatus = "";
        Transform chaseT;
        readonly List<Banner> banners = new List<Banner>();

        struct Candidate
        {
            public string name;
            public int actor;
            public Transform t;
        }

        static bool ConfirmedPrivate() =>
            PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null && !PhotonNetwork.CurrentRoom.IsVisible;

        // Everyone in the room except us, with their rig so we can see where they are.
        List<Candidate> GetCandidates()
        {
            var list = new List<Candidate>();
            if (!PhotonNetwork.InRoom) return list;
            int me = PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : -1;

            VRRig[] rigs;
            try { rigs = FindObjectsByType<VRRig>(FindObjectsSortMode.None); }
            catch { return list; }

            foreach (var rig in rigs)
            {
                if (rig == null) continue;
                string name = null;
                int actor = -1;

                // [VERSION] Newer builds expose the player through OwningNetPlayer; older ones use a PhotonView.
                object np = GetMember(rig, "OwningNetPlayer") ?? GetMember(rig, "creator");
                if (np != null)
                {
                    name = GetMember(np, "NickName") as string;
                    object a = GetMember(np, "ActorNumber");
                    if (a is int ai) actor = ai;
                }
                if (actor < 0)
                {
                    var pv = rig.GetComponent<PhotonView>();
                    if (pv != null && pv.Owner != null)
                    {
                        actor = pv.Owner.ActorNumber;
                        name = pv.Owner.NickName;
                    }
                }
                if (actor < 0 || actor == me || string.IsNullOrEmpty(name)) continue;
                list.Add(new Candidate { name = name, actor = actor, t = rig.transform });
            }
            return list;
        }

        void TryStartChase()
        {
            if (!ConfirmedPrivate())
            {
                chaseStatus = "waiting: chases only start in a private room";
                nextChaseTime = Time.time + 15f;
                return;
            }
            var cands = GetCandidates();
            if (cands.Count == 0)
            {
                chaseStatus = "waiting: no other players found";
                nextChaseTime = Time.time + 15f;
                return;
            }
            // Don't pick the same person twice in a row when there is a choice.
            if (cands.Count > 1) cands.RemoveAll(c => c.actor == lastChaseActor);
            Candidate pick = cands[Random.Range(0, cands.Count)];

            StopAllCoroutines();
            EndClimb();
            planning = false;
            chasePlanning = false;
            route.Clear();
            routeIndex = 0;

            chaseVel = Vector3.zero;
            targetTrail.Clear();
            chaseLastPos = pick.t != null ? pick.t.position : Vector3.zero;
            chasing = true;
            chaseActor = pick.actor;
            chaseName = pick.name;
            chaseT = pick.t;
            lastChaseActor = pick.actor;
            chaseGoTime = Time.time + AnnounceSeconds;
            chaseDeadline = chaseGoTime + ChaseTimeout;
            nextReplan = chaseGoTime;
            nextChaseTime = Time.time + ChaseInterval; // the next pick is 5 minutes from this one
            chaseStatus = "";

            ShowAnnouncement(chaseName);
            Speak(CleanName(chaseName) + ". I'm coming for you.");
            Send(EvAnnounce, null, new object[] { ModTag, chaseName, chaseActor });
            Debug.Log("[PathyBot] Chasing " + chaseName);
        }

        void UpdateChase()
        {
            if (Time.time < chaseGoTime) return; // still announcing
            if (chaseT == null) { EndChase("they left", false); return; }
            if (Time.time > chaseDeadline) { EndChase("gave up", false); return; }

            if (rb == null)
            {
                // [VERSION] GTPlayer on current builds, Player on older ones.
                var player = GTPlayer.Instance;
                if (player != null) rb = player.GetComponent<Rigidbody>();
                if (rb == null) return;
            }

            Vector3 tpos = chaseT.position;
            if (Time.deltaTime > 0f)
            {
                Vector3 tv = (tpos - chaseLastPos) / Time.deltaTime;
                tv.y = 0f;
                if (tv.magnitude < 12f) chaseVel = Vector3.Lerp(chaseVel, tv, 0.15f);
            }
            chaseLastPos = tpos;
            RecordTrail(tpos);
            if (Vector3.Distance(rb.position, tpos) < CatchDist) { DoCatch(); return; }

            if (!chasePlanning && !climbing && Time.time >= nextReplan)
                StartCoroutine(PlanChasePath(tpos));
        }

        void DoCatch()
        {
            string name = chaseName;
            int actor = chaseActor;

            // Face them and throw the arms up.
            Vector3 to = chaseT != null ? chaseT.position - rb.position : heading;
            to.y = 0f;
            if (to.sqrMagnitude > 0.01f) { heading = to.normalized; armDir = heading; }
            lungeUntil = Time.time + LungeSeconds;
            BeginFaceStick();
            PlayScream();

            // The jumpscare goes only to the person who was caught (and only reaches PathyBot users).
            Send(EvScare, new[] { actor }, new object[] { ModTag, name, actor });

            if (gameMode == GameMode.HideSeek && tracks.TryGetValue(actor, out Track found))
            {
                found.done = true;
                GameMsg("FOUND " + name.ToUpperInvariant() + "!", 1, 3f, null, "Found " + CleanName(name) + "!");
            }
            else if (gameMode == GameMode.Tag && tracks.TryGetValue(actor, out Track tagged))
            {
                tagged.done = true;
                GameMsg("TAGGED " + name.ToUpperInvariant() + "!", 1, 3f, null, "Tag! " + CleanName(name) + " is out.");
            }

            banners.Clear();
            banners.Add(new Banner { text = "CAUGHT " + name.ToUpperInvariant() + "!", start = Time.time, end = Time.time + 2.2f, color = new Color(1f, 0.25f, 0.25f) });
            EndChase("caught " + name, true);
        }

        void EndChase(string reason, bool caught)
        {
            StopAllCoroutines();
            EndClimb();
            chasing = false;
            chasePlanning = false;
            planning = false;
            route.Clear();
            routeIndex = 0;
            botVel = Vector3.zero;
            chaseStatus = "last chase: " + reason;
            resumeAt = Time.time + (caught ? 2.5f : 1f);
            if (caught && rb != null)
            {
                Vector3 v = rb.linearVelocity;
                v.x = 0f; v.z = 0f;
                rb.linearVelocity = v;
            }
        }

        // The target's path in 3D: every spot they have stood or hung on, so the bot can go up wherever they went.
        void RecordTrail(Vector3 tpos)
        {
            Vector3 feet = GroundRay(tpos + Vector3.up * 0.3f, 1.8f, out RaycastHit fh) ? fh.point : tpos - Vector3.up * 0.4f;
            if (targetTrail.Count == 0 || Vector3.Distance(targetTrail[targetTrail.Count - 1], feet) > 0.7f)
            {
                targetTrail.Add(feet);
                if (targetTrail.Count > 300) targetTrail.RemoveAt(0);
            }
        }

        // Builds a route that follows the target's own trail from the place nearest to anchor: plain steps where
        // it can be walked, a real wall climb where they climbed, and a straight fly-through only if neither works.
        List<Wp> BuildTrailRoute(Vector3 anchor, List<Wp> lead)
        {
            if (targetTrail.Count < 3) return null;
            int i0 = -1;
            float bd = 3f;
            for (int i = 0; i < targetTrail.Count; i++)
            {
                float d = Vector3.Distance(targetTrail[i], anchor);
                if (d < bd) { bd = d; i0 = i; }
            }
            if (i0 < 0 || i0 >= targetTrail.Count - 1) return null;

            var list = new List<Wp>();
            if (lead != null) list.AddRange(lead);
            Vector3 prev = anchor;
            for (int i = i0 + 1; i < targetTrail.Count && list.Count < 60; i++)
            {
                Vector3 p = targetTrail[i];
                bool last = i == targetTrail.Count - 1;
                Vector3 flatv = new Vector3(p.x - prev.x, 0f, p.z - prev.z);
                float rise = p.y - prev.y;
                if (!last && flatv.magnitude < 1.2f && Mathf.Abs(rise) < 0.6f) continue;   // too close to matter

                if (SegmentWalkable(prev, p)) { list.Add(new Wp { pos = p }); prev = p; continue; }

                if (rise < -0.8f) { list.Add(new Wp { pos = p }); prev = p; continue; }     // they dropped: just walk off

                if (rise > 0.8f)
                {
                    // Climb the wall in the direction they went (or any side, when they went straight up).
                    var dirs = new List<Vector3>();
                    if (flatv.sqrMagnitude > 0.01f)
                        dirs.Add(Mathf.Abs(flatv.x) > Mathf.Abs(flatv.z) ? new Vector3(Mathf.Sign(flatv.x), 0f, 0f) : new Vector3(0f, 0f, Mathf.Sign(flatv.z)));
                    dirs.Add(Vector3.forward); dirs.Add(Vector3.back); dirs.Add(Vector3.left); dirs.Add(Vector3.right);
                    bool climbed = false;
                    foreach (var d in dirs)
                    {
                        var fromNode = new Node { pos = prev, cell = ToCell(prev) };
                        if (WallClimbOn && TryClimb(fromNode, d, out Vector3 top, out float h, out Vector3 col) && Mathf.Abs(top.y - p.y) < 2f)
                        {
                            list.Add(new Wp { pos = top, climb = true, dir = d, column = col });
                            prev = top;
                            climbed = true;
                            break;
                        }
                    }
                    if (climbed) continue;
                }

                list.Add(new Wp { pos = p, phase = true });
                prev = p;
            }
            return list.Count > 0 ? list : null;
        }

        bool astarReached;

        IEnumerator PlanChasePath(Vector3 tpos)
        {
            chasePlanning = true;
            bool simOk = true;
            if (GetStart(out Vector3 start))
            {
                // Run to where they are going to be, not where they are now.
                Vector3 aim = tpos;
                float dist = rb != null ? Vector3.Distance(rb.position, tpos) : 0f;
                float lead = Mathf.Clamp(dist / ChaseSpeed, 0f, 1.2f) * 0.8f;
                if (chaseVel.magnitude > 0.8f) aim = tpos + chaseVel * lead;
                Vector3 goal = tpos;
                if (GroundRay(aim + Vector3.up * 1f, 14f, out RaycastHit gh)) goal = gh.point;
                else if (GroundRay(tpos + Vector3.up * 1f, 14f, out RaycastHit gh2)) goal = gh2.point;

                List<Wp> result = null;
                yield return StartCoroutine(AStar(start, goal, r => result = r));

                List<Wp> chosen = (result != null && result.Count > 0) ? Simplify(result, start) : null;
                if (chasing && !climbing && !astarReached)
                {
                    // The search could not get to them (they are up a wall, on a roof, in a tree...):
                    // follow the way they went instead, climbing where they climbed.
                    Vector3 trailFrom = (chosen != null && chosen.Count > 0) ? chosen[chosen.Count - 1].pos : start;
                    var trail = BuildTrailRoute(trailFrom, chosen);
                    if (trail != null) { chosen = trail; lastFail = "following where they climbed"; }
                }
                simOk = chosen == null || SimPasses(chosen, start);
                if (chasing && !climbing && chosen != null && chosen.Count > 0 && simOk)
                {
                    route = chosen;
                    routeIndex = 0;
                    lastProgressPos = rb != null ? rb.position : start;
                    lastProgressTime = Time.time;
                    lastFail = "";
                }
            }
            float dNow = (rb != null && chaseT != null) ? Vector3.Distance(rb.position, chaseT.position) : 20f;
            nextReplan = Time.time + (!simOk ? 0.1f : dNow < 6f ? 0.3f : dNow < 15f ? ChaseReplanInterval : 1.2f);
            chasePlanning = false;
        }

        // ---------- Network messages (PathyBot users only) ----------

        bool acceptIncoming = true;
        float lastAnnounceRecv = -999f, lastScareRecv = -999f;

        void Send(byte code, int[] targets, object[] data)
        {
            if (!PhotonNetwork.InRoom) return;
            try
            {
                var opts = new RaiseEventOptions();
                if (targets != null) opts.TargetActors = targets;
                else opts.Receivers = ReceiverGroup.Others;
                PhotonNetwork.RaiseEvent(code, data, opts, SendOptions.SendReliable);
            }
            catch (System.Exception e)
            {
                Debug.Log("[PathyBot] Couldn't send event: " + e.Message);
            }
        }

        public void OnEvent(EventData e)
        {
            if (e.Code < EvAnnounce || e.Code > EvSnow) return;
            var data = e.CustomData as object[];
            if (data == null || data.Length < 3) return;
            if (!(data[0] is string tag) || tag != ModTag) return;

            if (!acceptIncoming) return;
            string who = data[1] as string ?? "";

            if (e.Code == EvAnnounce)
            {
                if (Time.time - lastAnnounceRecv < 5f) return; // ignore floods
                lastAnnounceRecv = Time.time;
                if (!chasing) ShowAnnouncement(who);
            }
            else if (e.Code == EvScare)
            {
                if (Time.time - lastScareRecv < 30f) return;   // at most one scare per 30 seconds
                lastScareRecv = Time.time;
                StartScare();
            }
            else if (e.Code == EvGameMsg)
            {
                if (data.Length < 4 || Time.time - lastGameRecv < 0.25f) return;
                lastGameRecv = Time.time;
                int ci = data[2] is int colorIdx ? Mathf.Clamp(colorIdx, 0, StatusColors.Length - 1) : 0;
                float secs = data[3] is float sec ? Mathf.Clamp(sec, 0.5f, 25f) : 3f;
                ShowStatus(Clip(who, 90), StatusColors[ci], secs);
            }
            else if (e.Code == EvSnow)
            {
                // The bot is holding out a red and a green snowball: draw them on its rig for us too.
                if (data[1] is int actor && data[2] is float dur)
                    ShowRemoteSnowballs(actor, Mathf.Clamp(dur, 1f, 30f));
            }
        }

        float lastGameRecv = -999f;

        // ---------- Banners and the scare (screen / headset / sound) ----------

        float scareStart, scareUntil;
        GUIStyle bigStyle, scareStyle;
        GameObject flashQuad;
        Material flashMat;
        AudioSource scareAudio;
        AudioClip scareClip;

        void ShowAnnouncement(string name)
        {
            float t = Time.time;
            banners.Clear();
            banners.Add(new Banner { text = name, start = t, end = t + 1.8f, color = Color.white });
            banners.Add(new Banner { text = "I'M COMING FOR YOU", start = t + 1.8f, end = t + AnnounceSeconds, color = new Color(1f, 0.2f, 0.2f) });
        }

        void DrawBanners()
        {
            if (banners.Count == 0) return;
            if (bigStyle == null)
                bigStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 64 };

            foreach (var b in banners)
            {
                if (Time.time < b.start || Time.time >= b.end) continue;
                float a = Mathf.Clamp01(Mathf.Min((Time.time - b.start) / 0.2f, (b.end - Time.time) / 0.3f));
                var r = new Rect(0f, Screen.height * 0.18f, Screen.width, 120f);
                bigStyle.normal.textColor = new Color(0f, 0f, 0f, a);
                GUI.Label(new Rect(r.x + 3f, r.y + 3f, r.width, r.height), b.text, bigStyle);
                bigStyle.normal.textColor = new Color(b.color.r, b.color.g, b.color.b, a);
                GUI.Label(r, b.text, bigStyle);
            }
        }

        void StartScare()
        {
            scareStart = Time.time;
            scareUntil = Time.time + ScareSeconds;
            try
            {
                if (scareClip == null) scareClip = MakeScareClip();
                var cam = Camera.main;
                if (scareAudio == null)
                {
                    var go = new GameObject("PathyBot_ScareAudio");
                    go.hideFlags = HideFlags.HideAndDontSave;
                    scareAudio = go.AddComponent<AudioSource>();
                    scareAudio.spatialBlend = 0f;
                    scareAudio.playOnAwake = false;
                }
                if (cam != null) scareAudio.transform.position = cam.transform.position;
                scareAudio.volume = ScareVolume;
                scareAudio.PlayOneShot(scareClip);
            }
            catch (System.Exception e) { Debug.Log("[PathyBot] Scare sound failed: " + e.Message); }
        }

        // A short synthesized scream-ish sweep, so there are no audio files to ship.
        static AudioClip MakeScareClip()
        {
            const int rate = 44100;
            const float len = 1.1f;
            int n = (int)(rate * len);
            var data = new float[n];
            var rnd = new System.Random(7);
            float phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate;
                float f = Mathf.Lerp(520f, 70f, t / len);
                phase += f / rate;
                float saw = (phase % 1f) * 2f - 1f;
                float noise = (float)(rnd.NextDouble() * 2.0 - 1.0);
                float env = Mathf.Min(1f, t / 0.01f) * Mathf.Exp(-t * 2.6f);
                data[i] = (saw * 0.55f + noise * 0.35f) * env * 0.8f;
            }
            var clip = AudioClip.Create("PathyScare", n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        // Red flash on a quad in front of the camera, so it shows in a VR headset too.
        // It rises quickly once and fades out. It never flickers or strobes.
        void UpdateFlash()
        {
            bool active = Time.time < scareUntil;
            if (!active)
            {
                if (flashQuad != null && flashQuad.activeSelf) flashQuad.SetActive(false);
                return;
            }
            var cam = Camera.main;
            if (cam == null) return;

            if (flashQuad == null)
            {
                flashMat = MakeOverlayMaterial(new Color(0.5f, 0f, 0f, 0f), out _);
                if (flashMat == null) return;
                flashQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                flashQuad.hideFlags = HideFlags.HideAndDontSave;
                var col = flashQuad.GetComponent<Collider>();
                if (col != null) DestroyImmediate(col); // no collider, it must never touch physics
                flashQuad.GetComponent<Renderer>().sharedMaterial = flashMat;
            }
            if (flashQuad.transform.parent != cam.transform)
            {
                flashQuad.transform.SetParent(cam.transform, false);
                flashQuad.transform.localPosition = new Vector3(0f, 0f, 0.35f);
                flashQuad.transform.localRotation = Quaternion.identity;
                flashQuad.transform.localScale = new Vector3(4f, 4f, 1f);
            }
            flashQuad.SetActive(true);

            float t = (Time.time - scareStart) / ScareSeconds;
            float alpha = t < 0.06f ? t / 0.06f : 1f - (t - 0.06f) / 0.94f;
            if (flashMat.HasProperty("_Color"))
                flashMat.SetColor("_Color", new Color(0.5f, 0f, 0f, Mathf.Clamp01(alpha) * 0.85f));
        }

        void DrawScareText()
        {
            if (Time.time >= scareUntil) return;
            if (scareStyle == null)
                scareStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };

            float t = (Time.time - scareStart) / ScareSeconds;
            float scale = Mathf.Lerp(0.6f, 1.5f, Mathf.Clamp01(t * 4f));
            float alpha = Mathf.Clamp01(1f - t);
            Vector2 shake = Random.insideUnitCircle * 10f * (1f - t);
            scareStyle.fontSize = Mathf.RoundToInt(110f * scale);
            scareStyle.normal.textColor = new Color(1f, 1f, 1f, alpha);
            GUI.Label(new Rect(shake.x, shake.y, Screen.width, Screen.height), "CAUGHT!", scareStyle);
        }

        // ---------- Movement ----------

        void FixedUpdate()
        {
            if (rb != null)
            {
                Vector3 p = rb.position;
                measuredSpeed = new Vector2(p.x - lastFixedPos.x, p.z - lastFixedPos.z).magnitude / Time.fixedDeltaTime;
                lastFixedPos = p;
            }

            if (climbing && (!enabledBot || routeIndex >= route.Count)) EndClimb();
            if (sticking && rb != null) { DoStick(); return; }
            if (phasing && (!enabledBot || route.Count == 0)) EndPhase();
            if (phasing && rb != null) { DoPhase(); if (!phasing && phaseFails >= PhaseFailLimit) BeginRetrace(); return; }
            if (!enabledBot || planning || routeIndex >= route.Count) return;

            if (rb == null)
            {
                // [VERSION] GTPlayer on current builds, Player on older ones.
                var player = GTPlayer.Instance;
                if (player == null) return;
                rb = player.GetComponent<Rigidbody>();
                if (rb == null) { lastFail = "no Rigidbody on the player"; return; }
            }

            Vector3 pos = rb.position;
            float feetY = pos.y - feetOffset;
            Wp w = route[routeIndex];

            if (w.phase && BeginPhase(routeIndex)) return;

            if (w.climb)
            {
                if (!climbing) BeginClimb(w);
                DoClimb(w, pos, feetY);
                return;
            }

            Vector3 wp = w.pos;
            Vector3 flat = new Vector3(wp.x - pos.x, 0f, wp.z - pos.z);
            bool isLast = routeIndex == route.Count - 1;

            if (flat.magnitude < (isLast ? 0.5f : WaypointRadius))
            {
                routeIndex++;
                if (routeIndex >= route.Count)
                {
                    // End of the route: stop sliding.
                    Vector3 stop = rb.linearVelocity;
                    stop.x = 0f; stop.z = 0f;
                    rb.linearVelocity = stop;
                    botVel = Vector3.zero;
                    if (!chasing) resumeAt = Time.time + PauseAtTarget;
                }
                return;
            }

            // Desired direction: toward the waypoint, easing toward the next leg near a corner.
            Vector3 want = flat.normalized;
            float cornerFactor = 0f;
            if (!isLast && !route[routeIndex + 1].climb)
            {
                Vector3 next = route[routeIndex + 1].pos;
                Vector3 leg = new Vector3(next.x - wp.x, 0f, next.z - wp.z);
                if (leg.sqrMagnitude > 0.01f)
                {
                    float near = Mathf.Clamp01(1f - flat.magnitude / CornerLookahead);
                    cornerFactor = near * Mathf.Clamp01(Vector3.Angle(want, leg) / 90f);
                    want = Vector3.Slerp(want, leg.normalized, near * 0.5f);
                    want.y = 0f;
                    want.Normalize();
                }
            }

            // Turn smoothly instead of snapping.
            if (heading.sqrMagnitude < 0.01f) heading = want;
            heading = Vector3.RotateTowards(heading, want, TurnRate * Mathf.Deg2Rad * Time.fixedDeltaTime, 0f);
            heading.y = 0f;
            if (heading.sqrMagnitude < 0.0001f) heading = want;
            heading.Normalize();

            // Speed: slow into corners, slow into the final stop (wandering only), slow while still turning.
            float speed = chasing ? ChaseSpeed : MoveSpeed;
            if (isLast && !chasing) speed = Mathf.Clamp(flat.magnitude * 2.5f, 1.5f, MoveSpeed);
            else speed *= Mathf.Lerp(1f, CornerSlowdown, cornerFactor);
            speed *= Mathf.Lerp(0.3f, 1f, Mathf.Clamp01(Vector3.Dot(heading, want)));

            bool grounded = GroundRay(pos + Vector3.up * 0.1f, feetOffset + 0.4f, out RaycastHit groundHit);
            bool justJumped = Time.time - lastJumpTime < 0.35f;

            // Low obstacle ahead (step, slab, table)? Slow down for it, then hop up onto it.
            float hopRise = 0f, hopDist = 0f;
            bool hopNear = false, hopNow = false;
            if (grounded && !justJumped && speed > 0.5f && ProbeHop(pos, feetY, out hopRise, out hopDist))
            {
                hopNear = true;
                hopNow = hopDist < 0.45f;
            }
            if (hopNear && !hopNow) speed = Mathf.Min(speed, 3f);

            // Our own smoothed ground velocity. We write it every physics tick, so the game's own
            // movement code can't zero it out in between.
            botVel = Vector3.MoveTowards(botVel, heading * speed, (grounded ? Accel : AirAccel) * Time.fixedDeltaTime);

            Vector3 v = rb.linearVelocity;
            v.x = botVel.x;
            v.z = botVel.z;

            // Don't carry upward bounce off slopes and crests. This only caps an upward speed that
            // is more than the surface needs; it never pushes down into the floor.
            if (StickToGround && grounded && !justJumped && groundHit.normal.y > 0.6f)
            {
                Vector3 n = groundHit.normal;
                float surfaceY = -(n.x * v.x + n.z * v.z) / n.y;
                if (v.y > surfaceY + 0.5f) v.y = surfaceY + 0.5f;
            }

            // The hop itself: just high enough to clear the top with some room to spare.
            if (hopNow && Time.time - lastJumpTime > 0.4f)
            {
                float g = Mathf.Abs(Physics.gravity.y);
                v.y = Mathf.Clamp(Mathf.Sqrt(2f * g * (hopRise + 0.5f)), JumpSpeed * 0.8f, 8f);
                lastJumpTime = Time.time;
                blockedTime = 0f;
            }

            // Jump only when something is really holding it back: either a ledge that is clearly
            // higher and close, or it has been jammed in place for almost a second.
            bool held = grounded && speed > 1f && measuredSpeed < speed * 0.4f;
            blockedTime = held ? blockedTime + Time.fixedDeltaTime : Mathf.Max(0f, blockedTime - 2f * Time.fixedDeltaTime);
            bool ledgeAhead = blockedTime > 0.12f && wp.y - feetY > 0.3f && flat.magnitude < 1.5f;
            bool jammed = blockedTime > 0.5f;
            if (grounded && (ledgeAhead || jammed) && Time.time - lastJumpTime > 0.8f)
            {
                v.y = JumpSpeed;
                lastJumpTime = Time.time;
                blockedTime = 0f;
            }
            rb.linearVelocity = v;

            // Stuck detection.
            if ((pos - lastProgressPos).magnitude > 0.5f)
            {
                lastProgressPos = pos;
                lastProgressTime = Time.time;
                stuckStreak = 0;
                phaseFails = 0;
                DropCrumb(pos - Vector3.up * feetOffset);
            }
            else if (Time.time - lastProgressTime > StuckTime)
            {
                stuckCount++;
                stuckStreak++;
                MarkAvoid(pos, 1);
                if (stuckStreak >= 2) phaseFails++;   // phasing did not get it moving
                if (phaseFails >= PhaseFailLimit && crumbs.Count > 2) { BeginRetrace(); return; }
                if (stuckStreak == 1 && BeginPhase()) { lastProgressTime = Time.time; return; }
                stuckStreak = 0;
                lastFail = "got stuck, picking a new route";
                route.Clear();
                routeIndex = 0;
                resumeAt = Time.time;
                nextReplan = Time.time;
            }
        }

        // ---------- Wall climbing ----------
        // A climb waypoint means: from the previous waypoint, walk up to the wall in front of you,
        // go straight up it, then pull over the top onto the ledge. Gravity is switched off while
        // it holds onto the wall, and the hands reach up it in an alternating climb.

        bool climbing, mantling, gravityTaken, savedGravity;
        Vector3 climbDir;
        float climbStartTime, mantleUntil, climbBaseY;

        void BeginClimb(Wp w)
        {
            climbing = true;
            mantling = false;
            climbDir = w.dir;
            climbStartTime = Time.time;
            climbBaseY = rb.position.y - feetOffset;
            if (!gravityTaken)
            {
                savedGravity = rb.useGravity;
                rb.useGravity = false;
                gravityTaken = true;
            }
        }

        // ---------- Phasing (ghost move) ----------
        // When the bot is stuck it switches its own colliders and gravity off and flies in a straight
        // line to a waypoint further along the route, straight through whatever is in the way.
        // Everything is switched back on when it arrives, on timeout, on toggle-off and on destroy.
        const float PhaseSpeed = 4f;
        const float PhaseTimeout = 5f;
        bool phasing;
        Vector3 phaseTarget;
        int phaseIndex;
        float phaseStart;
        int stuckStreak;
        readonly List<Collider> phasedColliders = new List<Collider>();
        bool phaseGravityTaken, phaseSavedGravity;

        // Breadcrumbs: where the bot has actually walked, so it can go back the way it came.
        const int PhaseFailLimit = 5;
        int phaseFails;
        readonly List<Vector3> crumbs = new List<Vector3>();

        void DropCrumb(Vector3 feet)
        {
            if (crumbs.Count == 0 || (crumbs[crumbs.Count - 1] - feet).magnitude > 1f)
            {
                crumbs.Add(feet);
                if (crumbs.Count > 300) crumbs.RemoveAt(0);
            }
        }

        // Too many failed phases: walk back along the last stretch of the path actually travelled,
        // then plan again from there.
        void BeginRetrace(int steps = 25)
        {
            EndPhase();
            EndClimb();
            route.Clear();
            routeIndex = 0;
            int n = Mathf.Min(steps, crumbs.Count);
            for (int i = 0; i < n; i++)
                route.Add(new Wp { pos = crumbs[crumbs.Count - 1 - i] });
            if (crumbs.Count >= n) crumbs.RemoveRange(crumbs.Count - n, n);
            phaseFails = 0;
            stuckStreak = 0;
            lastProgressPos = rb.position;
            lastProgressTime = Time.time;
            nextReplan = Time.time + 8f;
            lastFail = "phase failed " + PhaseFailLimit + " times, retracing steps";
        }

        // Hotkey (Numpad 5): get unstuck right now by walking back the way it came, then plan again.
        void ManualUnstick()
        {
            if (!enabledBot || !EnsureRb()) return;
            StopAllCoroutines();
            planning = false;
            chasePlanning = false;
            if (crumbs.Count < 3)
            {
                // Nothing to retrace yet: just throw the route away and plan a new one.
                EndPhase(); EndClimb();
                route.Clear(); routeIndex = 0;
                resumeAt = Time.time; nextReplan = Time.time;
                lastFail = "unstick: no trail yet, replanning";
                return;
            }
            BeginRetrace(40);
            lastFail = "unstick: retracing steps";
        }

        bool BeginPhase(int forceIdx = -1)
        {
            if (rb == null || route.Count == 0) return false;
            Vector3 pos = rb.position;
            int idx = -1;
            for (int i = routeIndex; i < route.Count; i++)
            {
                Vector3 d = route[i].pos - pos; d.y = 0f;
                idx = i;
                if (d.magnitude >= 2f) break;
            }
            if (forceIdx >= 0 && forceIdx < route.Count) idx = forceIdx;
            if (idx < 0) return false;
            phaseIndex = idx;
            phaseTarget = route[idx].pos + Vector3.up * feetOffset;
            phaseStart = Time.time;
            phasedColliders.Clear();
            foreach (var c in rb.GetComponentsInChildren<Collider>())
                if (c != null && c.enabled && !c.isTrigger) { c.enabled = false; phasedColliders.Add(c); }
            if (!phaseGravityTaken)
            {
                phaseSavedGravity = rb.useGravity;
                rb.useGravity = false;
                phaseGravityTaken = true;
            }
            phasing = true;
            lastFail = route[phaseIndex].phase ? "flying to where they went" : "phasing through an obstacle";
            return true;
        }

        void EndPhase()
        {
            foreach (var c in phasedColliders) if (c != null) c.enabled = true;
            phasedColliders.Clear();
            if (phaseGravityTaken && rb != null) rb.useGravity = phaseSavedGravity;
            phaseGravityTaken = false;
            phasing = false;
            sticking = false;
        }

        void DoPhase()
        {
            Vector3 to = phaseTarget - rb.position;
            float dist = to.magnitude;
            bool timedOut = Time.time - phaseStart > PhaseTimeout;
            if (dist < 0.3f || timedOut)
            {
                // Only come back to solid when the spot is clear, or when time is up.
                if (timedOut || !BodyBlocked(phaseTarget - Vector3.up * (feetOffset - 0.3f)))
                {
                    rb.linearVelocity = Vector3.zero;
                    botVel = Vector3.zero;
                    EndPhase();
                    if (timedOut) phaseFails++;
                    routeIndex = Mathf.Min(phaseIndex + (phaseIndex < route.Count && route[phaseIndex].phase ? 1 : 0), route.Count);
                    lastProgressPos = rb.position;
                    lastProgressTime = Time.time;
                    stuckStreak = 0;
                    return;
                }
            }
            Vector3 dir = dist > 0.01f ? to / dist : Vector3.zero;
            rb.linearVelocity = dir * Mathf.Min(PhaseSpeed, dist / Time.fixedDeltaTime);
            Vector3 flatDir = new Vector3(dir.x, 0f, dir.z);
            if (flatDir.sqrMagnitude > 0.01f) heading = flatDir.normalized;
        }

        void EndClimb()
        {
            climbing = false;
            mantling = false;
            if (gravityTaken && rb != null) rb.useGravity = savedGravity;
            gravityTaken = false;
        }

        void DoClimb(Wp w, Vector3 pos, float feetY)
        {
            heading = climbDir;
            Vector3 v = rb.linearVelocity;

            if (Time.time - climbStartTime > ClimbTimeout)
            {
                lastFail = "climb timed out";
                EndClimb();
                route.Clear();
                routeIndex = 0;
                resumeAt = Time.time;
                nextReplan = Time.time;
                return;
            }

            if (!mantling && w.platform)
            {
                // Platform edge: walk to the spot just outside it, then go straight up beside it.
                Vector3 toCol = new Vector3(w.column.x - pos.x, 0f, w.column.z - pos.z);
                if (toCol.magnitude > 0.3f && feetY < climbBaseY + 0.4f)
                {
                    v = toCol.normalized * 2.5f;
                    v.y = 0f;
                }
                else
                {
                    Vector3 hold = toCol.magnitude > 0.05f ? toCol.normalized * Mathf.Min(2f, toCol.magnitude * 3f) : Vector3.zero;
                    v = Vector3.up * ClimbSpeed + hold;
                    if (feetY >= w.pos.y - 0.1f) { mantling = true; mantleUntil = Time.time + 0.7f; }
                }
            }
            else if (!mantling)
            {
                bool wallAhead = WallRay(pos, climbDir, 1.5f, out RaycastHit wh);
                bool touching = wallAhead && wh.distance < ClimbTouchDist;
                if (!touching)
                {
                    // Walk up to the wall first.
                    v = climbDir * 2.5f;
                    v.y = 0f;
                }
                else
                {
                    v = Vector3.up * ClimbSpeed + climbDir * 0.6f; // the small push keeps us against the wall
                    if (feetY >= w.pos.y - 0.1f) { mantling = true; mantleUntil = Time.time + 0.7f; }
                }
                // The wall ran out beneath us: we are at the lip.
                if (!wallAhead && feetY > climbBaseY + 0.6f) { mantling = true; mantleUntil = Time.time + 0.7f; }
            }
            else
            {
                // Pull over the lip onto the ledge.
                Vector3 toTop = new Vector3(w.pos.x - pos.x, 0f, w.pos.z - pos.z);
                v = climbDir * 3.5f;
                v.y = 0.4f;
                if (toTop.magnitude < 0.5f || Time.time > mantleUntil)
                {
                    EndClimb();
                    routeIndex++;
                    rb.linearVelocity = Vector3.zero;
                    lastProgressTime = Time.time;
                    lastProgressPos = pos;
                    return;
                }
            }
            rb.linearVelocity = v;
        }

        // ---------- Route line (local only) ----------

        bool linesOn = true;
        string lineStatus = "-";
        LineRenderer pathLine, targetRing;
        Material lineMat;
        const int RingPoints = 32;

        // Not every shader is included in every build, so try a few.
        Material MakeOverlayMaterial(Color color, out string shaderName)
        {
            string[] names = { "Hidden/Internal-Colored", "Sprites/Default", "UI/Default", "Unlit/Color", "GUI/Text Shader" };
            foreach (string n in names)
            {
                Shader sh = Shader.Find(n);
                if (sh == null) continue;
                var m = new Material(sh);
                m.hideFlags = HideFlags.HideAndDontSave;
                if (m.HasProperty("_Color")) m.SetColor("_Color", color);
                if (m.HasProperty("_Cull")) m.SetInt("_Cull", 0);
                if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 0);
                if (LineThroughWalls)
                {
                    if (m.HasProperty("_ZTest")) m.SetInt("_ZTest", 8); // always pass the depth test
                    m.renderQueue = 5000;                               // draw after everything else
                }
                shaderName = n;
                return m;
            }
            shaderName = "none found";
            return null;
        }

        LineRenderer MakeLine(string name, float width, bool loop)
        {
            var go = new GameObject(name);
            go.hideFlags = HideFlags.HideAndDontSave;
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = lineMat;
            lr.startWidth = width;
            lr.endWidth = width;
            lr.startColor = LineColor;
            lr.endColor = LineColor;
            lr.numCornerVertices = 0;   // sharp angular corners
            lr.numCapVertices = 0;
            lr.useWorldSpace = true;
            lr.loop = loop;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.enabled = false;
            return lr;
        }

        void EnsureLines()
        {
            if (pathLine != null) return;
            lineMat = MakeOverlayMaterial(LineColor, out string shaderName);
            lineStatus = shaderName;
            if (lineMat == null) return;
            pathLine = MakeLine("PathyBot_Route", LineWidth, false);
            targetRing = MakeLine("PathyBot_Target", LineWidth, true);
            targetRing.positionCount = RingPoints;
        }

        void HideLines()
        {
            if (pathLine != null) pathLine.enabled = false;
            if (targetRing != null) targetRing.enabled = false;
            HidePredictions();
        }

        // The "guide": the route as the bot will really travel it. Walking legs are straight lines;
        // a climb is drawn as it will be done: to the foot of the wall (or platform edge), straight up,
        // then over the top. The line, the hands and the hand prediction all use it.
        readonly List<Vector3> guide = new List<Vector3>();
        readonly List<float> guideLen = new List<float>();
        float guideTotal;

        void AddGuide(Vector3 p)
        {
            if (guide.Count > 0)
            {
                float d = Vector3.Distance(guide[guide.Count - 1], p);
                if (d < 0.01f) return;
                guideTotal += d;
            }
            guide.Add(p);
            guideLen.Add(guideTotal);
        }

        void BuildGuide()
        {
            guide.Clear();
            guideLen.Clear();
            guideTotal = 0f;
            if (rb == null || planning || route.Count == 0 || routeIndex >= route.Count) return;

            Vector3 feet = rb.position - Vector3.up * feetOffset;
            AddGuide(feet);
            Vector3 prev = feet;
            for (int i = routeIndex; i < route.Count; i++)
            {
                Wp w = route[i];
                if (w.climb)
                {
                    // No tracing up walls: the line stops at the foot of the wall.
                    if (!(climbing && i == routeIndex))
                    {
                        Vector3 b = w.column.sqrMagnitude > 0.001f
                            ? w.column
                            : new Vector3(w.pos.x - w.dir.x * 0.95f, prev.y, w.pos.z - w.dir.z * 0.95f);
                        AddGuide(b);
                    }
                    break;
                }
                AddGuide(w.pos);
                prev = w.pos;
            }
        }

        // The point d metres along the guide from the bot's feet, and which way the guide is heading there.
        Vector3 GuidePoint(float d, out Vector3 tangent)
        {
            tangent = armDir;
            if (guide.Count == 0) return Vector3.zero;
            if (guide.Count == 1) return guide[0];
            d = Mathf.Clamp(d, 0f, guideTotal);
            for (int i = 1; i < guide.Count; i++)
            {
                if (d > guideLen[i] && i < guide.Count - 1) continue;
                Vector3 a = guide[i - 1], b = guide[i];
                float seg = guideLen[i] - guideLen[i - 1];
                float t = seg > 0.001f ? (d - guideLen[i - 1]) / seg : 1f;
                Vector3 flat = b - a;
                flat.y = 0f;
                tangent = flat.sqrMagnitude > 0.0001f ? flat.normalized : armDir;
                return Vector3.Lerp(a, b, Mathf.Clamp01(t));
            }
            return guide[guide.Count - 1];
        }

        LineRenderer predL, predR;
        Material predMatL, predMatR;
        static readonly Color PredColorL = new Color(0.25f, 0.95f, 1f, 1f);
        static readonly Color PredColorR = new Color(0.25f, 0.95f, 1f, 1f);

        LineRenderer MakeColoredLine(string name, Color col, out Material mat)
        {
            mat = MakeOverlayMaterial(col, out string _);
            if (mat == null) return null;
            var lr = MakeLine(name, LineWidth, false);
            lr.sharedMaterial = mat;
            lr.startColor = col;
            lr.endColor = col;
            return lr;
        }

        void HidePredictions()
        {
            if (predL != null) predL.enabled = false;
            if (predR != null) predR.enabled = false;
        }

        // Draws where each hand is going to be over the next second, using the same maths that places them.
        void DrawPredictions()
        {
            if (predL == null) predL = MakeColoredLine("PathyBot_PredLeft", PredColorL, out predMatL);
            if (predR == null) predR = MakeColoredLine("PathyBot_PredRight", PredColorR, out predMatR);
            if (predL == null || predR == null) return;

            const int N = 14;
            const float Step = 0.07f;
            float spd = Mathf.Clamp(measuredSpeed, 0.5f, 8f);
            predL.positionCount = N;
            predR.positionCount = N;
            for (int k = 0; k < N; k++)
            {
                float t = k * Step;
                float s = spd * t;
                float ph = armPhase + spd * ArmCyclesPerMeter * t;
                predL.SetPosition(k, HandTarget(-1f, ph, s, rb.position));
                predR.SetPosition(k, HandTarget(1f, ph + 0.5f, s, rb.position));
            }
            predL.enabled = true;
            predR.enabled = true;
        }

        void UpdateLines()
        {
            bool show = linesOn && guide.Count >= 2 && rb != null;
            if (!show) { HideLines(); return; }

            EnsureLines();
            if (pathLine == null) return;

            Vector3 lift = Vector3.up * LineLift;

            // From the bot's feet to the destination, up every wall and platform on the way.
            pathLine.positionCount = guide.Count;
            for (int i = 0; i < guide.Count; i++) pathLine.SetPosition(i, guide[i] + lift);
            pathLine.enabled = true;

            // A pulsing ring marks the destination.
            Vector3 end = route[route.Count - 1].pos + lift;
            float radius = 0.4f + 0.05f * Mathf.Sin(Time.time * 4f);
            for (int k = 0; k < RingPoints; k++)
            {
                float a = k * Mathf.PI * 2f / RingPoints;
                targetRing.SetPosition(k, end + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius);
            }
            targetRing.enabled = true;

            // Where the hands will be next.
            if (armsOn && !climbing && measuredSpeed > 0.5f && Time.time >= lungeUntil) DrawPredictions();
            else HidePredictions();
        }

        // ---------- Hand gait ----------
        // While walking, each hand alternates between planted on the floor (sliding back relative
        // to the body) and lifted forward in an arc, like a gorilla knuckle-walking. On a wall the
        // hands reach up alternately. When it catches someone both arms go up. It fades in and out.

        // Where to find the left and right hand transforms. F7 cycles through these in case
        // the first one isn't what drives the arms in your version of the game.
        // Note: "rig hand targets" is visual only. The other sources may also feed the game's own
        // hand-collision locomotion, which can push the player around.
        static readonly string[][][] ArmSources =
        {
            new string[][]
            {
                new[] { "GorillaTagger", "Instance", "offlineVRRig", "leftHand", "rigTarget" },
                new[] { "GorillaTagger", "Instance", "offlineVRRig", "rightHand", "rigTarget" }
            },
            new string[][]
            {
                new[] { "GorillaLocomotion.GTPlayer", "Instance", "leftControllerTransform" },
                new[] { "GorillaLocomotion.GTPlayer", "Instance", "rightControllerTransform" }
            },
            new string[][]
            {
                new[] { "GorillaTagger", "Instance", "leftHandTransform" },
                new[] { "GorillaTagger", "Instance", "rightHandTransform" }
            }
        };
        static readonly string[] ArmSourceNames = { "rig hand targets", "controller transforms", "tagger hand transforms" };

        class Hand
        {
            public Transform t;
        }

        Hand leftArm = new Hand(), rightArm = new Hand();
        int armSource;
        bool armsOn = true;
        string armStatus = "looking...";
        float armPhase, armBlend;
        int armPose;
        Vector3 armDir = Vector3.forward;

        static object GetMember(object o, string name)
        {
            if (o == null) return null;
            bool isStatic = o is System.Type;
            System.Type t = isStatic ? (System.Type)o : o.GetType();
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                      | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
            var f = t.GetField(name, flags);
            if (f != null) return f.GetValue(isStatic ? null : o);
            var p = t.GetProperty(name, flags);
            if (p != null) return p.GetValue(isStatic ? null : o, null);
            return null;
        }

        // chain[0] is the type name, the rest are field/property names to walk.
        static Transform Resolve(string[] chain)
        {
            System.Type type = System.Type.GetType(chain[0] + ", Assembly-CSharp");
            if (type == null) return null;
            object cur = type;
            for (int i = 1; i < chain.Length; i++)
            {
                cur = GetMember(cur, chain[i]);
                if (cur == null) return null;
            }
            var go = cur as GameObject;
            if (go != null) return go.transform;
            var tr = cur as Transform;
            if (tr != null) return tr;
            var comp = cur as Component;
            return comp != null ? comp.transform : null;
        }

        void ResolveArms()
        {
            var src = ArmSources[armSource];
            leftArm = new Hand { t = Resolve(src[0]) };
            rightArm = new Hand { t = Resolve(src[1]) };
            bool ok = leftArm.t != null && rightArm.t != null;
            armStatus = ArmSourceNames[armSource] + (ok ? " (found)" : " (NOT found - press F7)");
        }

        // Where a hand goes for a given gait phase, with the body bodyS metres further along the line.
        // The hands land ON the route line (one each side of it) so you can see them follow it.
        // side: -1 = left, +1 = right.
        Vector3 HandTarget(float side, float phaseP, float bodyS, Vector3 body)
        {
            float p = phaseP % 1f;
            float along, lift;
            if (p < 0.5f)
            {
                // Planted: the hand stays still on the floor, so relative to the body it slides back.
                along = Mathf.Lerp(ArmStride, -ArmStride, p / 0.5f);
                lift = 0f;
            }
            else
            {
                // Swing: lift off and carry the hand forward in an arc.
                float t = (p - 0.5f) / 0.5f;
                along = Mathf.Lerp(-ArmStride, ArmStride, t);
                lift = Mathf.Sin(t * Mathf.PI) * ArmLift;
            }

            Vector3 xz;
            float baseY;
            if (guide.Count >= 2)
            {
                Vector3 pt = GuidePoint(Mathf.Max(0f, bodyS + along + ArmReach), out Vector3 tangent);
                // Directly on the line: no sideways offset, no re-snap to the floor.
                return new Vector3(pt.x, pt.y + lift, pt.z);
            }
            else
            {
                Vector3 right = Vector3.Cross(Vector3.up, armDir);
                xz = body + armDir * (along + ArmReach) + right * (side * ArmSpread);
                baseY = body.y - feetOffset;
            }

            // Rest on whatever floor is under this hand.
            float groundY = baseY;
            if (GroundRay(new Vector3(xz.x, baseY + 0.8f, xz.z), 1.8f, out RaycastHit gh))
                groundY = gh.point.y;

            return new Vector3(xz.x, groundY + HandRadius + lift, xz.z);
        }

        // phaseOffset staggers the two hands by half a cycle.
        void PlaceHand(Transform hand, float side, float phaseOffset, Vector3 body)
        {
            Vector3 target = HandTarget(side, armPhase + phaseOffset, 0f, body);
            hand.position = guide.Count >= 2 ? target : Vector3.Lerp(hand.position, target, armBlend);
        }

        // Hands on the wall, reaching up one after the other. Each hand aims at the wall at its own
        // height and snaps onto the surface it hits, so the hands really sit on the wall (or on the
        // edge of a platform) instead of floating where a wall would be.
        void PlaceHandsClimb(Vector3 body)
        {
            Vector3 right = Vector3.Cross(Vector3.up, climbDir);
            for (int i = 0; i < 2; i++)
            {
                Transform hand = i == 0 ? leftArm.t : rightArm.t;
                float side = i == 0 ? -1f : 1f;
                float ph = (armPhase + (i == 0 ? 0f : 0.5f)) % 1f;
                float reach = ph < 0.5f
                    ? Mathf.Lerp(0.35f, 0.95f, ph / 0.5f)
                    : Mathf.Lerp(0.95f, 0.35f, (ph - 0.5f) / 0.5f);

                Vector3 lateral = right * (side * 0.25f);
                Vector3 target = body + climbDir * 0.45f + lateral + Vector3.up * reach;   // no surface found: where a wall would be

                Vector3 origin = body + Vector3.up * reach + lateral - climbDir * 0.3f;
                if (WallRay(origin, climbDir, 1.4f, out RaycastHit wh))
                {
                    target = wh.point + wh.normal * HandRadius;                          // planted on the wall
                }
                else
                {
                    // Nothing in front (a platform edge): reach for its top or edge ahead and above.
                    Vector3 above = body + climbDir * 0.5f + lateral;
                    if (GroundRay(new Vector3(above.x, body.y + 1.6f, above.z), 2.2f, out RaycastHit eh)
                        && eh.point.y > body.y - 0.2f && eh.point.y < body.y + 1.7f)
                        target = eh.point + Vector3.up * HandRadius;
                    else if (SnapAny(target, 0.8f, out Vector3 sp)) target = sp;
                }
                hand.position = Vector3.Lerp(hand.position, target, armBlend);
            }
        }

        // Snaps a point onto the nearest surface of ANY kind (floor, wall, ceiling, edge, slope):
        // casts rays out from the point in every direction and takes the closest hit.
        static readonly Vector3[] SnapDirs =
        {
            Vector3.down, Vector3.up, Vector3.forward, Vector3.back, Vector3.left, Vector3.right,
            new Vector3(1,-1,0).normalized, new Vector3(-1,-1,0).normalized, new Vector3(0,-1,1).normalized, new Vector3(0,-1,-1).normalized,
            new Vector3(1,0,1).normalized, new Vector3(-1,0,1).normalized, new Vector3(1,0,-1).normalized, new Vector3(-1,0,-1).normalized,
            new Vector3(1,1,0).normalized, new Vector3(-1,1,0).normalized, new Vector3(0,1,1).normalized, new Vector3(0,1,-1).normalized
        };

        bool SnapAny(Vector3 from, float range, out Vector3 snapped)
        {
            snapped = from;
            float best = float.MaxValue;
            bool found = false;
            foreach (var d in SnapDirs)
            {
                if (WallRay(from, d, range, out RaycastHit h) && h.distance < best)
                {
                    best = h.distance;
                    snapped = h.point + h.normal * HandRadius;
                    found = true;
                }
            }
            return found;
        }

        // While phasing: both hands reach out and stick to whatever surface is closest, any direction.
        void PlaceHandsPhase(Vector3 body)
        {
            Vector3 right = Vector3.Cross(Vector3.up, armDir);
            for (int i = 0; i < 2; i++)
            {
                Transform hand = i == 0 ? leftArm.t : rightArm.t;
                float side = i == 0 ? -1f : 1f;
                Vector3 want = body + armDir * 0.6f + right * (side * 0.4f) + Vector3.up * 0.2f;
                if (SnapAny(want, 1.2f, out Vector3 sp)) want = sp;
                hand.position = Vector3.Lerp(hand.position, want, armBlend);
            }
        }

        // Pulling over the lip: both hands go flat on the ledge top ahead.
        void PlaceHandsMantle(Vector3 body)
        {
            Vector3 right = Vector3.Cross(Vector3.up, climbDir);
            for (int i = 0; i < 2; i++)
            {
                Transform hand = i == 0 ? leftArm.t : rightArm.t;
                float side = i == 0 ? -1f : 1f;
                Vector3 ahead = body + climbDir * 0.55f + right * (side * 0.3f);
                Vector3 target = new Vector3(ahead.x, body.y - feetOffset + 0.5f, ahead.z);
                if (GroundRay(new Vector3(ahead.x, body.y + 1.2f, ahead.z), 2.2f, out RaycastHit lh))
                    target = lh.point + Vector3.up * HandRadius;
                else if (SnapAny(target, 0.8f, out Vector3 sp)) target = sp;
                hand.position = Vector3.Lerp(hand.position, target, armBlend);
            }
        }

        // Both arms thrown up and forward, shaking.
        void PlaceHandsLunge(Vector3 body)
        {
            Vector3 right = Vector3.Cross(Vector3.up, armDir);
            float shake = Mathf.Sin(Time.time * 40f) * 0.05f;
            Vector3 up = Vector3.up * (1.5f + shake);
            leftArm.t.position = Vector3.Lerp(leftArm.t.position, body + armDir * 0.6f + up - right * 0.7f, armBlend);
            rightArm.t.position = Vector3.Lerp(rightArm.t.position, body + armDir * 0.6f + up + right * 0.7f, armBlend);
        }

        void UpdateArms()
        {
            if (leftArm.t == null || rightArm.t == null)
            {
                if (Time.frameCount % 60 == 0) ResolveArms(); // keep looking about once a second
                return;
            }
            if (rb == null) return;

            Vector3 vel = rb.linearVelocity;
            Vector3 flatVel = new Vector3(vel.x, 0f, vel.z);
            float speed = flatVel.magnitude;
            bool lunging = Time.time < lungeUntil;
            bool wallClimb = climbing && !mantling;
            bool walking = !climbing && !planning && routeIndex < route.Count && speed > 0.5f;
            bool offering = gameMode == GameMode.Vote && gamePhase == 0;
            // Whenever the bot is on, its hands are driven: gait, climb, lunge, offer, or a relaxed standing pose.
            bool active = armsOn || offering;

            // Ease into each new pose instead of snapping the hands there.
            bool mantleNow = climbing && mantling;
            int pose = lunging ? 1 : offering ? 2 : phasing ? 6 : wallClimb ? 3 : mantleNow ? 5 : walking ? 4 : 0;
            if (pose != armPose) { armPose = pose; if (pose != 0) armBlend = 0f; }
            armBlend = Mathf.MoveTowards(armBlend, active ? 1f : 0f, Time.deltaTime * 6f);
            if (walking)
            {
                armDir = heading.sqrMagnitude > 0.01f ? heading : flatVel / speed;
                armPhase = (armPhase + Time.deltaTime * speed * ArmCyclesPerMeter) % 1f;
            }
            else if (wallClimb)
            {
                armPhase = (armPhase + Time.deltaTime * ClimbSpeed * 0.6f) % 1f;
            }

            if (armBlend <= 0.001f) return;

            Vector3 body = rb.position;
            if (lunging) PlaceHandsLunge(body);
            else if (offering) PlaceHandsOffer();
            else if (phasing) PlaceHandsPhase(body);
            else if (wallClimb) PlaceHandsClimb(body);
            else if (mantleNow) PlaceHandsMantle(body);
            else if (walking)
            {
                PlaceHand(leftArm.t, -1f, 0f, body);
                PlaceHand(rightArm.t, 1f, 0.5f, body);
            }
            else PlaceHandsIdle(body);
        }

        // Standing still: arms hang at its sides with a slight sway, like it is standing up and
        // has its hands, instead of the hands being left wherever the game put them.
        void PlaceHandsIdle(Vector3 body)
        {
            Vector3 fwd = armDir;
            if (headT != null)
            {
                Vector3 f = headT.forward;
                f.y = 0f;
                if (f.sqrMagnitude > 0.04f) fwd = f.normalized;
            }
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            float floorY = GroundRay(body + Vector3.up * 0.1f, 4f, out RaycastHit gh) ? gh.point.y : body.y - feetOffset;
            float sway = Mathf.Sin(Time.time * 1.6f) * 0.03f;
            float k = 1f - Mathf.Exp(-Time.deltaTime * 10f);

            for (int i = 0; i < 2; i++)
            {
                Transform hand = i == 0 ? leftArm.t : rightArm.t;
                float side = i == 0 ? -1f : 1f;
                Vector3 target = new Vector3(body.x, floorY + ArmIdleHeight + sway * side, body.z)
                               + right * (side * ArmIdleSpread) + fwd * 0.08f;
                hand.position = Vector3.Lerp(hand.position, target, k);
            }
        }

        // ---------- Games (private rooms only) ----------
        // Numpad 1 = hide & seek, 2 = vote on the next game, 3 = red light green light, 0 = stop.
        // The bot is always the seeker / the caller. Other PathyBot users see big status text and can vote;
        // players without the mod just see the gorilla do its thing.

        enum GameMode { None, HideSeek, Vote, RedLight, Tag }

        const float MoveThreshold = 1.0f;     // smoothed speed (m/s) above which a player counts as "moving"
        const float RedGrace = 0.8f;          // seconds after RED before movement counts
        const float WinDist = 2.2f;           // reach the bot this closely to win red light green light
        const float StartLineDist = 6f;       // players must start at least this far from the bot
        const float RlLineup = 8f;            // seconds to get behind the start line
        const float RlTotal = 120f;           // length of a red light round
        const float HideTime = 30f;           // seconds the bot waits while everyone hides
        const float SeekTime = 180f;          // seconds the bot has to find everyone
        const float SeekChaseTime = 25f;      // gives up a sighting after this long
        const float SightRange = 20f;         // how far it can see a player in the open
        const float HearRange = 4f;           // it "hears" players this close even behind a wall
        static readonly Color[] StatusColors =
        {
            Color.white, new Color(1f, 0.25f, 0.25f), new Color(0.3f, 1f, 0.4f), new Color(1f, 0.9f, 0.3f), new Color(0.3f, 0.95f, 1f)
        };

        class Track
        {
            public int actor;
            public string name;
            public Transform t;
            public Vector3 last;
            public float speed;     // smoothed horizontal speed
            public bool inGame;     // took part from the start
            public bool done;       // found / out
        }

        const float AutoGameMin = 240f;       // a random vote every 4 to 8 minutes
        const float AutoGameMax = 480f;
        bool autoGames = true;
        float nextGameTime;

        void ScheduleNextGame() { nextGameTime = Time.time + Random.Range(AutoGameMin, AutoGameMax); }

        string AutoGameLine()
        {
            if (!autoGames) return "OFF";
            if (gameMode != GameMode.None) return "waiting for this game to finish";
            int left = Mathf.Max(0, Mathf.CeilToInt(nextGameTime - Time.time));
            return "next vote in " + (left / 60) + ":" + (left % 60).ToString("00");
        }

        GameMode gameMode = GameMode.None;
        string gameStatus = "";
        int gamePhase;
        float phaseEnd, gameEnd, nextRoster, nextScan, redStart;
        Vector3 gameFaceDir;                      // zero = no override of the body facing
        readonly Dictionary<int, Track> tracks = new Dictionary<int, Track>();

        // Status text shown on screen
        string statusText = "";
        Color statusColor = Color.white;
        float statusUntil;
        GUIStyle statusStyle;

        static string Clip(string s, int n) => s.Length > n ? s.Substring(0, n) : s;

        static int LocalActor() => PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : -1;

        void ShowStatus(string text, Color c, float secs)
        {
            statusText = text;
            statusColor = c;
            statusUntil = Time.time + secs;
        }

        // Shows the message here (when targets is null) and sends it to the other PathyBot users.
        void GameMsg(string text, int colorIdx, float secs, int[] targets, string say = null, bool urgent = false)
        {
            if (targets == null) ShowStatus(text, StatusColors[colorIdx], secs);
            if (targets == null && say != null) Speak(say, urgent);
            Send(EvGameMsg, targets, new object[] { ModTag, text, colorIdx, secs });
        }

        bool EnsureRb()
        {
            if (rb != null) return true;
            // [VERSION] GTPlayer on current builds, Player on older ones.
            var player = GTPlayer.Instance;
            if (player != null) rb = player.GetComponent<Rigidbody>();
            return rb != null;
        }

        // Bot stands still while these are running (the wander planner is paused).
        bool GameHolds() =>
            gameMode == GameMode.RedLight || gameMode == GameMode.Vote || (gameMode == GameMode.HideSeek && gamePhase == 0) || (gameMode == GameMode.Tag && gamePhase == 0);

        void HaltBot()
        {
            StopAllCoroutines();
            EndClimb();
            EndPhase();
            planning = false;
            chasing = false;
            chasePlanning = false;
            route.Clear();
            routeIndex = 0;
            botVel = Vector3.zero;
            gameFaceDir = Vector3.zero;
            if (rb != null)
            {
                Vector3 v = rb.linearVelocity;
                v.x = 0f; v.z = 0f;
                rb.linearVelocity = v;
            }
        }

        bool CanStartGame()
        {
            if (!enabledBot) { gameStatus = "turn the bot on first (F6)"; return false; }
            if (gameMode != GameMode.None) { gameStatus = "stop the current game first (Numpad 0)"; return false; }
            if (chasing) { gameStatus = "cancel the chase first (F11)"; return false; }
            if (!ConfirmedPrivate()) { gameStatus = "games only run in a private room"; return false; }
            if (GetCandidates().Count == 0) { gameStatus = "no other players found"; return false; }
            EnsureRb();
            return true;
        }

        void StopGame(string reason, bool announce)
        {
            if (gameMode == GameMode.None) return;
            gameMode = GameMode.None;
            pendingGame = -1;
            ShowSnowballs(false);
            HaltBot();
            resumeAt = Time.time + 1.5f;
            gameStatus = "last game: " + reason;
            ScheduleNextGame();
            if (announce) GameMsg(reason, 3, 5f, null, reason.ToLowerInvariant());
        }

        void RefreshRoster()
        {
            var cands = GetCandidates();
            var seen = new HashSet<int>();
            foreach (var c in cands)
            {
                seen.Add(c.actor);
                if (!tracks.TryGetValue(c.actor, out Track tr))
                {
                    tr = new Track { actor = c.actor, name = c.name, t = c.t, last = c.t.position };
                    tracks[c.actor] = tr;
                }
                else { tr.t = c.t; tr.name = c.name; }
            }
            var gone = new List<int>();
            foreach (int k in tracks.Keys) if (!seen.Contains(k)) gone.Add(k);
            foreach (int k in gone) tracks.Remove(k);
        }

        void TrackSpeeds()
        {
            float dt = Mathf.Max(Time.deltaTime, 0.001f);
            foreach (var tr in tracks.Values)
            {
                if (tr.t == null) continue;
                Vector3 p = tr.t.position;
                float inst = new Vector2(p.x - tr.last.x, p.z - tr.last.z).magnitude / dt;
                tr.speed = Mathf.Lerp(tr.speed, inst, 1f - Mathf.Exp(-dt * 5f));
                tr.last = p;
            }
        }

        int CountLive()
        {
            int n = 0;
            foreach (var tr in tracks.Values) if (tr.inGame && !tr.done && tr.t != null) n++;
            return n;
        }

        void BeginRound()
        {
            RefreshRoster();
            foreach (var tr in tracks.Values)
            {
                tr.inGame = true;
                tr.done = false;
                tr.speed = 0f;
                if (tr.t != null) tr.last = tr.t.position;
            }
            nextRoster = Time.time + 1f;
        }

        string GameLine()
        {
            switch (gameMode)
            {
                case GameMode.Vote:
                    return gamePhase == 0
                        ? "asking about " + GameNames[askGame] + ": yes " + yesCount + " / no " + noCount + ", " + Mathf.Max(0, Mathf.CeilToInt(voteEnd - Time.time)) + "s left"
                        : pendingGame >= 0 ? "yes! starting " + GameNames[pendingGame] + "..." : "no - next question...";
                case GameMode.RedLight:
                    return (gamePhase == 0 ? "lining up" : gamePhase == 1 ? "GREEN" : "RED") + " | " + CountLive() + " still in | "
                           + Mathf.Max(0, Mathf.CeilToInt(gameEnd - Time.time)) + "s left";
                case GameMode.Tag:
                    return gamePhase == 0
                        ? "runners get a head start: " + Mathf.Max(0, Mathf.CeilToInt(phaseEnd - Time.time)) + "s"
                        : "TAG | " + CountLive() + " not tagged | " + Mathf.Max(0, Mathf.CeilToInt(gameEnd - Time.time)) + "s left";
                case GameMode.HideSeek:
                    return gamePhase == 0
                        ? "hiders have " + Mathf.Max(0, Mathf.CeilToInt(phaseEnd - Time.time)) + "s to hide"
                        : "seeking | " + CountLive() + " not found | " + Mathf.Max(0, Mathf.CeilToInt(gameEnd - Time.time)) + "s left";
                default:
                    return gameStatus.Length > 0 ? gameStatus : "none running";
            }
        }

        void UpdateGame()
        {
            if (!EnsureRb()) return;
            if (!ConfirmedPrivate()) { StopGame("left the private room", false); return; }

            if (Time.time >= nextRoster) { RefreshRoster(); nextRoster = Time.time + 1f; }
            TrackSpeeds();

            switch (gameMode)
            {
                case GameMode.Vote: UpdateVote(); break;
                case GameMode.RedLight: UpdateRedLight(); break;
                case GameMode.HideSeek: UpdateHideSeek(); break;
                case GameMode.Tag: UpdateTag(); break;
            }
        }

        // ----- Asking the lobby: the snowball vote -----
        // The bot holds a green snowball in its right hand (yes) and a red one in its left (no) and asks
        // whether to play a game. Players vote by standing next to a snowball. When time is up the side
        // with more players wins. A "no" moves on to the next game; if every game gets a "no", that's it.

        static readonly string[] GameNames = { "Hide and Seek", "Red Light Green Light", "Tag" };
        const float VoteSeconds = 20f;
        const float VoteDist = 1.5f;          // stand this close (sideways) to a snowball to vote for it
        static readonly Color YesColor = new Color(0.2f, 0.9f, 0.3f);
        static readonly Color NoColor = new Color(0.95f, 0.15f, 0.15f);

        readonly List<int> askQueue = new List<int>();
        int askGame = -1, pendingGame = -1, yesCount, noCount;
        float voteEnd;
        Vector3 voteFace = Vector3.forward;

        void StartVote()
        {
            if (!CanStartGame()) return;
            HaltBot();
            askQueue.Clear();
            askQueue.Add(0);
            askQueue.Add(1);
            askQueue.Add(2);
            for (int i = askQueue.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                int tmp = askQueue[i]; askQueue[i] = askQueue[j]; askQueue[j] = tmp;
            }
            pendingGame = -1;
            gameMode = GameMode.Vote;
            AskNext();
        }

        void AskNext()
        {
            if (askQueue.Count == 0) { StopGame("OK, no game this time", true); return; }
            askGame = askQueue[0];
            askQueue.RemoveAt(0);
            gamePhase = 0;
            pendingGame = -1;
            voteEnd = Time.time + VoteSeconds;
            yesCount = noCount = 0;
            bool real = ShowRealSnowballs(true);
            if (!real)
            {
                // The game's own snowballs aren't available (they are a seasonal item): fall back to drawn balls,
                // which only PathyBot users can see. Everyone can still see the hands.
                ShowSnowballs(true);
                Send(EvSnow, null, new object[] { ModTag, LocalActor(), VoteSeconds + 1f });
            }

            string game = GameNames[askGame];
            if (real)
                GameMsg("SHOULD WE PLAY " + game.ToUpperInvariant() + " OR NOT?  GREEN SNOWBALL = YES, RED = NO", 3, VoteSeconds, null,
                        "Should we play " + game + " or not. Go to the green snowball for yes and red for no.");
            else
                GameMsg("SHOULD WE PLAY " + game.ToUpperInvariant() + " OR NOT?  RIGHT HAND (GREEN) = YES, LEFT HAND (RED) = NO", 3, VoteSeconds, null,
                        "Should we play " + game + " or not. Go to my right hand, the green ball, for yes. Go to my left hand, the red ball, for no.");
        }

        // Where the two hands (and snowballs) go: held out to the sides, facing the players.
        void OfferTargets(Vector3 fwd, out Vector3 red, out Vector3 green)
        {
            Vector3 body = rb.position;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            Vector3 c = new Vector3(body.x, body.y - feetOffset + 1.0f, body.z) + fwd * 0.5f;
            green = c + right * 0.9f;   // the bot's right hand
            red = c - right * 0.9f;     // the bot's left hand
        }

        void PlaceHandsOffer()
        {
            OfferTargets(armDir, out Vector3 red, out Vector3 green);
            leftArm.t.position = Vector3.Lerp(leftArm.t.position, red, armBlend);
            rightArm.t.position = Vector3.Lerp(rightArm.t.position, green, armBlend);
        }

        void CountVotes(Vector3 red, Vector3 green)
        {
            yesCount = noCount = 0;
            foreach (var tr in tracks.Values)
            {
                if (tr.t == null) continue;
                Vector3 p = tr.t.position;
                float dg = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(green.x, green.z));
                float dr = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(red.x, red.z));
                if (Mathf.Min(dg, dr) > VoteDist) continue;
                if (dg < dr) yesCount++; else noCount++;
            }
        }

        void UpdateVote()
        {
            // Face the group while asking.
            Vector3 sum = Vector3.zero;
            int n = 0;
            foreach (var tr in tracks.Values) if (tr.t != null) { sum += tr.t.position; n++; }
            if (n > 0)
            {
                Vector3 to = sum / n - rb.position;
                to.y = 0f;
                if (to.sqrMagnitude > 0.01f) voteFace = to.normalized;
            }
            gameFaceDir = voteFace;
            armDir = voteFace;

            if (gamePhase == 0)
            {
                CheckRealSnowballs();
                OfferTargets(voteFace, out Vector3 red, out Vector3 green);
                CountVotes(red, green);
                if (Time.time < voteEnd) return;

                ShowSnowballs(false);
                bool yes = yesCount > noCount;
                pendingGame = yes ? askGame : -1;
                GameMsg(yes ? "YES WINS!  (" + yesCount + " - " + noCount + ")" : "NO WINS  (" + yesCount + " - " + noCount + ")",
                        yes ? 2 : 1, 4f, null,
                        yes ? "Yes wins! Let's play." : "No wins.");
                gamePhase = 1;
                phaseEnd = Time.time + 4f;
                return;
            }

            if (Time.time < phaseEnd) return;
            if (pendingGame >= 0)
            {
                int g = pendingGame;
                gameMode = GameMode.None;
                pendingGame = -1;
                if (g == 0) StartHideSeek(); else if (g == 1) StartRedLight(); else StartTag();
            }
            else AskNext();
        }

        // ----- The snowballs themselves -----

        GameObject redBall, greenBall;
        Material redBallMat, greenBallMat;

        class RemoteBalls { public GameObject a, b; public float until; }
        readonly List<RemoteBalls> remoteBalls = new List<RemoteBalls>();

        Material MakeBallMaterial(Color col)
        {
            foreach (string n in new[] { "Unlit/Color", "Sprites/Default", "Hidden/Internal-Colored" })
            {
                Shader sh = Shader.Find(n);
                if (sh == null) continue;
                var m = new Material(sh);
                m.hideFlags = HideFlags.HideAndDontSave;
                if (m.HasProperty("_Color")) m.SetColor("_Color", col);
                return m;
            }
            return null;
        }

        GameObject MakeBall(Transform parent, bool yes)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            g.hideFlags = HideFlags.HideAndDontSave;
            var col = g.GetComponent<Collider>();
            if (col != null) DestroyImmediate(col); // it is only for show, never part of physics
            if (yes) { if (greenBallMat == null) greenBallMat = MakeBallMaterial(YesColor); if (greenBallMat != null) g.GetComponent<Renderer>().sharedMaterial = greenBallMat; }
            else { if (redBallMat == null) redBallMat = MakeBallMaterial(NoColor); if (redBallMat != null) g.GetComponent<Renderer>().sharedMaterial = redBallMat; }
            g.transform.SetParent(parent, false);
            g.transform.localPosition = Vector3.zero;
            g.transform.localScale = Vector3.one * (0.22f / Mathf.Max(0.01f, parent.lossyScale.x));
            return g;
        }

        // ----- The game's own snowballs (seen by everybody) -----
        // Each hand has a SnowballThrowable that the game shows to all players while it is "active"
        // (it is sent as part of the player's rig). The game also sends a colour for it. So this switches
        // the real ones on, and sets the left one red and the right one green.

        Component realBallL, realBallR;
        string snowStatus = "";
        const System.Reflection.BindingFlags AnyFlags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                                                      | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;

        static bool InvokeMethod(object o, string name, params object[] args)
        {
            if (o == null) return false;
            foreach (var m in o.GetType().GetMethods(AnyFlags))
            {
                if (m.Name != name || m.GetParameters().Length != args.Length) continue;
                m.Invoke(o, args);
                return true;
            }
            return false;
        }

        // The plain snowball for one hand (not the growing one, not a water balloon).
        static Component PickSnowball(object maker)
        {
            var arr = GetMember(maker, "snowballs") as System.Array;
            if (arr == null) return null;
            Component plain = null, any = null;
            foreach (object o in arr)
            {
                var c = o as Component;
                if (c == null) continue;
                if (any == null) any = c;
                if (c.GetType().Name != "SnowballThrowable") continue;
                var proj = GetMember(c, "projectilePrefab") as GameObject;
                if (proj != null && proj.name.IndexOf("snow", System.StringComparison.OrdinalIgnoreCase) >= 0) return c;
                if (plain == null) plain = c;
            }
            return plain ?? any;
        }

        // Sets the colour the game sends to other players for the thing in this hand.
        static void SetThrowColor(object rig, bool left, Color col)
        {
            foreach (var m in rig.GetType().GetMethods(AnyFlags))
            {
                if (m.Name != "SetThrowableProjectileColor") continue;
                var ps = m.GetParameters();
                if (ps.Length != 2) continue;
                object c = ps[1].ParameterType == typeof(Color32) ? (object)(Color32)col : col;
                m.Invoke(rig, new object[] { left, c });
                return;
            }
        }

        Component ActivateReal(object maker, object rig, bool left, Color col)
        {
            Component t = PickSnowball(maker);
            if (t == null) return null;

            // The game's own snowball maker hands the throwable the hand's velocity estimator; do the same.
            object ve = GetMember(maker, "velocityEstimator");
            var vf = t.GetType().GetField("velocityEstimator", AnyFlags);
            if (vf != null && ve != null) vf.SetValue(t, ve);

            SetThrowColor(rig, left, col);              // before it switches on, so others pick the colour up
            InvokeMethod(t, "SetSnowballActiveLocal", true);
            SetThrowColor(rig, left, col);              // some snowballs re-roll a random colour when picked up
            InvokeMethod(t, "ApplyColor", col);         // and show it on our own screen
            return t;
        }

        bool ShowRealSnowballs(bool on)
        {
            try
            {
                if (!on)
                {
                    foreach (var c in new[] { realBallL, realBallR })
                        if (c != null && c.gameObject.activeSelf) InvokeMethod(c, "SetSnowballActiveLocal", false);
                    realBallL = realBallR = null;
                    return true;
                }

                System.Type mt = System.Type.GetType("SnowballMaker, Assembly-CSharp");
                System.Type gt = System.Type.GetType("GorillaTagger, Assembly-CSharp");
                if (mt == null || gt == null) { snowStatus = "snowball classes not found"; return false; }
                object lm = GetMember(mt, "leftHandInstance"), rm = GetMember(mt, "rightHandInstance");
                object rig = GetMember(GetMember(gt, "Instance"), "offlineVRRig");
                if (lm == null || rm == null || rig == null) { snowStatus = "snowball makers / own rig not ready"; return false; }

                realBallL = ActivateReal(lm, rig, true, NoColor);    // left hand = red = no
                realBallR = ActivateReal(rm, rig, false, YesColor);  // right hand = green = yes
                bool ok = realBallL != null && realBallR != null && realBallL.gameObject.activeSelf && realBallR.gameObject.activeSelf;
                snowStatus = ok
                    ? "real snowballs on (" + realBallL.GetType().Name + " / " + realBallR.GetType().Name + ")"
                    : "real snowballs would not switch on";
                if (!ok) { ShowRealSnowballs(false); return false; }
                return true;
            }
            catch (System.Exception e)
            {
                snowStatus = "real snowballs failed: " + Short(e.InnerException != null ? e.InnerException.Message : e.Message);
                try { ShowRealSnowballs(false); } catch { }
                return false;
            }
        }

        // If the game let go of a snowball (it throws one when a grip is released), say so once.
        void CheckRealSnowballs()
        {
            if (realBallL == null && realBallR == null) return;
            if ((realBallL != null && !realBallL.gameObject.activeSelf) || (realBallR != null && !realBallR.gameObject.activeSelf))
            {
                snowStatus = "the game dropped a snowball (it does that when a grip is released) - hold both grips during votes";
                realBallL = realBallR = null;
            }
        }

        // On the bot's own screen: balls on its own hands.
        void ShowSnowballs(bool on)
        {
            if (!on) ShowRealSnowballs(false);
            if (redBall != null) Destroy(redBall);
            if (greenBall != null) Destroy(greenBall);
            redBall = greenBall = null;
            if (!on || leftArm.t == null || rightArm.t == null) return;
            redBall = MakeBall(leftArm.t, false);     // left hand = no
            greenBall = MakeBall(rightArm.t, true);   // right hand = yes
        }

        // On other PathyBot users' screens: balls on the bot's rig hands (it has no balls in the real game).
        void ShowRemoteSnowballs(int actor, float secs)
        {
            ClearRemoteBalls();
            foreach (var c in GetCandidates())
            {
                if (c.actor != actor || c.t == null) continue;
                var rig = c.t.GetComponent<VRRig>();
                if (rig == null) return;
                var lh = GetMember(GetMember(rig, "leftHand"), "rigTarget") as Transform;
                var rh = GetMember(GetMember(rig, "rightHand"), "rigTarget") as Transform;
                if (lh == null || rh == null) return;
                remoteBalls.Add(new RemoteBalls { a = MakeBall(lh, false), b = MakeBall(rh, true), until = Time.time + secs });
                return;
            }
        }

        void ClearRemoteBalls()
        {
            foreach (var r in remoteBalls)
            {
                if (r.a != null) Destroy(r.a);
                if (r.b != null) Destroy(r.b);
            }
            remoteBalls.Clear();
        }

        void UpdateRemoteBalls()
        {
            if (remoteBalls.Count == 0) return;
            for (int i = remoteBalls.Count - 1; i >= 0; i--)
            {
                var r = remoteBalls[i];
                if (Time.time < r.until && r.a != null && r.b != null) continue;
                if (r.a != null) Destroy(r.a);
                if (r.b != null) Destroy(r.b);
                remoteBalls.RemoveAt(i);
            }
        }

        // ----- Red light, green light -----

        void StartRedLight()
        {
            if (!CanStartGame()) return;
            HaltBot();
            BeginRound();
            gameMode = GameMode.RedLight;
            gamePhase = 0;
            phaseEnd = Time.time + RlLineup;
            gameEnd = phaseEnd + RlTotal;
            GameMsg("RED LIGHT, GREEN LIGHT!  Stand " + Mathf.RoundToInt(StartLineDist) + "m+ back", 4, RlLineup, null,
                    "Red light, green light! Stand back, and run to me when I say green.");
            Prefetch("Green light!");
            Prefetch("Red light!");
        }

        void UpdateRedLight()
        {
            Vector3 me = rb.position;

            // Where the remaining players are (for facing them / turning away).
            Vector3 sum = Vector3.zero;
            int n = 0;
            foreach (var tr in tracks.Values)
                if (tr.inGame && !tr.done && tr.t != null) { sum += tr.t.position; n++; }
            Vector3 toPlayers = n > 0 ? sum / n - me : Vector3.zero;
            toPlayers.y = 0f;
            Vector3 faceThem = toPlayers.sqrMagnitude > 0.01f ? toPlayers.normalized : gameFaceDir;

            if (Time.time >= gameEnd) { StopGame("TIME! Nobody reached the bot", true); return; }

            if (gamePhase == 0)
            {
                gameFaceDir = -faceThem;
                if (Time.time < phaseEnd) return;
                // Anyone standing too close to the bot can't take part this round.
                foreach (var tr in tracks.Values)
                    if (tr.t != null && Vector3.Distance(me, tr.t.position) < StartLineDist) tr.inGame = false;
                StartGreen();
                return;
            }

            if (CountLive() == 0) { StopGame("Everybody is out!", true); return; }

            // Reaching the bot wins.
            foreach (var tr in tracks.Values)
            {
                if (!tr.inGame || tr.done || tr.t == null) continue;
                if (Vector3.Distance(me, tr.t.position) < WinDist)
                {
                    StopGame(tr.name.ToUpperInvariant() + " WINS!", true);
                    return;
                }
            }

            if (gamePhase == 1)
            {
                gameFaceDir = -faceThem;                    // back turned: go!
                if (Time.time >= phaseEnd) StartRed();
            }
            else
            {
                gameFaceDir = faceThem;                     // watching: freeze!
                if (Time.time - redStart > RedGrace)
                {
                    foreach (var tr in tracks.Values)
                    {
                        if (!tr.inGame || tr.done || tr.t == null || tr.speed < MoveThreshold) continue;
                        tr.done = true;
                        GameMsg(tr.name.ToUpperInvariant() + " MOVED - OUT!", 1, 3f, null, CleanName(tr.name) + " moved. You are out!", true);
                    }
                }
                if (Time.time >= phaseEnd) StartGreen();
            }
        }

        void StartGreen()
        {
            gamePhase = 1;
            float len = Random.Range(2.5f, 5.5f);
            phaseEnd = Time.time + len;
            foreach (var tr in tracks.Values) tr.speed = 0f;
            GameMsg("GREEN LIGHT", 2, len + 0.5f, null, "Green light!", true);
        }

        void StartRed()
        {
            gamePhase = 2;
            redStart = Time.time;
            float len = Random.Range(2.5f, 4.5f);
            phaseEnd = Time.time + len;
            GameMsg("RED LIGHT", 1, len + 0.5f, null, "Red light!", true);
        }

        // ----- Hide and seek -----

        void StartHideSeek()
        {
            if (!CanStartGame()) return;
            HaltBot();
            BeginRound();
            gameMode = GameMode.HideSeek;
            gamePhase = 0;
            phaseEnd = Time.time + HideTime;
            gameEnd = phaseEnd + SeekTime;
            GameMsg("HIDE!  I'm counting to " + Mathf.RoundToInt(HideTime), 4, 5f, null,
                    "Go hide! I am counting to " + Mathf.RoundToInt(HideTime) + ".");
        }

        // Can the bot see this spot from where it stands? (Its own body doesn't block the check.)
        bool CanSee(Vector3 target)
        {
            Vector3 eye = headT != null ? headT.position : rb.position + Vector3.up * 0.5f;
            Vector3 d = target - eye;
            float m = d.magnitude;
            if (m < 1f) return true;
            return !LineBlocked(eye, eye + d / m * (m - 0.7f)); // stop short so the hider's own body doesn't block
        }

        void UpdateHideSeek()
        {
            if (gamePhase == 0)
            {
                if (Time.time < phaseEnd) return;
                gamePhase = 1;
                resumeAt = Time.time;
                nextScan = Time.time + 1f;
                GameMsg("READY OR NOT, HERE I COME!", 1, 4f, null, "Ready or not, here I come!");
                return;
            }

            if (CountLive() == 0) { StopGame("ALL FOUND! The seeker wins", true); return; }
            if (Time.time >= gameEnd)
            {
                string left = "";
                foreach (var tr in tracks.Values) if (tr.inGame && !tr.done) left += (left.Length > 0 ? ", " : "") + tr.name;
                StopGame("TIME! Hiders win: " + Clip(left, 60), true);
                return;
            }

            if (chasing || Time.time < nextScan) return;
            nextScan = Time.time + 0.4f;

            Track best = null;
            float bestD = float.MaxValue;
            foreach (var tr in tracks.Values)
            {
                if (!tr.inGame || tr.done || tr.t == null) continue;
                float d = Vector3.Distance(rb.position, tr.t.position);
                bool seen = d < HearRange || (d < SightRange && CanSee(tr.t.position));
                if (seen && d < bestD) { best = tr; bestD = d; }
            }
            if (best != null) BeginSeekChase(best);
        }

        // ----- Tag -----
        // The bot is "it" and runs after the nearest runner it can find. Anyone it tags is out.
        // Last runner standing (or the clock running out) wins for the runners.

        const float TagHeadStart = 10f;
        const float TagTotal = 180f;
        const float TagChaseTime = 30f;
        bool prevN4;

        void StartTag()
        {
            if (!CanStartGame()) return;
            HaltBot();
            BeginRound();
            gameMode = GameMode.Tag;
            gamePhase = 0;
            phaseEnd = Time.time + TagHeadStart;
            gameEnd = phaseEnd + TagTotal;
            GameMsg("TAG!  I'm it. Run!", 4, 5f, null,
                    "Let's play tag! I am it. You have " + Mathf.RoundToInt(TagHeadStart) + " seconds to run.");
        }

        void UpdateTag()
        {
            if (gamePhase == 0)
            {
                if (Time.time < phaseEnd) return;
                gamePhase = 1;
                resumeAt = Time.time;
                nextScan = Time.time;
                GameMsg("I'M COMING!", 1, 3f, null, "Here I come!");
                return;
            }

            if (CountLive() == 0) { StopGame("EVERYONE TAGGED! The bot wins", true); return; }
            if (Time.time >= gameEnd)
            {
                string left = "";
                foreach (var tr in tracks.Values) if (tr.inGame && !tr.done) left += (left.Length > 0 ? ", " : "") + tr.name;
                StopGame("TIME! Runners win: " + Clip(left, 60), true);
                return;
            }

            if (chasing || Time.time < nextScan) return;
            nextScan = Time.time + 0.5f;

            Track best = null;
            float bestD = float.MaxValue;
            foreach (var tr in tracks.Values)
            {
                if (!tr.inGame || tr.done || tr.t == null) continue;
                float d = Vector3.Distance(rb.position, tr.t.position);
                if (d < bestD) { best = tr; bestD = d; }
            }
            if (best != null)
            {
                BeginSeekChase(best);
                chaseDeadline = Time.time + TagChaseTime;
            }
        }

        void BeginSeekChase(Track tr)
        {
            StopAllCoroutines();
            EndClimb();
            planning = false;
            chasePlanning = false;
            route.Clear();
            routeIndex = 0;

            chaseVel = Vector3.zero;
            targetTrail.Clear();
            chaseLastPos = tr.t != null ? tr.t.position : Vector3.zero;
            chasing = true;
            chaseActor = tr.actor;
            chaseName = tr.name;
            chaseT = tr.t;
            chaseGoTime = Time.time;
            chaseDeadline = Time.time + SeekChaseTime;
            nextReplan = Time.time;
            GameMsg("I SEE YOU!", 1, 2f, new[] { tr.actor });   // only the person it spotted sees this
        }

        void DrawGameUi()
        {
            if (statusText.Length > 0 && Time.time < statusUntil)
            {
                if (statusStyle == null)
                    statusStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontStyle = FontStyle.Bold, fontSize = 46, wordWrap = true };
                var r = new Rect(Screen.width * 0.1f, Screen.height * 0.08f, Screen.width * 0.8f, 160f);
                statusStyle.normal.textColor = new Color(0f, 0f, 0f, 0.9f);
                GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), statusText, statusStyle);
                statusStyle.normal.textColor = statusColor;
                GUI.Label(r, statusText, statusStyle);
            }
        }

        // ---------- Path simulation ----------
        // Before it walks a new route, the bot "drives" it in its head: every 25 cm along every segment it
        // checks there is floor underneath and nothing solid in the body's way (with a little extra margin).
        // If the simulated run would hit something, the spot is remembered as bad, a red cross is shown there,
        // and it plans again (up to 6 retraces) until the route is clear.

        bool simOn = true;
        string simStatus = "-";
        int simAttempts;
        const int SimMaxAttempts = 6;
        LineRenderer simMark;
        float simMarkUntil;

        bool SimBlockedAt(Vector3 feet)
        {
            var cols = Physics.OverlapCapsule(feet + Vector3.up * 0.45f, feet + Vector3.up * 1.35f, 0.33f, ~0, QueryTriggerInteraction.Ignore);
            foreach (var c in cols) if (!IsMine(c)) return true;
            return false;
        }

        // Index of the first waypoint whose approach would hit something, or -1 when the whole route is clear.
        int SimulateRoute(List<Wp> r, Vector3 start, out Vector3 hit)
        {
            hit = default;
            Vector3 prev = start;
            int n = Mathf.Min(r.Count, 80);
            for (int i = 0; i < n; i++)
            {
                Wp w = r[i];
                if (w.climb || w.phase) { prev = w.pos; continue; }   // those move by their own rules
                Vector3 a = prev, b = w.pos;
                int steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(a, b) / 0.25f));
                float lastY = a.y;
                for (int k = 1; k <= steps; k++)
                {
                    Vector3 p = Vector3.Lerp(a, b, k / (float)steps);
                    Vector3 g = p;
                    if (GroundRay(new Vector3(p.x, lastY + StepHeight + 0.05f, p.z), StepHeight + MaxDrop + 0.05f, out RaycastHit gh))
                    { g = gh.point; lastY = g.y; }
                    else if (k < steps) { hit = p; return i; }          // would walk off into nothing
                    if (SimBlockedAt(g)) { hit = g; return i; }
                }
                prev = b;
            }
            return -1;
        }

        // True when the route is fine to follow. False means "it would hit something: plan again".
        bool SimPasses(List<Wp> r, Vector3 start)
        {
            if (!simOn || r == null || r.Count == 0) return true;
            int bad = SimulateRoute(r, start, out Vector3 hitPos);
            if (bad < 0) { simAttempts = 0; simStatus = "clear (" + r.Count + " steps)"; return true; }
            if (simAttempts >= SimMaxAttempts)
            {
                simAttempts = 0;
                simStatus = "still touching something after " + SimMaxAttempts + " retraces, going anyway";
                return true;
            }
            simAttempts++;
            MarkAvoid(hitPos, 1);
            ShowSimHit(hitPos);
            simStatus = "would hit something at step " + (bad + 1) + ", retracing (" + simAttempts + "/" + SimMaxAttempts + ")";
            return false;
        }

        void ShowSimHit(Vector3 p)
        {
            if (simMark == null)
            {
                simMark = MakeColoredLine("PathyBot_SimHit", new Color(1f, 0.15f, 0.15f, 1f), out Material _m);
                if (simMark == null) return;
            }
            Vector3 c = p + Vector3.up * 0.5f;
            Vector3[] pts =
            {
                c + Vector3.left * 0.35f, c + Vector3.right * 0.35f, c,
                c + Vector3.back * 0.35f, c + Vector3.forward * 0.35f, c,
                c + Vector3.down * 0.35f, c + Vector3.up * 0.35f
            };
            simMark.positionCount = pts.Length;
            for (int i = 0; i < pts.Length; i++) simMark.SetPosition(i, pts[i]);
            simMark.enabled = true;
            simMarkUntil = Time.time + 3f;
        }

        void HideSimMark()
        {
            if (simMark != null && simMark.enabled && Time.time > simMarkUntil) simMark.enabled = false;
        }

        // Extra clearance check used by the planner: is the space around a cell tight?
        bool WideBlocked(Vector3 feet)
        {
            var cols = Physics.OverlapCapsule(feet + Vector3.up * 0.5f, feet + Vector3.up * 1.2f, 0.62f, ~0, QueryTriggerInteraction.Ignore);
            foreach (var c in cols) if (!IsMine(c)) return true;
            return false;
        }

        // ---------- Top bar: every feature as a button or switch ----------
        // Click with the mouse on the game window. F4 hides or shows the bar.

        class BarItem
        {
            public string label;
            public System.Func<bool> on;   // null = plain button, otherwise lit up while true
            public System.Action click;
        }

        bool barOn = true, prevF4;
        float barH;
        List<BarItem> barItems;
        GUIStyle barBtn;
        Texture2D barTex;

        static BarItem Sep() { return new BarItem { label = "|" }; }
        static BarItem Btn(string label, System.Action click) { return new BarItem { label = label, click = click }; }
        static BarItem Sw(string label, System.Func<bool> on, System.Action click) { return new BarItem { label = label, on = on, click = click }; }

        void BuildBar()
        {
            barItems = new List<BarItem>
            {
                Sw("Bot", () => enabledBot, () => toggleRequest = true),
                Sep(),
                Sw("Path line", () => linesOn, () => linesOn = !linesOn),
                Sw("Arms", () => armsOn, () => { armsOn = !armsOn; armStatus = armsOn ? "on" : "OFF"; }),
                Btn("Arm source", () => { armSource = (armSource + 1) % ArmSources.Length; ResolveArms(); }),
                Sw("Face", () => faceOn, () => faceOn = !faceOn),
                Sw("Simulate path", () => simOn, () => simOn = !simOn),
                Sw("Walls", () => PathyBotPlugin.CfgWallClimb != null && PathyBotPlugin.CfgWallClimb.Value,
                    () => PathyBotPlugin.CfgWallClimb.Value = !PathyBotPlugin.CfgWallClimb.Value),
                Btn("Unstick", () => ManualUnstick()),
                Sep(),
                Btn("Hide & Seek", () => StartHideSeek()),
                Btn("Vote", () => StartVote()),
                Btn("Red Light", () => StartRedLight()),
                Btn("Tag", () => StartTag()),
                Sw("Stop game", () => gameMode != GameMode.None, () => { if (gameMode != GameMode.None) StopGame("stopped", true); }),
                Sw("Auto games", () => autoGames, () => { autoGames = !autoGames; if (autoGames) ScheduleNextGame(); }),
                Sep(),
                Sw("Chase", () => chasing, () => { if (!enabledBot) return; if (chasing) EndChase("cancelled", false); else if (gameMode == GameMode.None) TryStartChase(); }),
                Sw("Auto chase", () => autoChase, () => { autoChase = !autoChase; if (autoChase) nextChaseTime = Time.time + ChaseInterval; }),
                Sw("Accept incoming", () => acceptIncoming, () => acceptIncoming = !acceptIncoming),
                Sep(),
                Sw("AI listens", () => talkOn, () => ToggleTalk()),
                Sw("Talk (mic)", () => pcTalking, () => TogglePcTalk()),
                Sw("Text box", () => chatOpen, () => { chatOpen = !chatOpen; chatText = ""; }),
                Sw("Mute voice", () => voiceMuted, () => { voiceMuted = !voiceMuted; if (voiceMuted) StopVoice(); }),
                Sw("Voice swap", () => PathyBotPlugin.CfgVoiceSwap != null && PathyBotPlugin.CfgVoiceSwap.Value,
                    () => PathyBotPlugin.CfgVoiceSwap.Value = !PathyBotPlugin.CfgVoiceSwap.Value),
                Btn("Test scream", () => PlayScream()),
                Sep(),
                Sw("Overlay", () => PathyBotPlugin.CfgOverlay != null && PathyBotPlugin.CfgOverlay.Value,
                    () => PathyBotPlugin.CfgOverlay.Value = !PathyBotPlugin.CfgOverlay.Value),
                Btn("Diagnostics", () => DumpDiagnostics()),
                Btn("Hide bar (F4)", () => barOn = false)
            };
        }

        void DrawBar()
        {
            if (barItems == null) BuildBar();
            if (barTex == null)
            {
                barTex = new Texture2D(1, 1);
                barTex.SetPixel(0, 0, Color.white);
                barTex.Apply();
            }
            if (barBtn == null)
                barBtn = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 12, fontStyle = FontStyle.Bold, clipping = TextClipping.Clip };

            if (!barOn)
            {
                barH = 0f;
                var small = new Rect(Screen.width * 0.5f - 50f, 2f, 100f, 20f);
                Fill(small, new Color(0.07f, 0.08f, 0.1f, 0.8f), barTex);
                barBtn.normal.textColor = Color.white;
                if (GUI.Button(small, "PathyBot  v", barBtn)) barOn = true;
                return;
            }

            // Pass 1: lay the buttons out in rows.
            float totalW = Mathf.Min(Screen.width - 20f, 1220f);
            float x0 = (Screen.width - totalW) * 0.5f;
            const float rowH = 24f;
            float cx = x0 + 8f, cy = 4f;
            var rects = new Rect[barItems.Count];
            for (int i = 0; i < barItems.Count; i++)
            {
                var it = barItems[i];
                bool sep = it.label == "|";
                float w = sep ? 10f : 16f + it.label.Length * 7.4f;
                if (cx + w > x0 + totalW - 8f && !sep) { cx = x0 + 8f; cy += rowH + 4f; }
                rects[i] = new Rect(cx, cy, w, rowH);
                cx += w + (sep ? 2f : 5f);
            }
            barH = cy + rowH + 8f;

            Fill(new Rect(x0, 0f, totalW, barH), new Color(0.06f, 0.07f, 0.09f, 0.9f), barTex);
            Fill(new Rect(x0, barH - 1f, totalW, 1f), new Color(0.25f, 0.95f, 1f, 0.5f), barTex);

            // Pass 2: draw them.
            Vector2 mouse = Event.current != null ? Event.current.mousePosition : Vector2.zero;
            for (int i = 0; i < barItems.Count; i++)
            {
                var it = barItems[i];
                Rect r = rects[i];
                if (it.label == "|") { Fill(new Rect(r.x + 4f, r.y + 3f, 1f, r.height - 6f), new Color(1f, 1f, 1f, 0.18f), barTex); continue; }
                bool lit = it.on != null && it.on();
                bool hover = r.Contains(mouse);
                Color bg = lit ? new Color(0.12f, 0.55f, 0.3f, 0.95f) : new Color(0.17f, 0.19f, 0.24f, 0.95f);
                if (hover) bg = Color.Lerp(bg, Color.white, 0.18f);
                Fill(r, bg, barTex);
                barBtn.normal.textColor = lit ? Color.white : new Color(0.85f, 0.9f, 1f);
                if (GUI.Button(r, it.label, barBtn))
                {
                    try { it.click(); }
                    catch (System.Exception ex) { lastFail = "button '" + it.label + "' failed: " + Short(ex.Message); }
                }
            }
        }

        // ---------- On-screen overlay: voice panel, chat log and name tag ----------
        // A small dark panel in the top right like the promo: who is speaking with a green level bar,
        // then the recent chat lines (the bot's name in blue), plus a floating name tag over the bot.

        const string BotName = "PathyBot";

        class ChatLine { public string name, text; public float time; }
        readonly List<ChatLine> chatLog = new List<ChatLine>();
        Texture2D overlayTex;
        GUIStyle ovName, ovText, ovSmall, tagStyle;
        float levelSmooth;

        void LogLine(string name, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            chatLog.Add(new ChatLine { name = name, text = text, time = Time.time });
            while (chatLog.Count > 6) chatLog.RemoveAt(0);
        }

        static void Fill(Rect r, Color c, Texture2D tex)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, tex);
            GUI.color = old;
        }

        void DrawOverlay()
        {
            if (PathyBotPlugin.CfgOverlay == null || !PathyBotPlugin.CfgOverlay.Value || !enabledBot) return;
            if (overlayTex == null)
            {
                overlayTex = new Texture2D(1, 1);
                overlayTex.SetPixel(0, 0, Color.white);
                overlayTex.Apply();
            }
            if (ovName == null)
            {
                ovName = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, clipping = TextClipping.Clip };
                ovText = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true, clipping = TextClipping.Clip };
                ovSmall = new GUIStyle(GUI.skin.label) { fontSize = 10, alignment = TextAnchor.MiddleRight };
                tagStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            }

            // Which chat lines are still fresh (they fade after 9 seconds).
            var shown = new List<ChatLine>();
            foreach (var c in chatLog) if (Time.time - c.time < 9f) shown.Add(c);
            bool speaking = pumping || injecting || screaming;
            if (!speaking && shown.Count == 0) { DrawNameTag(); return; }

            float w = 300f, x = Screen.width - w - 16f, y = 16f + barH;
            float h = 34f + shown.Count * 34f;
            Fill(new Rect(x, y, w, h), new Color(0.07f, 0.08f, 0.1f, 0.82f), overlayTex);

            // Row 1: speaking dot, name, level bar.
            levelSmooth = Mathf.Lerp(levelSmooth, speaking ? 0.35f + 0.65f * Mathf.PerlinNoise(Time.time * 6f, 0.3f) : 0f, 0.3f);
            Fill(new Rect(x + 10f, y + 13f, 7f, 7f), speaking ? new Color(0.25f, 0.85f, 0.4f) : new Color(0.4f, 0.4f, 0.4f), overlayTex);
            ovName.normal.textColor = Color.white;
            GUI.Label(new Rect(x + 24f, y + 5f, 150f, 22f), BotName.ToUpperInvariant(), ovName);
            Fill(new Rect(x + w - 90f, y + 14f, 80f, 4f), new Color(1f, 1f, 1f, 0.12f), overlayTex);
            Fill(new Rect(x + w - 90f, y + 14f, 80f * levelSmooth, 4f), new Color(0.25f, 0.85f, 0.4f), overlayTex);
            ovSmall.normal.textColor = new Color(0.6f, 0.7f, 0.9f);
            if (speaking) GUI.Label(new Rect(x + w - 150f, y + 6f, 56f, 18f), "speaking", ovSmall);

            // Chat lines: coloured name, then the text under it.
            float ly = y + 32f;
            foreach (var c in shown)
            {
                float a = Mathf.Clamp01((9f - (Time.time - c.time)) / 1.5f);
                bool me = c.name == BotName;
                ovName.normal.textColor = me ? new Color(0.35f, 0.55f, 1f, a) : new Color(0.95f, 0.8f, 0.3f, a);
                GUI.Label(new Rect(x + 10f, ly, w - 20f, 18f), c.name, ovName);
                ovText.normal.textColor = new Color(0.85f, 0.88f, 0.95f, a);
                GUI.Label(new Rect(x + 10f, ly + 15f, w - 20f, 18f), c.text, ovText);
                ly += 34f;
            }
            DrawNameTag();
        }

        // The bot's name floating over its head, white with a dark outline.
        void DrawNameTag()
        {
            var cam = Camera.main;
            if (cam == null || headT == null) return;
            Vector3 wp = headT.position + Vector3.up * 0.45f;
            if (Vector3.Distance(cam.transform.position, wp) < 1.5f) return;   // not when the camera is the bot's own head
            Vector3 sp = cam.WorldToScreenPoint(wp);
            if (sp.z <= 0f) return;
            var r = new Rect(sp.x - 120f, Screen.height - sp.y - 14f, 240f, 28f);
            string t = BotName.ToUpperInvariant();
            tagStyle.normal.textColor = Color.black;
            for (int dx = -2; dx <= 2; dx += 2)
                for (int dy = -2; dy <= 2; dy += 2)
                    if (dx != 0 || dy != 0) GUI.Label(new Rect(r.x + dx, r.y + dy, r.width, r.height), t, tagStyle);
            tagStyle.normal.textColor = Color.white;
            GUI.Label(r, t, tagStyle);
        }

        // ---------- Catch: stick to the face and scream ----------
        // On a catch the bot flies to the caught player's face (its colliders are off, so walls and
        // bodies can't stop it), stays on it for a couple of seconds looking at them, and plays the
        // scream sound on your speakers and through the game mic (private rooms), then goes back to normal.

        const float StickSeconds = 2.4f;
        const float StickGap = 0.3f;          // metres in front of their face
        bool sticking, screaming, screamLoading;
        Transform stickHead;
        float stickUntil;
        AudioClip screamClip;
        string screamStatus = "loads when the bot is turned on";
        CoroutineHost screamHost;

        Transform FindHeadOf(Transform t)
        {
            if (t == null) return null;
            var rig = t.GetComponent<VRRig>();
            if (rig == null) rig = t.GetComponentInParent<VRRig>();
            if (rig != null)
            {
                var hm = GetMember(rig, "headMesh") as Transform;
                if (hm != null) return hm;
                object head = GetMember(rig, "head");
                var rt = head != null ? GetMember(head, "rigTarget") as Transform : null;
                if (rt != null) return rt;
            }
            foreach (var c in t.GetComponentsInChildren<Transform>(true))
            {
                string n = c.name.ToLowerInvariant();
                if (n.Contains("head") && !n.Contains("hand")) return c;
            }
            return null;
        }

        void BeginFaceStick()
        {
            if (chaseT == null || rb == null) return;
            stickHead = FindHeadOf(chaseT);
            if (stickHead == null) return;
            EndPhase();
            phasedColliders.Clear();
            foreach (var c in rb.GetComponentsInChildren<Collider>())
                if (c != null && c.enabled && !c.isTrigger) { c.enabled = false; phasedColliders.Add(c); }
            if (!phaseGravityTaken)
            {
                phaseSavedGravity = rb.useGravity;
                rb.useGravity = false;
                phaseGravityTaken = true;
            }
            sticking = true;
            stickUntil = Time.time + StickSeconds;
            lungeUntil = stickUntil;
        }

        void DoStick()
        {
            if (!enabledBot || stickHead == null || Time.time >= stickUntil)
            {
                EndPhase();                       // also switches sticking off and restores colliders and gravity
                resumeAt = Time.time + 0.5f;
                if (gameMode == GameMode.None) gameFaceDir = Vector3.zero;
                return;
            }
            Vector3 hp = stickHead.position;
            Vector3 hf = stickHead.forward;
            Vector3 want = hp + hf * StickGap;
            Vector3 off = headT != null ? headT.position - rb.position : Vector3.up * 0.4f;
            rb.position = want - off;
            rb.linearVelocity = Vector3.zero;
            botVel = Vector3.zero;
            Vector3 face = -hf; face.y = 0f;
            if (face.sqrMagnitude > 0.01f)
            {
                gameFaceDir = face.normalized;
                heading = gameFaceDir;
                armDir = gameFaceDir;
            }
        }

        string FindScreamFile()
        {
            string want = PathyBotPlugin.CfgScream != null ? PathyBotPlugin.CfgScream.Value : "scream.wav";
            if (string.IsNullOrWhiteSpace(want)) return null;
            try
            {
                if (System.IO.File.Exists(want)) return want;
                string stem = System.IO.Path.GetFileNameWithoutExtension(want);
                string[] roots = { BepInEx.Paths.PluginPath, BepInEx.Paths.BepInExRootPath, BepInEx.Paths.GameRootPath };
                string[] exts = { ".wav", ".mp3", ".ogg", ".m4a", ".flac", ".wma" };
                for (int pass = 0; pass < 2; pass++)
                    foreach (string r in roots)
                    {
                        if (string.IsNullOrEmpty(r) || !System.IO.Directory.Exists(r)) continue;
                        var opt = r == BepInEx.Paths.GameRootPath ? System.IO.SearchOption.TopDirectoryOnly : System.IO.SearchOption.AllDirectories;
                        string pattern = pass == 0 ? want : stem + ".*";
                        foreach (string f in System.IO.Directory.GetFiles(r, pattern, opt))
                        {
                            if (pass == 0) return f;
                            string e = System.IO.Path.GetExtension(f).ToLowerInvariant();
                            if (System.Array.IndexOf(exts, e) >= 0) return f;
                        }
                    }
            }
            catch (System.Exception) { }
            return null;
        }

        IEnumerator LoadScream()
        {
            if (screamClip != null || screamLoading) yield break;
            screamLoading = true;
            string file = FindScreamFile();
            if (file == null)
            {
                screamStatus = "no scream file found (put scream.wav somewhere under BepInEx\\plugins)";
                screamLoading = false;
                yield break;
            }
            string name = System.IO.Path.GetFileName(file);
            string err = null;
            AudioClip clip = null;
            string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".wav")
            {
                try { clip = WavToClip(System.IO.File.ReadAllBytes(file), out err); }
                catch (System.Exception ex) { err = ex.Message; }
            }

            if (clip == null)
            {
                // mp3 / ogg (or a wav the plain reader can't handle): let the game's own decoder do it.
                screamStatus = "decoding " + name + "...";
                AudioClip uc = null; string uerr = null;
                yield return screamHost.StartCoroutine(LoadClipViaUnity(file, ext, (c, e) => { uc = c; uerr = e; }));
                clip = uc;
                if (clip == null && uerr != null) err = uerr;
            }

            if (clip == null)
            {
                // Still nothing: convert it with ffmpeg if it is installed. Not a plain 16-bit wav (mp3, ogg, 24-bit wav...): convert it with ffmpeg if it is installed.
                screamStatus = "converting " + name + "...";
                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pathybot_scream_" + System.Guid.NewGuid().ToString("N") + ".wav");
                var res = new AsyncResult();
                yield return screamHost.StartCoroutine(RunProgram("ffmpeg",
                    "-y -loglevel error -i \"" + file + "\" -ac 1 -ar 44100 -c:a pcm_s16le \"" + tmp + "\"", null, 30, res));
                if (res.error == null)
                {
                    try { clip = WavToClip(System.IO.File.ReadAllBytes(tmp), out err); }
                    catch (System.Exception ex) { err = ex.Message; }
                }
                else err = "ffmpeg not available (" + Short(res.error) + ")";
                TryDelete(tmp);
            }

            if (clip == null) screamStatus = "can't use " + name + ": save it as a 16-bit .wav (" + Short(err) + ")";
            else { screamClip = clip; screamStatus = "ready: " + name + " (" + clip.length.ToString("0.0") + "s)"; }
            screamLoading = false;
        }

        IEnumerator LoadClipViaUnity(string file, string ext, System.Action<AudioClip, string> done)
        {
            object req = null;
            string e = null;
            System.Type dh = null;
            AudioType at = ext == ".mp3" ? AudioType.MPEG : ext == ".ogg" ? AudioType.OGGVORBIS : ext == ".wav" ? AudioType.WAV : AudioType.UNKNOWN;
            try
            {
                var mm = System.Type.GetType("UnityEngine.Networking.UnityWebRequestMultimedia, UnityEngine.UnityWebRequestAudioModule");
                dh = System.Type.GetType("UnityEngine.Networking.DownloadHandlerAudioClip, UnityEngine.UnityWebRequestAudioModule");
                if (mm == null || dh == null) e = "this game build has no audio loader";
                else
                {
                    var m = mm.GetMethod("GetAudioClip", new[] { typeof(string), typeof(AudioType) });
                    req = m.Invoke(null, new object[] { new System.Uri(file).AbsoluteUri, at });
                }
            }
            catch (System.Exception ex) { e = ex.Message; }
            if (req == null) { done(null, e); yield break; }

            object op = null;
            try { op = req.GetType().GetMethod("SendWebRequest").Invoke(req, null); }
            catch (System.Exception ex) { e = ex.Message; }
            var ao = op as AsyncOperation;
            if (ao == null) { done(null, e ?? "could not start loading"); yield break; }
            while (!ao.isDone) yield return null;

            AudioClip clip = null;
            try
            {
                string rerr = GetMember(req, "error") as string;
                if (!string.IsNullOrEmpty(rerr)) e = rerr;
                else
                {
                    var gc = dh.GetMethod("GetContent", new[] { req.GetType() });
                    clip = gc.Invoke(null, new object[] { req }) as AudioClip;
                    if (clip == null) e = "could not decode the audio";
                }
            }
            catch (System.Exception ex) { e = ex.Message; }
            try { var dis = req as System.IDisposable; if (dis != null) dis.Dispose(); } catch (System.Exception) { }
            done(clip, e);
        }

        void PlayScream()
        {
            if (voiceMuted || PathyBotPlugin.CfgSpeak == null || !PathyBotPlugin.CfgSpeak.Value) return;
            if (screamClip == null) { if (screamHost != null) screamHost.StartCoroutine(LoadScream()); return; }
            StopVoice();
            screamHost.StartCoroutine(ScreamOne());
        }

        IEnumerator ScreamOne()
        {
            screaming = true;
            var clip = screamClip;
            PlayLocal(clip);
            bool sent = BeginMicInject(clip);
            yield return new WaitForSeconds(clip.length + (sent ? 0.4f : 0f));
            if (sent) EndMicInject();
            screaming = false;
        }

        void StopScream()
        {
            if (screamHost != null) screamHost.StopAllCoroutines();
            if (screaming && injecting) EndMicInject();
            screaming = false;
            if (voiceAudio != null) voiceAudio.Stop();
        }

        // ---------- Talk: players speak to the bot and a local AI answers ----------
        // Everything stays on your own PC: the bot listens to the voices of players standing near it,
        // a local whisper.cpp server turns the speech into text, your local Ollama model writes a short
        // reply, and the reply is spoken through the bot's voice. Nothing is saved, nothing leaves the
        // machine (only localhost addresses are accepted), it only works in a confirmed private room,
        // it announces out loud that it is listening, and it only answers speech that contains a wake
        // word (default "pathy" or "bot"). Page Up turns it on and off.

        class Listener
        {
            public string name;
            public Transform t;
            public int actor;
            public readonly List<float> utt = new List<float>();
            public int voiced;
            public float lastLoud;
            public float recentLoud = -999f;   // last time this player was audibly talking near the bot
        }

        const int FloodSpeakers = 4;          // this many people talking at once...
        const float FloodSeconds = 3f;        // ...for this long counts as too much noise
        const float FloodPause = 60f;
        float talkPausedUntil, floodSince = -1f;

        void TriggerFlood()
        {
            floodSince = -1f;
            talkPausedUntil = Time.time + FloodPause;
            talkStatus = "too much noise, AI is paused for " + Mathf.CeilToInt(FloodPause) + "s";
            foreach (var kv in listeners) { kv.Value.utt.Clear(); kv.Value.voiced = 0; }
            Speak("Woah, that's a lot of noise. I'm going to turn off my AI for a bit.", true);
        }

        const float TalkRange = 8f;           // only players this close to the bot are listened to
        const float TalkThreshold = 0.004f;   // loudness that counts as speech
        const float TalkSilence = 0.8f;       // quiet this long ends a sentence
        const float TalkMaxSeconds = 12f;
        static readonly System.Net.Http.HttpClient Http =
            new System.Net.Http.HttpClient { Timeout = System.TimeSpan.FromSeconds(60) };

        bool talkOn, prevPgUp, talkBusy;
        string talkStatus = "press Page Up in a private room";
        float nextTalkScan;
        readonly Dictionary<VoiceTap, Listener> listeners = new Dictionary<VoiceTap, Listener>();
        readonly object talkLock = new object();
        bool talkDone;
        string talkReply, talkErr, talkWho, talkHeard;
        readonly List<string> talkHistory = new List<string>();   // alternating user / assistant lines

        static bool IsLocalUrl(string url)
        {
            if (!System.Uri.TryCreate(url, System.UriKind.Absolute, out System.Uri u)) return false;
            if (u.Scheme != "http" && u.Scheme != "https") return false;
            string h = u.Host.ToLowerInvariant();
            return h == "localhost" || h == "127.0.0.1" || h == "::1" || h == "[::1]";
        }

        void ToggleTalk()
        {
            if (talkOn) { StopTalk(); talkStatus = "off"; return; }
            if (!enabledBot) { talkStatus = "turn the bot on first (F6)"; return; }
            if (!ConfirmedPrivate()) { talkStatus = "private rooms only"; return; }
            if (PathyBotPlugin.CfgAiUrl == null || !IsLocalUrl(PathyBotPlugin.CfgAiUrl.Value)
                || !IsLocalUrl(PathyBotPlugin.CfgWhisperUrl.Value))
            { talkStatus = "the Ollama and Whisper addresses must be localhost (see the config file)"; return; }
            talkOn = true;
            talkStatus = "waiting for someone to say a wake word";
            Speak("I am listening to people near me, so say Pathy Bot if you want to talk. Nothing is recorded, and I only answer when you say Pathy Bot.", true);
        }

        void StopTalk()
        {
            talkOn = false;
            foreach (var kv in listeners) if (kv.Key != null) Destroy(kv.Key);
            listeners.Clear();
        }

        void RefreshListeners()
        {
            var cands = GetCandidates();
            var alive = new HashSet<VoiceTap>();
            foreach (var c in cands)
            {
                if (c.t == null) continue;
                foreach (var src in c.t.GetComponentsInChildren<AudioSource>(true))
                {
                    var tap = src.GetComponent<VoiceTap>();
                    if (tap == null) tap = src.gameObject.AddComponent<VoiceTap>();
                    alive.Add(tap);
                    if (!listeners.TryGetValue(tap, out Listener l))
                        listeners[tap] = l = new Listener();
                    l.name = c.name; l.t = c.t; l.actor = c.actor;
                }
            }
            var gone = new List<VoiceTap>();
            foreach (var k in listeners.Keys) if (k == null || !alive.Contains(k)) gone.Add(k);
            foreach (var k in gone) { if (k != null) Destroy(k); listeners.Remove(k); }
        }

        void UpdateTalk()
        {
            UpdatePcMic();
            if (!talkOn && !talkBusy) return;
            if (talkOn && (!enabledBot || !ConfirmedPrivate() || rb == null)) { StopTalk(); talkStatus = "stopped (bot off or left the private room)"; return; }

            // Result from the background request.
            string reply = null, err = null, who = null, heard = null;
            bool done;
            lock (talkLock)
            {
                done = talkDone;
                if (done) { reply = talkReply; err = talkErr; who = talkWho; heard = talkHeard; talkDone = false; talkReply = talkErr = talkWho = talkHeard = null; }
            }
            if (done)
            {
                talkBusy = false;
                if (err != null) talkStatus = err;
                else if (reply == null) talkStatus = "waiting for someone to say a wake word";
                else
                {
                    LogLine(CleanName(who), heard);
                    talkStatus = who + " said: " + Short(heard) + "  ->  " + Short(reply);
                    talkHistory.Add(CleanName(who) + ": " + heard);
                    talkHistory.Add(reply);
                    while (talkHistory.Count > 8) talkHistory.RemoveAt(0);
                    Speak(reply);
                }
            }
            if (!talkOn) return;

            if (Time.time >= nextTalkScan) { RefreshListeners(); nextTalkScan = Time.time + 2f; }

            bool botSpeaking = pumping || speechQueue.Count > 0 || injecting;

            // Too many people talking at once near the bot: say so and take a break from listening.
            if (Time.time >= talkPausedUntil)
            {
                int loudNow = 0;
                foreach (var kv in listeners)
                    if (kv.Value.t != null && Time.time - kv.Value.recentLoud < 1.5f) loudNow++;
                if (loudNow >= FloodSpeakers)
                {
                    if (floodSince < 0f) floodSince = Time.time;
                    if (Time.time - floodSince >= FloodSeconds) TriggerFlood();
                }
                else floodSince = -1f;
            }
            else if (talkOn) talkStatus = "too much noise, AI is paused for " + Mathf.CeilToInt(talkPausedUntil - Time.time) + "s";
            int block = 0;
            var chunk = new List<float>();
            foreach (var kv in listeners)
            {
                var tap = kv.Key; var l = kv.Value;
                if (tap == null) continue;
                chunk.Clear();
                int rate;
                lock (tap.gate) { chunk.AddRange(tap.buf); tap.buf.Clear(); rate = tap.rate; }

                bool near = l.t != null && Vector3.Distance(rb.position, l.t.position) <= TalkRange;
                if (near && !botSpeaking && chunk.Count > 0)
                {
                    float cs = 0f;
                    for (int j = 0; j < chunk.Count; j++) cs += chunk[j] * chunk[j];
                    if (Mathf.Sqrt(cs / chunk.Count) > TalkThreshold) l.recentLoud = Time.time;
                }
                if (Time.time < talkPausedUntil) { l.utt.Clear(); l.voiced = 0; continue; }       // AI is taking a break
                if (!near || botSpeaking || talkBusy) { l.utt.Clear(); l.voiced = 0; continue; }   // don't hear itself or pile up

                block = Mathf.Max(256, rate / 50);
                for (int i = 0; i < chunk.Count; i += block)
                {
                    int n = Mathf.Min(block, chunk.Count - i);
                    float sum = 0f;
                    for (int j = 0; j < n; j++) sum += chunk[i + j] * chunk[i + j];
                    bool loud = Mathf.Sqrt(sum / n) > TalkThreshold;
                    if (loud) { l.lastLoud = Time.time; l.voiced += n; }
                    if (loud || l.utt.Count > 0) for (int j = 0; j < n; j++) l.utt.Add(chunk[i + j]);
                }

                bool ended = l.utt.Count > 0 && (Time.time - l.lastLoud > TalkSilence || l.utt.Count > rate * TalkMaxSeconds);
                if (!ended) continue;
                float[] samples = l.utt.ToArray();
                int voicedSamples = l.voiced;
                l.utt.Clear(); l.voiced = 0;
                if (voicedSamples < rate * 0.5f || talkBusy) continue;   // a cough or a click
                StartTalkRequest(l.name, samples, rate);
                break;
            }
        }

        void StartTalkRequest(string who, float[] samples, int rate, bool direct = false, string typed = null)
        {
            talkBusy = true;
            talkStatus = "heard something, thinking...";
            byte[] wav = typed == null ? MakeWav16k(samples, rate) : null;
            string whisperUrl = PathyBotPlugin.CfgWhisperUrl.Value;
            string ollamaUrl = PathyBotPlugin.CfgAiUrl.Value;
            string model = PathyBotPlugin.CfgAiModel.Value;
            string wake = PathyBotPlugin.CfgWake.Value ?? "";
            string[] hist = talkHistory.ToArray();
            string speaker = CleanName(who);

            System.Threading.Tasks.Task.Run(async () =>
            {
                string reply = null, err = null, heard = null;
                string stage = "speech recognition";
                try
                {
                    string text = typed != null ? typed : await TranscribeAsync(whisperUrl, wav);
                    stage = "Ollama";
                    text = (text ?? "").Trim();
                    bool junk = text.Length < 3 || text.StartsWith("[") || text.StartsWith("(");
                    bool addressed = direct || wake.Trim().Length == 0;
                    string flat = LettersOnly(text);   // "Pathy, bot!" and "PathyBot" both become "pathybot"
                    foreach (string w in wake.Split(','))
                    {
                        string ww = LettersOnly(w);
                        if (ww.Length > 0 && flat.Contains(ww)) addressed = true;
                    }
                    if (!junk && addressed)
                    {
                        heard = text;
                        reply = await OllamaAsync(ollamaUrl, model, hist, speaker + ": " + text);
                        reply = StripLabel(CleanLine(reply ?? ""));
                        if (reply.Length == 0 || !BasicFilterOk(reply)) reply = null;
                    }
                }
                catch (System.Exception e)
                {
                    err = "AI error in " + stage + ": " + Short(e.GetBaseException().Message)
                        + (stage == "Ollama" ? " (is Ollama running at " + ollamaUrl + " and is the model name right?)" : "");
                }
                lock (talkLock) { talkReply = reply; talkErr = err; talkWho = who; talkHeard = heard; talkDone = true; }
            });
        }

        // Speech to text: the local whisper server when it answers, otherwise Windows' own built-in
        // speech recognition (no setup needed, less accurate). A dead whisper server is skipped for 90 s.
        static System.DateTime whisperDownUntil = System.DateTime.MinValue;

        static async System.Threading.Tasks.Task<string> TranscribeAsync(string url, byte[] wav)
        {
            if (!string.IsNullOrWhiteSpace(url) && System.DateTime.UtcNow >= whisperDownUntil)
            {
                try { return await WhisperAsync(url, wav); }
                catch (System.Exception) { whisperDownUntil = System.DateTime.UtcNow.AddSeconds(90); }
            }
            return await System.Threading.Tasks.Task.Run(() => WindowsRecognize(wav));
        }

        static string WindowsRecognize(byte[] wav)
        {
            string id = System.Guid.NewGuid().ToString("N");
            string dir = System.IO.Path.GetTempPath();
            string wavFile = System.IO.Path.Combine(dir, "pathybot_stt_" + id + ".wav");
            string ps1 = System.IO.Path.Combine(dir, "pathybot_stt_" + id + ".ps1");
            try
            {
                System.IO.File.WriteAllBytes(wavFile, wav);
                System.IO.File.WriteAllText(ps1,
                    "param($Wav)\r\n" +
                    "Add-Type -AssemblyName System.Speech\r\n" +
                    "$r = New-Object System.Speech.Recognition.SpeechRecognitionEngine\r\n" +
                    "$r.LoadGrammar((New-Object System.Speech.Recognition.DictationGrammar))\r\n" +
                    "$r.SetInputToWaveFile($Wav)\r\n" +
                    "$out = ''\r\n" +
                    "while ($true) { $res = $r.Recognize(); if (-not $res) { break }; $out += $res.Text + ' ' }\r\n" +
                    "[Console]::Out.Write($out.Trim())\r\n" +
                    "$r.Dispose()\r\n");
                var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -File \"" + ps1 + "\" -Wav \"" + wavFile + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    var errTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(40000)) { try { p.Kill(); } catch (System.Exception) { } throw new System.Exception("Windows speech recognition timed out"); }
                    if (p.ExitCode != 0) throw new System.Exception("Windows speech recognition failed: " + errTask.Result);
                    return outTask.Result.Trim();
                }
            }
            finally
            {
                try { System.IO.File.Delete(wavFile); } catch (System.Exception) { }
                try { System.IO.File.Delete(ps1); } catch (System.Exception) { }
            }
        }

        static async System.Threading.Tasks.Task<string> WhisperAsync(string url, byte[] wav)
        {
            using (var form = new System.Net.Http.MultipartFormDataContent())
            {
                var file = new System.Net.Http.ByteArrayContent(wav);
                file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
                form.Add(file, "file", "speech.wav");
                form.Add(new System.Net.Http.StringContent("json"), "response_format");
                form.Add(new System.Net.Http.StringContent("0.0"), "temperature");
                var resp = await Http.PostAsync(url, form);
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) throw new System.Exception("whisper server said " + (int)resp.StatusCode);
                return JsonGetString(body, "text");
            }
        }

        static async System.Threading.Tasks.Task<string> OllamaAsync(string url, string model, string[] hist, string userLine)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"model\":\"").Append(JsonEsc(model)).Append("\",\"stream\":false,\"keep_alive\":\"10m\",");
            sb.Append("\"options\":{\"num_predict\":80,\"temperature\":0.8},\"messages\":[");
            sb.Append("{\"role\":\"system\",\"content\":\"")
              .Append(JsonEsc("You are PathyBot, a friendly bot that plays Gorilla Tag, the VR game where everyone is a gorilla who moves with their arms. "
                  + "You love tag, hide and seek, and red light green light, and you are kind and welcoming to every player. "
                  + "Players talk to you out loud or by text. Reply in one or two short sentences, under 25 words, plain text only, family safe, no emoji. "
                  + "Messages start with the speaker's name so you know who is talking, but never start your own reply with a name or a label, just speak. "
                  + "Never repeat personal information."))
              .Append("\"}");
            for (int i = 0; i < hist.Length; i++)
                sb.Append(",{\"role\":\"").Append(i % 2 == 0 ? "user" : "assistant").Append("\",\"content\":\"").Append(JsonEsc(hist[i])).Append("\"}");
            sb.Append(",{\"role\":\"user\",\"content\":\"").Append(JsonEsc(userLine)).Append("\"}]}");

            var content = new System.Net.Http.StringContent(sb.ToString(), System.Text.Encoding.UTF8, "application/json");
            var resp = await Http.PostAsync(url, content);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) throw new System.Exception("Ollama said " + (int)resp.StatusCode);
            return JsonGetString(body, "content");
        }

        // Removes a leading "Name:" label the AI sometimes adds, like "PathyBot: hello".
        static string StripLabel(string s)
        {
            int c = s.IndexOf(':');
            if (c > 0 && c <= 24 && s.Substring(0, c).Trim().Split(' ').Length <= 2) s = s.Substring(c + 1).TrimStart();
            return s;
        }

        static string LettersOnly(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s ?? "") if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        static string JsonEsc(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s ?? "")
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') { }
                else if (c == '\t') sb.Append(' ');
                else if (c < ' ') { }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // Reads one string value out of a JSON text without needing a JSON library (safe on any thread).
        static string JsonGetString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return "";
            int k = json.IndexOf("\"" + key + "\"", System.StringComparison.Ordinal);
            if (k < 0) return "";
            int colon = json.IndexOf(':', k);
            if (colon < 0) return "";
            int q = json.IndexOf('"', colon + 1);
            if (q < 0) return "";
            var sb = new System.Text.StringBuilder();
            for (int i = q + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"') break;
                if (c == '\\' && i + 1 < json.Length)
                {
                    char n = json[++i];
                    if (n == 'n' || n == 't' || n == 'r') sb.Append(' ');
                    else if (n == 'u' && i + 4 < json.Length)
                    {
                        int code;
                        if (int.TryParse(json.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out code)) sb.Append((char)code);
                        i += 4;
                    }
                    else sb.Append(n);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // 16 kHz mono 16-bit WAV, volume-normalised, which is what whisper wants.
        static byte[] MakeWav16k(float[] s, int rate)
        {
            const int outRate = 16000;
            int n = (int)((long)s.Length * outRate / rate);
            float peak = 0f;
            foreach (float f in s) { float a = Mathf.Abs(f); if (a > peak) peak = a; }
            float gain = peak > 0.001f ? Mathf.Min(0.9f / peak, 30f) : 1f;
            double step = (double)rate / outRate;

            using (var ms = new System.IO.MemoryStream(44 + n * 2))
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + n * 2);
                w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(outRate); w.Write(outRate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                w.Write(n * 2);
                for (int i = 0; i < n; i++)
                {
                    int a = (int)(i * step), b = Mathf.Min(s.Length, (int)((i + 1) * step));
                    if (b <= a) b = Mathf.Min(s.Length, a + 1);
                    float sum = 0f;
                    for (int j = a; j < b; j++) sum += s[j];
                    float v = Mathf.Clamp(sum / Mathf.Max(1, b - a) * gain, -1f, 1f);
                    w.Write((short)(v * 32767f));
                }
                return ms.ToArray();
            }
        }

        // ----- Talking to the bot yourself, through your own PC microphone (Numpad 6) -----
        // Press once and speak; it stops when you pause (or press again) and the bot answers.
        // No wake word needed. Only your own mic is used, and nothing is saved.

        bool pcTalking, prevN6, prevN5, toggleRequest;
        AudioClip pcClip;
        int pcLastPos, pcVoiced;
        float pcStart, pcLastLoud;

        void TogglePcTalk()
        {
            if (pcTalking) { FinishPcTalk(true); return; }
            if (talkBusy) { talkStatus = "still thinking about the last thing"; return; }
            if (!enabledBot) { talkStatus = "turn the bot on first (F6)"; return; }
            if (PathyBotPlugin.CfgAiUrl == null || !IsLocalUrl(PathyBotPlugin.CfgAiUrl.Value)
                || !IsLocalUrl(PathyBotPlugin.CfgWhisperUrl.Value))
            { talkStatus = "the Ollama and Whisper addresses must be localhost (see the config file)"; return; }
            if (Microphone.devices.Length == 0) { talkStatus = "no microphone found"; return; }
            pcClip = Microphone.Start(null, false, 12, 16000);
            pcLastPos = 0; pcVoiced = 0;
            pcStart = pcLastLoud = Time.time;
            pcTalking = true;
            talkStatus = "go ahead and talk to the bot...";
        }

        void UpdatePcMic()
        {
            if (!pcTalking) return;
            if (!enabledBot) { FinishPcTalk(false); return; }
            int pos = Microphone.GetPosition(null);
            if (pcClip != null && pos > pcLastPos)
            {
                float[] tmp = new float[pos - pcLastPos];
                pcClip.GetData(tmp, pcLastPos);
                int step = 320;   // 20 ms at 16 kHz
                for (int i = 0; i < tmp.Length; i += step)
                {
                    int n = Mathf.Min(step, tmp.Length - i);
                    float sum = 0f;
                    for (int j = 0; j < n; j++) sum += tmp[i + j] * tmp[i + j];
                    if (Mathf.Sqrt(sum / n) > 0.01f) { pcVoiced += n; pcLastLoud = Time.time; }
                }
                pcLastPos = pos;
            }
            bool paused = pcVoiced > 16000 * 0.4f && Time.time - pcLastLoud > 0.9f;
            bool full = pos <= 0 && Time.time - pcStart > 1f || Time.time - pcStart > 11.5f;
            if (paused || full) FinishPcTalk(true);
        }

        void FinishPcTalk(bool send)
        {
            if (!pcTalking) return;
            pcTalking = false;
            int pos = Microphone.GetPosition(null);
            Microphone.End(null);
            if (!send || pcClip == null || pcVoiced < 16000 * 0.4f) { talkStatus = "didn't hear anything"; return; }
            int len = Mathf.Clamp(pos, 1, pcClip.samples);
            float[] data = new float[len];
            pcClip.GetData(data, 0);
            StartTalkRequest("You", data, 16000, true);
        }

        GUIStyle talkStyle;
        void DrawTalkUi()
        {
            if (pcTalking || (talkBusy && !talkOn))
            {
                if (talkStyle == null)
                    talkStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 20, alignment = TextAnchor.LowerLeft };
                talkStyle.normal.textColor = pcTalking ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.9f, 0.3f);
                GUI.Label(new Rect(14f, Screen.height - 44f, 700f, 34f), pcTalking ? "TALKING TO BOT... (Numpad 6 = send)" : "THINKING...", talkStyle);
                return;
            }
            if (!talkOn) return;
            if (talkStyle == null)
                talkStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 20, alignment = TextAnchor.LowerLeft };
            talkStyle.normal.textColor = talkBusy ? new Color(1f, 0.9f, 0.3f) : new Color(0.3f, 1f, 0.4f);
            GUI.Label(new Rect(14f, Screen.height - 44f, 600f, 34f), talkBusy ? "THINKING..." : "LISTENING (Page Up = stop)", talkStyle);
        }

        // ---------- Voice (free Windows speech, sent out through the game's voice chat) ----------
        // The bot's announcements are spoken with the built-in Windows voice (no AI, no internet). The
        // sound plays on your machine, and in a confirmed private room it is also sent out through the
        // game's voice chat in place of your microphone for as long as the clip lasts.
        //
        // It never listens to anyone. The only text ever spoken is the bot's own fixed lines, which can
        // include a player's name (letters and numbers only, run through a small word filter).

        const int MaxSpokenChars = 160;
        CoroutineHost voiceHost;
        readonly Queue<string> speechQueue = new Queue<string>();
        readonly Dictionary<string, AudioClip> clipCache = new Dictionary<string, AudioClip>();
        bool pumping, voiceMuted, injecting, prevEnd, viaVoiceManager;
        object voiceManagerClip, voiceManagerObj;
        string voiceStatus = "ready";
        AudioSource voiceAudio;
        object recorder, prevSource, prevTransmit, prevDetect;

        static readonly string[] BannedBits =
        {
            "kill yourself", "kys", "suicide", "nigg", "fagg", "retard", "rape", "nazi", "hitler",
            "address is", "my address", "phone number", "credit card", "password"
        };

        static bool BasicFilterOk(string text)
        {
            string t = text.ToLowerInvariant();
            foreach (string b in BannedBits) if (t.Contains(b)) return false;
            return true;
        }

        static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length > 160 ? s.Substring(0, 160) + "..." : s;
        }

        static string CleanLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Replace("\"", "").Replace("*", "").Replace("#", "");
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char ch in s)
                if (!char.IsControl(ch) && !char.IsSurrogate(ch)) sb.Append(ch); // no emoji or control characters
            s = sb.ToString().Trim();
            return s.Length > MaxSpokenChars ? s.Substring(0, MaxSpokenChars) : s;
        }

        // A player's name as it will be spoken: letters, numbers, spaces and underscores only.
        static string CleanName(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s ?? "")
                if (char.IsLetterOrDigit(c) || c == ' ') sb.Append(c);
                else if (c == '_') sb.Append(' ');
            string r = sb.ToString().Trim();
            if (r.Length > 12) r = r.Substring(0, 12);
            return r.Length == 0 ? "friend" : r;
        }

        // Queue a line. urgent = drop anything still waiting (used for red light / green light).
        void Speak(string text, bool urgent = false)
        {
            if (voiceMuted || voiceHost == null || PathyBotPlugin.CfgSpeak == null || !PathyBotPlugin.CfgSpeak.Value) return;
            text = CleanLine(text);
            if (text.Length == 0) return;
            if (!BasicFilterOk(text)) { voiceStatus = "that line was blocked by the word filter"; return; }
            LogLine(BotName, text);
            if (urgent) speechQueue.Clear();
            if (speechQueue.Count < 3) speechQueue.Enqueue(text);
            if (!pumping) voiceHost.StartCoroutine(SpeechPump());
        }

        // Makes the clip for a line ahead of time, so saying it later is instant.
        void Prefetch(string text)
        {
            if (voiceHost == null || PathyBotPlugin.CfgSpeak == null || !PathyBotPlugin.CfgSpeak.Value) return;
            if (clipCache.ContainsKey(text)) return;
            voiceHost.StartCoroutine(PrefetchOne(text));
        }

        IEnumerator PrefetchOne(string text)
        {
            AudioClip clip = null;
            yield return voiceHost.StartCoroutine(TtsWindows(text, (c, e) => clip = c));
            if (clip != null) clipCache[text] = clip;
        }

        void ClearClipCache()
        {
            foreach (var c in clipCache.Values) if (c != null) Destroy(c);
            clipCache.Clear();
        }

        IEnumerator SpeechPump()
        {
            pumping = true;
            while (speechQueue.Count > 0)
            {
                while (screaming) yield return null;   // let the scream finish first
                string line = speechQueue.Dequeue();
                yield return voiceHost.StartCoroutine(SpeakOne(line));
                yield return new WaitForSeconds(0.25f);
            }
            pumping = false;
        }

        IEnumerator SpeakOne(string text)
        {
            AudioClip clip;
            string err = null;
            if (!clipCache.TryGetValue(text, out clip) || clip == null)
            {
                voiceStatus = "making speech...";
                clip = null;
                yield return voiceHost.StartCoroutine(TtsWindows(text, (c, e) => { clip = c; err = e; }));
                if (clip == null) { voiceStatus = "speech failed: " + err; yield break; }
                if (clipCache.Count >= 30) ClearClipCache();
                clipCache[text] = clip;
            }

            voiceStatus = "speaking: " + text;
            PlayLocal(clip);
            bool sent = BeginMicInject(clip);
            if (sent)
            {
                yield return new WaitForSeconds(0.5f);
                voiceStatus = "speaking: " + text + "   [" + TransmitReport() + "]";
                yield return new WaitForSeconds(Mathf.Max(0f, clip.length - 0.2f));
            }
            else yield return new WaitForSeconds(clip.length);
            yield return new WaitForSeconds(0.3f);
            if (sent) EndMicInject();
            voiceStatus = "ready";
        }

        void StopVoice()
        {
            if (voiceHost != null) voiceHost.StopAllCoroutines();
            speechQueue.Clear();
            pumping = false;
            if (injecting) EndMicInject();
            if (voiceAudio != null) voiceAudio.Stop();
            voiceStatus = "ready";
        }

        void PlayLocal(AudioClip clip)
        {
            if (voiceAudio == null)
            {
                var go = new GameObject("PathyBot_VoiceAudio");
                go.hideFlags = HideFlags.HideAndDontSave;
                voiceAudio = go.AddComponent<AudioSource>();
                voiceAudio.spatialBlend = 0f;
                voiceAudio.playOnAwake = false;
                voiceAudio.volume = 0.8f;
            }
            voiceAudio.PlayOneShot(clip);
        }

        // Runs a program on a background thread. Optionally feeds it stdin. Fills res.error on failure.
        IEnumerator RunProgram(string exe, string args, string stdin, int timeoutSec, AsyncResult res)
        {
            var th = new System.Threading.Thread(() =>
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(exe, args);
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardInput = stdin != null;
                    psi.RedirectStandardError = true;
                    psi.RedirectStandardOutput = true;
                    using (var p = System.Diagnostics.Process.Start(psi))
                    {
                        if (stdin != null)
                        {
                            var bytes = System.Text.Encoding.UTF8.GetBytes(stdin);
                            p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                            p.StandardInput.Close();
                        }
                        // Drain output so the program can't block on a full pipe.
                        p.StandardOutput.ReadToEndAsync();
                        var errTask = p.StandardError.ReadToEndAsync();
                        if (!p.WaitForExit(timeoutSec * 1000))
                        {
                            try { p.Kill(); } catch { }
                            res.error = "timed out";
                        }
                        else if (p.ExitCode != 0)
                        {
                            string se = "";
                            try { se = errTask.Result; } catch { }
                            res.error = "exit " + p.ExitCode + " " + se;
                        }
                    }
                }
                catch (System.Exception ex) { res.error = ex.Message; }
                res.done = true;
            });
            th.IsBackground = true;
            th.Start();
            while (!res.done) yield return null;
        }

        // Turns a .wav file (16-bit PCM, mono or stereo) into an AudioClip.
        static AudioClip WavToClip(byte[] w, out string err)
        {
            err = null;
            if (w == null || w.Length < 44 || w[0] != 'R' || w[1] != 'I' || w[2] != 'F' || w[3] != 'F')
            { err = "not a wav file"; return null; }

            int channels = 1, rate = 22050, bits = 16, dataStart = -1, dataLen = 0;
            int pos = 12;
            while (pos + 8 <= w.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(w, pos, 4);
                int len = System.BitConverter.ToInt32(w, pos + 4);
                int body = pos + 8;
                if (id == "fmt ")
                {
                    channels = System.BitConverter.ToInt16(w, body + 2);
                    rate = System.BitConverter.ToInt32(w, body + 4);
                    bits = System.BitConverter.ToInt16(w, body + 14);
                }
                else if (id == "data")
                {
                    dataStart = body;
                    dataLen = len;
                    if (dataLen < 0 || dataStart + dataLen > w.Length) dataLen = w.Length - dataStart; // streamed files
                    break;
                }
                if (len < 0) break;
                pos = body + len + (len & 1);
            }
            if (dataStart < 0) { err = "no audio in wav"; return null; }
            if (bits != 16) { err = "wav is " + bits + "-bit, need 16"; return null; }
            if (channels < 1 || channels > 2) { err = "wav has " + channels + " channels"; return null; }

            int frames = dataLen / (2 * channels);
            if (frames < 1) { err = "empty audio"; return null; }
            var samples = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                int o = dataStart + i * 2 * channels;
                float s = System.BitConverter.ToInt16(w, o) / 32768f;
                if (channels == 2) s = (s + System.BitConverter.ToInt16(w, o + 2) / 32768f) * 0.5f;
                samples[i] = s;
            }
            // Windows speech is fairly quiet: bring the loudest point up to about 90% so it carries in voice chat.
            float peak = 0f;
            for (int i = 0; i < frames; i++) peak = Mathf.Max(peak, Mathf.Abs(samples[i]));
            if (peak > 0.01f)
            {
                float gain = Mathf.Min(0.9f / peak, 6f);
                for (int i = 0; i < frames; i++) samples[i] = Mathf.Clamp(samples[i] * gain, -1f, 1f);
            }
            var clip = AudioClip.Create("PathyVoice", frames, 1, rate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        static string TempFile(string ext) =>
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pathybot_" + System.Guid.NewGuid().ToString("N") + ext);

        static void TryDelete(string p) { try { if (System.IO.File.Exists(p)) System.IO.File.Delete(p); } catch { } }

        IEnumerator TtsWindows(string text, System.Action<AudioClip, string> done)
        {
            string txt = TempFile(".txt"), wav = TempFile(".wav"), ps1 = TempFile(".ps1");
            string voice = PathyBotPlugin.CfgWinVoice.Value.Replace("\"", "").Replace("'", "").Trim();
            AudioClip clip = null;
            string err = null;
            try
            {
                System.IO.File.WriteAllText(txt, text, new System.Text.UTF8Encoding(false));
                // The text and names travel in files and arguments, never pasted into the script itself.
                System.IO.File.WriteAllText(ps1,
                    "param($TextFile,$OutFile,$Voice,$Swap)\r\n" +
                    "Add-Type -AssemblyName System.Speech\r\n" +
                    "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer\r\n" +
                    "$s.Rate = 0\r\n" +
                    "$s.SetOutputToWaveFile($OutFile)\r\n" +
                    "$text = [IO.File]::ReadAllText($TextFile)\r\n" +
                    "$males = @($s.GetInstalledVoices() | Where-Object { $_.Enabled -and $_.VoiceInfo.Gender -eq 'Male' } | ForEach-Object { $_.VoiceInfo.Name })\r\n" +
                    "if ($Voice) { try { $s.SelectVoice($Voice) } catch {}; $s.Speak($text) }\r\n" +
                    "elseif ($Swap -eq '1' -and $males.Count -ge 1) {\r\n" +
                    "  $sb = New-Object System.Text.StringBuilder\r\n" +
                    "  [void]$sb.Append(\"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>\")\r\n" +
                    "  $i = Get-Random -Maximum $males.Count\r\n" +
                    "  foreach ($w in ($text -split '\\s+')) {\r\n" +
                    "    if (-not $w) { continue }\r\n" +
                    "    $p = Get-Random -Minimum -7 -Maximum 8\r\n" +
                    "    if ($p -ge 0) { $ps = '+' + $p + 'st' } else { $ps = '' + $p + 'st' }\r\n" +
                    "    $v = Get-Random -Minimum 55 -Maximum 101\r\n" +
                    "    $name = $males[$i % $males.Count]\r\n" +
                    "    $word = [System.Security.SecurityElement]::Escape($w)\r\n" +
                    "    [void]$sb.Append(\"<voice name='\" + $name + \"'><prosody pitch='\" + $ps + \"' volume='\" + $v + \"'>\" + $word + \"</prosody></voice>\")\r\n" +
                    "    $i++\r\n" +
                    "  }\r\n" +
                    "  [void]$sb.Append('</speak>')\r\n" +
                    "  try { $s.SpeakSsml($sb.ToString()) } catch { $s.Speak($text) }\r\n" +
                    "} else { if ($males.Count -ge 1) { try { $s.SelectVoice($males[0]) } catch {} }; $s.Speak($text) }\r\n" +
                    "$s.Dispose()\r\n");
            }
            catch (System.Exception ex) { done(null, "can't write temp files: " + ex.Message); yield break; }

            var res = new AsyncResult();
            string args = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + ps1 + "\" -TextFile \"" + txt +
                          "\" -OutFile \"" + wav + "\" -Voice \"" + voice + "\" -Swap " + (PathyBotPlugin.CfgVoiceSwap != null && PathyBotPlugin.CfgVoiceSwap.Value ? "1" : "0");
            yield return voiceHost.StartCoroutine(RunProgram("powershell.exe", args, null, 30, res));

            if (res.error != null) err = "Windows voice failed: " + Short(res.error);
            else
            {
                try { clip = WavToClip(System.IO.File.ReadAllBytes(wav), out err); }
                catch (System.Exception ex) { err = "can't read speech file: " + ex.Message; }
            }
            TryDelete(txt); TryDelete(wav); TryDelete(ps1);
            done(clip, clip == null ? err : null);
        }

        // ---- Sending the clip out through the game's voice chat ----
        // The game's microphone recorder is switched to play our clip instead of the microphone, then
        // switched back. It is all done by name (reflection) so it doesn't depend on the voice library's
        // exact version. Only ever done in a confirmed private room.

        object FindRecorder()
        {
            if (recorder != null)
            {
                var u = recorder as UnityEngine.Object;
                if (u != null) return recorder;
                recorder = null;
            }
            try
            {
                System.Type gt = System.Type.GetType("GorillaTagger, Assembly-CSharp");
                object inst = gt != null ? GetMember(gt, "Instance") : null;
                object rec = inst != null ? GetMember(inst, "myRecorder") : null;
                if (rec != null) { recorder = rec; return rec; }

                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    System.Type rt = asm.GetType("Photon.Voice.Unity.Recorder");
                    if (rt == null) continue;
                    var objs = FindObjectsByType(rt, FindObjectsSortMode.None);
                    if (objs != null && objs.Length > 0) { recorder = objs[0]; return recorder; }
                }
            }
            catch { }
            return null;
        }

        static void RestartRecorder(object rec)
        {
            var t = rec.GetType();
            foreach (var m in t.GetMethods())
            {
                if (m.Name != "RestartRecording") continue;
                m.Invoke(rec, m.GetParameters().Length == 0 ? null : new object[] { true });
                break;
            }
            // If it still isn't recording (the game may start it lazily), start it.
            var rp = t.GetProperty("IsRecording");
            if (rp != null && !(rp.GetValue(rec, null) is bool on && on))
            {
                var start = t.GetMethod("StartRecording", System.Type.EmptyTypes);
                if (start != null) start.Invoke(rec, null);
            }
        }

        // The voice codec only takes certain sample rates (8, 12, 16, 24 and 48 kHz). Windows speech is
        // 22.05 kHz, which the codec can refuse, so give the recorder a copy at a rate it accepts.
        readonly Dictionary<string, AudioClip> rateClips = new Dictionary<string, AudioClip>();

        AudioClip ClipAtRate(AudioClip src, int hz)
        {
            if (src.frequency == hz) return src;
            string key = src.GetInstanceID() + "_" + hz;
            if (rateClips.TryGetValue(key, out AudioClip have) && have != null) return have;

            var inp = new float[src.samples * src.channels];
            src.GetData(inp, 0);
            int ch = src.channels;
            int frames = inp.Length / ch;
            int outFrames = Mathf.Max(1, Mathf.RoundToInt(frames * (hz / (float)src.frequency)));
            var outp = new float[outFrames];
            for (int i = 0; i < outFrames; i++)
            {
                float pos = i * (src.frequency / (float)hz);
                int a = Mathf.Min((int)pos, frames - 1);
                int b = Mathf.Min(a + 1, frames - 1);
                float f = pos - a;
                float va = ch == 1 ? inp[a] : (inp[a * ch] + inp[a * ch + 1]) * 0.5f;
                float vb = ch == 1 ? inp[b] : (inp[b * ch] + inp[b * ch + 1]) * 0.5f;
                outp[i] = Mathf.Lerp(va, vb, f);
            }
            var clip = AudioClip.Create("PathyVoice" + hz, outFrames, 1, hz, false);
            clip.SetData(outp, 0);
            if (rateClips.Count > 40) { foreach (var c in rateClips.Values) if (c != null) Destroy(c); rateClips.Clear(); }
            rateClips[key] = clip;
            return clip;
        }

        static int RecorderRate(object rec)
        {
            try
            {
                var sp = rec.GetType().GetProperty("SamplingRate");
                if (sp != null)
                {
                    int hz = System.Convert.ToInt32(sp.GetValue(rec, null));
                    if (hz == 8000 || hz == 12000 || hz == 16000 || hz == 24000 || hz == 48000) return hz;
                }
            }
            catch { }
            return 48000;
        }

        // Your voice input is not the stock Gorilla Tag one: it is a mod menu's own "VoiceManager"
        // (Poison.Managers.VoiceManager) that mixes audio clips into the microphone stream. If it is
        // there, it is the right thing to hand the bot's speech to.
        static object FindVoiceManager()
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type t = null;
                try { t = asm.GetType("Poison.Managers.VoiceManager"); } catch { }
                if (t == null) continue;
                return GetMember(t, "Instance");
            }
            return null;
        }

        bool BeginVoiceManagerInject(AudioClip clip)
        {
            object vm = FindVoiceManager();
            if (vm == null) return false;
            try
            {
                // Make sure the game's recorder is sending, and not gating the clip out as silence.
                object rec = FindRecorder();
                if (rec != null)
                {
                    var t = rec.GetType();
                    var txProp = t.GetProperty("TransmitEnabled");
                    var vdProp = t.GetProperty("VoiceDetection");
                    prevTransmit = txProp != null ? txProp.GetValue(rec, null) : null;
                    prevDetect = vdProp != null ? vdProp.GetValue(rec, null) : null;
                    if (vdProp != null) vdProp.SetValue(rec, false, null);
                    if (txProp != null) txProp.SetValue(rec, true, null);
                }

                object pclip = null;
                foreach (var m in vm.GetType().GetMethods())
                {
                    if (m.Name != "AudioClip") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 0) continue;
                    // second argument = turn the real microphone off while the clip plays
                    pclip = m.Invoke(vm, ps.Length == 1 ? new object[] { clip } : new object[] { clip, true });
                    break;
                }
                if (pclip == null) { if (rec != null) EndMicInjectRecorderSettings(rec); return false; }

                var loop = pclip.GetType().GetProperty("Looping");
                if (loop != null) loop.SetValue(pclip, false, null);   // play once, whatever the menu's loop setting is

                voiceManagerObj = vm;
                voiceManagerClip = pclip;
                viaVoiceManager = true;
                injecting = true;
                Debug.Log("[PathyBot] mic: sent the clip through " + vm.GetType().FullName);
                return true;
            }
            catch (System.Exception e)
            {
                Debug.Log("[PathyBot] mic: voice manager path failed: " + e.Message);
                return false;
            }
        }

        void EndMicInjectRecorderSettings(object rec)
        {
            var t = rec.GetType();
            var txProp = t.GetProperty("TransmitEnabled");
            var vdProp = t.GetProperty("VoiceDetection");
            if (txProp != null && prevTransmit != null) txProp.SetValue(rec, prevTransmit, null);
            if (vdProp != null && prevDetect != null) vdProp.SetValue(rec, prevDetect, null);
        }

        bool BeginMicInject(AudioClip clip)
        {
            if (!ConfirmedPrivate()) { voiceStatus = "speaking (this machine only: not in a private room)"; return false; }
            if (BeginVoiceManagerInject(clip)) return true;
            try
            {
                object rec = FindRecorder();
                if (rec == null) { voiceStatus = "speaking (this machine only: voice recorder not found)"; return false; }

                var t = rec.GetType();
                var srcProp = t.GetProperty("SourceType");
                var clipProp = t.GetProperty("AudioClip");
                var loopProp = t.GetProperty("LoopAudioClip");
                var txProp = t.GetProperty("TransmitEnabled");
                var vdProp = t.GetProperty("VoiceDetection");
                if (srcProp == null || clipProp == null)
                {
                    voiceStatus = "speaking (this machine only: voice recorder has no clip input)";
                    return false;
                }

                prevSource = srcProp.GetValue(rec, null);
                prevTransmit = txProp != null ? txProp.GetValue(rec, null) : null;
                prevDetect = vdProp != null ? vdProp.GetValue(rec, null) : null;
                if (vdProp != null) vdProp.SetValue(rec, false, null);   // a quiet clip must not be gated out as "silence"

                int hz = RecorderRate(rec);
                AudioClip sendClip = ClipAtRate(clip, hz);
                Debug.Log("[PathyBot] mic: recorder " + t.FullName + ", rate " + hz + ", was " + prevSource + ", sending " + sendClip.frequency + " Hz clip");
                clipProp.SetValue(rec, sendClip, null);
                if (loopProp != null) loopProp.SetValue(rec, false, null);
                srcProp.SetValue(rec, System.Enum.Parse(srcProp.PropertyType, "AudioClip"), null);
                if (txProp != null) txProp.SetValue(rec, true, null);
                RestartRecorder(rec);

                injecting = true;
                return true;
            }
            catch (System.Exception e)
            {
                voiceStatus = "speaking (this machine only: " + Short(e.Message) + ")";
                injecting = true;      // make sure we put things back
                EndMicInject();
                return false;
            }
        }

        // While a clip is going out, keep forcing transmit on, in case the game's push-to-talk or
        // mute logic switches it off again every frame.
        void HoldTransmit()
        {
            try
            {
                object rec = FindRecorder();
                if (rec == null) return;
                var tx = rec.GetType().GetProperty("TransmitEnabled");
                if (tx != null) tx.SetValue(rec, true, null);
            }
            catch { }
        }

        // What the recorder says it is doing, so you can see whether the clip is really going out.
        string TransmitReport()
        {
            try
            {
                object rec = FindRecorder();
                if (rec == null) return "no recorder";
                var t = rec.GetType();
                var rp = t.GetProperty("IsRecording");
                var tp = t.GetProperty("IsCurrentlyTransmitting");
                string r = rp != null ? "recording " + rp.GetValue(rec, null) : "recording ?";
                string x = tp != null ? "transmitting " + tp.GetValue(rec, null) : "transmitting ?";
                return r + ", " + x;
            }
            catch { return "status unknown"; }
        }

        void EndMicInject()
        {
            injecting = false;
            if (viaVoiceManager)
            {
                viaVoiceManager = false;
                try
                {
                    if (voiceManagerObj != null && voiceManagerClip != null)
                        InvokeMethod(voiceManagerObj, "StopAudioClip", voiceManagerClip);   // no-op if it already finished
                    object rec0 = FindRecorder();
                    if (rec0 != null) EndMicInjectRecorderSettings(rec0);
                }
                catch (System.Exception e) { Debug.Log("[PathyBot] Couldn't finish the voice clip: " + e.Message); }
                voiceManagerClip = null;
                voiceManagerObj = null;
                return;
            }
            try
            {
                object rec = FindRecorder();
                if (rec == null) return;
                var t = rec.GetType();
                var srcProp = t.GetProperty("SourceType");
                var txProp = t.GetProperty("TransmitEnabled");
                if (srcProp != null && prevSource != null) srcProp.SetValue(rec, prevSource, null);
                if (txProp != null && prevTransmit != null) txProp.SetValue(rec, prevTransmit, null);
                var vdProp = t.GetProperty("VoiceDetection");
                if (vdProp != null && prevDetect != null) vdProp.SetValue(rec, prevDetect, null);
                RestartRecorder(rec);
            }
            catch (System.Exception e) { Debug.Log("[PathyBot] Couldn't restore the microphone: " + e.Message); }
        }


        // ---- Typed text box (Home) ----
        // Type a line and press Enter: the bot says it out loud with the same Windows voice and sends it
        // out through the game mic (private rooms only), exactly like its own announcements.

        bool chatOpen, prevHome, chatAi;
        string chatText = "";

        void SubmitChat()
        {
            string t = CleanLine(chatText);
            chatText = "";
            if (t.Length == 0) return;
            if (chatAi)
            {
                if (talkBusy) { talkStatus = "still thinking about the last thing"; return; }
                if (PathyBotPlugin.CfgAiUrl == null || !IsLocalUrl(PathyBotPlugin.CfgAiUrl.Value)) { talkStatus = "the Ollama address must be localhost (see the config file)"; return; }
                StartTalkRequest("You", null, 16000, true, t);
                return;
            }
            if (voiceMuted) { voiceStatus = "voice is muted (End to unmute)"; return; }
            Speak(t);
        }

        void DrawChatBox()
        {
            if (!chatOpen) return;
            Event ev = Event.current;
            if (ev != null && ev.type == EventType.KeyDown)
            {
                if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter) { SubmitChat(); ev.Use(); return; }
                if (ev.keyCode == KeyCode.Escape) { chatOpen = false; ev.Use(); return; }
                if (ev.keyCode == KeyCode.Tab) { chatAi = !chatAi; ev.Use(); return; }
            }

            var r = new Rect(Screen.width * 0.5f - 300f, Screen.height - 90f, 600f, 34f);
            GUI.Box(new Rect(r.x - 8f, r.y - 34f, r.width + 16f, 80f), chatAi ? "Ask the AI   (Enter = send, Tab = switch to plain speech, Esc = close)" : "Type what the bot says   (Enter = speak, Tab = switch to ask the AI, Esc = close)");
            GUI.SetNextControlName("pathy_chat");
            chatText = GUI.TextField(r, chatText, MaxSpokenChars);
            GUI.FocusControl("pathy_chat");
        }

        // ---------- Diagnostics (F12) ----------
        // Writes what the game actually has for the voice recorder and for snowballs to a text file, so
        // the mic and snowball code can be matched to this version of the game instead of guessed.

        static string Describe(System.Type t)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("TYPE " + t.FullName + (t.BaseType != null ? " : " + t.BaseType.Name : ""));
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                      | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                      | System.Reflection.BindingFlags.DeclaredOnly;
            int n = 0;
            foreach (var f in t.GetFields(flags)) { if (++n > 70) break; sb.AppendLine("   field    " + f.FieldType.Name + " " + f.Name); }
            foreach (var p in t.GetProperties(flags)) { if (++n > 110) break; sb.AppendLine("   property " + p.PropertyType.Name + " " + p.Name); }
            foreach (var m in t.GetMethods(flags))
            {
                if (++n > 170) break;
                if (m.IsSpecialName) continue;
                var ps = new List<string>();
                foreach (var pr in m.GetParameters()) ps.Add(pr.ParameterType.Name + " " + pr.Name);
                sb.AppendLine("   method   " + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", ps.ToArray()) + ")");
            }
            return sb.ToString();
        }

        void DumpDiagnostics()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("PathyBot diagnostics  " + System.DateTime.Now);
            sb.AppendLine("Unity " + Application.unityVersion + "   game version " + Application.version);
            sb.AppendLine("Private room: " + ConfirmedPrivate());

            // 1) The voice recorder.
            sb.AppendLine("\n=== VOICE RECORDER ===");
            try
            {
                object rec = FindRecorder();
                if (rec == null) sb.AppendLine("not found");
                else
                {
                    var rt = rec.GetType();
                    sb.AppendLine("type " + rt.FullName + "   found via GorillaTagger.myRecorder or a scene search");
                    foreach (var pr in rt.GetProperties())
                    {
                        if (pr.GetIndexParameters().Length > 0) continue;
                        string val;
                        try { val = System.Convert.ToString(pr.GetValue(rec, null)); } catch { val = "(error)"; }
                        sb.AppendLine("   " + pr.PropertyType.Name + " " + pr.Name + " = " + val);
                    }
                }
            }
            catch (System.Exception e) { sb.AppendLine("error: " + e.Message); }

            // 2) Everything snowball-like the game defines.
            sb.AppendLine("\n=== SNOWBALL / THROWABLE TYPES ===");
            int types = 0;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type[] all;
                try { all = asm.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException ex) { all = ex.Types; }
                catch { continue; }
                foreach (var t in all)
                {
                    if (t == null) continue;
                    string n = t.Name;
                    if (n.IndexOf("Snowball", System.StringComparison.OrdinalIgnoreCase) < 0
                        && n.IndexOf("Throwable", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (++types > 18) break;
                    try { sb.Append(Describe(t)); } catch { }
                }
            }

            // 3) What is on the player's own rig.
            sb.AppendLine("\n=== OWN RIG: snowball / throwable / holding components ===");
            try
            {
                var gt = System.Type.GetType("GorillaTagger, Assembly-CSharp");
                object inst = gt != null ? GetMember(gt, "Instance") : null;
                var rig = inst != null ? GetMember(inst, "offlineVRRig") as Component : null;
                if (rig == null) sb.AppendLine("offline rig not found");
                else
                {
                    foreach (var c in rig.GetComponentsInChildren<Component>(true))
                    {
                        if (c == null) continue;
                        string tn = c.GetType().Name;
                        if (tn.IndexOf("Snow", System.StringComparison.OrdinalIgnoreCase) < 0
                            && tn.IndexOf("Throw", System.StringComparison.OrdinalIgnoreCase) < 0
                            && tn.IndexOf("Transferrable", System.StringComparison.OrdinalIgnoreCase) < 0
                            && tn.IndexOf("BodyDock", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                        sb.AppendLine("   " + tn + " on " + c.gameObject.name + "  active=" + c.gameObject.activeInHierarchy);
                    }
                }
            }
            catch (System.Exception e) { sb.AppendLine("error: " + e.Message); }

            string dir;
            try { dir = BepInEx.Paths.BepInExRootPath; } catch { dir = Application.persistentDataPath; }
            string path = System.IO.Path.Combine(dir, "PathyBot-diagnostics.txt");
            try
            {
                System.IO.File.WriteAllText(path, sb.ToString());
                voiceStatus = "diagnostics saved: " + path;
            }
            catch (System.Exception e) { voiceStatus = "couldn't save diagnostics: " + e.Message; }
            Debug.Log("[PathyBot] " + voiceStatus);
        }

        // ---------- Body turning ----------
        // Rotates the whole player the same way the game's own snap/smooth turning does: by
        // spinning the turn parent around the head. The body turns to face the direction the
        // bot is moving (the wall while climbing, the finishing point on the last leg) and holds
        // that facing while it waits.

        bool faceOn = true;
        string faceStatus = "looking...";
        Transform turnT, headT;

        void ResolveFacing()
        {
            // [VERSION] GTPlayer on current builds, Player on older ones.
            turnT = Resolve(new[] { "GorillaLocomotion.GTPlayer", "Instance", "turnParent" })
                 ?? Resolve(new[] { "GorillaLocomotion.Player", "Instance", "turnParent" });
            headT = Resolve(new[] { "GorillaLocomotion.GTPlayer", "Instance", "headCollider" })
                 ?? Resolve(new[] { "GorillaLocomotion.Player", "Instance", "headCollider" });
            faceStatus = turnT != null ? "on (turn parent found)" : "NOT found - can't rotate the body";
        }

        void UpdateFacing()
        {
            if (!faceOn) return;
            if (turnT == null)
            {
                if (Time.frameCount % 60 == 0) ResolveFacing(); // keep looking about once a second
                return;
            }
            if (rb == null) return;

            Vector3 vel = rb.linearVelocity;
            bool moving = new Vector2(vel.x, vel.z).magnitude > 0.5f;
            bool walking = !planning && routeIndex < route.Count && (moving || climbing) && heading.sqrMagnitude > 0.01f;
            bool forced = gameFaceDir.sqrMagnitude > 0.01f;  // a game wants the bot to face a certain way
            if (!walking && !forced && Time.time >= lungeUntil) return; // standing still: hold the current facing

            // Which way the body faces now (flattened; fall back to the turn parent if looking straight up/down).
            Vector3 fwd = headT != null ? headT.forward : turnT.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.04f) { fwd = turnT.forward; fwd.y = 0f; }
            float curYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            Vector3 goalDir = forced ? gameFaceDir : heading;
            float goalYaw = Mathf.Atan2(goalDir.x, goalDir.z) * Mathf.Rad2Deg;

            float maxStep = FaceTurnRate * Time.deltaTime;
            float step = Mathf.Clamp(Mathf.DeltaAngle(curYaw, goalYaw), -maxStep, maxStep);

            Vector3 pivot = headT != null ? headT.position : rb.position;
            turnT.RotateAround(pivot, Vector3.up, step);
        }

        void LateUpdate()
        {
            UpdateFlash(); // the scare can arrive even while the bot is off
            if (!enabledBot) { armBlend = 0f; HideLines(); return; }
            UpdateFacing();
            BuildGuide();
            UpdateLines();
            UpdateArms();
        }

        // ---------- Collision helpers (these ignore the player's own colliders) ----------

        bool IsMine(Collider c)
        {
            if (playerRoot == null) return false;
            if (c.transform.IsChildOf(playerRoot)) return true;
            return c.attachedRigidbody != null && c.attachedRigidbody.transform.IsChildOf(playerRoot);
        }

        bool GroundRay(Vector3 origin, float dist, out RaycastHit best)
        {
            best = default;
            float bestDist = float.MaxValue;
            bool found = false;
            var hits = Physics.RaycastAll(origin, Vector3.down, dist, ~0, QueryTriggerInteraction.Ignore);
            foreach (var h in hits)
            {
                if (IsMine(h.collider)) continue;
                if (h.distance < bestDist) { bestDist = h.distance; best = h; found = true; }
            }
            return found;
        }

        // Nearest solid thing along a ray (any direction).
        bool WallRay(Vector3 origin, Vector3 dir, float dist, out RaycastHit best)
        {
            best = default;
            float bestDist = float.MaxValue;
            bool found = false;
            var hits = Physics.RaycastAll(origin, dir, dist, ~0, QueryTriggerInteraction.Ignore);
            foreach (var h in hits)
            {
                if (IsMine(h.collider)) continue;
                if (h.distance < bestDist) { bestDist = h.distance; best = h; found = true; }
            }
            return found;
        }

        bool LineBlocked(Vector3 a, Vector3 b)
        {
            Vector3 d = b - a;
            float m = d.magnitude;
            if (m < 1e-3f) return false;
            var hits = Physics.RaycastAll(a, d / m, m, ~0, QueryTriggerInteraction.Ignore);
            foreach (var h in hits) if (!IsMine(h.collider)) return true;
            return false;
        }

        bool BodyBlocked(Vector3 point)
        {
            var cols = Physics.OverlapCapsule(point + Vector3.up * 0.5f, point + Vector3.up * 1.2f,
                                              0.3f, ~0, QueryTriggerInteraction.Ignore);
            foreach (var c in cols) if (!IsMine(c)) return true;
            return false;
        }

        bool ClearBetween(Vector3 a, Vector3 b) =>
            !LineBlocked(a + Vector3.up * 0.6f, b + Vector3.up * 0.6f);

        // Like ClearBetween, but when the move goes up onto something the check line starts above
        // the obstacle's top edge, so the step itself doesn't count as a wall in the way.
        bool ClearForMove(Vector3 a, Vector3 b)
        {
            float rise = Mathf.Max(0f, b.y - a.y);
            Vector3 s = a + Vector3.up * Mathf.Max(0.6f, rise + 0.2f);
            Vector3 e = b + Vector3.up * 0.6f;
            return !LineBlocked(s, e);
        }

        // Looks just ahead for a low obstacle (step, slab, table edge) that can be hopped onto.
        // On success: rise = height of its top above our feet, dist = how far ahead it starts.
        bool ProbeHop(Vector3 pos, float feetY, out float rise, out float dist)
        {
            rise = 0f;
            dist = 0f;
            Vector3 fwd = heading;
            if (fwd.sqrMagnitude < 0.01f) return false;

            // Two thin sweeps, one near the floor (steps, slabs) and one at waist height (table edges).
            RaycastHit low = default;
            Vector3 hitPoint = Vector3.zero;
            float bestDist = float.MaxValue;
            bool found = false;
            float[] heights = { 0.3f, 0.7f };
            foreach (float hgt in heights)
            {
                var hits = Physics.SphereCastAll(new Vector3(pos.x, feetY + hgt, pos.z), 0.12f, fwd, HopLook,
                                                 ~0, QueryTriggerInteraction.Ignore);
                foreach (var h in hits)
                {
                    if (IsMine(h.collider) || h.normal.y >= 0.6f) continue; // skip ourselves and walkable slopes
                    if (h.distance < bestDist)
                    {
                        bestDist = h.distance;
                        low = h;
                        // If we're already touching it the sweep reports no point, so estimate one.
                        hitPoint = h.distance > 0f ? h.point : new Vector3(pos.x, 0f, pos.z) + fwd * 0.25f;
                        found = true;
                    }
                }
            }
            if (!found) return false;

            // Too tall to hop? Then it's a wall; the planner handles those as climbs.
            Vector3 tall = new Vector3(pos.x, feetY + HopMax + 0.15f, pos.z);
            if (WallRay(tall, fwd, bestDist + 0.6f, out RaycastHit _)) return false;

            // Find the top surface just beyond its edge.
            Vector3 beyond = hitPoint + fwd * 0.35f;
            if (!GroundRay(new Vector3(beyond.x, feetY + HopMax + 0.3f, beyond.z), HopMax + 0.45f, out RaycastHit top)) return false;
            if (top.normal.y < 0.6f) return false;

            rise = top.point.y - feetY;
            if (rise < 0.15f || rise > HopMax) return false;
            if (BodyBlocked(top.point)) return false; // no room to stand up there

            dist = bestDist;
            return true;
        }

        // Finds walkable ground in a cell: up to a hop above, or a short drop below, the previous height.
        bool TryGround(Vector2Int cell, float fromY, out Vector3 point)
        {
            Vector3 origin = new Vector3(cell.x * CellSize, fromY + HopMax + 0.05f, cell.y * CellSize);
            if (GroundRay(origin, HopMax + MaxDrop + 0.05f, out RaycastHit hit) && hit.normal.y > 0.6f)
            {
                point = hit.point;
                if (!BodyBlocked(point)) return true;
            }
            point = default;
            return false;
        }

        // Is there a wall in front of this node (in direction dir) that can be climbed to a ledge?
        // The wall has to be near-vertical and solid all the way up, with nothing overhead, and
        // a clear spot to stand at the top.
        bool TryClimb(Node cur, Vector3 dir, out Vector3 top, out float height, out Vector3 baseCol)
        {
            top = default;
            height = 0f;
            baseCol = default;

            Vector3 chest = cur.pos + Vector3.up * 0.8f;
            if (!WallRay(chest, dir, ClimbReach, out RaycastHit wall)) return false;
            if (Mathf.Abs(wall.normal.y) > 0.35f) return false;

            // Surfaces just beyond the wall face, lowest first.
            Vector3 beyond = wall.point + dir * 0.5f;
            Vector3 origin = new Vector3(beyond.x, cur.pos.y + MaxClimb + 0.5f, beyond.z);
            var hits = Physics.RaycastAll(origin, Vector3.down, MaxClimb + 0.45f - StepHeight, ~0, QueryTriggerInteraction.Ignore);
            var cands = new List<RaycastHit>();
            foreach (var h in hits)
                if (!IsMine(h.collider) && h.normal.y > 0.6f) cands.Add(h);
            cands.Sort((a, b) => a.point.y.CompareTo(b.point.y));

            Vector3 column = cur.pos + dir * Mathf.Max(0f, wall.distance - ClimbStandoff);
            int tried = 0;
            foreach (var c in cands)
            {
                if (++tried > 3) break;
                float rise = c.point.y - cur.pos.y;
                if (rise < HopMax || rise > MaxClimb) continue;
                if (BodyBlocked(c.point)) continue;

                // Nothing overhead between the climber and the ledge.
                Vector3 overheadA = column + Vector3.up * 1.0f;
                Vector3 overheadB = new Vector3(column.x, c.point.y + 1.2f, column.z);
                if (LineBlocked(overheadA, overheadB)) continue;

                // The wall has to be solid all the way up (no windows or gaps).
                bool solid = true;
                for (float y = 1.0f; y < rise; y += 1.0f)
                {
                    Vector3 o = new Vector3(cur.pos.x, cur.pos.y + y, cur.pos.z);
                    if (!WallRay(o, dir, ClimbReach + 0.3f, out RaycastHit w2) || Mathf.Abs(w2.normal.y) > 0.35f)
                    {
                        solid = false;
                        break;
                    }
                }
                if (!solid) continue;

                top = c.point;
                height = rise;
                baseCol = new Vector3(column.x, cur.pos.y, column.z);
                return true;
            }
            return false;
        }

        // Is there a platform up ahead (in direction dir) that can be climbed onto from beside its edge?
        // This covers ledges with no wall under them: floating or overhanging platforms. It finds the top
        // surface, walks back to its near edge, and checks that the column of air just outside that edge
        // is open all the way up and can be reached on foot.
        bool TryPlatform(Node cur, Vector3 dir, out Vector3 top, out float height, out Vector3 column)
        {
            top = default;
            height = 0f;
            column = default;

            foreach (float k in new[] { 0.9f, 1.5f, 2.1f })
            {
                Vector3 probe = cur.pos + dir * k;
                Vector3 origin = new Vector3(probe.x, cur.pos.y + MaxClimb + 0.5f, probe.z);
                var hits = Physics.RaycastAll(origin, Vector3.down, MaxClimb + 0.5f - HopMax, ~0, QueryTriggerInteraction.Ignore);

                RaycastHit best = default;
                bool have = false;
                foreach (var h in hits)
                {
                    if (IsMine(h.collider) || h.normal.y < 0.6f) continue;
                    float r = h.point.y - cur.pos.y;
                    if (r < HopMax + 0.1f || r > MaxClimb) continue;
                    if (!have || h.point.y < best.point.y) { best = h; have = true; }
                }
                if (!have) continue;
                float rise = best.point.y - cur.pos.y;
                if (BodyBlocked(best.point)) continue;

                // Walk back toward us along the top surface to find its near edge.
                Vector3 edge = best.point;
                bool foundEdge = false;
                for (float b = 0.15f; b <= 1.2f; b += 0.15f)
                {
                    Vector3 q = best.point - dir * b;
                    if (GroundRay(new Vector3(q.x, best.point.y + 0.3f, q.z), 0.6f, out RaycastHit eh)
                        && eh.normal.y > 0.6f && Mathf.Abs(eh.point.y - best.point.y) < 0.15f)
                        edge = eh.point;
                    else { foundEdge = true; break; }
                }
                if (!foundEdge) continue; // the surface runs on over our head, so there is no edge to climb beside

                // The spot on the floor just outside the edge.
                Vector3 colXZ = edge - dir * ClimbStandoff;
                if (!GroundRay(new Vector3(colXZ.x, cur.pos.y + 0.8f, colXZ.z), 1.8f, out RaycastHit cf) || cf.normal.y < 0.6f) continue;
                Vector3 colFloor = cf.point;
                if (Mathf.Abs(colFloor.y - cur.pos.y) > 0.6f) continue;
                if (BodyBlocked(colFloor)) continue;

                // Open air all the way up beside the edge, and a clear walk there.
                if (LineBlocked(colFloor + Vector3.up * 1.0f, new Vector3(colXZ.x, best.point.y + 1.2f, colXZ.z))) continue;
                if (Vector3.Distance(cur.pos, colFloor) > 0.3f && !SegmentWalkable(cur.pos, colFloor)) continue;

                // Where we end up standing on the platform.
                Vector3 land = edge + dir * 0.6f;
                if (GroundRay(new Vector3(land.x, best.point.y + 0.4f, land.z), 0.9f, out RaycastHit lh) && lh.normal.y > 0.6f) land = lh.point;
                else land = best.point;
                if (BodyBlocked(land)) continue;

                top = land;
                height = rise;
                column = colFloor;
                return true;
            }
            return false;
        }

        // True if a straight walk from a to b stays on floor that follows the line between them
        // (no walls, no gaps, no ledges in the way).
        bool SegmentWalkable(Vector3 a, Vector3 b)
        {
            if (!ClearBetween(a, b)) return false;

            float len = new Vector2(b.x - a.x, b.z - a.z).magnitude;
            int steps = Mathf.CeilToInt(len / 0.5f);
            float lastY = a.y;
            for (int k = 1; k < steps; k++)
            {
                Vector3 p = Vector3.Lerp(a, b, k / (float)steps);
                if (!GroundRay(new Vector3(p.x, lastY + StepHeight + 0.05f, p.z),
                               StepHeight + MaxDrop + 0.05f, out RaycastHit hit)) return false;
                if (hit.normal.y < 0.6f) return false;
                if (Mathf.Abs(hit.point.y - p.y) > 0.5f) return false; // must follow the slope between a and b
                if (BodyBlocked(hit.point)) return false;
                lastY = hit.point.y;
            }
            return true;
        }

        // Cuts corners: from each point, jump straight to the farthest of the next few plain
        // points that can be walked to in a line. Climb steps are never skipped or merged.
        List<Wp> Simplify(List<Wp> raw, Vector3 start)
        {
            const int Lookahead = 14;
            var result = new List<Wp>();
            Vector3 anchor = start;
            int i = 0;
            while (i < raw.Count)
            {
                if (raw[i].climb)
                {
                    result.Add(raw[i]);
                    anchor = raw[i].pos;
                    i++;
                    continue;
                }

                // The run of plain waypoints we may look across (stops before the next climb).
                int limit = i;
                while (limit + 1 < raw.Count && limit + 1 <= i + Lookahead && !raw[limit + 1].climb) limit++;

                int far = i;
                for (int j = limit; j > i; j--)
                {
                    if (SegmentWalkable(anchor, raw[j].pos)) { far = j; break; }
                }
                result.Add(raw[far]);
                anchor = raw[far].pos;
                i = far + 1;
            }
            return result;
        }

        // ---------- Planning ----------

        // Snaps to the floor under the player and works out how far the player's transform sits above it.
        bool GetStart(out Vector3 start)
        {
            start = default;

            // [VERSION] GTPlayer on current builds, Player on older ones.
            var player = GTPlayer.Instance;
            if (player == null)
            {
                lastFail = "GTPlayer.Instance is null (wrong class name for this game version?)";
                return false;
            }
            playerRoot = player.transform;

            if (!GroundRay(player.transform.position + Vector3.up * 1f, 10f, out RaycastHit under))
            {
                lastFail = "no ground found under the player";
                return false;
            }

            // Standing on the floor is the smallest offset, so keep the smallest we have seen
            // (a reading taken mid-jump would otherwise be too big).
            float off = player.transform.position.y - under.point.y;
            if (!feetOffsetKnown || off < feetOffset) { feetOffset = off; feetOffsetKnown = true; }

            start = under.point;
            return true;
        }

        IEnumerator PlanNewPath()
        {
            planning = true;
            route.Clear();
            routeIndex = 0;
            heading = Vector3.zero;
            botVel = Vector3.zero;

            if (!GetStart(out Vector3 start))
            {
                planning = false;
                resumeAt = Time.time + 1f;
                yield break;
            }

            List<Wp> result = null;
            yield return StartCoroutine(ExploreAndPick(start, r => result = r));

            if (enabledBot && result != null && result.Count > 0)
            {
                int rawCount = result.Count;
                var candidate = Simplify(result, start);
                if (!SimPasses(candidate, start))
                {
                    lastFail = simStatus;
                    planning = false;
                    resumeAt = Time.time;      // plan again straight away, steering around the bad spot
                    yield break;
                }
                route = candidate;
                routeIndex = 0;
                lastProgressPos = playerRoot != null ? playerRoot.position : start;
                lastProgressTime = Time.time;
                lastFail = "";
                Debug.Log("[PathyBot] New route: " + rawCount + " nodes shortcut to " + route.Count + ".");
            }
            else
            {
                lastFail = "no route: only found " + lastExplored + " walkable cells nearby";
                resumeAt = Time.time + 1f; // retry shortly
            }
            planning = false;
        }

        // ---------- Search over a raycast-built grid ----------

        class Node
        {
            public Vector2Int cell;
            public Vector3 pos;
            public float g, f;
            public Node parent;
            public bool climb;    // reached by climbing up a wall from the parent
            public Vector3 dir;   // direction into that wall
            public bool platform; // a platform climb (see Wp)
            public Vector3 column;
        }

        // Binary min-heap on Node.f: the old planner scanned the whole open list every step.
        class NodeHeap
        {
            readonly List<Node> a = new List<Node>();
            public int Count { get { return a.Count; } }
            public void Add(Node n)
            {
                a.Add(n);
                int i = a.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (a[p].f <= a[i].f) break;
                    Node t = a[p]; a[p] = a[i]; a[i] = t;
                    i = p;
                }
            }
            public Node Pop()
            {
                Node top = a[0];
                int last = a.Count - 1;
                a[0] = a[last];
                a.RemoveAt(last);
                int n = a.Count, i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < n && a[l].f < a[m].f) m = l;
                    if (r < n && a[r].f < a[m].f) m = r;
                    if (m == i) break;
                    Node t = a[m]; a[m] = a[i]; a[i] = t;
                    i = m;
                }
                return top;
            }
        }

        const float HeurWeight = 1.25f;       // slightly greedy A*: much faster, nearly as short
        const float AvoidPenalty = 6f;        // extra cost for cells where it recently got stuck
        const float AvoidSeconds = 90f;
        readonly Dictionary<Vector2Int, float> avoidUntil = new Dictionary<Vector2Int, float>();

        // Remembers a spot (and its neighbours) where it got stuck, so new routes steer around it for a while.
        void MarkAvoid(Vector3 p, int radius)
        {
            Vector2Int c = ToCell(p);
            for (int x = -radius; x <= radius; x++)
                for (int z = -radius; z <= radius; z++)
                    avoidUntil[new Vector2Int(c.x + x, c.y + z)] = Time.time + AvoidSeconds;
            if (avoidUntil.Count > 400)
            {
                var old = new List<Vector2Int>();
                foreach (var kv in avoidUntil) if (kv.Value < Time.time) old.Add(kv.Key);
                foreach (var k in old) avoidUntil.Remove(k);
            }
        }

        static readonly Vector2Int[] Neighbors =
        {
            new Vector2Int(1,0), new Vector2Int(-1,0), new Vector2Int(0,1), new Vector2Int(0,-1),
            new Vector2Int(1,1), new Vector2Int(1,-1), new Vector2Int(-1,1), new Vector2Int(-1,-1)
        };

        static Vector2Int ToCell(Vector3 p) =>
            new Vector2Int(Mathf.RoundToInt(p.x / CellSize), Mathf.RoundToInt(p.z / CellSize));

        void Relax(Node cur, Vector2Int nc, Vector3 np, float extraCost, bool climb, Vector3 dir,
                   NodeHeap open, Dictionary<Vector2Int, Node> best, Vector3? goal,
                   bool platform = false, Vector3 column = default)
        {
            if (avoidUntil.Count > 0 && avoidUntil.TryGetValue(nc, out float au) && au > Time.time) extraCost += AvoidPenalty;
            float g = cur.g + Vector3.Distance(cur.pos, np) + extraCost;
            if (best.TryGetValue(nc, out Node existing) && existing.g <= g) return;
            if (WideBlocked(np)) { g += 1.2f; if (existing != null && existing.g <= g) return; }   // prefer roomy routes

            var n = new Node { cell = nc, pos = np, g = g, parent = cur, climb = climb, dir = dir, platform = platform, column = column };
            n.f = g + (goal.HasValue ? Vector3.Distance(np, goal.Value) * HeurWeight : 0f);
            best[nc] = n;
            open.Add(n);
        }

        // Adds every place reachable from cur: walking to the 8 neighbours, or climbing a wall
        // in front of it (straight ahead only, so climbs line up with the grid).
        void Expand(Node cur, NodeHeap open, Dictionary<Vector2Int, Node> best,
                    HashSet<Vector2Int> closed, Vector3? goal)
        {
            foreach (var d in Neighbors)
            {
                Vector2Int nc = cur.cell + d;
                if (closed.Contains(nc)) continue;

                if (TryGround(nc, cur.pos.y, out Vector3 np) && ClearForMove(cur.pos, np))
                {
                    // Hopping up onto something costs more than walking, so it prefers ramps and stairs.
                    float rise = np.y - cur.pos.y;
                    float hopCost = rise > StepHeight ? 1f + 2f * rise : 0f;
                    // Dropping off a ledge is allowed but costs a little, so it prefers walking down ramps and stairs.
                    if (rise < -1f) hopCost = 0.5f * -rise;
                    Relax(cur, nc, np, hopCost, false, Vector3.zero, open, best, goal);
                }
                else if (CanClimb && (d.x == 0 || d.y == 0))
                {
                    Vector3 dir = new Vector3(d.x, 0f, d.y);

                    // 1) A solid wall in front: climb it.
                    if (WallClimbOn && climbBudget > 0)
                    {
                        climbBudget--;
                        if (TryClimb(cur, dir, out Vector3 top, out float rise, out Vector3 baseCol))
                        {
                            Vector2Int tc = ToCell(top);
                            if (!closed.Contains(tc))
                                Relax(cur, tc, top, ClimbBaseCost + rise * ClimbCostPerMeter, true, dir, open, best, goal, false, baseCol);
                            continue;
                        }
                    }

                    // 2) A platform up ahead with no wall under it (a floating or overhanging ledge): go up beside it.
                    if (platformBudget > 0)
                    {
                        platformBudget--;
                        if (TryPlatform(cur, dir, out Vector3 ptop, out float prise, out Vector3 pcol))
                        {
                            Vector2Int pc = ToCell(ptop);
                            if (!closed.Contains(pc))
                                Relax(cur, pc, ptop, ClimbBaseCost + prise * ClimbCostPerMeter + 1f, true, dir, open, best, goal, true, pcol);
                        }
                    }
                }
            }
        }

        // Goal-directed search (used for the chase). If it can't reach the goal it heads for
        // the closest point it did reach.
        IEnumerator AStar(Vector3 start, Vector3 goal, System.Action<List<Wp>> done)
        {
            var open = new NodeHeap();
            var best = new Dictionary<Vector2Int, Node>();
            var closed = new HashSet<Vector2Int>();
            climbBudget = ClimbBudgetPerSearch;
            platformBudget = PlatformBudgetPerSearch;
            astarReached = false;

            var s = new Node { cell = ToCell(start), pos = start, g = 0f };
            s.f = Vector3.Distance(start, goal) * HeurWeight;
            open.Add(s);
            best[s.cell] = s;

            Node nearest = s;
            float nearestH = s.f;
            int expansions = 0;
            while (open.Count > 0 && expansions < MaxExpansionsChase)
            {
                Node cur = open.Pop();

                if (!closed.Add(cur.cell)) continue;

                float h = Vector3.Distance(cur.pos, goal);
                if (h < nearestH) { nearestH = h; nearest = cur; }
                if (h < GoalReach) { astarReached = true; done(Rebuild(cur)); yield break; }

                Expand(cur, open, best, closed, goal);

                expansions++;
                if (expansions % MaxExpansionsPerFrame == 0) yield return null; // don't hitch the game
            }

            done(nearest == s ? null : Rebuild(nearest));
        }

        // Explores outward from the start, then picks a random reached spot and returns the
        // shortest route to it. Because the target comes from cells we actually reached, a route
        // is guaranteed.
        static bool UsesClimb(Node n)
        {
            for (Node q = n; q != null; q = q.parent) if (q.climb) return true;
            return false;
        }

        IEnumerator ExploreAndPick(Vector3 start, System.Action<List<Wp>> done)
        {
            var open = new NodeHeap();
            var best = new Dictionary<Vector2Int, Node>();
            var closed = new HashSet<Vector2Int>();
            var reached = new List<Node>();
            climbBudget = ClimbBudgetPerSearch;
            platformBudget = PlatformBudgetPerSearch;

            var s = new Node { cell = ToCell(start), pos = start, g = 0f };
            s.f = 0f;
            open.Add(s);
            best[s.cell] = s;

            int expansions = 0;
            while (open.Count > 0 && expansions < MaxExpansionsTotal)
            {
                Node cur = open.Pop();

                if (!closed.Add(cur.cell)) continue;
                reached.Add(cur);
                if (cur.g > MaxTargetDist) continue; // far enough, don't grow past here

                Expand(cur, open, best, closed, null);

                expansions++;
                if (expansions % MaxExpansionsPerFrame == 0) yield return null; // don't hitch the game
            }

            lastExplored = reached.Count;

            // Prefer targets in the distance window; fall back to anything not right under us.
            var candidates = reached.FindAll(n => n.g >= MinTargetDist && n.g <= MaxTargetDist);
            if (candidates.Count == 0) candidates = reached.FindAll(n => n.g >= 3f);
            if (candidates.Count == 0) { done(null); yield break; }

            // Half the time, pick somewhere that needs a climb, so the wall/platform climbing actually gets used.
            var climbPicks = candidates.FindAll(n => UsesClimb(n));
            if (climbPicks.Count > 0 && Random.value < 0.5f) candidates = climbPicks;

            Node pick = candidates[Random.Range(0, candidates.Count)];
            done(Rebuild(pick));
        }

        static List<Wp> Rebuild(Node end)
        {
            var list = new List<Wp>();
            for (Node n = end; n != null; n = n.parent)
                list.Add(new Wp { pos = n.pos, climb = n.climb, dir = n.dir, platform = n.platform, column = n.column });
            list.Reverse();
            if (list.Count > 0) list.RemoveAt(0); // drop the node we're standing on
            return list;
        }
    }
}