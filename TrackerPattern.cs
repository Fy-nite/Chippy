using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Input;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace Chippy;

public readonly struct TrackerNote
{
    private const int EmptyMidi = -1;
    private const int NoteOffMidi = -2;

    public static TrackerNote Empty { get; } = new(EmptyMidi, 0);
    public static TrackerNote NoteOff => new(NoteOffMidi, 0);

    public int Midi { get; }
    public int Instrument { get; }

    public bool IsEmpty => Midi == EmptyMidi;
    public bool IsNoteOff => Midi == NoteOffMidi;

    public TrackerNote(int midi, int instrument)
    {
        Midi = midi;
        Instrument = instrument;
    }

// Serialization helpers will be implemented after the TrackerPattern type so instance members are available.

    public float GetFrequency(float basePitch = 440f)
    {
        if (IsEmpty || IsNoteOff)
        {
            return 0f;
        }

        return basePitch * MathF.Pow(2f, (Midi - 69) / 12f);
    }

    public string ToDisplay()
    {
        if (IsEmpty)
        {
            return "---";
        }

        if (IsNoteOff)
        {
            return "===";
        }

        int noteIndex = ((Midi % 12) + 12) % 12;
        int octave = (Midi / 12) - 1;
        return $"{NoteNames[noteIndex]}{octave}";
    }

    public TrackerNote WithInstrument(int instrument) => new(Midi, instrument);

    public static int ClampInstrument(int instrument, int maxInstrument)
    {
        if (instrument < 0)
        {
            return 0;
        }

        if (instrument >= maxInstrument)
        {
            return maxInstrument - 1;
        }

        return instrument;
    }

    public static TrackerNote FromMidi(int midi, int instrument) => new(midi, instrument);

    public static bool TryParseName(string name, out int midi)
    {
        midi = 0;
        if (string.IsNullOrWhiteSpace(name) || name.Length < 2)
        {
            return false;
        }

        name = name.Trim().ToUpperInvariant();
        string pitchPart = name[..^1];
        if (!int.TryParse(name[^1].ToString(), out int octave))
        {
            return false;
        }

        int noteIndex = Array.IndexOf(NoteNames, pitchPart);
        if (noteIndex < 0)
        {
            return false;
        }

        midi = (octave + 1) * 12 + noteIndex;
        return true;
    }

    private static readonly string[] NoteNames =
    {
        "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"
    };
}

public struct TrackerEffect
{
    public bool Enabled { get; set; }
    public byte Value { get; set; }

    public static TrackerEffect Disabled => new() { Enabled = false, Value = 0 };

    public static TrackerEffect FromByte(byte value) => new() { Enabled = true, Value = value };

    public int Command => Value >> 4; // high nibble
    public int Param => Value & 0x0F; // low nibble

    public string ToDisplay()
    {
        if (!Enabled) return "....";
        switch (Command)
        {
            case 0x3: // pitch detune command
                return $"3C{Value:X2}";
            case 0x4: // release command
                return $"4R{Value & 0x0F:X2}";
            default:
                return $"{Value:X2}";
        }
    }

    public float ToSemitoneOffset() => Enabled && Command == 0x3 ? (Value - 0x80) / 16f : 0f;

    public bool IsReleaseCommand() => Enabled && Command == 0x4;
    public double ReleaseParamNormalized() => (Param) / 15.0; // 0..1
}

public struct TrackerStep
{
    public TrackerNote Note;
    public TrackerEffect Effect;

    public static TrackerStep Empty => new()
    {
        Note = TrackerNote.Empty,
        Effect = TrackerEffect.Disabled
    };
}

public sealed partial class TrackerPattern
{
    public int Rows { get; }
    public int Channels { get; }

    private readonly TrackerStep[,] _grid;

    public TrackerPattern(int rows, int channels)
    {
        Rows = rows;
        Channels = channels;
        _grid = new TrackerStep[Rows, Channels];

        for (int row = 0; row < Rows; row++)
        {
            for (int channel = 0; channel < Channels; channel++)
            {
                _grid[row, channel] = TrackerStep.Empty;
            }
        }
    }

    public TrackerStep this[int row, int channel]
    {
        get => _grid[row, channel];
        set => _grid[row, channel] = value;
    }

