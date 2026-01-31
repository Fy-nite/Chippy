using System;
using System.Collections.Generic;
using Adamantite.GFX;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Input;
using ObjectIR.MonoGame.SFX;
using AsmoV2.AudioEngine;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using System.Reflection;
using System.Linq;

namespace Chippy
{
    // Chippy, a chiptune tracker for Asmo

    public class Game : IConsoleGame, AsmoV2.IEngineHost
    {
        // Cache for background gradient row colors
        private static Color[] gradientCache = null;

        private const int PatternRows = 256;
        private const int PatternChannels = 8;
        private const double BeatsPerStep = 0.25; // Sixteenth notes at the current tempo
        private const int NoteFieldIndex = 0;
        private const int EffectHighFieldIndex = 1;
        private const int EffectLowFieldIndex = 2;
        private const int ChannelColumnWidth = 60;
        private const int NoteColumnWidth = 18;
        private const int EffectColumnWidth = 30;
        private const double MinNoteDurationSeconds = 0.005;
        private const double MinReleaseSeconds = 0.9;
        private const double MaxReleaseSeconds = 2;

        private readonly Color[] instrumentColors =
        {
            Colors.Cyan,
            Colors.Magenta,
            Colors.Yellow,
            Colors.Orange
        };

        // Canvas provided by the VBlank host
        private Canvas canvas = null!;
        // reference to engine for UI access
        private AsmoV2.AsmoGameEngine? _engine;
        // input state tracking
        private KeyboardState prevKeyboardState;
        private KeyboardState currKeyboardState;
        private MouseState prevMouseState;
        private MouseState currMouseState;

        private readonly List<SoundEffectInstance> activeVoices = new();
        private readonly Queue<QueuedNote> oneShotQueue = new();
        private readonly SoundEffectInstance[] channelVoices = new SoundEffectInstance[PatternChannels];
        // Remember last triggered note params per channel so we can retrigger when release changes
        private readonly bool[] _hasLastPlay = new bool[PatternChannels];
        private readonly int[] _lastInstrument = new int[PatternChannels];
        private readonly float[] _lastFrequency = new float[PatternChannels];
        private readonly double[] _lastDuration = new double[PatternChannels];
        private readonly float[] _lastAmplitude = new float[PatternChannels];
        private readonly double[] _lastBaseRelease = new double[PatternChannels];
        private readonly bool[] channelMute = new bool[PatternChannels];
        private readonly double[] channelRelease = new double[PatternChannels];
        private bool isInitialized;
        private TrackerPattern pattern = new(PatternRows, PatternChannels);
        // Keep track of the last loaded/saved pattern file path so F9 can save back to the same file
        private string? currentFilePath;

        private int cursorRow;
        private int cursorChannel;
        private int cursorField;
        private int currentOctave = 4;
        private int currentInstrument;
        private bool followMode = true;
        // Whether the editor should auto-advance the cursor to the next row after completing an edit
        private bool autoAdvance = true;

        private bool isPlaying;
        private double bpm = 140.0;
        private double playbackTimer;
        private int nextPlaybackRow;
        private int activeRow;
        private double blinkTimer;
        private bool blinkVisible = true;
        // Global release multiplier for instrument release tails (adjustable at runtime)
        private double releaseScale = 1.0;

        private readonly struct QueuedNote
        {
            public QueuedNote(int channel, SoundEffect effect, float volume)
            {
                Channel = channel;
                Effect = effect;
                Volume = volume;
            }

            public int Channel { get; }
            public SoundEffect Effect { get; }
            public float Volume { get; }
        }

        private static readonly Dictionary<Microsoft.Xna.Framework.Input.Keys, int> HexKeyMap = new()
        {
            { Microsoft.Xna.Framework.Input.Keys.D0, 0x0 },
            { Microsoft.Xna.Framework.Input.Keys.D1, 0x1 },
            { Microsoft.Xna.Framework.Input.Keys.D2, 0x2 },
            { Microsoft.Xna.Framework.Input.Keys.D3, 0x3 },
            { Microsoft.Xna.Framework.Input.Keys.D4, 0x4 },
            { Microsoft.Xna.Framework.Input.Keys.D5, 0x5 },
            { Microsoft.Xna.Framework.Input.Keys.D6, 0x6 },
            { Microsoft.Xna.Framework.Input.Keys.D7, 0x7 },
            { Microsoft.Xna.Framework.Input.Keys.D8, 0x8 },
            { Microsoft.Xna.Framework.Input.Keys.D9, 0x9 },
            { Microsoft.Xna.Framework.Input.Keys.A, 0xA },
            { Microsoft.Xna.Framework.Input.Keys.B, 0xB },
            { Microsoft.Xna.Framework.Input.Keys.C, 0xC },
            { Microsoft.Xna.Framework.Input.Keys.D, 0xD },
            { Microsoft.Xna.Framework.Input.Keys.E, 0xE },
            { Microsoft.Xna.Framework.Input.Keys.F, 0xF }
        };


    private static readonly string[] instrumentNames = new[] { "Square", "Triangle", "Bass", "Noise" };
    private static readonly float[] instrumentAmps = new[] { 1.0f, 1.0f, 1.0f, 1.0f };

        // Per-instrument ADSR settings
        private readonly float[] instrumentAttack = new float[] { 0.005f, 0.005f, 0.005f, 0.005f };
        private readonly float[] instrumentDecay = new float[] { 0.04f, 0.04f, 0.04f, 0.04f };
        private readonly float[] instrumentSustain = new float[] { 0.85f, 0.85f, 0.85f, 0.85f };
        // Per-instrument release multiplier (applied to computed release seconds)
        private readonly float[] instrumentRelease = new float[] { 1.0f, 1.0f, 1.0f, 1.0f };

        public Game() { }

        // UI buttons for mixer
        private readonly List<Adamantite.GFX.UI.Button> _muteButtons = new();
        private readonly List<Adamantite.GFX.UI.Button> _releaseButtons = new();
        // Instrument editor UI elements
        private bool _instrumentEditorOpen = false;
        private readonly List<Adamantite.GFX.UI.UIElement> _editorElements = new();

        public void Init(Canvas surface)
        {
            canvas = surface;
            // initialize input snapshots
            prevKeyboardState = Keyboard.GetState();
            currKeyboardState = prevKeyboardState;
            prevMouseState = Mouse.GetState();
            currMouseState = prevMouseState;

            activeRow = 0;
            nextPlaybackRow = 0;
            cursorField = NoteFieldIndex;
            // initialize per-channel release multipliers
            for (int i = 0; i < channelRelease.Length; i++) channelRelease[i] = 1.0;
            // no auto-load by default; user can open files using F10
            isInitialized = true;

            // create UI buttons if engine is available
            if (_engine != null)
            {
                SetupMixerButtons(surface);
            }
        }

        public void SetEngine(AsmoV2.AsmoGameEngine engine)
        {
            _engine = engine;
            // If we've already initialized (Init called earlier), set up UI now
            if (isInitialized && canvas != null)
            {
                SetupMixerButtons(canvas);
            }
        }

