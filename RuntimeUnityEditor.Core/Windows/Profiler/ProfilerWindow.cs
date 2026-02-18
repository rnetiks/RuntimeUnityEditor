using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RuntimeUnityEditor.Core.Utils;
using RuntimeUnityEditor.Core.Utils.Abstractions;
using UnityEngine;

#pragma warning disable CS1591

namespace RuntimeUnityEditor.Core.Profiler
{
    public sealed class ProfilerWindow : Window<ProfilerWindow>
    {
        #region Constants and Layout Options

        private const string OnGuiMethodName = "OnGUI";
        private const int DefaultRowHeight = 20;

        private static readonly string[] _orderingStrings = { "#", "Time", "Memory", "Name" };
        private static readonly GUILayoutOption[] _colOrderW = { GUILayoutShim.MinWidth(35), GUILayoutShim.MaxWidth(35) };
        private static readonly GUILayoutOption[] _colRanW = { GUILayoutShim.MinWidth(20), GUILayoutShim.MaxWidth(20) };
        private static readonly GUILayoutOption[] _colTimeW = { GUILayoutShim.MinWidth(80), GUILayoutShim.MaxWidth(80) };
        private static readonly GUILayoutOption[] _colMemW = { GUILayoutShim.MinWidth(55), GUILayoutShim.MaxWidth(55) };
        private static readonly GUILayoutOption[] _colNumW = { GUILayoutShim.MinWidth(40), GUILayoutShim.MaxWidth(40) };
        private static readonly GUILayoutOption[] _width22 = { GUILayoutShim.MinWidth(22), GUILayoutShim.MaxWidth(22) };
        private static readonly GUILayoutOption[] _width50 = { GUILayoutShim.MinWidth(50), GUILayoutShim.MaxWidth(50) };
        private static readonly GUILayoutOption[] _width60 = { GUILayoutShim.MinWidth(60), GUILayoutShim.MaxWidth(60) };
        private static readonly GUILayoutOption[] _width80 = { GUILayoutShim.MinWidth(80), GUILayoutShim.MaxWidth(80) };
        private static readonly GUILayoutOption[] _width90 = { GUILayoutShim.MinWidth(90), GUILayoutShim.MaxWidth(90) };
        private static readonly GUILayoutOption[] _width100 = { GUILayoutShim.MinWidth(100), GUILayoutShim.MaxWidth(100) };
        private static readonly GUILayoutOption[] _width120 = { GUILayoutShim.MinWidth(120), GUILayoutShim.MaxWidth(120) };
        private static readonly GUILayoutOption[] _width140 = { GUILayoutShim.MinWidth(140), GUILayoutShim.MaxWidth(140) };
        private static readonly GUILayoutOption[] _width300 = { GUILayoutShim.MinWidth(300), GUILayoutShim.MaxWidth(300) };
        private static readonly GUILayoutOption[] _sliderWidth = { GUILayoutShim.MinWidth(60), GUILayoutShim.MaxWidth(60) };

        private static readonly GUIContent _headerOrder = new GUIContent("#", null, "Execution order within the frame.");
        private static readonly GUIContent _headerRan = new GUIContent("R", null, "Whether the method ran this frame.");
        private static readonly GUIContent _headerTime = new GUIContent("Time", null, "Time spent (current/average).");
        private static readonly GUIContent _headerMem = new GUIContent("Mem", null, "Bytes allocated.");
        private static readonly GUIContent _headerNum = new GUIContent("Num", null, "Instance count.");
        private static readonly GUIContent _headerName = new GUIContent("Method", null, "Full method name.");

        #endregion

        #region Frame Phase Timing

        private static FrameTimingHelper _timingHelper;
        private static PhaseTiming _lastPhases;
        private static ComponentCounts _lastCounts;

        private struct PhaseTiming
        {
            public float FixedUpdateMs;
            public float UpdateMs;
            public float LateUpdateMs;
            public float RenderMs;
            public float AnimationEstimateMs;
            public int FixedUpdateCount;

            public float TotalTracked
            {
                get { return FixedUpdateMs + UpdateMs + LateUpdateMs + RenderMs + AnimationEstimateMs; }
            }
        }

        private struct ComponentCounts
        {
            public int Animators;
            public int SkinnedMeshes;
            public int Rigidbodies;
            public int Colliders;
            public int ParticleSystems;
            public int AudioSources;
            public int Lights;
            public int Cloths;
            
            public float EstimatedAnimatorMs { get { return Animators * 0.3f; } }
            public float EstimatedSkinningMs { get { return SkinnedMeshes * 0.15f; } }
        }

        #endregion

        #region Diagnostic State
        
        private static bool _animatorsDisabled;
        private static bool _skinnedMeshesDisabled;
        private static bool _particlesDisabled;
        private static bool _audioDisabled;
        private static bool _clothDisabled;
        
        private static readonly List<Animator> _disabledAnimators = new List<Animator>();
        private static readonly List<SkinnedMeshRenderer> _disabledSkinned = new List<SkinnedMeshRenderer>();
        private static readonly List<ParticleSystem> _disabledParticles = new List<ParticleSystem>();
        private static readonly List<AudioSource> _disabledAudio = new List<AudioSource>();
        private static readonly List<Cloth> _disabledCloth = new List<Cloth>();

        #endregion

        #region Method Profiling State

        private static readonly Dictionary<long, MethodProfile> _profiles = new Dictionary<long, MethodProfile>();
        private static readonly List<MethodProfile> _displayList = new List<MethodProfile>();
        private static readonly List<long> _removeKeys = new List<long>();
        private static readonly Dictionary<AggregateKey, AggregateProfile> _aggregates = new Dictionary<AggregateKey, AggregateProfile>();
        private static readonly List<AggregateProfile> _aggregateDisplay = new List<AggregateProfile>();
        private static readonly List<MethodProfile> _filteredProfiles = new List<MethodProfile>();
        private static readonly List<AggregateProfile> _filteredAggregates = new List<AggregateProfile>();

        private static readonly Harmony _harmony = new Harmony("rue-profiler-v3");
        private static readonly WaitForEndOfFrame _waitForEndOfFrame = new WaitForEndOfFrame();

        private static int _executionCounter;
        private static int _fixedUpdateCount;
        private static int _lastFixedUpdateCount;
        private static float _totalScriptMs;
        private static float _lastTotalScriptMs;
        private static long _totalScriptBytes;
        private static long _lastTotalScriptBytes;
        private static float _lastFrameMs;

        private static long _lastMonoUsed;
        private static long _lastMonoHeap;
        private static long _lastTotalAlloc;
        private static bool _profilerAvailable;
        private static Func<uint> _getMonoUsedSize;
        private static Func<uint> _getMonoHeapSize;
        private static Func<uint> _getTotalAllocatedMemory;
        private static Action<string> _beginSample;
        private static Action _endSample;

        private static float _updateInterval = 0.2f;
        private static float _lastBuildTime;

        private static int _rowHeight = DefaultRowHeight;
        private static bool _needsHeightMeasure = true;
        private static float _scrollViewHeight;