    public void SetNote(int row, int channel, TrackerNote note)
    {
        var step = _grid[row, channel];
        step.Note = note;
        _grid[row, channel] = step;
    }

    public void SetEffect(int row, int channel, TrackerEffect effect)
    {
        var step = _grid[row, channel];
        step.Effect = effect;
        _grid[row, channel] = step;
    }

    public void ClearRow(int row)
    {
        for (int channel = 0; channel < Channels; channel++)
        {
            _grid[row, channel] = TrackerStep.Empty;
        }
    }

    public void ClearCell(int row, int channel)
    {
        _grid[row, channel] = TrackerStep.Empty;
    }

    public void ClearEffect(int row, int channel)
    {
        var step = _grid[row, channel];
        step.Effect = TrackerEffect.Disabled;
        _grid[row, channel] = step;
    }

    public void FillRow(int row, TrackerNote note)
    {
        for (int channel = 0; channel < Channels; channel++)
        {
            var step = _grid[row, channel];
            step.Note = note;
            step.Effect = TrackerEffect.Disabled;
            _grid[row, channel] = step;
        }
    }

    public IEnumerable<(int row, TrackerStep[] steps)> EnumerateRows()
    {
        for (int row = 0; row < Rows; row++)
        {
            var copy = new TrackerStep[Channels];
            for (int channel = 0; channel < Channels; channel++)
            {
                copy[channel] = _grid[row, channel];
            }

            yield return (row, copy);
        }
    }
}

// Serialization helpers for TrackerPattern to save/load JSON
public partial class TrackerPattern
{
    private class TrackerStepDto
    {
        public int Midi { get; set; }
        public int Instrument { get; set; }
        public bool EffectEnabled { get; set; }
        public byte EffectValue { get; set; }
    }

    private class TrackerPatternDto
    {
        public int Rows { get; set; }
        public int Channels { get; set; }
        public TrackerStepDto[][] Grid { get; set; } = Array.Empty<TrackerStepDto[]>();
    }

    public class PatternMetadata
    {
        public string? Title { get; set; }
        public double? BPM { get; set; }
        public int? RowsPerBeat { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime ModifiedUtc { get; set; }
    }

    private class PatternFileDto
    {
        public PatternMetadata? Meta { get; set; }
        public TrackerPatternDto Pattern { get; set; } = new TrackerPatternDto();
    }

    public string ToJson()
    {
        var dto = new TrackerPatternDto
        {
            Rows = this.Rows,
            Channels = this.Channels,
            Grid = new TrackerStepDto[this.Rows][]
        };

        for (int r = 0; r < this.Rows; r++)
        {
            dto.Grid[r] = new TrackerStepDto[this.Channels];
            for (int c = 0; c < this.Channels; c++)
            {
                var s = this._grid[r, c];
                dto.Grid[r][c] = new TrackerStepDto
                {
                    Midi = s.Note.Midi,
                    Instrument = s.Note.Instrument,
                    EffectEnabled = s.Effect.Enabled,
                    EffectValue = s.Effect.Value
                };
            }
        }

        var opts = new JsonSerializerOptions { WriteIndented = true };
        return JsonSerializer.Serialize(dto, opts);
    }

    public void SaveToFile(string path)
    {
        SaveToFile(path, null);
    }

    public void SaveToFile(string path, PatternMetadata? metadata)
    {
        var dto = new PatternFileDto
        {
            Meta = metadata ?? new PatternMetadata { Title = "", BPM = null, RowsPerBeat = null, CreatedUtc = DateTime.UtcNow, ModifiedUtc = DateTime.UtcNow },
            Pattern = new TrackerPatternDto { Rows = this.Rows, Channels = this.Channels, Grid = new TrackerStepDto[this.Rows][] }
        };

        for (int r = 0; r < this.Rows; r++)
        {
            dto.Pattern.Grid[r] = new TrackerStepDto[this.Channels];
            for (int c = 0; c < this.Channels; c++)
            {
                var s = this._grid[r, c];
                dto.Pattern.Grid[r][c] = new TrackerStepDto
                {
                    Midi = s.Note.Midi,
                    Instrument = s.Note.Instrument,
                    EffectEnabled = s.Effect.Enabled,
                    EffectValue = s.Effect.Value
                };
            }
        }

        var opts = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(dto, opts);
        File.WriteAllText(path, json);
    }