        void SetupMixerButtons(Canvas surface)
        {
            // Avoid creating buttons multiple times
            if (_muteButtons.Count > 0 || _releaseButtons.Count > 0) return;

            // compute same layout as Draw/Update
            int visibleRows = Math.Min(PatternRows, 28);
            int highlightWidth = PatternChannels * ChannelColumnWidth + 40;
            int rowHeight = 9;
            int headerHeight = rowHeight + 2;
            int patternHeight = visibleRows * rowHeight + headerHeight;
            int patternX = (surface.width - (highlightWidth + 12)) / 2;
            int patternY = 58 - headerHeight - 8;
            int mixerY = patternY + patternHeight + 28;

            for (int channel = 0; channel < PatternChannels; channel++)
            {
                int channelX = patternX + 38 + channel * ChannelColumnWidth;
                int muteBtnX = channelX + 16;
                int muteBtnY = mixerY + 12;
                var muteBtn = new Adamantite.GFX.UI.Button(new Microsoft.Xna.Framework.Rectangle(muteBtnX, muteBtnY, 16, 16), channelMute[channel] ? "X" : "M");
                int captured = channel;
                muteBtn.Clicked += () =>
                {
                    channelMute[captured] = !channelMute[captured];
                    muteBtn.Text = channelMute[captured] ? "X" : "M";
                    muteBtn.Invalidate();
                    Console.WriteLine($"[UI] Mute clicked channel {captured} -> {channelMute[captured]}");
                    // also mark approximate mixer area dirty so chippy's draw updates are uploaded
                    _engine?.UI.NotifyInvalid(new Microsoft.Xna.Framework.Rectangle(patternX - 12, mixerY, highlightWidth + 36, 40));
                    if (channelMute[captured])
                    {
                        RemoveQueuedNotesForChannel(captured);
                        StopChannelVoice(captured);
                    }
                };
                _engine.UI.Add(muteBtn);
                _muteButtons.Add(muteBtn);

                int relBtnX = channelX + 36;
                int relBtnY = mixerY + 12;
                var relBtn = new Adamantite.GFX.UI.Button(new Microsoft.Xna.Framework.Rectangle(relBtnX, relBtnY, 16, 16), "R");
                relBtn.Clicked += () =>
                {
                    bool shift = Microsoft.Xna.Framework.Input.Keyboard.GetState().IsKeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift) || Microsoft.Xna.Framework.Input.Keyboard.GetState().IsKeyDown(Microsoft.Xna.Framework.Input.Keys.RightShift);
                    if (shift)
                        channelRelease[captured] = Math.Clamp(channelRelease[captured] + 0.1, 0.1, 5.0);
                    else
                        channelRelease[captured] = Math.Clamp(channelRelease[captured] - 0.1, 0.1, 5.0);
                    relBtn.Invalidate();
                    Console.WriteLine($"[UI] Release clicked channel {captured} -> {channelRelease[captured]:0.0}");
                    _engine?.UI.NotifyInvalid(new Microsoft.Xna.Framework.Rectangle(patternX - 12, mixerY, highlightWidth + 36, 40));
                    // if a note is currently known for this channel, retrigger with new release
                    if (_hasLastPlay[captured])
                    {
                        StopChannelVoice(captured);
                        PlayInstrumentNote(_lastInstrument[captured], _lastFrequency[captured], _lastDuration[captured], _lastAmplitude[captured], captured, _lastBaseRelease[captured] * channelRelease[captured]);
                    }
                };
                Console.WriteLine($"[UI] Adding release button for channel {channel} at ({relBtnX},{relBtnY})");
                _engine.UI.Add(relBtn);
                _releaseButtons.Add(relBtn);
            }
        }

        public void Update(double deltaTime)
        {
            if (!isInitialized)
            {
                return;
            }

            // --- Layout variables for tracker and mixer (shared with Draw) ---
            int visibleRows = Math.Min(PatternRows, 28);
            int highlightWidth = PatternChannels * ChannelColumnWidth + 40;
            int rowHeight = 9;
            int headerHeight = rowHeight + 2;
            int patternHeight = visibleRows * rowHeight + headerHeight;
            int patternX = (640 - (highlightWidth + 12)) / 2; // fallback width, adjust if you have actual surface width
            int patternY = 58 - headerHeight - 8;
            int mixerY = patternY + patternHeight + 28;

            UpdateBlink(deltaTime);
            // sample input states
            prevKeyboardState = currKeyboardState;
            currKeyboardState = Keyboard.GetState();
            prevMouseState = currMouseState;
            currMouseState = Mouse.GetState();

            HandleInput();
            AdvancePlayback(deltaTime);
            UpdateAudio(deltaTime);


            // Mixer UI handled via retained UI buttons
        }

        public void Draw(Canvas surface)
        {
            if (_instrumentEditorOpen)
            {
                DrawInstrumentEditor(surface);
                return;
            }

            // --- Optimized Background: cache gradient row colors ---
            if (gradientCache == null || gradientCache.Length != surface.height)
            {
                gradientCache = new Color[surface.height];
                for (int y = 0; y < surface.height; y++)
                {
                    int r = 16 + (int)(32 * y / (float)surface.height);
                    int g = 18 + (int)(36 * y / (float)surface.height);
                    int b = 32 + (int)(48 * y / (float)surface.height);
                    gradientCache[y] = new Color(r, g, b, 255);
                }
            }

            for (int y = 0; y < surface.height; y++)
            {
                Color rowColor = gradientCache[y];
                for (int x = 0; x < surface.width; x++)
                    surface.SetPixel(x, y, rowColor);
            }

            // Center the tracker pattern horizontally
            int visibleRows = Math.Min(PatternRows, 28);
            int highlightWidth = PatternChannels * ChannelColumnWidth + 40;
            int rowHeight = 9;
            int headerHeight = rowHeight + 2;
            int patternHeight = visibleRows * rowHeight + headerHeight;
            int patternX = (surface.width - (highlightWidth + 12)) / 2;
            int patternY = 58 - headerHeight - 8;

            // --- Title Bar ---
            int titleBarHeight = 28;
            // Use DrawFilledRect for background and DrawText via Canvas extension methods if present
            surface.DrawFilledRect(patternX - 12, patternY - titleBarHeight - 8, highlightWidth + 36, titleBarHeight, new Color(32, 36, 56, 255));
            surface.DrawText(patternX + 12, patternY - titleBarHeight - 2, "Chippy Tracker", Colors.Yellow);

            // --- Tracker Background ---
            surface.DrawFilledRect(patternX - 12, patternY - 12, highlightWidth + 36, patternHeight + 32, new Color(18, 20, 32, 255));

            // --- Tracker Pattern ---
            DrawPattern(surface, patternX + 10);

            // --- Mixer UI ---
            int mixerY = patternY + patternHeight + 28;
            int mixerHeight = 40;
            surface.DrawRect(patternX - 12, mixerY, highlightWidth + 36, mixerHeight, new Color(24, 26, 38, 255));
            for (int channel = 0; channel < PatternChannels; channel++)
            {
                int channelX = patternX + 38 + channel * ChannelColumnWidth;
                // Draw volume bar (dummy value for now)
                int volBarHeight = 24;
                int volBarWidth = 8;
                int volBarY = mixerY + 8;
                int vol = channelMute[channel] ? 0 : 18; // Show 0 if muted
                surface.DrawFilledRect(channelX, volBarY + (volBarHeight - vol), volBarWidth, vol, channelMute[channel] ? Colors.DarkGray : Colors.Green);
                surface.DrawOutlinedRect(channelX, volBarY, volBarWidth, volBarHeight, Colors.DarkGray);
                // Draw mute button (clickable)
                int muteBtnX = channelX + 16;
                int muteBtnY = mixerY + 12;
                bool mouseOverMute = currMouseState.X >= muteBtnX && currMouseState.X < muteBtnX + 16 && currMouseState.Y >= muteBtnY && currMouseState.Y < muteBtnY + 16;
                Color muteColor = channelMute[channel]
                    ? (mouseOverMute ? new Color(180, 60, 60, 255) : new Color(120, 30, 30, 255))
                    : (mouseOverMute ? new Color(90, 90, 90, 255) : new Color(60, 60, 60, 255));
                surface.DrawFilledRect(muteBtnX, muteBtnY, 16, 16, muteColor);
                surface.DrawText(muteBtnX + 2, muteBtnY + 2, channelMute[channel] ? "X" : "M", Colors.White);
                // Draw per-channel release indicator
                string relText = $"R:{channelRelease[channel]:0.0}";
                surface.DrawText(channelX, mixerY + 28, relText, Colors.White);
            }

            // --- Footer ---
            DrawFooter(surface);
        }