        private static bool _hookFixed, _hookUpdate, _hookLate, _hookOnGUI;
        private static bool _pause, _hideInputEvents, _showMs = true, _aggregate;
        private static bool _emitProfilerSamples;
        private static int _ordering = 1;
        private static string _searchText = "";
        private static bool _searchCaseSensitive;
        private static Vector2 _scrollPos;

        private static bool _foldoutPhases;
        private static bool _foldoutDiagnostics;
        private static bool _foldoutSearch;

        private static readonly Comparison<MethodProfile> _cmpOrder = (a, b) => a.LastExecutionOrder.CompareTo(b.LastExecutionOrder);
        private static readonly Comparison<MethodProfile> _cmpTime = (a, b) => b.LastTicks.CompareTo(a.LastTicks);
        private static readonly Comparison<MethodProfile> _cmpMem = (a, b) => b.LastBytes.CompareTo(a.LastBytes);
        private static readonly Comparison<MethodProfile> _cmpName = (a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName);

        private static readonly Comparison<AggregateProfile> _cmpAggOrder = (a, b) => a.MaxExecutionOrder.CompareTo(b.MaxExecutionOrder);
        private static readonly Comparison<AggregateProfile> _cmpAggTime = (a, b) => b.TotalTicks.CompareTo(a.TotalTicks);
        private static readonly Comparison<AggregateProfile> _cmpAggMem = (a, b) => b.TotalBytes.CompareTo(a.TotalBytes);
        private static readonly Comparison<AggregateProfile> _cmpAggName = (a, b) => string.CompareOrdinal(a.FullName, b.FullName);

        #endregion

        #region Data Structures

        private struct AggregateKey : IEquatable<AggregateKey>
        {
            public string FullName;
            public bool Equals(AggregateKey other) { return FullName == other.FullName; }
            public override int GetHashCode() { return FullName.GetHashCode(); }
        }

        private sealed class MethodProfile
        {
            public readonly MonoBehaviour Owner;
            public readonly MethodBase Method;
            public readonly string FullName;
            public readonly string DisplayName;
            public readonly string SampleName; public readonly EventType GuiEvent;
            public readonly Stopwatch Timer = new Stopwatch();

            public long LastTicks;
            public long AvgTicks;
            public long GcStart;
            public long LastBytes;
            public int LastExecutionOrder;
            public int FramesSinceRun;
            public bool RanThisFrame;
            public bool OriginalRan;

            public string CachedTimeStr;
            public string CachedMemStr;
            public string CachedOrderStr;
            public long CachedTimeTicks = -1;
            public long CachedMemBytes = -1;
            public int CachedOrder = -1;

            private long _ticksSum;
            private int _sampleCount;
            private const int MaxSamples = 60;

            public MethodProfile(MonoBehaviour owner, MethodBase method, EventType guiEvent)
            {
                Owner = owner;
                Method = method;
                GuiEvent = guiEvent;

                var typeName = owner.GetType().FullDescription();
                FullName = typeName + "::" + method.Name;
                if ((int)guiEvent >= 0)
                    FullName = FullName + "(" + guiEvent + ")";

                DisplayName = owner.transform.name + " > " + FullName;
                
                SampleName = "RUE:" + owner.GetType().Name + "." + method.Name;
            }

            public void RecordSample(long ticks, long bytes)
            {
                LastTicks = ticks;
                LastBytes = bytes;

                _ticksSum += ticks;
                _sampleCount++;

                if (_sampleCount > MaxSamples)
                {
                    _ticksSum = _ticksSum * (MaxSamples - 1) / MaxSamples;
                    _sampleCount = MaxSamples;
                }

                AvgTicks = _sampleCount > 0 ? _ticksSum / _sampleCount : 0;
            }
        }

        private sealed class AggregateProfile
        {
            public readonly string FullName;
            public int InstanceCount;
            public long TotalTicks;
            public long TotalBytes;
            public int MaxExecutionOrder;
            public bool AnyRanThisFrame;

            public string CachedTimeStr;
            public string CachedMemStr;
            public string CachedNumStr;
            public string CachedOrderStr;
            public long CachedTimeTicks = -1;
            public long CachedMemBytes = -1;
            public int CachedNum = -1;
            public int CachedOrder = -1;

            public AggregateProfile(string fullName) { FullName = fullName; }

            public void Reset()
            {
                InstanceCount = 0;
                TotalTicks = 0;
                TotalBytes = 0;
                MaxExecutionOrder = 0;
                AnyRanThisFrame = false;
            }

            public void Add(MethodProfile profile)
            {
                InstanceCount++;
                TotalTicks += profile.LastTicks;
                TotalBytes += profile.LastBytes;
                if (profile.LastExecutionOrder > MaxExecutionOrder)
                    MaxExecutionOrder = profile.LastExecutionOrder;
                if (profile.RanThisFrame)
                    AnyRanThisFrame = true;
            }
        }

        #endregion

        #region Timing Helper Components

        private sealed class FrameTimingHelper : MonoBehaviour
        {
            public long FixedUpdateStartTicks;
            public long FixedUpdateEndTicks;
            public long UpdateStartTicks;
            public long UpdateEndTicks;
            public long LateUpdateStartTicks;
            public long LateUpdateEndTicks;
            public long PreRenderTicks;
            public long PostRenderTicks;
            public int FixedUpdateCallCount;

            private Camera _mainCam;
            private bool _registeredCallbacks;

            void OnEnable() { RegisterCameraCallbacks(); }
            void OnDisable() { UnregisterCameraCallbacks(); }
            void OnDestroy() { UnregisterCameraCallbacks(); }

            private void RegisterCameraCallbacks()
            {
                if (_registeredCallbacks) return;
                Camera.onPreRender = (Camera.CameraCallback)Delegate.Combine(
                    Camera.onPreRender, new Camera.CameraCallback(OnCamPreRender));
                Camera.onPostRender = (Camera.CameraCallback)Delegate.Combine(
                    Camera.onPostRender, new Camera.CameraCallback(OnCamPostRender));
                _registeredCallbacks = true;
            }

            private void UnregisterCameraCallbacks()
            {
                if (!_registeredCallbacks) return;
                Camera.onPreRender = (Camera.CameraCallback)Delegate.Remove(
                    Camera.onPreRender, new Camera.CameraCallback(OnCamPreRender));
                Camera.onPostRender = (Camera.CameraCallback)Delegate.Remove(
                    Camera.onPostRender, new Camera.CameraCallback(OnCamPostRender));
                _registeredCallbacks = false;
            }

            private void OnCamPreRender(Camera cam)
            {
                if (_mainCam == null) _mainCam = Camera.main;
                if (cam == _mainCam && PreRenderTicks == 0)
                    PreRenderTicks = Stopwatch.GetTimestamp();
            }

            private void OnCamPostRender(Camera cam)
            {
                if (cam == _mainCam)
                    PostRenderTicks = Stopwatch.GetTimestamp();
            }