    public static TrackerPattern FromJson(string json)
    {
        // Deserialize just the pattern DTO (legacy compatibility)
        var dto = JsonSerializer.Deserialize<TrackerPatternDto>(json);
        if (dto == null) throw new InvalidDataException("Invalid pattern JSON");

        var p = new TrackerPattern(dto.Rows, dto.Channels);
        for (int r = 0; r < dto.Rows; r++)
        {
            for (int c = 0; c < dto.Channels; c++)
            {
                var s = dto.Grid[r][c];
                var note = s.Midi == -1 ? TrackerNote.Empty : TrackerNote.FromMidi(s.Midi, s.Instrument);
                var step = p[r, c];
                step.Note = note;
                step.Effect = s.EffectEnabled ? TrackerEffect.FromByte(s.EffectValue) : TrackerEffect.Disabled;
                p[r, c] = step;
            }
        }

        return p;
    }

    public static TrackerPattern LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        // Try to detect file format: if it contains Meta, deserialize file wrapper
        try
        {
            var wrapper = JsonSerializer.Deserialize<PatternFileDto>(json);
            if (wrapper != null && wrapper.Pattern != null && wrapper.Pattern.Grid.Length > 0)
            {
                // build pattern from wrapper
                var dto = wrapper.Pattern;
                var p = new TrackerPattern(dto.Rows, dto.Channels);
                for (int r = 0; r < dto.Rows; r++)
                {
                    for (int c = 0; c < dto.Channels; c++)
                    {
                        var s = dto.Grid[r][c];
                        var note = s.Midi == -1 ? TrackerNote.Empty : TrackerNote.FromMidi(s.Midi, s.Instrument);
                        var step = p[r, c];
                        step.Note = note;
                        step.Effect = s.EffectEnabled ? TrackerEffect.FromByte(s.EffectValue) : TrackerEffect.Disabled;
                        p[r, c] = step;
                    }
                }
                return p;
            }
        }
        catch { }

        return FromJson(json);
    }

    public static (TrackerPattern pattern, PatternMetadata? meta) LoadFromFileWithMetadata(string path)
    {
        var json = File.ReadAllText(path);
        var wrapper = JsonSerializer.Deserialize<PatternFileDto>(json);
        if (wrapper == null) throw new InvalidDataException("Invalid pattern file");
        var dto = wrapper.Pattern;
        var p = new TrackerPattern(dto.Rows, dto.Channels);
        for (int r = 0; r < dto.Rows; r++)
        {
            for (int c = 0; c < dto.Channels; c++)
            {
                var s = dto.Grid[r][c];
                var note = s.Midi == -1 ? TrackerNote.Empty : TrackerNote.FromMidi(s.Midi, s.Instrument);
                var step = p[r, c];
                step.Note = note;
                step.Effect = s.EffectEnabled ? TrackerEffect.FromByte(s.EffectValue) : TrackerEffect.Disabled;
                p[r, c] = step;
            }
        }
        return (p, wrapper.Meta);
    }
}

public static class TrackerKeyboardMap
{
        private static readonly Dictionary<Keys, int> KeyOffsets = new()
    {
        { Keys.Z, 0 },
        { Keys.S, 1 },
        { Keys.X, 2 },
        { Keys.D, 3 },
        { Keys.C, 4 },
        { Keys.V, 5 },
        { Keys.G, 6 },
        { Keys.B, 7 },
        { Keys.H, 8 },
        { Keys.N, 9 },
        { Keys.J, 10 },
        { Keys.M, 11 },
            { Keys.OemComma, 12 },
            { Keys.L, 13 },
            { Keys.OemPeriod, 14 },
            { Keys.OemSemicolon, 15 },
            { Keys.OemQuestion, 16 }
    };

    public static bool TryGetSemitoneOffset(Keys key, out int offset) => KeyOffsets.TryGetValue(key, out offset);

    public static IEnumerable<Keys> AllMappedKeys => KeyOffsets.Keys;
}