        private void UpdateBlink(double deltaTime)
        {
            blinkTimer += deltaTime;
            if (blinkTimer >= 0.4f)
            {
                blinkTimer = 0;
                blinkVisible = !blinkVisible;
            }
        }

        private bool IsKeyPressed(Keys key)
        {
            return currKeyboardState.IsKeyDown(key) && prevKeyboardState.IsKeyUp(key);
        }

        private bool IsKeyDown(Keys key)
        {
            return currKeyboardState.IsKeyDown(key);
        }

        private void HandleInput()
        {
            if (!isInitialized)
            {
                return;
            }

            HandleTransport();
            HandleSettings();
            HandleNavigation();
            HandleEditing();
            HandleEditorToggle();
        }

        private void HandleEditorToggle()
        {
            // Toggle instrument editor with F2 or Ctrl+Tab
            bool ctrlTab = (IsKeyPressed(Keys.Tab) && (IsKeyDown(Keys.LeftControl) || IsKeyDown(Keys.RightControl)));
            if (IsKeyPressed(Keys.F2) || ctrlTab)
            {
                if (_instrumentEditorOpen) CloseInstrumentEditor(); else OpenInstrumentEditor();
            }
        }

        void OpenInstrumentEditor()
        {
            if (_engine == null || canvas == null) return;
            if (_instrumentEditorOpen) return;
            _instrumentEditorOpen = true;
            // Build editor UI: one button pair (Atk+, Sustain-) per instrument
            int baseY = 24;
            int spacing = 20;
            for (int i = 0; i < instrumentNames.Length; i++)
            {
                int iy = baseY + i * spacing;
                var nameLbl = new Adamantite.GFX.UI.Label(new Microsoft.Xna.Framework.Rectangle(16, iy, 120, 12), instrumentNames[i], Colors.White);
                _engine.UI.Add(nameLbl); _editorElements.Add(nameLbl);

                int bx = 140;
                int captured = i;

                // Attack +/-
                var atkInc = new Adamantite.GFX.UI.Button(new Microsoft.Xna.Framework.Rectangle(bx, iy, 18, 12), "+A");
                atkInc.Clicked += () => { instrumentAttack[captured] = Math.Clamp(instrumentAttack[captured] + 0.002f, 0.001f, 2f); Subsystem.Sound.ClearPrecomputed(); InvalidateEditor(); };
                _engine.UI.Add(atkInc); _editorElements.Add(atkInc);

                var atkDec = new Adamantite.GFX.UI.Button(new Microsoft.Xna.Framework.Rectangle(bx + 20, iy, 18, 12), "-A");
                atkDec.Clicked += () => { instrumentAttack[captured] = Math.Clamp(instrumentAttack[captured] - 0.002f, 0.001f, 2f); Subsystem.Sound.ClearPrecomputed(); InvalidateEditor(); };
                _engine.UI.Add(atkDec); _editorElements.Add(atkDec);

                // Decay +/-
                var decInc = new Adamantite.GFX.UI.Button(new Microsoft.Xna.Framework.Rectangle(bx + 44, iy, 18, 12), "+D");
                decInc.Clicked += () => { instrumentDecay[captured] = Math.Clamp(instrumentDecay[captured] + 0.005f, 0.001f, 5f); Subsystem.Sound.ClearPrecomputed(); InvalidateEditor(); };
                _engine.UI.Add(decInc); _editorElements.Add(decInc);

                var decDec = new Adamantite.GFX.UI.Button(new Microsoft.Xna.Framework.Rectangle(bx + 64, iy, 18, 12), "-D");
                decDec.Clicked += () => { instrumentDecay[captured] = Math.Clamp(instrumentDecay[captured] - 0.005f, 0.001f, 5f); Subsystem.Sound.ClearPrecomputed(); InvalidateEditor(); };
                _engine.UI.Add(decDec); _editorElements.Add(decDec);

                // Sustain +/-
                var susInc = new Adamantite.GFX.UI.Button(new Microsoft.Xna.Framework.Rectangle(bx + 88, iy, 18, 12), "+S");
                susInc.Clicked += () => { instrumentSustain[captured] = Math.Clamp(instrumentSustain[captured] + 0.02f, 0.0f, 1f); Subsystem.Sound.ClearPrecomputed(); InvalidateEditor(); };
                _engine.UI.Add(susInc); _editorElements.Add(susInc);

                var susDec = new Adamantite.GFX.UI.Button(new Microsoft.Xna.Framework.Rectangle(bx + 108, iy, 18, 12), "-S");
                susDec.Clicked += () => { instrumentSustain[captured] = Math.Clamp(instrumentSustain[captured] - 0.02f, 0.0f, 1f); Subsystem.Sound.ClearPrecomputed(); InvalidateEditor(); };
                _engine.UI.Add(susDec); _editorElements.Add(susDec);

                // Release +/-
                var relInc = new Adamantite.GFX.UI.Button(new Microsoft.Xna.Framework.Rectangle(bx + 132, iy, 18, 12), "+R");
                relInc.Clicked += () => { instrumentRelease[captured] = Math.Clamp(instrumentRelease[captured] + 0.1f, 0.1f, 10f); Subsystem.Sound.ClearPrecomputed(); InvalidateEditor(); };
                _engine.UI.Add(relInc); _editorElements.Add(relInc);

                var relDec = new Adamantite.GFX.UI.Button(new Microsoft.Xna.Framework.Rectangle(bx + 152, iy, 18, 12), "-R");
                relDec.Clicked += () => { instrumentRelease[captured] = Math.Clamp(instrumentRelease[captured] - 0.1f, 0.1f, 10f); Subsystem.Sound.ClearPrecomputed(); InvalidateEditor(); };
                _engine.UI.Add(relDec); _editorElements.Add(relDec);

                // Add ADSR graph to the right
                var graph = new Adamantite.GFX.UI.AdsrGraph(new Microsoft.Xna.Framework.Rectangle(bx + 176, iy - 2, 140, 18),
                    () => instrumentAttack[captured],
                    () => instrumentDecay[captured],
                    () => instrumentSustain[captured],
                    () => instrumentRelease[captured]);
                _engine.UI.Add(graph); _editorElements.Add(graph);
            }
        }