            public void ResetTimings()
            {
                FixedUpdateStartTicks = 0;
                FixedUpdateEndTicks = 0;
                UpdateStartTicks = 0;
                UpdateEndTicks = 0;
                LateUpdateStartTicks = 0;
                LateUpdateEndTicks = 0;
                PreRenderTicks = 0;
                PostRenderTicks = 0;
                FixedUpdateCallCount = 0;
            }

            public PhaseTiming CalculatePhases()
            {
                var p = new PhaseTiming();
                p.FixedUpdateCount = FixedUpdateCallCount;

                if (FixedUpdateStartTicks > 0 && FixedUpdateEndTicks > FixedUpdateStartTicks)
                    p.FixedUpdateMs = TicksToMs(FixedUpdateEndTicks - FixedUpdateStartTicks);

                if (UpdateStartTicks > 0 && UpdateEndTicks > UpdateStartTicks)
                    p.UpdateMs = TicksToMs(UpdateEndTicks - UpdateStartTicks);

                if (LateUpdateStartTicks > 0 && LateUpdateEndTicks > LateUpdateStartTicks)
                    p.LateUpdateMs = TicksToMs(LateUpdateEndTicks - LateUpdateStartTicks);

                if (UpdateEndTicks > 0 && LateUpdateStartTicks > UpdateEndTicks)
                    p.AnimationEstimateMs = TicksToMs(LateUpdateStartTicks - UpdateEndTicks);

                if (PreRenderTicks > 0 && PostRenderTicks > PreRenderTicks)
                    p.RenderMs = TicksToMs(PostRenderTicks - PreRenderTicks);

                return p;
            }

            private static float TicksToMs(long ticks)
            {
                if (Stopwatch.IsHighResolution)
                    return (float)(ticks * 1000.0 / Stopwatch.Frequency);
                return ticks / 10000f;
            }
        }
        
        private sealed class FixedUpdateStartMarker : MonoBehaviour
        {
            void FixedUpdate()
            {
                if (_timingHelper == null) return;
                if (_timingHelper.FixedUpdateStartTicks == 0)
                    _timingHelper.FixedUpdateStartTicks = Stopwatch.GetTimestamp();
                _timingHelper.FixedUpdateCallCount++;
            }
        }
        
        private sealed class FixedUpdateEndMarker : MonoBehaviour
        {
            void FixedUpdate()
            {
                if (_timingHelper != null)
                    _timingHelper.FixedUpdateEndTicks = Stopwatch.GetTimestamp();
            }
        }
        
        private sealed class UpdateStartMarker : MonoBehaviour
        {
            void Update()
            {
                if (_timingHelper != null && _timingHelper.UpdateStartTicks == 0)
                    _timingHelper.UpdateStartTicks = Stopwatch.GetTimestamp();
            }
        }
        
        private sealed class UpdateEndMarker : MonoBehaviour
        {
            void Update()
            {
                if (_timingHelper != null)
                    _timingHelper.UpdateEndTicks = Stopwatch.GetTimestamp();
            }
        }
        
        private sealed class LateUpdateStartMarker : MonoBehaviour
        {
            void LateUpdate()
            {
                if (_timingHelper != null && _timingHelper.LateUpdateStartTicks == 0)
                    _timingHelper.LateUpdateStartTicks = Stopwatch.GetTimestamp();
            }
        }
        
        private sealed class LateUpdateEndMarker : MonoBehaviour
        {
            void LateUpdate()
            {
                if (_timingHelper != null)
                    _timingHelper.LateUpdateEndTicks = Stopwatch.GetTimestamp();
            }
        }

        #endregion

        #region Initialization

        protected override void Initialize(InitSettings initSettings)
        {
            Title = "Profiler";
            MinimumSize = new Vector2(700, 400);
            Enabled = false;
            DefaultScreenPosition = ScreenPartition.CenterUpper;

            InitializeProfilerReflection();
            SetupTimingHelpers();
            RuntimeUnityEditorCore.PluginObject.AbstractStartCoroutine(FrameEndCoroutine());
        }

        private static void InitializeProfilerReflection()
        {
            try
            {
                var profilerType = Type.GetType("UnityEngine.Profiler, UnityEngine") 
                    ?? Type.GetType("UnityEngine.Profiling.Profiler, UnityEngine");
                
                if (profilerType == null)
                {
                    RuntimeUnityEditorCore.Logger.Log(LogLevel.Debug, "[Profiler] UnityEngine.Profiler not found, memory stats disabled");
                    return;
                }

                var getMonoUsed = profilerType.GetMethod("GetMonoUsedSize", BindingFlags.Static | BindingFlags.Public);
                var getMonoHeap = profilerType.GetMethod("GetMonoHeapSize", BindingFlags.Static | BindingFlags.Public);
                var getTotalAlloc = profilerType.GetMethod("GetTotalAllocatedMemory", BindingFlags.Static | BindingFlags.Public);
                var beginSample = profilerType.GetMethod("BeginSample", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(string) }, null);
                var endSample = profilerType.GetMethod("EndSample", BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);

                if (getMonoUsed != null)
                    _getMonoUsedSize = (Func<uint>)Delegate.CreateDelegate(typeof(Func<uint>), getMonoUsed);
                if (getMonoHeap != null)
                    _getMonoHeapSize = (Func<uint>)Delegate.CreateDelegate(typeof(Func<uint>), getMonoHeap);
                if (getTotalAlloc != null)
                    _getTotalAllocatedMemory = (Func<uint>)Delegate.CreateDelegate(typeof(Func<uint>), getTotalAlloc);
                if (beginSample != null)
                    _beginSample = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), beginSample);
                if (endSample != null)
                    _endSample = (Action)Delegate.CreateDelegate(typeof(Action), endSample);

                _profilerAvailable = _getMonoUsedSize != null || _beginSample != null;
                
