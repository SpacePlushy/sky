using System.Globalization;

namespace Sky.Orbital.Tests.Propagation;

/// <summary>One reference state from tcppver.out: minutes since epoch, TEME km and km/s.</summary>
internal readonly record struct ReferenceState(double Minutes, Vec3 Position, Vec3 Velocity);

/// <summary>One run from SGP4-VER.TLE with the reference states the C++ driver printed for it.</summary>
internal sealed record VerificationRun(
    string Key,
    long CatalogNumber,
    string Line1,
    string Line2,
    double StartMinutes,
    double StopMinutes,
    double StepMinutes,
    IReadOnlyList<ReferenceState> States);

/// <summary>
/// Loads Vallado's verification set from Data/Vallado. See the README there for provenance.
/// </summary>
internal static class ValladoVerificationData
{
    private static readonly Lazy<IReadOnlyList<VerificationRun>> LazyRuns = new(Load);

    public static IReadOnlyList<VerificationRun> Runs => LazyRuns.Value;

    public static VerificationRun Get(string key) => Runs.Single(r => r.Key == key);

    private static List<VerificationRun> Load()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Data", "Vallado");
        var tleLines = File.ReadAllLines(Path.Combine(directory, "SGP4-VER.TLE"))
            .Where(line => line.Length > 0 && line[0] != '#')
            .ToList();
        var blocks = ReadOutputBlocks(Path.Combine(directory, "tcppver.out"));

        if (tleLines.Count != blocks.Count * 2)
        {
            throw new InvalidDataException($"{tleLines.Count} TLE lines but {blocks.Count} output blocks.");
        }

        var runs = new List<VerificationRun>();
        for (int i = 0; i < blocks.Count; i++)
        {
            string line1 = tleLines[2 * i];
            string line2 = tleLines[(2 * i) + 1];
            long catalogNumber = long.Parse(line1.AsSpan(2, 5), CultureInfo.InvariantCulture);
            if (catalogNumber != blocks[i].CatalogNumber)
            {
                throw new InvalidDataException($"Run {i + 1}: TLE is {catalogNumber}, output is {blocks[i].CatalogNumber}.");
            }

            // Line 2 carries start, stop, and step in minutes after the standard 69 columns.
            double[] times = line2[69..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => double.Parse(t, CultureInfo.InvariantCulture))
                .ToArray();

            runs.Add(new VerificationRun(
                Key: $"{i + 1:00} {catalogNumber:00000}",
                CatalogNumber: catalogNumber,
                Line1: line1,
                Line2: line2[..69],
                StartMinutes: times[0],
                StopMinutes: times[1],
                StepMinutes: times[2],
                States: blocks[i].States));
        }

        return runs;
    }

    private static List<(long CatalogNumber, List<ReferenceState> States)> ReadOutputBlocks(string path)
    {
        var blocks = new List<(long, List<ReferenceState>)>();
        foreach (string line in File.ReadLines(path))
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0)
            {
                continue;
            }

            if (fields is [var satnum, "xx"])
            {
                blocks.Add((long.Parse(satnum, CultureInfo.InvariantCulture), []));
                continue;
            }

            double[] v = fields.Take(7).Select(f => double.Parse(f, CultureInfo.InvariantCulture)).ToArray();
            blocks[^1].Item2.Add(new ReferenceState(v[0], new Vec3(v[1], v[2], v[3]), new Vec3(v[4], v[5], v[6])));
        }

        return blocks;
    }
}