        void InvalidateEditor()
        {
            if (_engine == null) return;
            _engine.UI.NotifyInvalid(new Microsoft.Xna.Framework.Rectangle(0, 0, canvas.width, 80));
        }

        void CloseInstrumentEditor()
        {
            if (_engine == null) return;
            foreach (var e in _editorElements) _engine.UI.Remove(e);
            _editorElements.Clear();
            _instrumentEditorOpen = false;
        }

        private void HandleTransport()
        {
            if (!isInitialized)
            {
                return;
            }

            if (IsKeyPressed(Keys.Space))
            {
                if (!isPlaying)
                {
                    StartPlayback();
                }
                else
                {
                    StopPlayback();
                }
            }

            // Save/Load pattern shortcuts
            if (IsKeyPressed(Keys.F9)) // save (show small in-game filename prompt)
            {
                try
                {
                    // If we already have a current file path, save directly. Otherwise prompt the user.
                    if (!string.IsNullOrEmpty(currentFilePath))
                    {
                        pattern.SaveToFile(currentFilePath);
                        Console.WriteLine("Pattern saved: " + currentFilePath);
                    }
                    else
                    {
                        string? path = CrossPlatformFileDialog.ShowSaveFileDialog("Chippy Pattern (*.chip)|*.chip|All Files (*.*)|*.*", "song.chip", "chip");
                        if (!string.IsNullOrEmpty(path))
                        {
                            pattern.SaveToFile(path);
                            currentFilePath = path;
                            Console.WriteLine("Pattern saved: " + path);
                        }
                    }
                }
                catch (Exception ex) { Console.WriteLine("Save pattern error: " + ex.Message); }
            }

            if (IsKeyPressed(Keys.F10)) // load (cycle through recent pattern files in working dir)
            {
                try
                {
                    // Show native Open File dialog so the user can select a .chip file
                    string? path = CrossPlatformFileDialog.ShowOpenFileDialog("Chippy Pattern (*.chip)|*.chip|All Files (*.*)|*.*");
                    if (!string.IsNullOrEmpty(path))
                    {
                        pattern = TrackerPattern.LoadFromFile(path);
                        currentFilePath = path;
                        Console.WriteLine("Pattern loaded: " + path);
                    }
                    else
                    {
                        // Fallback: if no file selected, keep previous behavior of loading most recent in working dir
                        var dir = Environment.CurrentDirectory;
                        var candidates = new List<string>();
                        candidates.AddRange(System.IO.Directory.GetFiles(dir, "chippy_pattern_*.chip"));
                        candidates.AddRange(System.IO.Directory.GetFiles(dir, "*.chip"));
                        if (candidates.Count > 0)
                        {
                            string latest = null!;
                            DateTime latestTime = DateTime.MinValue;
                            foreach (var f in candidates)
                            {
                                try
                                {
                                    var t = System.IO.File.GetLastWriteTimeUtc(f);
                                    if (t > latestTime)
                                    {
                                        latestTime = t;
                                        latest = f;
                                    }
                                }
                                catch { }
                            }

                            if (!string.IsNullOrEmpty(latest))
                            {
                                pattern = TrackerPattern.LoadFromFile(latest);
                                currentFilePath = latest;
                                Console.WriteLine("Pattern loaded: " + latest);
                            }
                        }
                    }
                }
                catch (Exception ex) { Console.WriteLine("Load pattern error: " + ex.Message); }
            }

            if (IsKeyPressed(Keys.Tab))
            {
                bool shiftDown = IsKeyDown(Keys.LeftShift) || IsKeyDown(Keys.RightShift);
                if (shiftDown)
                {
                    followMode = !followMode;
                }
            }
        }

        private void HandleSettings()
        {
            if (!isInitialized)
            {
                return;
            }

            if (IsKeyPressed(Keys.OemOpenBrackets))
            {
                currentOctave = Math.Clamp(currentOctave - 1, 0, 8);
            }

            if (IsKeyPressed(Keys.OemCloseBrackets))
            {
                currentOctave = Math.Clamp(currentOctave + 1, 0, 8);
            }

            if (IsKeyPressed(Keys.PageUp))
            {
                bpm = Math.Min(300.0, bpm + 5.0);
            }

            if (IsKeyPressed(Keys.PageDown))
            {
                bpm = Math.Max(40.0, bpm - 5.0);
            }

            // Adjust global release multiplier with + / - keys
            if (IsKeyPressed(Keys.OemPlus) || IsKeyPressed(Keys.Add))
            {
                releaseScale = Math.Clamp(releaseScale + 0.1, 0.1, 5.0);
            }

            if (IsKeyPressed(Keys.OemMinus) || IsKeyPressed(Keys.Subtract))
            {
                releaseScale = Math.Clamp(releaseScale - 0.1, 0.1, 5.0);
            }

            if (IsKeyPressed(Keys.F1)) SetInstrument(0);
            if (IsKeyPressed(Keys.F2)) SetInstrument(1);
            if (IsKeyPressed(Keys.F3)) SetInstrument(2);
            if (IsKeyPressed(Keys.F4)) SetInstrument(3);
            if (IsKeyPressed(Keys.F5))
            {
                autoAdvance = !autoAdvance;
            }
        }

        private void HandleNavigation()
        {
            if (!isInitialized)
            {
                return;
            }

            if (IsKeyPressed(Keys.Up))
            {
                MoveRow(-1);
            }

            if (IsKeyPressed(Keys.Down))
            {
                MoveRow(1);
            }

            if (IsKeyPressed(Keys.Left))
            {
                MoveHorizontal(-1);
            }

            if (IsKeyPressed(Keys.Right))
            {
                MoveHorizontal(1);
            }

            if (IsKeyPressed(Keys.Home))
            {
                cursorRow = 0;
                if (!isPlaying)
                {
                    activeRow = cursorRow;
                    nextPlaybackRow = cursorRow;
                }
            }

            if (IsKeyPressed(Keys.End))
            {
                cursorRow = PatternRows - 1;
                if (!isPlaying)
                {
                    activeRow = cursorRow;
                    nextPlaybackRow = cursorRow;
                }
            }
        }