                if (_profilerAvailable)
                    RuntimeUnityEditorCore.Logger.Log(LogLevel.Debug, "[Profiler] UnityEngine.Profiler found, memory stats enabled");
            }
            catch (Exception e)
            {
                RuntimeUnityEditorCore.Logger.Log(LogLevel.Debug, "[Profiler] Failed to initialize Profiler reflection: " + e.Message);
            }
        }

        private static void SetupTimingHelpers()
        {
            if (_timingHelper != null) return;

            var go = new GameObject("RUE_ProfilerTimingHelper");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;

            _timingHelper = go.AddComponent<FrameTimingHelper>();
            go.AddComponent<FixedUpdateStartMarker>();
            go.AddComponent<FixedUpdateEndMarker>();
            go.AddComponent<UpdateStartMarker>();
            go.AddComponent<UpdateEndMarker>();
            go.AddComponent<LateUpdateStartMarker>();
            go.AddComponent<LateUpdateEndMarker>();
        }

        #endregion

        #region Main Coroutine

        private IEnumerator FrameEndCoroutine()
        {
            while (true)
            {
                yield return _waitForEndOfFrame;

                if (!Enabled)
                {
                    if (_timingHelper != null)
                        _timingHelper.ResetTimings();
                    continue;
                }

                if (_timingHelper != null)
                {
                    _lastPhases = _timingHelper.CalculatePhases();
                    _timingHelper.ResetTimings();
                }

                _lastFrameMs = Time.unscaledDeltaTime * 1000f;
                _lastTotalScriptMs = _totalScriptMs;
                _lastTotalScriptBytes = _totalScriptBytes;
                _lastFixedUpdateCount = _fixedUpdateCount;

                if (_getMonoUsedSize != null)
                    _lastMonoUsed = _getMonoUsedSize();
                if (_getMonoHeapSize != null)
                    _lastMonoHeap = _getMonoHeapSize();
                if (_getTotalAllocatedMemory != null)
                    _lastTotalAlloc = _getTotalAllocatedMemory();

                if (Time.frameCount % 30 == 0)
                    _lastCounts = CountComponents();

                _totalScriptMs = 0;
                _totalScriptBytes = 0;
                _fixedUpdateCount = 0;
                _executionCounter = 0;

                if (_pause)
                    continue;

                ProcessProfiles();

                if (Time.unscaledTime - _lastBuildTime >= _updateInterval)
                {
                    BuildDisplayList();
                    BuildFilteredList();
                    _lastBuildTime = Time.unscaledTime;
                }
            }
        }

        private static void ProcessProfiles()
        {
            _removeKeys.Clear();

            foreach (var kvp in _profiles)
            {
                var profile = kvp.Value;
                if (profile.Owner == null)
                {
                    _removeKeys.Add(kvp.Key);
                    continue;
                }

                profile.FramesSinceRun++;
                if (!profile.RanThisFrame)
                {
                    profile.LastTicks = 0;
                    profile.LastBytes = 0;
                }
                profile.RanThisFrame = false;
            }

            for (int i = 0; i < _removeKeys.Count; i++)
                _profiles.Remove(_removeKeys[i]);
        }

        private static void BuildDisplayList()
        {
            if (_aggregate)
            {
                foreach (var kvp in _aggregates)
                    kvp.Value.Reset();

                foreach (var kvp in _profiles)
                {
                    var profile = kvp.Value;
                    var key = new AggregateKey { FullName = profile.FullName };

                    AggregateProfile agg;
                    if (!_aggregates.TryGetValue(key, out agg))
                    {
                        agg = new AggregateProfile(profile.FullName);
                        _aggregates[key] = agg;
                    }
                    agg.Add(profile);
                }

                _aggregateDisplay.Clear();
                foreach (var kvp in _aggregates)
                    if (kvp.Value.InstanceCount > 0)
                        _aggregateDisplay.Add(kvp.Value);

                switch (_ordering)
                {
                    case 0: _aggregateDisplay.Sort(_cmpAggOrder); break;
                    case 1: _aggregateDisplay.Sort(_cmpAggTime); break;
                    case 2: _aggregateDisplay.Sort(_cmpAggMem); break;
                    case 3: _aggregateDisplay.Sort(_cmpAggName); break;
                }
            }
            else
            {
                _displayList.Clear();
                foreach (var kvp in _profiles)
                    _displayList.Add(kvp.Value);

                switch (_ordering)
                {
                    case 0: _displayList.Sort(_cmpOrder); break;
                    case 1: _displayList.Sort(_cmpTime); break;
                    case 2: _displayList.Sort(_cmpMem); break;
                    case 3: _displayList.Sort(_cmpName); break;
                }
            }
        }

        private static void BuildFilteredList()
        {
            var isSearching = _searchText.Length > 0;
            var comp = _searchCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            if (_aggregate)
            {
                _filteredAggregates.Clear();
                for (int i = 0; i < _aggregateDisplay.Count; i++)
                {
                    var agg = _aggregateDisplay[i];
                    if (isSearching && !agg.FullName.Contains(_searchText, comp))
                        continue;
                    _filteredAggregates.Add(agg);
                }
            }
            else
            {
                _filteredProfiles.Clear();
                for (int i = 0; i < _displayList.Count; i++)
                {
                    var profile = _displayList[i];
                    if (_hideInputEvents && (int)profile.GuiEvent >= 0 &&
                        profile.GuiEvent != EventType.Layout && profile.GuiEvent != EventType.Repaint)
                        continue;
                    if (isSearching && !profile.DisplayName.Contains(_searchText, comp))
                        continue;
                    _filteredProfiles.Add(profile);
                }
            }
        }

        #endregion

        #region Drawing

        protected override void DrawContents()
        {
            DrawToolbar();
            DrawStatsBar();
            DrawFoldoutPhases();
            DrawFoldoutDiagnostics();
            DrawFoldoutSearch();
            DrawColumnHeaders();
            DrawMethodList();
            DrawSummary();
        }

        private void DrawToolbar()
        {
            GUILayout.BeginHorizontal(GUI.skin.box);
            {
                _hookFixed = GUILayout.Toggle(_hookFixed, "Fixed", _width50);
                _hookUpdate = GUILayout.Toggle(_hookUpdate, "Update", _width50);
                _hookLate = GUILayout.Toggle(_hookLate, "Late", _width50);
                _hookOnGUI = GUILayout.Toggle(_hookOnGUI, "GUI", _width50);
                
                if (GUILayout.Button("Apply", _width50))
                    ApplyHooks();
                if (GUILayout.Button("Clear", _width50))
                    ClearAll();

                GUILayout.Space(10);

                _pause = GUILayout.Toggle(_pause, "⏸", _width22);
                _aggregate = GUILayout.Toggle(_aggregate, "Σ", _width22);
                _showMs = GUILayout.Toggle(_showMs, "ms", _width22);
                _hideInputEvents = GUILayout.Toggle(_hideInputEvents, "–In", _width22);
                
                GUILayout.Space(10);

                GUILayout.Label("Sort:", _width50);
                var newOrdering = GUILayout.SelectionGrid(_ordering, _orderingStrings, 4, _width140);
                if (newOrdering != _ordering)
                {
                    _ordering = newOrdering;
                    BuildDisplayList();
                    BuildFilteredList();
                }

                GUILayout.FlexibleSpace();

                _updateInterval = GUILayout.HorizontalSlider(_updateInterval, 0.05f, 2f, _sliderWidth);
                GUILayout.Label(string.Format("{0:F1}s", _updateInterval), _width22);
            }
            GUILayout.EndHorizontal();
        }

        private static void DrawStatsBar()
        {
            var origColor = GUI.color;

            GUILayout.BeginHorizontal(GUI.skin.box);
            {
                var fps = _lastFrameMs > 0.001f ? 1000f / _lastFrameMs : 0f;
                GUI.color = fps < 30 ? Color.red : (fps < 60 ? Color.yellow : Color.green);
                GUILayout.Label(string.Format("{0:F0}fps", fps), _width50);
                GUI.color = origColor;

                GUILayout.Label(string.Format("{0:F1}ms", _lastFrameMs), _width50);

                GUILayout.Label("|", _width22);

                GUILayout.Label(string.Format("Scripts:{0:F1}", _lastTotalScriptMs), _width80);

                if (_lastFixedUpdateCount > 1) GUI.color = Color.yellow;
                GUILayout.Label(string.Format("Fx{0}", _lastFixedUpdateCount), _width22);
                GUI.color = origColor;

                GUILayout.Label("|", _width22);

                if (_getMonoUsedSize != null)
                {
                    var pressure = _lastMonoHeap > 0 ? (float)_lastMonoUsed / _lastMonoHeap : 0f;
                    if (pressure > 0.9f) GUI.color = Color.red;
                    else if (pressure > 0.7f) GUI.color = Color.yellow;
                    GUILayout.Label(string.Format("Heap:{0:F0}%", pressure * 100f), _width60);
                    GUI.color = origColor;
                    GUILayout.Label(string.Format("({0}/{1})", FormatBytes(_lastMonoUsed), FormatBytes(_lastMonoHeap)), _width120);
                }
                else
                {
                    GUILayout.Label(string.Format("GC:{0}", FormatBytes(GC.GetTotalMemory(false))), _width80);
                }

                GUILayout.Label("|", _width22);

                if (_lastTotalScriptBytes > 10000) GUI.color = Color.red;
                else if (_lastTotalScriptBytes > 1000) GUI.color = Color.yellow;
                GUILayout.Label(string.Format("Alloc:{0}", FormatBytes(_lastTotalScriptBytes)), _width80);
                GUI.color = origColor;

                GUILayout.FlexibleSpace();
            }
            GUILayout.EndHorizontal();
        }

        private static void DrawFoldoutPhases()
        {
            GUILayout.BeginHorizontal();
            {
                _foldoutPhases = GUILayout.Toggle(_foldoutPhases, _foldoutPhases ? "▼ Phases" : "▶ Phases", "button", _width80);
                
                if (!_foldoutPhases)
                {
                    var p = _lastPhases;
                    var origColor = GUI.color;
                    
                    if (p.UpdateMs > 0.01f)
                    {
                        GUI.color = new Color(0.4f, 0.8f, 0.4f);
                        GUILayout.Label(string.Format("Upd:{0:F1}", p.UpdateMs), _width60);
                    }
                    if (p.AnimationEstimateMs > 0.5f)
                    {
                        GUI.color = new Color(0.8f, 0.4f, 0.8f);
                        GUILayout.Label(string.Format("Anim:{0:F1}", p.AnimationEstimateMs), _width60);
                    }
                    if (p.LateUpdateMs > 0.01f)
                    {
                        GUI.color = new Color(0.8f, 0.8f, 0.2f);
                        GUILayout.Label(string.Format("Late:{0:F1}", p.LateUpdateMs), _width60);
                    }
                    if (p.RenderMs > 0.01f)
                    {
                        GUI.color = new Color(1f, 0.5f, 0.2f);
                        GUILayout.Label(string.Format("Rend:{0:F1}", p.RenderMs), _width60);
                    }
                    
                    var other = _lastFrameMs - p.TotalTracked;
                    if (other > 1f)
                    {
                        GUI.color = Color.gray;
                        GUILayout.Label(string.Format("Other:{0:F1}", other), _width80);
                    }
                    
                    GUI.color = origColor;
                }
                
                GUILayout.FlexibleSpace();
            }
            GUILayout.EndHorizontal();

            if (_foldoutPhases)
            {
                DrawPhaseTimings();
            }
        }

        private static void DrawPhaseTimings()
        {
            var origColor = GUI.color;
            var p = _lastPhases;

            GUILayout.BeginHorizontal();
            {
                GUILayout.Space(20);
                
                DrawPhaseBox("FixedUpdate", p.FixedUpdateMs, new Color(0.2f, 0.8f, 0.8f),
                    p.FixedUpdateCount > 0 ? string.Format("x{0}", p.FixedUpdateCount) : null);
                DrawPhaseBox("Update", p.UpdateMs, new Color(0.4f, 0.8f, 0.4f), null);
                DrawPhaseBox("Animation*", p.AnimationEstimateMs, new Color(0.8f, 0.4f, 0.8f), "Anim+IK");
                DrawPhaseBox("LateUpdate", p.LateUpdateMs, new Color(0.8f, 0.8f, 0.2f), null);
                DrawPhaseBox("Render", p.RenderMs, new Color(1f, 0.5f, 0.2f), null);

                var unaccounted = _lastFrameMs - p.TotalTracked;
                if (unaccounted > 0.5f)
                    DrawPhaseBox("Other", unaccounted, Color.gray, "Engine");

                GUILayout.FlexibleSpace();
            }
            GUILayout.EndHorizontal();

            GUI.color = origColor;
        }

        private static void DrawPhaseBox(string name, float ms, Color color, string extra)
        {
            if (ms < 0.01f) return;

            var origColor = GUI.color;
            var pct = _lastFrameMs > 0 ? (ms / _lastFrameMs * 100f) : 0f;

            GUILayout.BeginVertical(GUI.skin.box, _width80);
            {
                GUI.color = color;
                GUILayout.Label(name);
                GUI.color = origColor;
                GUILayout.Label(string.Format("{0:F1}ms ({1:F0}%)", ms, pct));
                if (!string.IsNullOrEmpty(extra))
                {
                    GUI.color = Color.gray;
                    GUILayout.Label(extra);
                    GUI.color = origColor;
                }
            }
            GUILayout.EndVertical();
        }

        private static void DrawFoldoutDiagnostics()
        {
            var c = _lastCounts;
            var anyDisabled = _animatorsDisabled || _skinnedMeshesDisabled || _particlesDisabled || _audioDisabled || _clothDisabled;

            GUILayout.BeginHorizontal();
            {
                _foldoutDiagnostics = GUILayout.Toggle(_foldoutDiagnostics, _foldoutDiagnostics ? "▼ Diagnostics" : "▶ Diagnostics", "button", _width100);
                
                if (!_foldoutDiagnostics)
                {
                    var origColor = GUI.color;
                    
                    if (c.Animators > 0)
                    {
                        GUI.color = _animatorsDisabled ? Color.red : origColor;
                        GUILayout.Label(string.Format("Anim:{0}", c.Animators), _width60);
                    }
                    if (c.SkinnedMeshes > 0)
                    {
                        GUI.color = _skinnedMeshesDisabled ? Color.red : origColor;
                        GUILayout.Label(string.Format("Skin:{0}", c.SkinnedMeshes), _width60);
                    }
                    if (c.Cloths > 0)
                    {
                        GUI.color = _clothDisabled ? Color.red : (c.Cloths > 5 ? Color.yellow : origColor);
                        GUILayout.Label(string.Format("Cloth:{0}", c.Cloths), _width60);
                    }
                    
                    GUI.color = origColor;
                    
                    if (anyDisabled && GUILayout.Button("Restore", _width60))
                        RestoreAllDisabled();
                }
                
                GUILayout.FlexibleSpace();
            }
            GUILayout.EndHorizontal();

            if (_foldoutDiagnostics)
            {
                DrawDiagnosticsContent();
            }
        }

        private static void DrawDiagnosticsContent()
        {
            var origColor = GUI.color;
            var c = _lastCounts;

            GUILayout.BeginHorizontal();
            {
                GUILayout.Space(20);

                DrawComponentToggle("Animators", c.Animators, string.Format("~{0:F1}ms", c.EstimatedAnimatorMs),
                    ref _animatorsDisabled, ToggleAnimators, c.Animators > 20);

                DrawComponentToggle("SkinnedMesh", c.SkinnedMeshes, string.Format("~{0:F1}ms", c.EstimatedSkinningMs),
                    ref _skinnedMeshesDisabled, ToggleSkinnedMeshes, c.SkinnedMeshes > 50);

                if (c.Cloths > 0)
                    DrawComponentToggle("Cloth", c.Cloths, "expensive!",
                        ref _clothDisabled, ToggleCloth, c.Cloths > 5);

                DrawComponentToggle("Particles", c.ParticleSystems, null,
                    ref _particlesDisabled, ToggleParticles, false);

                DrawComponentToggle("Audio", c.AudioSources, null,
                    ref _audioDisabled, ToggleAudio, false);

                GUILayout.BeginVertical(GUI.skin.box, _width80);
                {
                    GUILayout.Label(string.Format("RB:{0} Col:{1}", c.Rigidbodies, c.Colliders));
                    GUILayout.Label(string.Format("Lights:{0}", c.Lights));
                }
                GUILayout.EndVertical();

                GUILayout.FlexibleSpace();

                if (_animatorsDisabled || _skinnedMeshesDisabled || _particlesDisabled || _audioDisabled || _clothDisabled)
                {
                    if (GUILayout.Button("Restore All", _width80))
                        RestoreAllDisabled();
                }
            }
            GUILayout.EndHorizontal();

            GUI.color = origColor;
        }
        
        private static void DrawComponentToggle(string name, int count, string extra, ref bool disabled, Action<bool> toggle, bool warn)
        {
            var origColor = GUI.color;

            GUILayout.BeginVertical(GUI.skin.box, _width80);
            {
                GUI.color = disabled ? Color.red : (warn ? Color.yellow : origColor);
                GUILayout.Label(string.Format("{0}: {1}", name, count));
                GUI.color = origColor;
                
                if (!string.IsNullOrEmpty(extra))
                {
                    GUI.color = Color.gray;
                    GUILayout.Label(extra);
                    GUI.color = origColor;
                }

                var newDisabled = GUILayout.Toggle(disabled, disabled ? "Enable" : "Disable");
                if (newDisabled != disabled)
                {
                    toggle(newDisabled);
                    disabled = newDisabled;
                }
            }
            GUILayout.EndVertical();
        }

        private void DrawFoldoutSearch()
        {
            GUILayout.BeginHorizontal();
            {
                _foldoutSearch = GUILayout.Toggle(_foldoutSearch, _foldoutSearch ? "▼ Search" : "▶ Search", "button", _width80);
                
                if (!_foldoutSearch && !string.IsNullOrEmpty(_searchText))
                {
                    GUILayout.Label(string.Format("\"{0}\"", _searchText), _width140);
                    if (GUILayout.Button("✕", _width22))
                    {
                        _searchText = "";
                        BuildFilteredList();
                    }
                }
                
                GUILayout.FlexibleSpace();
                
                _emitProfilerSamples = GUILayout.Toggle(_emitProfilerSamples, "Samples", _width60);
            }
            GUILayout.EndHorizontal();

            if (_foldoutSearch)
            {
                GUILayout.BeginHorizontal();
                {
                    GUILayout.Space(20);
                    GUILayout.Label("Filter:", _width50);
                    var newSearch = GUILayout.TextField(_searchText, _width140);
                    if (newSearch != _searchText)
                    {
                        _searchText = newSearch;
                        BuildFilteredList();
                    }
                    if (GUILayout.Button("✕", _width22))
                    {
                        _searchText = "";
                        BuildFilteredList();
                    }
                    var newCase = GUILayout.Toggle(_searchCaseSensitive, "Case", _width50);
                    if (newCase != _searchCaseSensitive)
                    {
                        _searchCaseSensitive = newCase;
                        BuildFilteredList();
                    }
                    GUILayout.FlexibleSpace();
                }
                GUILayout.EndHorizontal();
            }
        }

        private void DrawColumnHeaders()
        {
            GUILayout.BeginHorizontal(GUI.skin.box);
            {
                GUILayout.Label(_headerOrder, _colOrderW);
                GUILayout.Label(_headerRan, _colRanW);
                GUILayout.Label(_headerTime, _colTimeW);
                GUILayout.Label(_headerMem, _colMemW);
                if (_aggregate)
                    GUILayout.Label(_headerNum, _colNumW);
                GUILayout.Label(_headerName, IMGUIUtils.LayoutOptionsExpandWidthTrue);
            }
            GUILayout.EndHorizontal();
        }

        private void DrawMethodList()
        {
            var origColor = GUI.color;
            var itemCount = _aggregate ? _filteredAggregates.Count : _filteredProfiles.Count;

            if (itemCount == 0)
            {
                GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandHeight(true));
                {
                    GUILayout.FlexibleSpace();
                    GUILayout.BeginHorizontal();
                    {
                        GUILayout.FlexibleSpace();
                        GUILayout.Label(_profiles.Count == 0
                            ? "Select hooks above and click Apply"
                            : "No methods match filter");
                        GUILayout.FlexibleSpace();
                    }
                    GUILayout.EndHorizontal();
                    GUILayout.FlexibleSpace();
                }
                GUILayout.EndVertical();
                return;
            }

            _scrollPos = GUILayout.BeginScrollView(_scrollPos, false, true);
            {
                var visibleStart = Mathf.Max(0, Mathf.FloorToInt(_scrollPos.y / _rowHeight));
                var visibleEnd = Mathf.Min(itemCount, Mathf.CeilToInt((_scrollPos.y + _scrollViewHeight) / _rowHeight) + 1);

                if (visibleStart > 0)
                    GUILayout.Space(visibleStart * _rowHeight);

                if (_aggregate)
                {
                    for (int i = visibleStart; i < visibleEnd; i++)
                        DrawAggregateRow(_filteredAggregates[i], origColor);
                }
                else
                {
                    for (int i = visibleStart; i < visibleEnd; i++)
                        DrawProfileRow(_filteredProfiles[i], origColor);
                }

                if (_needsHeightMeasure && Event.current.type == EventType.Repaint && visibleEnd > visibleStart)
                {
                    var rect = GUILayoutUtilityShim.GetLastRect();
                    if (rect.height > 1)
                    {
                        _rowHeight = Mathf.CeilToInt(rect.height);
                        _needsHeightMeasure = false;
                    }
                }

                var remaining = itemCount - visibleEnd;
                if (remaining > 0)
                    GUILayout.Space(remaining * _rowHeight);
            }
            GUILayout.EndScrollView();

            if (Event.current.type == EventType.Repaint)
                _scrollViewHeight = GUILayoutUtilityShim.GetLastRect().height;

            GUI.color = origColor;
        }

        private void DrawProfileRow(MethodProfile p, Color origColor)
        {
            GUILayout.BeginHorizontal();
            {
                if (p.CachedOrder != p.LastExecutionOrder)
                {
                    p.CachedOrderStr = p.LastExecutionOrder.ToString();
                    p.CachedOrder = p.LastExecutionOrder;
                }
                GUILayout.Label(p.CachedOrderStr, _colOrderW);

                GUI.color = p.FramesSinceRun < 2 ? Color.green : Color.gray;
                GUILayout.Label(p.FramesSinceRun < 2 ? "●" : "○", _colRanW);
                GUI.color = origColor;

                if (p.CachedTimeTicks != p.LastTicks)
                {
                    if (_showMs)
                    {
                        var ms = ConvertTicksToMs(p.LastTicks);
                        var avgMs = ConvertTicksToMs(p.AvgTicks);
                        p.CachedTimeStr = string.Format("{0:F2}/{1:F2}", ms, avgMs);
                    }
                    else
                    {
                        p.CachedTimeStr = string.Format("{0}/{1}", p.LastTicks, p.AvgTicks);
                    }
                    p.CachedTimeTicks = p.LastTicks;
                }

                var msVal = ConvertTicksToMs(p.LastTicks);
                GUI.color = msVal >= 1f ? Color.red : (msVal >= 0.1f ? Color.yellow : origColor);
                GUILayout.Label(p.CachedTimeStr, _colTimeW);
                GUI.color = origColor;

                if (p.CachedMemBytes != p.LastBytes)
                {
                    p.CachedMemStr = FormatBytes(p.LastBytes);
                    p.CachedMemBytes = p.LastBytes;
                }
                GUI.color = p.LastBytes > 1000 ? Color.red : (p.LastBytes > 100 ? Color.yellow : origColor);
                GUILayout.Label(p.CachedMemStr, _colMemW);
                GUI.color = origColor;

                GUILayout.Label(p.DisplayName, IMGUIUtils.LayoutOptionsExpandWidthTrue);
                GUILayout.FlexibleSpace();

                ContextMenu.Instance.DrawContextButton(p.Owner, p.Method, p.FullName, null, null);
                DnSpyHelper.DrawDnSpyButtonIfAvailable(p.Method);
            }
            GUILayout.EndHorizontal();
        }

        private void DrawAggregateRow(AggregateProfile a, Color origColor)
        {
            GUILayout.BeginHorizontal();
            {
                if (a.CachedOrder != a.MaxExecutionOrder)
                {
                    a.CachedOrderStr = a.MaxExecutionOrder.ToString();
                    a.CachedOrder = a.MaxExecutionOrder;
                }
                GUILayout.Label(a.CachedOrderStr, _colOrderW);

                GUI.color = a.AnyRanThisFrame ? Color.green : Color.gray;
                GUILayout.Label(a.AnyRanThisFrame ? "●" : "○", _colRanW);
                GUI.color = origColor;

                if (a.CachedTimeTicks != a.TotalTicks)
                {
                    var ms = ConvertTicksToMs(a.TotalTicks);
                    a.CachedTimeStr = _showMs ? string.Format("{0:F2}ms", ms) : a.TotalTicks.ToString();
                    a.CachedTimeTicks = a.TotalTicks;
                }

                var msVal = ConvertTicksToMs(a.TotalTicks);
                GUI.color = msVal >= 5f ? Color.red : (msVal >= 1f ? Color.yellow : origColor);
                GUILayout.Label(a.CachedTimeStr, _colTimeW);
                GUI.color = origColor;

                if (a.CachedMemBytes != a.TotalBytes)
                {
                    a.CachedMemStr = FormatBytes(a.TotalBytes);
                    a.CachedMemBytes = a.TotalBytes;
                }
                GUI.color = a.TotalBytes > 10000 ? Color.red : (a.TotalBytes > 1000 ? Color.yellow : origColor);
                GUILayout.Label(a.CachedMemStr, _colMemW);
                GUI.color = origColor;

                if (a.CachedNum != a.InstanceCount)
                {
                    a.CachedNumStr = a.InstanceCount.ToString();
                    a.CachedNum = a.InstanceCount;
                }
                GUI.color = a.InstanceCount > 50 ? Color.yellow : origColor;
                GUILayout.Label(a.CachedNumStr, _colNumW);
                GUI.color = origColor;

                GUILayout.Label(a.FullName, IMGUIUtils.LayoutOptionsExpandWidthTrue);
                GUILayout.FlexibleSpace();
            }
            GUILayout.EndHorizontal();
        }

        private static void DrawSummary()
        {
            var origColor = GUI.color;

            GUILayout.BeginHorizontal(GUI.skin.box);
            {
                int total = _profiles.Count;
                int active = 0;
                long ticks = 0;
                long bytes = 0;

                foreach (var kvp in _profiles)
                {
                    var p = kvp.Value;
                    if (p.FramesSinceRun < 2)
                    {
                        active++;
                        ticks += p.LastTicks;
                        bytes += p.LastBytes;
                    }
                }

                var ms = ConvertTicksToMs(ticks);
                var pct = _lastFrameMs > 0 ? (ms / _lastFrameMs * 100f) : 0f;

                GUILayout.Label(string.Format("Methods: {0} ({1} active)", total, active), _width120);
                GUILayout.Label(string.Format("Time: {0:F1}ms ({1:F0}%)", ms, pct), _width100);

                var untracked = _lastFrameMs - ms;
                if (untracked > 1f)
                {
                    GUI.color = Color.cyan;
                    GUILayout.Label(string.Format("Untracked: {0:F1}ms", untracked), _width100);
                    GUI.color = origColor;
                }

                GUILayout.FlexibleSpace();
            }
            GUILayout.EndHorizontal();
        }

        #endregion

        #region Hooking

        private static void ApplyHooks()
        {
            ClearAll();

            var mbType = typeof(MonoBehaviour);
            var methods = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => a.GetTypesSafe())
                .Where(t => mbType.IsAssignableFrom(t) && !t.IsAbstract)
                .Select(t =>
                {
                    if (t.ContainsGenericParameters)
                    {
                        try { return t.MakeGenericType(t.GetGenericArgumentsSafe().Select(x => x.BaseType ?? typeof(object)).ToArray()); }
                        catch { return null; }
                    }
                    return t;
                })
                .Where(t => t != null)
                .SelectMany(t => t.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                .Where(m =>
                {
                    var n = m.Name;
                    return (_hookFixed && n == "FixedUpdate") || (_hookUpdate && n == "Update") ||
                           (_hookLate && n == "LateUpdate") || (_hookOnGUI && n == OnGuiMethodName);
                })
                .ToList();

            var prefix = new HarmonyMethod(typeof(ProfilerWindow), nameof(Prefix)) { priority = int.MaxValue };
            var postfix = new HarmonyMethod(typeof(ProfilerWindow), nameof(Postfix)) { priority = int.MinValue };

            foreach (var method in methods)
            {
                try { _harmony.Patch(method, prefix, postfix); }
                catch (Exception e) { RuntimeUnityEditorCore.Logger.Log(LogLevel.Debug, "[Profiler] Failed: " + method.FullDescription() + " - " + e.Message); }
            }

            RuntimeUnityEditorCore.Logger.Log(LogLevel.Info, "[Profiler] Hooked " + methods.Count + " methods");
        }

        private static void ClearAll()
        {
            _harmony.UnpatchSelf();
            _profiles.Clear();
            _displayList.Clear();
            _aggregates.Clear();
            _aggregateDisplay.Clear();
            _filteredProfiles.Clear();
            _filteredAggregates.Clear();
            _needsHeightMeasure = true;
            RestoreAllDisabled();
        }

        #endregion

        #region Harmony Patches

        private static bool Prefix(MethodBase __originalMethod, MonoBehaviour __instance, out MethodProfile __state)
        {
            __state = null;
            if (_pause) return true;

            if (__originalMethod.Name == "FixedUpdate")
                _fixedUpdateCount++;

            var hash = __instance.GetHashCode() + ((long)__originalMethod.GetHashCode() << 32);
            if (__originalMethod.Name == OnGuiMethodName)
                hash ^= (long)Event.current.type << 17;

            MethodProfile profile;
            if (!_profiles.TryGetValue(hash, out profile))
            {
                var evt = __originalMethod.Name == OnGuiMethodName ? Event.current.type : (EventType)(-1);
                profile = new MethodProfile(__instance, __originalMethod, evt);
                _profiles[hash] = profile;
            }

            profile.LastExecutionOrder = _executionCounter++;
            profile.RanThisFrame = true;
            profile.FramesSinceRun = 0;
            profile.GcStart = GC.GetTotalMemory(false);
            profile.Timer.Reset();
            profile.Timer.Start();

            if (_emitProfilerSamples && _beginSample != null)
                _beginSample(profile.SampleName);

            __state = profile;
            return true;
        }

        private static void Postfix(bool __runOriginal, MethodProfile __state)
        {
            if (__state == null) return;

            if (_emitProfilerSamples && _endSample != null)
                _endSample();

            __state.Timer.Stop();
            __state.OriginalRan = __runOriginal;

            var ticks = __state.Timer.ElapsedTicks;
            var bytes = GC.GetTotalMemory(false) - __state.GcStart;
            if (bytes < 0) bytes = 0;

            __state.RecordSample(ticks, bytes);

            _totalScriptMs += ConvertTicksToMs(ticks);
            _totalScriptBytes += bytes;
        }

        #endregion

        #region Utilities

        private static float ConvertTicksToMs(long ticks)
        {
            if (Stopwatch.IsHighResolution)
                return (float)(ticks * 1000.0 / Stopwatch.Frequency);
            return ticks / 10000f;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + "B";
            if (bytes < 1024 * 1024) return (bytes / 1024f).ToString("F1") + "KB";
            return (bytes / (1024f * 1024f)).ToString("F2") + "MB";
        }

        private static ComponentCounts CountComponents()
        {
            var counts = new ComponentCounts();
            
            counts.Animators = UnityEngine.Object.FindObjectsOfType<Animator>().Length;
            counts.SkinnedMeshes = UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>().Length;
            counts.Rigidbodies = UnityEngine.Object.FindObjectsOfType<Rigidbody>().Length;
            counts.Colliders = UnityEngine.Object.FindObjectsOfType<Collider>().Length;
            counts.ParticleSystems = UnityEngine.Object.FindObjectsOfType<ParticleSystem>().Length;
            counts.AudioSources = UnityEngine.Object.FindObjectsOfType<AudioSource>().Length;
            counts.Lights = UnityEngine.Object.FindObjectsOfType<Light>().Length;
            
            try { counts.Cloths = UnityEngine.Object.FindObjectsOfType<Cloth>().Length; }
            catch { counts.Cloths = 0; }
            
            return counts;
        }

        private static void ToggleAnimators(bool disable)
        {
            if (disable && !_animatorsDisabled)
            {
                _disabledAnimators.Clear();
                foreach (var a in UnityEngine.Object.FindObjectsOfType<Animator>())
                {
                    if (a.enabled)
                    {
                        _disabledAnimators.Add(a);
                        a.enabled = false;
                    }
                }
                _animatorsDisabled = true;
            }
            else if (!disable && _animatorsDisabled)
            {
                foreach (var a in _disabledAnimators)
                    if (a != null) a.enabled = true;
                _disabledAnimators.Clear();
                _animatorsDisabled = false;
            }
        }

        private static void ToggleSkinnedMeshes(bool disable)
        {
            if (disable && !_skinnedMeshesDisabled)
            {
                _disabledSkinned.Clear();
                foreach (var s in UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>())
                {
                    if (s.enabled)
                    {
                        _disabledSkinned.Add(s);
                        s.enabled = false;
                    }
                }
                _skinnedMeshesDisabled = true;
            }
            else if (!disable && _skinnedMeshesDisabled)
            {
                foreach (var s in _disabledSkinned)
                    if (s != null) s.enabled = true;
                _disabledSkinned.Clear();
                _skinnedMeshesDisabled = false;
            }
        }

        private static void ToggleParticles(bool disable)
        {
            if (disable && !_particlesDisabled)
            {
                _disabledParticles.Clear();
                foreach (var p in UnityEngine.Object.FindObjectsOfType<ParticleSystem>())
                {
                    if (p.isPlaying)
                    {
                        _disabledParticles.Add(p);
                        p.Pause();
                    }
                }
                _particlesDisabled = true;
            }
            else if (!disable && _particlesDisabled)
            {
                foreach (var p in _disabledParticles)
                    if (p != null) p.Play();
                _disabledParticles.Clear();
                _particlesDisabled = false;
            }
        }

        private static void ToggleAudio(bool disable)
        {
            if (disable && !_audioDisabled)
            {
                _disabledAudio.Clear();
                foreach (var a in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                {
                    if (a.isPlaying)
                    {
                        _disabledAudio.Add(a);
                        a.Pause();
                    }
                }
                _audioDisabled = true;
            }
            else if (!disable && _audioDisabled)
            {
                foreach (var a in _disabledAudio)
                    if (a != null) a.UnPause();
                _disabledAudio.Clear();
                _audioDisabled = false;
            }
        }

        private static void ToggleCloth(bool disable)
        {
            try
            {
                if (disable && !_clothDisabled)
                {
                    _disabledCloth.Clear();
                    foreach (var c in UnityEngine.Object.FindObjectsOfType<Cloth>())
                    {
                        if (c.enabled)
                        {
                            _disabledCloth.Add(c);
                            c.enabled = false;
                        }
                    }
                    _clothDisabled = true;
                }
                else if (!disable && _clothDisabled)
                {
                    foreach (var c in _disabledCloth)
                        if (c != null) c.enabled = true;
                    _disabledCloth.Clear();
                    _clothDisabled = false;
                }
            }
            catch { }
        }

        private static void RestoreAllDisabled()
        {
            ToggleAnimators(false);
            ToggleSkinnedMeshes(false);
            ToggleParticles(false);
            ToggleAudio(false);
            ToggleCloth(false);
        }

        #endregion
    }
}