using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sky.CelesTrak;

/// <summary>
/// Disk cache for CelesTrak GP data that keeps Sky within CelesTrak's usage policy
/// (https://celestrak.org/usage-policy.php).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Data is downloaded on demand once it is more than 6 hours old. CelesTrak updates every 2 hours.</item>
/// <item>No group is requested within 2 hours of its last request, even when a refresh is forced.</item>
/// <item>
/// Any HTTP answer other than a 200 with valid element sets blocks the group until a person
/// calls <see cref="ClearBlock"/>. CelesTrak requires automated clients to stop on any non-200
/// response and report it; 50 errors in 2 hours firewalls the IP address.
/// </item>
/// <item>When CelesTrak cannot be reached at all, retries back off from 2 hours, doubling to 24.</item>
/// <item>A response replaces cached data only after it validates, and files are replaced by rename.</item>
/// </list>
/// State lives on disk, so the rules hold across restarts. One instance serializes its own
/// requests; separate processes sharing a directory are not coordinated.
/// </remarks>
public sealed partial class GpCache(string directory, CelesTrakClient client, TimeProvider time) : IDisposable
{
    private static readonly TimeSpan RefreshAge = TimeSpan.FromHours(6);
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromHours(2);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromHours(24);
    private static readonly TimeSpan StaleEpochAge = TimeSpan.FromDays(3);
    private static readonly JsonSerializerOptions StateJson = new() { WriteIndented = true };

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Gets the element sets for a group, downloading them only when the policy allows.</summary>
    /// <param name="group">A CelesTrak group name, such as "stations" or "visual".</param>
    /// <param name="cancellationToken">Cancels the wait or the download.</param>
    /// <param name="forceRefresh">Download even if the cached data is under 6 hours old, if the 2-hour rule allows.</param>
    public async Task<GpCacheResult> GetGroupAsync(string group, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        ValidateGroup(group);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GetGroupCoreAsync(group, forceRefresh, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Clears a block left by a CelesTrak error, after a person has looked at it.</summary>
    public void ClearBlock(string group)
    {
        ValidateGroup(group);
        State state = LoadState(group, []);
        if (state.Blocked is not null)
        {
            SaveState(group, state with { Blocked = null });
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private async Task<GpCacheResult> GetGroupCoreAsync(string group, bool forceRefresh, CancellationToken cancellationToken)
    {
        DateTimeOffset now = time.GetUtcNow();
        var warnings = new List<string>();
        State state = LoadState(group, warnings);
        IReadOnlyList<GpRecord>? cached = LoadCachedRecords(group, warnings);

        bool due = cached is null || state.DownloadedUtc is null || now - state.DownloadedUtc >= RefreshAge;
        if (due || forceRefresh)
        {
            if (state.Blocked is { } blocked)
            {
                warnings.Add(BlockedMessage(group, blocked));
            }
            else if (state.LastAttemptUtc is { } last && now < last + WaitAfter(state))
            {
                warnings.Add(state.NetworkFailures > 0
                    ? $"CelesTrak could not be reached for GROUP={group} on the last {state.NetworkFailures} attempts; the next attempt is allowed after {Format(last + WaitAfter(state))}."
                    : $"CelesTrak updates GP data every 2 hours and allows one download per update. GROUP={group} was last requested at {Format(last)}; the next request is allowed after {Format(last + MinimumInterval)}.");
            }
            else
            {
                (state, IReadOnlyList<GpRecord>? downloaded) = await DownloadAsync(group, state, now, warnings, cancellationToken).ConfigureAwait(false);
                SaveState(group, state);
                if (downloaded is not null)
                {
                    return Result(group, downloaded, GpDataSource.Downloaded, now, now, warnings);
                }
            }
        }

        return cached is null
            ? new GpCacheResult { Group = group, Records = [], Source = GpDataSource.None, Warnings = warnings }
            : Result(group, cached, GpDataSource.Cache, state.DownloadedUtc, now, warnings);
    }

    private async Task<(State State, IReadOnlyList<GpRecord>? Records)> DownloadAsync(
        string group, State state, DateTimeOffset now, List<string> warnings, CancellationToken cancellationToken)
    {
        // Record the attempt on disk before sending anything. If this run is interrupted while the
        // request is in flight (Ctrl+C, a crash, a debugger stop), CelesTrak may still have served
        // it, so the 2-hour rule must count it.
        state = state with { LastAttemptUtc = now };
        SaveState(group, state);
        FetchOutcome outcome = await client.FetchGroupAsync(group, cancellationToken).ConfigureAwait(false);

        switch (outcome)
        {
            case FetchOutcome.Answered { Status: 200 } answered:
                IReadOnlyList<GpRecord> records;
                try
                {
                    records = OmmParser.Parse(answered.Body);
                }
                catch (FormatException ex)
                {
                    return (Block(state, 200, answered.Body, now, group, warnings, ex.Message), null);
                }

                if (records.Count == 0)
                {
                    return (Block(state, 200, answered.Body, now, group, warnings, "The response held no element sets."), null);
                }

                WriteAtomically(DataPath(group), answered.Body);
                return (state with { DownloadedUtc = now, NetworkFailures = 0, Blocked = null }, records);

            case FetchOutcome.Answered answered:
                return (Block(state, answered.Status, answered.Body, now, group, warnings, null), null);

            case FetchOutcome.Unreachable unreachable:
                state = state with { NetworkFailures = state.NetworkFailures + 1 };
                warnings.Add($"Could not reach CelesTrak for GROUP={group}: {unreachable.Reason} The next attempt is allowed after {Format(now + WaitAfter(state))}.");
                return (state, null);

            default:
                throw new InvalidOperationException($"Unknown fetch outcome {outcome}.");
        }
    }

    private static State Block(State state, int status, string body, DateTimeOffset now, string group, List<string> warnings, string? problem)
    {
        var blocked = new BlockRecord(status, Excerpt(body), problem, now);
        warnings.Add(BlockedMessage(group, blocked));
        return state with { Blocked = blocked };
    }

    /// <summary>How long to wait after the last attempt: 2 hours, or the network-failure backoff.</summary>
    private static TimeSpan WaitAfter(State state)
    {
        if (state.NetworkFailures <= 0)
        {
            return MinimumInterval;
        }

        // 2, 4, 8, 16, then 24 hours. The exponent is capped so the multiplication cannot overflow.
        TimeSpan backoff = MinimumInterval * Math.Pow(2, Math.Min(state.NetworkFailures - 1, 8));
        return backoff < MaximumBackoff ? backoff : MaximumBackoff;
    }

    private static GpCacheResult Result(
        string group, IReadOnlyList<GpRecord> records, GpDataSource source, DateTimeOffset? downloaded, DateTimeOffset now, List<string> warnings)
    {
        DateTimeOffset newestEpoch = records.Max(r => r.Elements.Epoch);
        if (now - newestEpoch > StaleEpochAge)
        {
            warnings.Add($"The newest element set for GROUP={group} is {(now - newestEpoch).TotalDays.ToString("F1", CultureInfo.InvariantCulture)} days old (epoch {Format(newestEpoch)}). Predictions degrade by kilometers per day of element age.");
        }

        return new GpCacheResult { Group = group, Records = records, Source = source, DownloadedUtc = downloaded, Warnings = warnings };
    }

    private IReadOnlyList<GpRecord>? LoadCachedRecords(string group, List<string> warnings)
    {
        string path = DataPath(group);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return OmmParser.Parse(File.ReadAllText(path));
        }
        catch (FormatException ex)
        {
            warnings.Add($"The cached data for GROUP={group} could not be read and was ignored: {ex.Message}");
            return null;
        }
    }

    private State LoadState(string group, List<string> warnings)
    {
        string path = StatePath(group);
        if (!File.Exists(path))
        {
            return new State();
        }

        try
        {
            return JsonSerializer.Deserialize<State>(File.ReadAllText(path), StateJson) ?? new State();
        }
        catch (JsonException ex)
        {
            // Fail safe on both rules. The unreadable file might have recorded a block, which only
            // a person may clear, so the group is treated as blocked. And the file is rewritten on
            // every attempt, so its modification time is no earlier than the last request, which
            // keeps the 2-hour rule once the block is cleared.
            DateTimeOffset written = new(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            string problem = $"The request state for GROUP={group} could not be read ({ex.Message}), so any earlier CelesTrak error it recorded is unknown.";
            warnings.Add(problem);
            return new State
            {
                LastAttemptUtc = written,
                Blocked = new BlockRecord(0, string.Empty, problem, written),
            };
        }
    }

    private void SaveState(string group, State state) =>
        WriteAtomically(StatePath(group), JsonSerializer.Serialize(state, StateJson));

    private void WriteAtomically(string path, string contents)
    {
        System.IO.Directory.CreateDirectory(directory);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, contents);
        File.Move(temporary, path, overwrite: true);
    }

    private string DataPath(string group) => Path.Combine(directory, $"{group}.json");

    private string StatePath(string group) => Path.Combine(directory, $"{group}.state.json");

    private static void ValidateGroup(string group)
    {
        if (!GroupName().IsMatch(group))
        {
            throw new ArgumentException($"\"{group}\" is not a CelesTrak group name: use lowercase letters, digits, and hyphens.", nameof(group));
        }
    }

    private static string BlockedMessage(string group, BlockRecord blocked)
    {
        string problem = blocked.Problem is null ? string.Empty : $" {blocked.Problem}";
        string cause = blocked.Status == 0
            ? $"GROUP={group} is blocked:{problem}"
            : $"CelesTrak answered HTTP {blocked.Status} for GROUP={group} at {Format(blocked.AtUtc)}: \"{blocked.Body}\".{problem}";
        return cause + " Sky has stopped requesting this group, as CelesTrak's usage policy requires, and is using cached data if it has any. " +
            "Clear the block after checking CelesTrak's status to try again.";
    }

    private static string Excerpt(string text)
    {
        string oneLine = text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= 500 ? oneLine : string.Concat(oneLine.AsSpan(0, 500), "...");
    }

    private static string Format(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex GroupName();

    /// <summary>Per-group request history, persisted next to the data.</summary>
    private sealed record State
    {
        public DateTimeOffset? LastAttemptUtc { get; init; }

        public DateTimeOffset? DownloadedUtc { get; init; }

        public int NetworkFailures { get; init; }

        public BlockRecord? Blocked { get; init; }
    }

    /// <summary>Why a group is blocked: a CelesTrak answer, or status 0 for unreadable local state.</summary>
    private sealed record BlockRecord(int Status, string Body, string? Problem, DateTimeOffset AtUtc);
}