        private void HandleEditing()
        {
            if (!isInitialized)
            {
                return;
            }

            bool ctrl = IsKeyDown(Keys.LeftControl) || IsKeyDown(Keys.RightControl);

            if (ctrl && IsKeyPressed(Keys.Delete))
            {
                pattern.ClearRow(cursorRow);
                return;
            }

            if (cursorField == NoteFieldIndex)
            {
                bool shiftDown = IsKeyDown(Keys.LeftShift) || IsKeyDown(Keys.RightShift);
                if (IsKeyPressed(Keys.Tab) && !shiftDown)
                {
                    InsertNoteOff();
                    return;
                }

                foreach (var key in TrackerKeyboardMap.AllMappedKeys)
                {
                    if (IsKeyPressed(key) && TrackerKeyboardMap.TryGetSemitoneOffset(key, out int offset))
                    {
                        InsertNote(offset);
                        return;
                    }
                }

                if (IsKeyPressed(Keys.Back) || IsKeyPressed(Keys.Delete))
                {
                    pattern.ClearCell(cursorRow, cursorChannel);
                }
            }
            else
            {
                // Support a shortcut: press 'R' to set the effect to release command (4Rxx)
                if (IsKeyPressed(Keys.R) && cursorField == EffectHighFieldIndex)
                {
                    var step = pattern[cursorRow, cursorChannel];
                    // set high nibble to 4, low nibble remains 0 until typed
                    step.Effect = TrackerEffect.FromByte((byte)(0x4 << 4));
                    pattern[cursorRow, cursorChannel] = step;
                    // move to low nibble for user to type parameter
                    cursorField = EffectLowFieldIndex;
                    return;
                }

                foreach (var (key, value) in HexKeyMap)
                {
                    if (IsKeyPressed(key))
                    {
                        ApplyEffectNibble(value & 0xF);
                        return;
                    }
                }

                if (IsKeyPressed(Keys.Back) || IsKeyPressed(Keys.Delete))
                {
                    pattern.ClearEffect(cursorRow, cursorChannel);
                }
            }
        }

        private void MoveRow(int delta)
        {
            cursorRow = (cursorRow + delta + PatternRows) % PatternRows;
            if (!isPlaying)
            {
                activeRow = cursorRow;
                nextPlaybackRow = cursorRow;
            }
        }

        private void MoveHorizontal(int delta)
        {
            if (delta < 0)
            {
                if (cursorField > NoteFieldIndex)
                {
                    cursorField--;
                }
                else
                {
                    cursorChannel = (cursorChannel + PatternChannels - 1) % PatternChannels;
                    cursorField = EffectLowFieldIndex;
                }
            }
            else if (delta > 0)
            {
                if (cursorField < EffectLowFieldIndex)
                {
                    cursorField++;
                }
                else
                {
                    cursorChannel = (cursorChannel + 1) % PatternChannels;
                    cursorField = NoteFieldIndex;
                }
            }
        }

        private void InsertNote(int semitoneOffset)
        {
            int baseMidi = (currentOctave + 1) * 12;
            int midi = Math.Clamp(baseMidi + semitoneOffset, 0, 127);
            var note = TrackerNote.FromMidi(midi, currentInstrument);
            var step = pattern[cursorRow, cursorChannel];
            step.Note = note;
            pattern[cursorRow, cursorChannel] = step;

            cursorField = EffectHighFieldIndex;
        }

        private void InsertNoteOff()
        {
            var step = pattern[cursorRow, cursorChannel];
            step.Note = TrackerNote.NoteOff;
            pattern[cursorRow, cursorChannel] = step;

            cursorField = EffectHighFieldIndex;
        }

        private void ApplyEffectNibble(int nibble)
        {
            nibble &= 0xF;

            var step = pattern[cursorRow, cursorChannel];
            if (!step.Effect.Enabled)
            {
                step.Effect = TrackerEffect.FromByte(0);
            }

            byte current = step.Effect.Value;

            if (cursorField == EffectHighFieldIndex)
            {
                current = (byte)((nibble << 4) | (current & 0x0F));
                step.Effect = TrackerEffect.FromByte(current);
                pattern[cursorRow, cursorChannel] = step;
                cursorField = EffectLowFieldIndex;
            }
            else
            {
                current = (byte)((current & 0xF0) | nibble);
                step.Effect = TrackerEffect.FromByte(current);
                pattern[cursorRow, cursorChannel] = step;
                cursorField = NoteFieldIndex;
                if (autoAdvance)
                {
                    MoveRow(1);
                }
            }
        }

        private void StartPlayback()
        {
            if (!isInitialized)
            {
                return;
            }

            isPlaying = true;
            playbackTimer = 0;
            // Start playback from the top of the pattern instead of the current cursor selection
            nextPlaybackRow = 0;
            TriggerAndAdvance();
        }

        private void StopPlayback()
        {
            if (!isInitialized)
            {
                return;
            }

            isPlaying = false;
            playbackTimer = 0;
            nextPlaybackRow = cursorRow;
            activeRow = cursorRow;
            StopAllVoices();
        }

        private void AdvancePlayback(double deltaTime)
        {
            if (!isPlaying || !isInitialized)
            {
                return;
            }

            playbackTimer += deltaTime;
            double stepDuration = GetStepDurationSeconds();

            while (playbackTimer >= stepDuration)
            {
                playbackTimer -= stepDuration;
                TriggerAndAdvance();
            }
        }

        private void TriggerAndAdvance()
        {
            if (!isInitialized)
            {
                return;
            }

            int rowToPlay = nextPlaybackRow;
            TriggerRow(rowToPlay);
            activeRow = rowToPlay;

            nextPlaybackRow = (nextPlaybackRow + 1) % PatternRows;
        }

        private void TriggerRow(int row)
        {
            if (!isInitialized)
            {
                return;
            }

            for (int channel = 0; channel < PatternChannels; channel++)
            {
                var step = pattern[row, channel];
                var note = step.Note;
                    // skip muted channels
                    if (channelMute[channel])
                    {
                        System.Diagnostics.Debug.WriteLine($"[Row {row} Ch {channel}] Skipping muted channel");
                        continue;
                    }
                if (note.IsNoteOff)
                {
                    System.Diagnostics.Debug.WriteLine($"[Row {row} Ch {channel}] NoteOff: StopChannelVoice");
                    StopChannelVoice(channel);
                    continue;
                }

                if (note.IsEmpty)
                {
                    continue;
                }

                int instrumentIndex = Math.Clamp(note.Instrument, 0, instrumentNames.Length - 1);
                float amplitude = instrumentAmps[instrumentIndex];
                float frequency = note.GetFrequency();
                // Derive an actual musical duration from upcoming empty rows so we don't allocate giant 12s clips every time.
                double durationSeconds = ComputeNoteDuration(row, channel);

                if (step.Effect.Enabled)
                {
                    float semitoneOffset = step.Effect.ToSemitoneOffset();
                    frequency *= MathF.Pow(2f, semitoneOffset / 12f);
                }
                // Determine release multiplier from effect (4Rxx) if present
                double noteRel = 1.0;
                if (step.Effect.IsReleaseCommand())
                {
                    noteRel = MapReleaseParam(step.Effect.Param);
                }
                double finalRel = noteRel * Math.Clamp(channelRelease[channel], 0.1, 5.0);
                System.Diagnostics.Debug.WriteLine($"[Row {row} Ch {channel}] NewNote: StopChannelVoice, then PlayInstrumentNote freq={frequency} dur={durationSeconds} rel={finalRel}");
                StopChannelVoice(channel);
                // store last play params so UI changes can retrigger
                _hasLastPlay[channel] = true;
                _lastInstrument[channel] = instrumentIndex;
                _lastFrequency[channel] = frequency;
                _lastDuration[channel] = durationSeconds;
                _lastAmplitude[channel] = amplitude;
                _lastBaseRelease[channel] = finalRel;
                PlayInstrumentNote(instrumentIndex, frequency, durationSeconds, amplitude, channel, finalRel);
            }
        }

