namespace RimRunner;

internal static class RimRunnerFilmAudioRows
{
    internal const string Seek = "seek";
    internal const string Volume = "volume";
    internal const string Mute = "mute";
    internal const string Pitch = "pitch";
    internal const string Pause = "pause";
    internal const string Loop = "loop";
    internal const string Pan = "pan";
    internal const string Mix = "mix";
    internal const string Spatial = "spatial";
    internal const string Position = "position";
    internal const string Schedule = "schedule";
    internal const string LowPass = "lowpass";
    internal const string HighPass = "highpass";
    internal const string Echo = "echo";
    internal const string Reverb = "reverb";
    internal const string Chorus = "chorus";
    internal const string Distortion = "distortion";
    internal const string Stop = "stop";

    internal static bool Unsupported(string name) => name is Chorus or Distortion;

    internal static bool Route(string name) => name is Pan or Mix or Spatial;
}
