using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Mono.CSharp;
using RuntimeUnityEditor.Core.ChangeHistory;
using RuntimeUnityEditor.Core.REPL.MCS;
using RuntimeUnityEditor.Core.Utils;
using RuntimeUnityEditor.Core.Utils.Abstractions;
using UnityEngine;
using Event = UnityEngine.Event;
#pragma warning disable CS1591

namespace RuntimeUnityEditor.Core.REPL
{
    /// <summary>
    /// C# REPL console window using the new TextEditor component.
    /// </summary>
    public sealed class ReplWindow : Window<ReplWindow>
    {
        private string _autostartFilename;
        private static readonly char[] _inputSplitChars = { ',', ';', '<', '>', '(', ')', '[', ']', '=', '|', '&' };

        private const int HistoryLimit = 50;

        private ScriptEvaluator _evaluator;

        private readonly List<string> _history = new List<string>();
        private int _historyPosition;

        private readonly StringBuilder _sb = new StringBuilder();

        private Vector2 _logScrollPosition = Vector2.zero;

        private HashSet<string> _namespaces;
        private Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();

        private HashSet<string> Namespaces
        {
            get
            {
                if (_namespaces == null)
                {
                    _namespaces = new HashSet<string>(
                        AppDomain.CurrentDomain.GetAssemblies()
                            .SelectMany(Extensions.GetTypesSafe)
                            .Where(x => !string.IsNullOrEmpty(x.Namespace))
                            .Select(x => x.Namespace));
                    RuntimeUnityEditorCore.Logger.Log(LogLevel.Debug, $"[REPL] Found {_namespaces.Count} public namespaces");
                }
                return _namespaces;
            }
        }

        private readonly List<Suggestion> _suggestions = new List<Suggestion>();
        private List<Suggestion> _filteredSuggestions = new List<Suggestion>();
        private int _selectedSuggestionIndex = -1;
        private bool _suggestionsActive = false;

        // Parameter info state
        private bool _showingParameterInfo = false;
        private string _currentMethodName = "";
        private List<MethodOverload> _currentOverloads = new List<MethodOverload>();
        private int _currentParameterIndex = 0;
        private int _selectedOverloadIndex = 0;

        private const string SnippletSeparator = "/****************************************/";
        private string _snippletFilename;
        private readonly List<string> _savedSnipplets = new List<string>();
        private bool _snippletsShown;

        // The new TextEditor instance
        private TextEditor _codeEditor;
        private string _previousText = "";

        // Textures for drawing
        private Texture2D _suggestionBgTexture;
        private Texture2D _suggestionHighlightTexture;
        private Texture2D _paramInfoBgTexture;
        private GUIStyle _suggestionStyle;
        private GUIStyle _suggestionHighlightStyle;
        private GUIStyle _kindIconStyle;
        private GUIStyle _typeInfoStyle;
        private GUIStyle _paramInfoStyle;
        private GUIStyle _logStyle;

        /// <inheritdoc />
        protected override void Initialize(InitSettings initSettings)
        {
            if (!UnityFeatureHelper.SupportsRepl) throw new InvalidOperationException("mcs is not supported on this Unity version");

            var disable = initSettings.RegisterSetting("General", "Disable REPL function", false,
                "Completely turn off REPL even if it's supported. Useful if mcs is causing compatibility issues (e.g. in rare cases it can crash the game when used together with some versions of RuntimeDetours in some Unity versions).");
            if (disable.Value) throw new InvalidOperationException("REPL is disabled in config");

            var configPath = initSettings.ConfigPath;
            _autostartFilename = Path.Combine(configPath, "RuntimeUnityEditor.Autostart.cs");
            _snippletFilename = Path.Combine(configPath, "RuntimeUnityEditor.Snipplets.cs");
            Title = "C# REPL Console";

            _evaluator = new ScriptEvaluator(new StringWriter(_sb)) { InteractiveBaseClass = typeof(REPL) };

            // Initialize the new TextEditor
            _codeEditor = new TextEditor("", true);
            _codeEditor.ShowLineNumbers = true;
            _codeEditor.HighlightCurrentLine = true;
            _codeEditor.FontSize = 14f;
            _codeEditor.TabSize = 4;

            // Subscribe to text changes
            _codeEditor.OnTextChanged += OnCodeEditorTextChanged;
            _codeEditor.OnCursorPositionChanged += OnCursorPositionChanged;

            initSettings.PluginMonoBehaviour.AbstractStartCoroutine(DelayedReplSetup());

            DisplayName = "REPL console";
            MinimumSize = new Vector2(280, 200);
            Enabled = false;
            DefaultScreenPosition = ScreenPartition.CenterLower;

            CreateTextures();
            BuildTypeCache();
        }

        private void BuildTypeCache()
        {
            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var type in assembly.GetTypesSafe())
                    {
                        if (!string.IsNullOrEmpty(type.Name) && !_typeCache.ContainsKey(type.Name))
                        {
                            _typeCache[type.Name] = type;
                        }
                        if (!string.IsNullOrEmpty(type.FullName) && !_typeCache.ContainsKey(type.FullName))
                        {
                            _typeCache[type.FullName] = type;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RuntimeUnityEditorCore.Logger.Log(LogLevel.Debug, "[REPL] Error building type cache: " + ex.Message);
            }
        }