        private double ComputeNoteDuration(int startRow, int channel)
        {
            double stepDuration = GetStepDurationSeconds();
            int steps = 1;
            int row = (startRow + 1) % PatternRows;

            while (row != startRow)
            {
                var nextStep = pattern[row, channel];
                if (nextStep.Note.IsNoteOff)
                {
                    break;
                }

                if (!nextStep.Note.IsEmpty)
                {
                    break;
                }

                steps++;
                // Advance one row at a time (previously jumped 4, shrinking duration incorrectly)
                row = (row + 1) % PatternRows;
            }

            double sustain = Math.Max(steps * stepDuration, MinNoteDurationSeconds);
            return sustain;
        }

        private void UpdateAudio(double deltaTime)
        {
            if (!isInitialized)
            {
                return;
            }
            // Drain one-shot queue by creating SoundEffectInstances via Subsystem.Sound
            while (oneShotQueue.Count > 0)
            {
                var q = oneShotQueue.Dequeue();
                try
                {
                    // If the channel is muted at playback time, skip creating/playing the instance
                    if (channelMute[q.Channel])
                    {
                        System.Diagnostics.Debug.WriteLine($"[Audio] Skipping queued note for muted channel {q.Channel}");
                        continue;
                    }
                    var inst = q.Effect.CreateInstance();
                    inst.Volume = Math.Clamp(q.Volume, 0f, 1f);
                    inst.Play();
                    channelVoices[q.Channel] = inst;
                    activeVoices.Add(inst);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("UpdateAudio enqueue error: " + ex.Message);
                }
            }

            // Clean up finished voices
            for (int i = activeVoices.Count - 1; i >= 0; i--)
            {
                var inst = activeVoices[i];
                if (inst.State != SoundState.Playing)
                {
                    activeVoices.RemoveAt(i);
                    for (int ch = 0; ch < channelVoices.Length; ch++)
                    {
                        if (channelVoices[ch] == inst)
                        {
                            channelVoices[ch] = null;
                            break;
                        }
                    }
                }
            }
        }

