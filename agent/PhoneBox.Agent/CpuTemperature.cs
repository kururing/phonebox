namespace PhoneBox.Agent;

internal static class CpuTemperature
{
    internal static double? Select(IEnumerable<(string Name, float? Value)> sensors)
    {
        var readings = sensors.Where(s => s.Value is { } v && float.IsFinite(v) && v is >= -40 and <= 125).ToArray();
        string[] preferred = ["CPU Package", "Core (Tctl/Tdie)", "Tctl/Tdie", "Core (Tdie)", "Tdie", "Tctl", "Core Max"];
        foreach (var name in preferred)
            if (readings.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { } value)
                return value;
        var cores = readings.Where(s => (s.Name.StartsWith("CPU Core #") || s.Name.StartsWith("Core #"))
            && !s.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)).Select(s => (double)s.Value!.Value).ToArray();
        return cores.Length == 0 ? null : cores.Max();
    }
}