        private void CreateTextures()
        {
            _suggestionBgTexture = new Texture2D(1, 1);
            _suggestionBgTexture.SetPixel(0, 0, new Color(0.15f, 0.15f, 0.15f, 0.98f));
            _suggestionBgTexture.Apply();

            _suggestionHighlightTexture = new Texture2D(1, 1);
            _suggestionHighlightTexture.SetPixel(0, 0, new Color(0.3f, 0.5f, 0.8f, 0.8f));
            _suggestionHighlightTexture.Apply();

            _paramInfoBgTexture = new Texture2D(1, 1);
            _paramInfoBgTexture.SetPixel(0, 0, new Color(0.18f, 0.18f, 0.2f, 0.98f));
            _paramInfoBgTexture.Apply();
        }

        private void EnsureStyles()
        {
            if (_suggestionStyle == null)
            {
                _suggestionStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12,
                    padding = new RectOffset(5, 5, 2, 2),
                    margin = new RectOffset(0, 0, 0, 0),
                    normal = { textColor = Color.white }
                };
            }

            if (_suggestionHighlightStyle == null)
            {
                _suggestionHighlightStyle = new GUIStyle(_suggestionStyle)
                {
                    normal = { background = _suggestionHighlightTexture, textColor = Color.white }
                };
            }

            if (_kindIconStyle == null)
            {
                _kindIconStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 10,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                    padding = new RectOffset(2, 2, 2, 2),
                    normal = { textColor = Color.white }
                };
            }