        private void PlayInstrumentNote(int instrumentIndex, float frequency, double durationSeconds, float amplitude, int channel, double releaseMultiplier = 1.0)
        {
            double sustainSeconds = Math.Max(durationSeconds, MinNoteDurationSeconds);
            double releaseSeconds = Math.Clamp(sustainSeconds * 0.75 * releaseScale * releaseMultiplier, MinReleaseSeconds, MaxReleaseSeconds);
            double totalDurationSeconds = sustainSeconds + releaseSeconds;

            Console.WriteLine($"PlayInstrumentNote: freq={frequency} durationSeconds={durationSeconds} sustainSeconds={sustainSeconds} releaseSeconds={releaseSeconds} totalDurationSeconds={totalDurationSeconds}");

            int midi = (int)Math.Round(69 + 12 * Math.Log(frequency / 440.0, 2.0));
            double dur = totalDurationSeconds;
            // include the release multiplier in the cache key so different release params
            // (4Rxx, per-channel release, global releaseScale) produce distinct precomputed clips
            string key = $"chippy_synth_{instrumentIndex}_{midi}_{dur:0.####}_{releaseMultiplier:0.###}";
            try
            {
                if (Subsystem.Sound != null)
                {
                    var se = Subsystem.Sound.PrecomputeFromPcm(key, () =>
                    {
                        var waveform = instrumentIndex switch
                        {
                            0 => SimpleSynth.Waveform.Square,
                            1 => SimpleSynth.Waveform.Triangle,
                            2 => SimpleSynth.Waveform.Square,
                            3 => SimpleSynth.Waveform.Noise,
                            _ => SimpleSynth.Waveform.Sine
                        };

                        float synthFreq = instrumentIndex switch
                        {
                            2 => Math.Max(10f, frequency / 2f),
                            3 => 440f,
                            _ => frequency
                        };

                        // Generate PCM for the requested total duration using per-instrument ADSR envelope.
                        float attack = instrumentAttack[instrumentIndex];
                        float decay = instrumentDecay[instrumentIndex];
                        float sustainLevel = instrumentSustain[instrumentIndex];
                        float relMult = instrumentRelease[instrumentIndex];
                        var pcm = SimpleSynth.GenerateWavePcm(synthFreq, (float)dur, waveform, 44100, amplitude, 1, attack, decay, sustainLevel, (float)(releaseSeconds * relMult));
                        return pcm;
                    }, 44100, AudioChannels.Mono);
                    oneShotQueue.Enqueue(new QueuedNote(channel, se, amplitude));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("PlayInstrumentNote synth error: " + ex.Message);
            }
        }

        private void StopChannelVoice(int channel)
        {
            if (channel < 0 || channel >= channelVoices.Length)
            {
                return;
            }

            var handle = channelVoices[channel];
            if (handle == null)
            {
                return;
            }

            handle.Stop();
            activeVoices.Remove(handle);
            channelVoices[channel] = null;
        }

        private void StopAllVoices()
        {
            for (int channel = 0; channel < channelVoices.Length; channel++)
            {
                StopChannelVoice(channel);
            }

            Array.Clear(channelVoices, 0, channelVoices.Length);
            activeVoices.Clear();
            oneShotQueue.Clear();
        }

        private void RemoveQueuedNotesForChannel(int channel)
        {
            if (oneShotQueue.Count == 0) return;
            var newQ = new Queue<QueuedNote>();
            while (oneShotQueue.Count > 0)
            {
                var q = oneShotQueue.Dequeue();
                if (q.Channel != channel) newQ.Enqueue(q);
            }
            while (newQ.Count > 0) oneShotQueue.Enqueue(newQ.Dequeue());
        }

        // Generate instrument-specific clip using ProceduralSynth
        // Remove ProceduralSynth dependency: generation is now done via SimpleSynth precompute per-note
        // Keep a placeholder to satisfy any callers (not used anymore)
        private object GenerateInstrumentClip(int instrumentIndex, float frequency, double totalDurationSeconds, float amplitude, float releaseSeconds)
        {
            throw new NotSupportedException("GenerateInstrumentClip is replaced by SimpleSynth-based precomputed SoundEffects.");
        }

        private static double MapReleaseParam(int paramNibble)
        {
            // Map 0x0..0xF into a release multiplier range 0.25 .. 3.0
            double t = Math.Clamp(paramNibble / 15.0, 0.0, 1.0);
            return 0.25 + t * (3.0 - 0.25);
        }

    private double GetStepDurationSeconds() => 60.0 / Math.Max(1.0, bpm) * BeatsPerStep;

        private void DrawHeader(Canvas surface)
        {
            string mode = isPlaying ? "PLAY" : "EDIT";
            surface.DrawText(10, 10, $"Mode: {mode}  BPM: {bpm:0}  Octave: {currentOctave}", Colors.White);
            surface.DrawText(10, 22, $"Instrument: {instrumentNames[currentInstrument]} (F{currentInstrument + 1})", instrumentColors[currentInstrument]);
            surface.DrawText(10, 34, $"Follow: {(followMode ? "ON" : "OFF")}  Step: 1/4 beat (16th)", Colors.White);
        }

        private void DrawPattern(Canvas surface, int originX = 10)
        {
            int visibleRows = Math.Min(PatternRows, 28);
            int anchorRow = followMode && isPlaying ? activeRow : cursorRow;
            int half = visibleRows / 2;
            int startRow = Math.Clamp(anchorRow - half, 0, Math.Max(0, PatternRows - visibleRows));
            int highlightWidth = PatternChannels * ChannelColumnWidth + 40;
            int rowHeight = 9;
            int originY = 58;
            int headerHeight = rowHeight + 2;

            // Draw border around the pattern area
            int patternHeight = visibleRows * rowHeight + headerHeight;
            surface.DrawOutlinedRect(originX - 8, originY - headerHeight, highlightWidth + 12, patternHeight + 6, Colors.Gray);

            // Draw channel headers
            for (int channel = 0; channel < PatternChannels; channel++)
            {
                int channelX = originX + 28 + channel * ChannelColumnWidth;
                surface.DrawText(channelX, originY - headerHeight + 2, $"CH{channel + 1}", Colors.Yellow);
            }

            // Draw vertical separators between channels
            for (int channel = 1; channel < PatternChannels; channel++)
            {
                int sepX = originX + 28 + channel * ChannelColumnWidth - 8;
                surface.DrawLine(sepX, originY - headerHeight, sepX, originY + visibleRows * rowHeight, Colors.DarkGray);
            }

            // Draw pattern rows
            for (int i = 0; i < visibleRows && startRow + i < PatternRows; i++)
            {
                int rowIndex = startRow + i;
                int y = originY + i * rowHeight;

                bool playingRow = isPlaying && rowIndex == activeRow;
                bool cursorRowActive = rowIndex == cursorRow;

                // Alternating row backgrounds
                if (i % 2 == 0)
                {
                    surface.DrawFilledRect(originX - 6, y - 1, highlightWidth, rowHeight + 2, new Color(30, 30, 40, 255));
                }

                if (playingRow)
                {
                    surface.DrawFilledRect(originX - 6, y - 1, highlightWidth, rowHeight + 2, Colors.DarkGreen);
                }
                else if (cursorRowActive && blinkVisible)
                {
                    surface.DrawFilledRect(originX - 6, y - 1, highlightWidth, rowHeight + 2, Colors.DarkGray);
                }

                // Row number
                surface.DrawText(originX, y, rowIndex.ToString("00"), Colors.Gray);

                for (int channel = 0; channel < PatternChannels; channel++)
                {
                    var step = pattern[rowIndex, channel];
                    var note = step.Note;
                    var effect = step.Effect;

                    string noteText = note.ToDisplay();
                    string effectText = effect.ToDisplay();

                    int channelX = originX + 28 + channel * ChannelColumnWidth;
                    int noteX = channelX;
                    int effectX = channelX + NoteColumnWidth + 6;

                    Color noteColor;
                    if (note.IsEmpty)
                    {
                        noteColor = Colors.White;
                    }
                    else if (note.IsNoteOff)
                    {
                        noteColor = Colors.Gray;
                    }
                    else
                    {
                        noteColor = instrumentColors[Math.Clamp(note.Instrument, 0, instrumentColors.Length - 1)];
                    }
                    Color effectColor = effect.Enabled ? Colors.Green : Colors.DarkGray;

                    surface.DrawText(noteX, y, noteText, noteColor);
                    surface.DrawText(effectX, y, effectText, effectColor);

                    if (cursorRowActive && channel == cursorChannel && blinkVisible)
                    {
                        if (cursorField == NoteFieldIndex)
                        {
                            surface.DrawOutlinedRect(noteX - 2, y - 2, NoteColumnWidth + 4, rowHeight + 2, Colors.Cyan);
                        }
                        else
                        {
                            surface.DrawOutlinedRect(effectX - 2, y - 2, EffectColumnWidth + 4, rowHeight + 2, Colors.Cyan);
                        }
                        // show current channel release for this channel
                        surface.DrawText(effectX + EffectColumnWidth + 8, y, $"R{channelRelease[channel]:0.0}", Colors.Gray);
                    }
                }
            }
        }

        private void DrawFooter(Canvas surface)
        {
            int y = surface.height - 48;
            surface.DrawText(10, y, "Space Play/Stop  Shift+Tab Follow  F5 Toggle Auto-Advance  Tab Note-Off  Arrows Move", Colors.White);
            surface.DrawText(10, y + 12, "Z-M Notes  [ ] Octave  PgUp/PgDn Tempo  F1-F4 Instruments  +/- Release", Colors.White);
            surface.DrawText(10, y + 24, "F9 Save  F10 Load  Backspace/Delete Clear  Ctrl+Del Row", Colors.White);
            surface.DrawText(10, y + 36, "FX: Move to 3Cxx column with Left/Right, type 0-9/A-F for pitch detune", Colors.White);
        }

        private void DrawInstrumentEditor(Canvas surface)
        {
            // clear background
            surface.Clear(new Color(8, 10, 16, 255));
            int margin = 16;
            surface.DrawText(margin, margin, "Instrument Editor", Colors.Yellow);

            int y = margin + 18;
            int rowHeight = 18;
            for (int i = 0; i < instrumentNames.Length; i++)
            {
                int x = margin;
                surface.DrawText(x, y + i * rowHeight, instrumentNames[i], Colors.White);
                x += 120;
                surface.DrawText(x, y + i * rowHeight, $"Attack: {instrumentAttack[i]:0.000}s", Colors.Gray);
                x += 150;
                surface.DrawText(x, y + i * rowHeight, $"Decay: {instrumentDecay[i]:0.000}s", Colors.Gray);
                x += 120;
                surface.DrawText(x, y + i * rowHeight, $"Sustain: {instrumentSustain[i]:0.00}", Colors.Gray);
            }

            // footer hints
            surface.DrawText(margin, surface.height - 28, "F2 / Ctrl+Tab: Close editor    Click A/R buttons to adjust Attack/Sustain    Changes clear precomputed cache", Colors.White);
        }

        private void SetInstrument(int index)
        {
            currentInstrument = Math.Clamp(index, 0, instrumentNames.Length - 1);
        }
    }
}

// Cross-platform file dialog helper. On Windows uses comdlg32, on macOS uses AppleScript via osascript,
// on Linux tries zenity/kdialog and otherwise falls back to a console chooser.
internal static class CrossPlatformFileDialog
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class OpenFileName
    {
        public int lStructSize = Marshal.SizeOf(typeof(OpenFileName));
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter = null!;
        public string lpstrCustomFilter = null!;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public string lpstrFile = null!;
        public int nMaxFile = 260;
        public string lpstrFileTitle = null!;
        public int nMaxFileTitle = 260;
        public string lpstrInitialDir = null!;
        public string lpstrTitle = null!;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt = null!;
        public IntPtr lCustData = IntPtr.Zero;
        public IntPtr lpfnHook = IntPtr.Zero;
        public string lpTemplateName = null!;
        public IntPtr pvReserved = IntPtr.Zero;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetOpenFileName([In, Out] OpenFileName ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetSaveFileName([In, Out] OpenFileName ofn);

    public static string? ShowOpenFileDialog(string filter)
    {
        

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var ofn = new OpenFileName();
            ofn.lpstrFilter = filter.Replace('|', '\0') + "\0";
            ofn.lpstrFile = new string('\0', 260);
            ofn.nMaxFile = ofn.lpstrFile.Length;
            ofn.Flags = 0x00000008 | 0x00080000; // OFN_EXPLORER | OFN_PATHMUSTEXIST

            bool ok = GetOpenFileName(ofn);
            if (ok)
            {
                string result = ofn.lpstrFile;
                int idx = result.IndexOf('\0');
                if (idx >= 0) result = result.Substring(0, idx);
                return result;
            }
            return null;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                string script = "POSIX path of (choose file with prompt \"Select file\")";
                var outp = RunProcessCaptureOutput("osascript", $"-e \"{script}\"");
                return string.IsNullOrWhiteSpace(outp) ? null : outp.Trim();
            }
            catch { }
        }

        // Try zenity (common on Linux/Gtk) or kdialog (KDE)
        try
        {
            string zenity = "zenity";
            var outp = RunProcessCaptureOutput(zenity, "--file-selection");
            if (!string.IsNullOrWhiteSpace(outp)) return outp.Trim();
        }
        catch { }

        try
        {
            string kdialog = "kdialog";
            var outp = RunProcessCaptureOutput(kdialog, "--getopenfilename");
            if (!string.IsNullOrWhiteSpace(outp)) return outp.Trim();
        }
        catch { }

        // Fallback: simple console chooser of .chip files in current directory
        var candidates = System.IO.Directory.GetFiles(Environment.CurrentDirectory, "*.chip");
        if (candidates.Length == 0) return null;
        Console.WriteLine("Select file:");
        for (int i = 0; i < candidates.Length; i++) Console.WriteLine($"[{i}] {candidates[i]}");
        Console.Write("Enter number: ");
        var line = Console.ReadLine();
        if (int.TryParse(line, out int idxSel) && idxSel >= 0 && idxSel < candidates.Length) return candidates[idxSel];
        return null;
    }

    public static string? ShowSaveFileDialog(string filter, string defaultName, string defaultExt)
    {
        // Try NativeFileDialogSharp first (reflection, optional dependency)
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name?.IndexOf("NativeFileDialogSharp", StringComparison.OrdinalIgnoreCase) >= 0);
            if (asm == null)
            {
                try { asm = Assembly.Load(new AssemblyName("NativeFileDialogSharp")); } catch { }
            }

            if (asm != null)
            {
                foreach (var t in asm.GetTypes())
                {
                    var mi = t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name.IndexOf("Save", StringComparison.OrdinalIgnoreCase) >= 0 && m.ReturnType == typeof(string) && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(string) && m.GetParameters()[1].ParameterType == typeof(string));
                    if (mi != null)
                    {
                        object? inst = mi.IsStatic ? null : Activator.CreateInstance(t);
                        var res = mi.Invoke(inst, new object[] { defaultName, filter }) as string;
                        if (!string.IsNullOrEmpty(res)) return res;
                    }

                    // bool Save(..., out string)
                    mi = t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name.IndexOf("Save", StringComparison.OrdinalIgnoreCase) >= 0 && m.ReturnType == typeof(bool));
                    if (mi != null)
                    {
                        var pars = mi.GetParameters();
                        object? inst = mi.IsStatic ? null : Activator.CreateInstance(t);
                        object?[] args = new object?[pars.Length];
                        for (int i = 0; i < pars.Length; i++) args[i] = pars[i].HasDefaultValue ? pars[i].DefaultValue : null;
                        for (int i = 0; i < pars.Length; i++)
                        {
                            if (pars[i].ParameterType == typeof(string).MakeByRefType()) args[i] = null;
                            else if (pars[i].ParameterType == typeof(string)) args[i] = defaultName;
                        }
                        var ok = (bool)mi.Invoke(inst, args)!;
                        for (int i = 0; i < pars.Length; i++)
                        {
                            if (pars[i].ParameterType == typeof(string).MakeByRefType())
                            {
                                var val = args[i] as string;
                                if (!string.IsNullOrEmpty(val)) return val;
                            }
                        }
                    }
                }
            }
        }
        catch { }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var ofn = new OpenFileName();
            ofn.lpstrFilter = filter.Replace('|', '\0') + "\0";
            ofn.lpstrFile = (defaultName ?? string.Empty) + new string('\0', 260);
            ofn.nMaxFile = ofn.lpstrFile.Length;
            ofn.lpstrDefExt = defaultExt;
            ofn.Flags = 0x00000002 | 0x00080000; // OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST

            bool ok = GetSaveFileName(ofn);
            if (ok)
            {
                string result = ofn.lpstrFile;
                int idx = result.IndexOf('\0');
                if (idx >= 0) result = result.Substring(0, idx);
                return result;
            }
            return null;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                string script = $"POSIX path of (choose file name with prompt \"Save file\" default name \"{defaultName}\")";
                var outp = RunProcessCaptureOutput("osascript", $"-e \"{script}\"");
                return string.IsNullOrWhiteSpace(outp) ? null : outp.Trim();
            }
            catch { }
        }

        try
        {
            string zenity = "zenity";
            var args = $"--file-selection --save --confirm-overwrite --filename=\"{defaultName}\"";
            var outp = RunProcessCaptureOutput(zenity, args);
            if (!string.IsNullOrWhiteSpace(outp)) return outp.Trim();
        }
        catch { }

        try
        {
            string kdialog = "kdialog";
            var args = $"--getsavefilename \"{defaultName}\"";
            var outp = RunProcessCaptureOutput(kdialog, args);
            if (!string.IsNullOrWhiteSpace(outp)) return outp.Trim();
        }
        catch { }

        // Fallback: prompt in console for filename
        Console.WriteLine($"Enter filename to save (default: {defaultName}):");
        var line = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(line)) line = defaultName;
        return System.IO.Path.Combine(Environment.CurrentDirectory, line!);
    }

    private static string? RunProcessCaptureOutput(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p == null) return null;
        var outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10000);
        return outp;
    }
}