            if (_typeInfoStyle == null)
            {
                _typeInfoStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 11,
                    fontStyle = FontStyle.Italic,
                    normal = { textColor = new Color(0.6f, 0.6f, 0.6f) }
                };
            }

            if (_paramInfoStyle == null)
            {
                _paramInfoStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12,
                    richText = true,
                    padding = new RectOffset(8, 8, 4, 4),
                    normal = { textColor = Color.white }
                };
            }

            if (_logStyle == null)
            {
                _logStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    wordWrap = true,
                    richText = true,
                    normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
                };
            }
        }

        private void OnCodeEditorTextChanged(string newText)
        {
            if (newText != _previousText)
            {
                _previousText = newText;
                UpdateSuggestionsAndParameterInfo();
            }
        }

        private void OnCursorPositionChanged(int newPosition)
        {
            UpdateSuggestionsAndParameterInfo();
        }

        private void UpdateSuggestionsAndParameterInfo()
        {
            var text = _codeEditor.Text;
            var cursorPos = _codeEditor.CursorPosition;

            // Check if we're inside a method call for parameter info
            var methodContext = GetMethodContext(text, cursorPos);
            if (methodContext != null)
            {
                ShowParameterInfo(methodContext.Value.MethodName, methodContext.Value.ParamIndex);
                ClearSuggestions();
                return;
            }
            else
            {
                _showingParameterInfo = false;
            }

            // Otherwise, show normal suggestions
            var inputText = GetTextBeforeCursor(text, cursorPos);
            FetchSuggestions(inputText);

            if (_suggestions.Count > 0)
            {
                _filteredSuggestions = _suggestions
                    .GroupBy(e => e.Result.Split('.')[0])
                    .Select(e => e.First())
                    .OrderBy(e => e.Kind == SuggestionKind.Namespace ? 1 : 0)
                    .ThenBy(e => e.Result)
                    .ToList();

                _suggestionsActive = true;
                _selectedSuggestionIndex = 0;
            }
            else
            {
                ClearSuggestions();
            }
        }

        struct _P
        {
            public string MethodName;
            public int ParamIndex;
            public _P(string fullName, int paramIndex)
            {
                MethodName = fullName;
                ParamIndex = paramIndex;
            }
        }

        private _P? GetMethodContext(string text, int cursorPos)
        {
            if (cursorPos <= 0 || cursorPos > text.Length) return null;

            int parenDepth = 0;
            int paramIndex = 0;
            int methodStart = -1;

            // Scan backwards from cursor
            for (int i = cursorPos - 1; i >= 0; i--)
            {
                char c = text[i];

                if (c == ')')
                {
                    parenDepth++;
                }
                else if (c == '(')
                {
                    if (parenDepth == 0)
                    {
                        // Found the opening paren of our method call
                        methodStart = i;
                        break;
                    }
                    parenDepth--;
                }
                else if (c == ',' && parenDepth == 0)
                {
                    paramIndex++;
                }
            }

            if (methodStart <= 0) return null;

            // Extract method name
            int nameEnd = methodStart;
            int nameStart = nameEnd - 1;
            while (nameStart >= 0 && (char.IsLetterOrDigit(text[nameStart]) || text[nameStart] == '_' || text[nameStart] == '.'))
            {
                nameStart--;
            }
            nameStart++;

            if (nameStart >= nameEnd) return null;

            string fullName = text.Substring(nameStart, nameEnd - nameStart);
            return new _P(fullName, paramIndex);
        }

        private void ShowParameterInfo(string methodName, int paramIndex)
        {
            _currentMethodName = methodName;
            _currentParameterIndex = paramIndex;

            // Try to resolve the method and get overloads
            _currentOverloads.Clear();

            try
            {
                var overloads = ResolveMethodOverloads(methodName);
                _currentOverloads.AddRange(overloads);
            }
            catch (Exception ex)
            {
                RuntimeUnityEditorCore.Logger.Log(LogLevel.Debug, "[REPL] Error resolving method: " + ex.Message);
            }

            _showingParameterInfo = _currentOverloads.Count > 0;
            if (_showingParameterInfo && _selectedOverloadIndex >= _currentOverloads.Count)
            {
                _selectedOverloadIndex = 0;
            }
        }

        private List<MethodOverload> ResolveMethodOverloads(string fullMethodName)
        {
            var overloads = new List<MethodOverload>();

            // Split into parts (e.g., "Console.WriteLine" -> ["Console", "WriteLine"])
            var parts = fullMethodName.Split('.');
            if (parts.Length == 0) return overloads;

            string methodName = parts[parts.Length - 1];
            string typePath = parts.Length > 1 ? string.Join(".", parts.Take(parts.Length - 1).ToArray()) : null;

            Type targetType = null;

            if (typePath != null)
            {
                // Try to find the type
                targetType = FindType(typePath);
            }

            if (targetType == null)
            {
                // Try common types
                var commonTypes = new[] { typeof(Console), typeof(Math), typeof(string), typeof(Convert), 
                    typeof(UnityEngine.Debug), typeof(UnityEngine.Object), typeof(GameObject), typeof(Transform) };
                foreach (var type in commonTypes)
                {
                    if (type.Name == typePath || type.FullName == typePath)
                    {
                        targetType = type;
                        break;
                    }
                }
            }

            if (targetType == null) return overloads;

            // Get all methods with the given name
            var methods = targetType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.Name == methodName)
                .ToList();

            foreach (var method in methods)
            {
                var overload = new MethodOverload
                {
                    ReturnType = GetFriendlyTypeName(method.ReturnType),
                    IsStatic = method.IsStatic,
                    IsGeneric = method.IsGenericMethod,
                    GenericArguments = method.IsGenericMethod 
                        ? method.GetGenericArguments().Select(t => t.Name).ToArray() 
                        : null
                };

                foreach (var param in method.GetParameters())
                {
                    overload.Parameters.Add(new ParameterInfo
                    {
                        Name = param.Name,
                        TypeName = GetFriendlyTypeName(param.ParameterType),
                        HasDefaultValue = param.RawDefaultValue != DBNull.Value,
                        DefaultValue = param.RawDefaultValue != DBNull.Value ? param.DefaultValue?.ToString() ?? "null" : null,
                        IsParams = param.GetCustomAttributes(typeof(ParamArrayAttribute), false).Length > 0,
                        IsOut = param.IsOut,
                        IsRef = param.ParameterType.IsByRef && !param.IsOut
                    });
                }

                overloads.Add(overload);
            }

            return overloads;
        }

        private Type FindType(string typeName)
        {
            if (_typeCache.TryGetValue(typeName, out var cachedType))
                return cachedType;

            // Try with common namespaces
            var commonNamespaces = new[] { "System", "System.Collections.Generic", "System.Linq", 
                "UnityEngine", "UnityEngine.UI" };

            foreach (var ns in commonNamespaces)
            {
                var fullName = ns + "." + typeName;
                if (_typeCache.TryGetValue(fullName, out var type))
                    return type;
            }

            // Search all assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(typeName);
                    if (type != null) return type;

                    foreach (var ns in commonNamespaces)
                    {
                        type = assembly.GetType(ns + "." + typeName);
                        if (type != null) return type;
                    }
                }
                catch { }
            }

            return null;
        }

        private string GetFriendlyTypeName(Type type)
        {
            if (type == null) return "void";

            if (type == typeof(void)) return "void";
            if (type == typeof(int)) return "int";
            if (type == typeof(string)) return "string";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(float)) return "float";
            if (type == typeof(double)) return "double";
            if (type == typeof(long)) return "long";
            if (type == typeof(short)) return "short";
            if (type == typeof(byte)) return "byte";
            if (type == typeof(char)) return "char";
            if (type == typeof(object)) return "object";
            if (type == typeof(decimal)) return "decimal";

            if (type.IsArray)
                return GetFriendlyTypeName(type.GetElementType()) + "[]";

            if (type.IsGenericType)
            {
                var genericDef = type.GetGenericTypeDefinition();
                var baseName = genericDef.Name;
                var tickIndex = baseName.IndexOf('`');
                if (tickIndex > 0) baseName = baseName.Substring(0, tickIndex);

                var genericArgs = string.Join(", ", type.GetGenericArguments().Select(GetFriendlyTypeName).ToArray());
                return $"{baseName}<{genericArgs}>";
            }

            if (type.IsByRef)
                return GetFriendlyTypeName(type.GetElementType());

            return type.Name;
        }

        public static string GetTextBeforeCursor(string text, int cursorPosition)
        {
            if (string.IsNullOrEmpty(text) || cursorPosition <= 0)
                return string.Empty;

            int startPosition = cursorPosition - 1;

            while (startPosition > 0)
            {
                char c = text[startPosition - 1];
                if (char.IsWhiteSpace(c) || _inputSplitChars.Contains(c))
                    break;
                startPosition--;
            }

            return text.Substring(startPosition, cursorPosition - startPosition);
        }

        /// <inheritdoc />
        protected override void VisibleChanged(bool visible)
        {
            base.VisibleChanged(visible);
            _namespaces = null;
        }

        private IEnumerator DelayedReplSetup()
        {
            yield return null;
            try
            {
                RunEnvSetup();
            }
            catch (Exception ex)
            {
                RuntimeUnityEditorCore.Logger.Log(LogLevel.Warning, "Failed to initialize REPL environment - " + ex.Message);
                try
                {
                    RuntimeUnityEditorCore.Instance.RemoveFeature(this);
                    _evaluator.Dispose();
                }
                catch (Exception e)
                {
                    RuntimeUnityEditorCore.Logger.Log(LogLevel.Debug, e);
                }
            }
        }

        /// <summary>
        /// Set up the REPL environment.
        /// </summary>
        public void RunEnvSetup()
        {
            var envSetup = "using System;" +
                           "using UnityEngine;" +
                           "using System.Linq;" +
                           "using System.Collections;" +
                           "using System.Collections.Generic;";

            Evaluate(envSetup);
            RunAutostart(_autostartFilename);
        }

        private void RunAutostart(string autostartFilename)
        {
            if (File.Exists(autostartFilename))
            {
                var allLines = File.ReadAllLines(autostartFilename)
                    .Select(x => x.Trim('\t', ' ', '\r', '\n'))
                    .Where(x => !string.IsNullOrEmpty(x) && !x.StartsWith("//"))
                    .ToArray();

                if (allLines.Length > 0)
                {
                    var message = "Executing code from " + autostartFilename;
                    RuntimeUnityEditorCore.Logger.Log(LogLevel.Info, message);
                    AppendLogLine(message);
                    foreach (var line in allLines)
                        Evaluate(line);
                }
            }
        }

        private Vector2 _suggestionsScrollPosition = Vector2.zero;
        private const int MaxVisibleSuggestions = 10;
        private const float SuggestionItemHeight = 22f;
        private const float SuggestionBoxWidth = 400f;

        /// <inheritdoc />
        protected override void DrawContents()
        {
            EnsureStyles();

            var currentEvent = Event.current;

            // Handle keyboard input for suggestions/parameter info
            if (_suggestionsActive && _filteredSuggestions.Count > 0)
            {
                if (currentEvent.type == EventType.KeyDown)
                {
                    bool handled = false;

                    switch (currentEvent.keyCode)
                    {
                        case KeyCode.UpArrow:
                            _selectedSuggestionIndex--;
                            if (_selectedSuggestionIndex < 0)
                                _selectedSuggestionIndex = _filteredSuggestions.Count - 1;
                            EnsureSuggestionVisible();
                            currentEvent.Use();
                            handled = true;
                            break;

                        case KeyCode.DownArrow:
                            _selectedSuggestionIndex++;
                            if (_selectedSuggestionIndex >= _filteredSuggestions.Count)
                                _selectedSuggestionIndex = 0;
                            EnsureSuggestionVisible();
                            currentEvent.Use();
                            handled = true;
                            break;

                        case KeyCode.Tab:
                        case KeyCode.Return:
                        case KeyCode.KeypadEnter:
                            if (!currentEvent.control && _selectedSuggestionIndex >= 0 && _selectedSuggestionIndex < _filteredSuggestions.Count)
                            {
                                AcceptSuggestion(_filteredSuggestions[_selectedSuggestionIndex]);
                                currentEvent.Use();
                                handled = true;
                            }
                            break;

                        case KeyCode.Escape:
                            ClearSuggestions();
                            currentEvent.Use();
                            handled = true;
                            break;
                    }

                    if (handled)
                        return;
                }
            }
            else if (_showingParameterInfo && _currentOverloads.Count > 1)
            {
                // Navigate overloads with Up/Down when showing param info
                if (currentEvent.type == EventType.KeyDown)
                {
                    if (currentEvent.keyCode == KeyCode.UpArrow && currentEvent.control)
                    {
                        _selectedOverloadIndex--;
                        if (_selectedOverloadIndex < 0)
                            _selectedOverloadIndex = _currentOverloads.Count - 1;
                        currentEvent.Use();
                        return;
                    }
                    else if (currentEvent.keyCode == KeyCode.DownArrow && currentEvent.control)
                    {
                        _selectedOverloadIndex++;
                        if (_selectedOverloadIndex >= _currentOverloads.Count)
                            _selectedOverloadIndex = 0;
                        currentEvent.Use();
                        return;
                    }
                }
            }

            // Handle execution and history
            if (currentEvent.type == EventType.KeyDown && _codeEditor.IsFocused)
            {
                bool ctrl = currentEvent.control || currentEvent.command;

                // Ctrl+Enter to execute
                if ((currentEvent.keyCode == KeyCode.Return || currentEvent.keyCode == KeyCode.KeypadEnter) && ctrl)
                {
                    AcceptInput();
                    currentEvent.Use();
                    return;
                }

                // Ctrl+Shift+F to format
                if (currentEvent.keyCode == KeyCode.F && ctrl && currentEvent.shift)
                {
                    FormatCode();
                    currentEvent.Use();
                    return;
                }

                // History navigation for single-line input without suggestions
                if (!_suggestionsActive && !_showingParameterInfo && !_codeEditor.Text.Contains('\n'))
                {
                    if (currentEvent.keyCode == KeyCode.UpArrow)
                    {
                        FetchHistory(-1);
                        currentEvent.Use();
                        return;
                    }
                    else if (currentEvent.keyCode == KeyCode.DownArrow)
                    {
                        FetchHistory(1);
                        currentEvent.Use();
                        return;
                    }
                }
            }

            GUILayout.BeginVertical();

            #region Log Area

            float logHeight = Mathf.Max(80, 300);

            _logScrollPosition = GUILayout.BeginScrollView(_logScrollPosition, false, true,
                GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.textArea,
                GUILayout.Height(logHeight));

            if (_snippletsShown)
            {
                DrawSnipplets();
            }
            else
            {
                GUILayout.Label(_sb.ToString(), _logStyle);
            }

            GUILayout.EndScrollView();

            #endregion

            #region Editor Area

            float editorHeight = 300f;
            Rect editorRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.Height(editorHeight));

            editorRect.x = 2;
            editorRect.width = WindowRect.width - 20;

            _codeEditor.OnGUI(editorRect);

            #endregion

            #region Suggestions/Parameter Info Popup

            if (_showingParameterInfo && _currentOverloads.Count > 0)
            {
                DrawParameterInfo(editorRect);
            }
            else if (_suggestionsActive && _filteredSuggestions.Count > 0)
            {
                DrawSuggestionsPopup(editorRect);
            }

            #endregion

            #region Bottom Bar

            GUILayout.BeginHorizontal();

            if (GUILayout.Button("Run (Ctrl+Enter)", IMGUIUtils.LayoutOptionsExpandWidthFalse))
                AcceptInput();

            if (GUILayout.Button("Format (Ctrl+Shift+F)", IMGUIUtils.LayoutOptionsExpandWidthFalse))
                FormatCode();

            if (GUILayout.Button("Clear", IMGUIUtils.LayoutOptionsExpandWidthFalse))
                Clear();

            if (GUILayout.Button(_snippletsShown ? "Cancel" : (_codeEditor.Text.Length == 0 ? "Load" : "Save"), IMGUIUtils.LayoutOptionsExpandWidthFalse))
            {
                HandleSnippletButton();
            }

            if (GUILayout.Button("History", IMGUIUtils.LayoutOptionsExpandWidthFalse))
            {
                ShowHistory();
            }

            GUILayout.FlexibleSpace();

            GUILayout.EndHorizontal();

            #endregion

            GUILayout.EndVertical();
        }

        private void DrawSnipplets()
        {
            if (_savedSnipplets.Count == 0)
            {
                GUILayout.Label("This is a list of saved snipplets of code that you can load later.\n\n" +
                                "To save: type code and click Save.\n" +
                                "To load: clear input and click Load.\n" +
                                "Click Cancel to close.", GUI.skin.box);
            }
            else
            {
                foreach (var snipplet in _savedSnipplets)
                {
                    if (GUILayout.Button(snipplet, GUI.skin.box, IMGUIUtils.LayoutOptionsExpandWidthTrue))
                    {
                        _codeEditor.Text = snipplet;
                        _codeEditor.MoveCursorToEnd();
                        _snippletsShown = false;
                        break;
                    }
                }

                if (GUILayout.Button(">> Edit snipplet list in external editor <<", GUI.skin.box, IMGUIUtils.LayoutOptionsExpandWidthTrue))
                {
                    AppendLogLine("Opening snipplet file at " + _snippletFilename);
                    if (!File.Exists(_snippletFilename))
                        File.WriteAllText(_snippletFilename, "");
                    try { Process.Start(_snippletFilename); }
                    catch (Exception e) { AppendLogLine(e.Message); }
                }
            }
        }

        private void DrawParameterInfo(Rect editorRect)
        {
            if (_currentOverloads.Count == 0) return;

            var cursorPos = _codeEditor.GetCursorLocalPosition();
            var overload = _currentOverloads[_selectedOverloadIndex];

            // Build the signature string with rich text highlighting for current param
            var sigBuilder = new StringBuilder();

            if (_currentOverloads.Count > 1)
            {
                sigBuilder.Append($"<color=#888888>({_selectedOverloadIndex + 1}/{_currentOverloads.Count}) Ctrl+↑↓ to switch</color>\n");
            }

            sigBuilder.Append($"<color=#4EC9B0>{overload.ReturnType}</color> ");
            sigBuilder.Append($"<color=#DCDCAA>{_currentMethodName.Split('.').Last()}</color>");

            if (overload.IsGeneric && overload.GenericArguments != null)
            {
                sigBuilder.Append("<");
                sigBuilder.Append(string.Join(", ", overload.GenericArguments.Select(g => $"<color=#4EC9B0>{g}</color>").ToArray()));
                sigBuilder.Append(">");
            }

            sigBuilder.Append("(");

            for (int i = 0; i < overload.Parameters.Count; i++)
            {
                if (i > 0) sigBuilder.Append(", ");

                var param = overload.Parameters[i];
                bool isCurrent = (i == _currentParameterIndex);

                if (isCurrent) sigBuilder.Append("<b>");

                if (param.IsParams) sigBuilder.Append("<color=#569CD6>params</color> ");
                if (param.IsOut) sigBuilder.Append("<color=#569CD6>out</color> ");
                if (param.IsRef) sigBuilder.Append("<color=#569CD6>ref</color> ");

                sigBuilder.Append($"<color=#4EC9B0>{param.TypeName}</color> ");
                sigBuilder.Append(isCurrent ? $"<color=#FFFFFF>{param.Name}</color>" : $"<color=#9CDCFE>{param.Name}</color>");

                if (param.HasDefaultValue)
                {
                    sigBuilder.Append($" = <color=#B5CEA8>{param.DefaultValue}</color>");
                }

                if (isCurrent) sigBuilder.Append("</b>");
            }

            sigBuilder.Append(")");

            // Calculate popup position and size
            var content = new GUIContent(sigBuilder.ToString());
            var size = _paramInfoStyle.CalcSize(content);
            size.x = Mathf.Min(size.x + 20, WindowRect.width - 40);
            size.y += 10;

            float popupX = editorRect.x + cursorPos.x;
            float popupY = editorRect.y + cursorPos.y - size.y - 5;

            // Clamp to window bounds
            if (popupX + size.x > WindowRect.width - 10)
                popupX = WindowRect.width - size.x - 10;
            if (popupX < 5) popupX = 5;
            if (popupY < 5) popupY = editorRect.y + cursorPos.y + 25;

            Rect popupRect = new Rect(popupX, popupY, size.x, size.y);

            GUI.DrawTexture(popupRect, _paramInfoBgTexture);
            GUI.Label(new Rect(popupRect.x, popupRect.y, popupRect.width, popupRect.height), sigBuilder.ToString(), _paramInfoStyle);
        }

        private void DrawSuggestionsPopup(Rect editorRect)
        {
            var cursorPos = _codeEditor.GetCursorLocalPosition();

            float popupX = editorRect.x + cursorPos.x + 5;
            float popupY = editorRect.y + cursorPos.y + 25;

            int visibleCount = Mathf.Min(_filteredSuggestions.Count, MaxVisibleSuggestions);
            float popupHeight = visibleCount * SuggestionItemHeight + 4;

            // Clamp to window bounds
            if (popupX + SuggestionBoxWidth > WindowRect.width - 10)
                popupX = WindowRect.width - SuggestionBoxWidth - 10;
            if (popupX < 5) popupX = 5;
            if (popupY + popupHeight > WindowRect.height - 30)
                popupY = editorRect.y + cursorPos.y - popupHeight - 5;
            if (popupY < 5) popupY = 5;

            Rect popupRect = new Rect(popupX, popupY, SuggestionBoxWidth, popupHeight);
            Rect contentRect = new Rect(0, 0, SuggestionBoxWidth - 20, _filteredSuggestions.Count * SuggestionItemHeight);

            GUI.DrawTexture(popupRect, _suggestionBgTexture);

            _suggestionsScrollPosition = GUI.BeginScrollView(popupRect, _suggestionsScrollPosition, contentRect);

            for (int i = 0; i < _filteredSuggestions.Count; i++)
            {
                Rect itemRect = new Rect(0, i * SuggestionItemHeight, SuggestionBoxWidth - 20, SuggestionItemHeight);
                var suggestion = _filteredSuggestions[i];

                bool isSelected = (i == _selectedSuggestionIndex);

                // Draw background for selected item
                if (isSelected)
                {
                    GUI.DrawTexture(itemRect, _suggestionHighlightTexture);
                }

                // Draw kind icon
                Rect iconRect = new Rect(itemRect.x + 2, itemRect.y + 2, 18, itemRect.height - 4);
                var iconColor = suggestion.GetTextColor();
                _kindIconStyle.normal.textColor = iconColor;
                GUI.Label(iconRect, suggestion.GetKindIcon(), _kindIconStyle);

                // Draw main text
                Rect textRect = new Rect(itemRect.x + 24, itemRect.y, itemRect.width - 24, itemRect.height);
                _suggestionStyle.normal.textColor = suggestion.GetTextColor();
                
                string displayText = suggestion.Result;
                if (suggestion.Kind == SuggestionKind.Method)
                {
                    displayText += "()";
                    if (suggestion.Overloads != null && suggestion.Overloads.Count > 0)
                    {
                        displayText += $" [{suggestion.Overloads.Count}]";
                    }
                }
                
                GUI.Label(textRect, displayText, _suggestionStyle);

                // Draw type info on the right
                if (!string.IsNullOrEmpty(suggestion.TypeName))
                {
                    var typeContent = new GUIContent(suggestion.TypeName);
                    var typeSize = _typeInfoStyle.CalcSize(typeContent);
                    Rect typeRect = new Rect(itemRect.xMax - typeSize.x - 10, itemRect.y, typeSize.x, itemRect.height);
                    GUI.Label(typeRect, suggestion.TypeName, _typeInfoStyle);
                }

                // Handle click
                if (Event.current.type == EventType.MouseDown && itemRect.Contains(Event.current.mousePosition))
                {
                    AcceptSuggestion(suggestion);
                    Event.current.Use();
                }
            }

            GUI.EndScrollView();

            // Prevent clicks in popup from affecting underlying controls
            if (popupRect.Contains(Event.current.mousePosition))
            {
                if (Event.current.type == EventType.MouseDown || Event.current.type == EventType.MouseUp)
                {
                    Event.current.Use();
                }
            }
        }

        private void EnsureSuggestionVisible()
        {
            if (_selectedSuggestionIndex < 0) return;

            float itemTop = _selectedSuggestionIndex * SuggestionItemHeight;
            float itemBottom = itemTop + SuggestionItemHeight;
            float viewHeight = MaxVisibleSuggestions * SuggestionItemHeight;

            if (itemTop < _suggestionsScrollPosition.y)
            {
                _suggestionsScrollPosition.y = itemTop;
            }
            else if (itemBottom > _suggestionsScrollPosition.y + viewHeight)
            {
                _suggestionsScrollPosition.y = itemBottom - viewHeight;
            }
        }

        private void HandleSnippletButton()
        {
            if (_snippletsShown)
            {
                _snippletsShown = false;
            }
            else if (_codeEditor.Text.Length == 0)
            {
                _snippletsShown = true;

                var items = File.Exists(_snippletFilename)
                    ? File.ReadAllText(_snippletFilename)
                        .Split(new[] { SnippletSeparator }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Replace("\t", "    ").Trim(' ', '\r', '\n'))
                        .Where(x => x.Length > 0)
                    : new string[0];
                _savedSnipplets.Clear();
                _savedSnipplets.AddRange(items);
            }
            else
            {
                var contents = File.Exists(_snippletFilename)
                    ? $"{_codeEditor.Text}{Environment.NewLine}{SnippletSeparator}{Environment.NewLine}{File.ReadAllText(_snippletFilename)}"
                    : _codeEditor.Text;
                File.WriteAllText(_snippletFilename, contents);
                AppendLogLine("Saved to snipplets.");
            }
        }

        private void ShowHistory()
        {
            AppendLogLine("");
            AppendLogLine("# History:");
            foreach (var h in _history)
                AppendLogLine(h);

            ScrollToBottom();
        }

        /// <summary>
        /// Evaluate string as C# code.
        /// </summary>
        public object Evaluate(string str)
        {
            object ret = VoidType.Value;
            _evaluator.Compile(str, out var compiled);
            try
            {
                compiled?.Invoke(ref ret);
            }
            catch (Exception e)
            {
                AppendLogLine(e.ToString());
            }

            return ret;
        }

        private void FetchHistory(int move)
        {
            if (_history.Count == 0) return;

            _historyPosition += move;
            _historyPosition %= _history.Count;
            if (_historyPosition < 0)
                _historyPosition = _history.Count - 1;

            _codeEditor.Text = _history[_historyPosition];
            _codeEditor.MoveCursorToEnd();
        }

        private void FetchSuggestions(string input)
        {
            try
            {
                _suggestions.Clear();

                if (string.IsNullOrEmpty(input))
                    return;

                if (input.IndexOfAny(new[] { '?', '{', '}', '[', ']' }) < 0)
                {
                    var logLen = _sb.Length;
                    var completions = _evaluator.GetCompletions(input, out string prefix);
                    _sb.Length = logLen;

                    if (completions != null)
                    {
                        if (prefix == null)
                            prefix = input;

                        foreach (var completion in completions.Where(x => !string.IsNullOrEmpty(x)))
                        {
                            var kind = DetermineSuggestionKind(input, completion);
                            var typeName = GetSuggestionTypeName(input, completion, kind);
                            var overloads = kind == SuggestionKind.Method ? GetMethodOverloadsForSuggestion(input, completion) : null;

                            _suggestions.Add(new Suggestion(completion, prefix, kind, typeName, overloads));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RuntimeUnityEditorCore.Logger.Log(LogLevel.Debug, "[REPL] " + ex);
                ClearSuggestions();
            }
        }

        private SuggestionKind DetermineSuggestionKind(string input, string completion)
        {
            // Check if it's a namespace
            if (Namespaces.Contains(completion))
                return SuggestionKind.Namespace;

            // Try to find the type/member
            string fullPath = input.Contains('.') 
                ? input.Substring(0, input.LastIndexOf('.') + 1) + completion 
                : completion;

            // Check type cache
            if (_typeCache.TryGetValue(completion, out var type) || _typeCache.TryGetValue(fullPath, out type))
            {
                if (type.IsClass) return SuggestionKind.Class;
                if (type.IsValueType && !type.IsEnum) return SuggestionKind.Struct;
                if (type.IsInterface) return SuggestionKind.Interface;
                if (type.IsEnum) return SuggestionKind.Enum;
                if (typeof(System.Delegate).IsAssignableFrom(type)) return SuggestionKind.Delegate;
            }

            // Try to determine if it's a member
            var parts = input.Split('.');
            if (parts.Length >= 1)
            {
                var typeName = string.Join(".", parts.Take(parts.Length - 1).ToArray());
                var memberType = FindType(typeName);
                if (memberType != null)
                {
                    var members = memberType.GetMember(completion, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
                    if (members.Length > 0)
                    {
                        var member = members[0];
                        if (member is MethodInfo) return SuggestionKind.Method;
                        if (member is PropertyInfo) return SuggestionKind.Property;
                        if (member is FieldInfo) return SuggestionKind.Field;
                        if (member is EventInfo) return SuggestionKind.Event;
                    }
                }
            }

            // Check for keywords
            var keywords = new HashSet<string> {
                "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
                "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
                "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
                "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
                "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
                "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
                "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
                "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "var",
                "virtual", "void", "volatile", "while"
            };
            if (keywords.Contains(completion))
                return SuggestionKind.Keyword;

            return SuggestionKind.Unknown;
        }

        private string GetSuggestionTypeName(string input, string completion, SuggestionKind kind)
        {
            if (kind == SuggestionKind.Namespace || kind == SuggestionKind.Keyword)
                return "";

            try
            {
                var parts = input.Split('.');
                if (parts.Length >= 1)
                {
                    var typeName = string.Join(".", parts.Take(parts.Length - 1).ToArray());
                    var memberType = FindType(typeName);
                    if (memberType != null)
                    {
                        var members = memberType.GetMember(completion, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
                        if (members.Length > 0)
                        {
                            var member = members[0];
                            if (member is MethodInfo mi) return GetFriendlyTypeName(mi.ReturnType);
                            if (member is PropertyInfo pi) return GetFriendlyTypeName(pi.PropertyType);
                            if (member is FieldInfo fi) return GetFriendlyTypeName(fi.FieldType);
                            if (member is EventInfo ei) return GetFriendlyTypeName(ei.EventHandlerType);
                        }
                    }
                }

                if (_typeCache.TryGetValue(completion, out var t))
                {
                    return t.Namespace ?? "";
                }
            }
            catch { }

            return "";
        }

        private List<MethodOverload> GetMethodOverloadsForSuggestion(string input, string completion)
        {
            try
            {
                var parts = input.Split('.');
                if (parts.Length >= 1)
                {
                    var typeName = string.Join(".", parts.Take(parts.Length - 1).ToArray());
                    var type = FindType(typeName);
                    if (type != null)
                    {
                        return ResolveMethodOverloads(typeName + "." + completion);
                    }
                }
            }
            catch { }
            return null;
        }

        private void ClearSuggestions()
        {
            _suggestions.Clear();
            _filteredSuggestions.Clear();
            _suggestionsActive = false;
            _selectedSuggestionIndex = -1;
            _suggestionsScrollPosition = Vector2.zero;
        }

        private void AcceptSuggestion(Suggestion suggestion)
        {
            int cursorPos = _codeEditor.CursorPosition;
            string text = _codeEditor.Text;

            int removeStart = cursorPos - suggestion.Original.Length;
            if (removeStart >= 0)
            {
                text = text.Remove(removeStart, suggestion.Original.Length);

                // Use smart completion
                string insertText = suggestion.GetCompletionText();

                // For methods, place cursor between parentheses if there are parameters
                bool hasParams = suggestion.Overloads != null && suggestion.Overloads.Any(o => o.Parameters.Count > 0);
                int cursorOffset = insertText.Length;

                if (suggestion.Kind == SuggestionKind.Method && hasParams)
                {
                    // Place cursor between the ()
                    cursorOffset = insertText.Length - 1;
                }

                text = text.Insert(removeStart, insertText);
                _codeEditor.Text = text;
                _codeEditor.CursorPosition = removeStart + cursorOffset;
            }

            ClearSuggestions();
        }

        private void AcceptInput()
        {
            var inputField = _codeEditor.Text.Trim();

            if (string.IsNullOrEmpty(inputField)) return;

            _history.Add(inputField);
            if (_history.Count > HistoryLimit)
                _history.RemoveRange(0, _history.Count - HistoryLimit);
            _historyPosition = 0;

            Change.Report("(REPL)::" + inputField);

            if (inputField.Contains("geti()"))
            {
                try
                {
                    var val = REPL.geti();
                    if (val != null)
                        inputField = inputField.Replace("geti()", $"geti<{val.GetType().GetSourceCodeRepresentation()}>()");
                }
                catch (SystemException) { }
            }

            AppendLogLine($"> {inputField}");
            var result = Evaluate(inputField);
            if (result != null && !Equals(result, VoidType.Value))
                AppendLogLine($"=> {result}");

            ScrollToBottom();

            _codeEditor.Text = string.Empty;
            ClearSuggestions();
            _showingParameterInfo = false;
        }

        private void FormatCode()
        {
            var text = _codeEditor.Text;
            if (string.IsNullOrEmpty(text)) return;

            var formatted = FormatCSharpCode(text);
            _codeEditor.Text = formatted;
            _codeEditor.MoveCursorToEnd();
        }

        private string FormatCSharpCode(string code)
        {
            var lines = code.Split('\n');
            var result = new StringBuilder();
            int indentLevel = 0;
            string indentString = new string(' ', _codeEditor.TabSize);

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                
                if (string.IsNullOrEmpty(line))
                {
                    result.AppendLine();
                    continue;
                }

                // Decrease indent for closing braces before writing
                if (line.StartsWith("}") || line.StartsWith(")"))
                {
                    indentLevel = Math.Max(0, indentLevel - 1);
                }

                // Check if this is a single-statement after control structure
                bool needsIndent = false;
                if (i > 0)
                {
                    var prevLine = lines[i - 1].Trim();
                    if (IsControlStatement(prevLine) && !prevLine.EndsWith("{") && !line.StartsWith("{"))
                    {
                        needsIndent = true;
                    }
                }

                // Write the line with proper indentation
                for (int j = 0; j < indentLevel + (needsIndent ? 1 : 0); j++)
                {
                    result.Append(indentString);
                }
                result.AppendLine(line);

                // Increase indent after opening braces
                if (line.EndsWith("{") || (line.EndsWith("(") && !line.Contains(")")))
                {
                    indentLevel++;
                }

                // Don't keep the extra indent for single statements
                if (needsIndent && !line.EndsWith("{"))
                {
                    // Single statement indentation was temporary
                }
            }

            return result.ToString().TrimEnd('\r', '\n');
        }

        private bool IsControlStatement(string line)
        {
            var controlKeywords = new[] { "if", "else", "for", "foreach", "while", "do", "switch", "using", "lock" };
            foreach (var keyword in controlKeywords)
            {
                if (line.StartsWith(keyword + " ") || line.StartsWith(keyword + "(") || line == keyword)
                    return true;
                if (line == "else")
                    return true;
            }
            return false;
        }

        private void ScrollToBottom()
        {
            _logScrollPosition.y = float.MaxValue;
        }

        private class VoidType
        {
            public static readonly VoidType Value = new VoidType();
            private VoidType() { }
        }

        internal void AppendLogLine(string message)
        {
            _sb.AppendLine(message);
        }

        /// <summary>
        /// Clear the log.
        /// </summary>
        public void Clear()
        {
            _sb.Length = 0;
        }

        /// <summary>
        /// Use to send an object into the REPL environment.
        /// </summary>
        public void IngestObject(object obj)
        {
            if (obj == null)
            {
                RuntimeUnityEditorCore.Logger.Log(LogLevel.Warning, "obj is null in: " + new StackTrace());
                return;
            }

            REPL.InteropTempVar = obj;
            _codeEditor.Text = $"var {GetUniqueVarName()} = ({obj.GetType().GetSourceCodeRepresentation()}){nameof(REPL.InteropTempVar)}";
            _codeEditor.MoveCursorToEnd();
            ClearSuggestions();
        }

        private string GetUniqueVarName()
        {
            var lastVarName = _evaluator.GetCompletions("q", out _).Max(x =>
            {
                var m = Regex.Match(x, @"^q(\d*)$", RegexOptions.Singleline);
                if (m.Success)
                {
                    var value = m.Groups[1].Value;
                    return string.IsNullOrEmpty(value) ? 0 : int.Parse(value);
                }
                return -1;
            });

            return "q" + (lastVarName >= 0 ? (lastVarName + 1).ToString() : "");
        }
    }
}