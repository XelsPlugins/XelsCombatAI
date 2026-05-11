using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using BossMod;

namespace FightReview;

internal static partial class BmrReplayReader
{
    private const int MaxBmrEvents = 20000;
    private const int MaxBmrParticipantPositionSamples = 16;
    private static bool initializedBossMod;
    private static bool registeredAssemblyResolver;

    public static BmrReplayData Read(string path)
    {
        EnsureBossModInitialized();

        var progress = 0f;
        var replay = ReplayParserLog.Parse(path, ref progress, CancellationToken.None);
        if (replay.Ops.Count == 0)
        {
            throw new InvalidDataException($"BMR replay '{path}' did not parse or contains no operations.");
        }

        return new BmrReplayData(Path.GetFullPath(path), replay, BuildSummary(path, replay));
    }

    private static void EnsureBossModInitialized()
    {
        if (initializedBossMod)
        {
            return;
        }

        Service.LogHandlerDebug ??= message => Console.Error.WriteLine(message);
        Service.LogHandlerVerbose ??= message => Console.Error.WriteLine(message);
        RegisterAssemblyResolver();
        Service.LuminaGameData ??= new Lumina.GameData(FindGameDataPath());
        Service.Config.Initialize();
        initializedBossMod = true;
    }

    private static void RegisterAssemblyResolver()
    {
        if (registeredAssemblyResolver)
        {
            return;
        }

        AssemblyLoadContext.Default.Resolving += ResolveFromBaseDirectory;
        registeredAssemblyResolver = true;
    }

    private static Assembly? ResolveFromBaseDirectory(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName.Name))
        {
            return null;
        }

        var path = Path.Combine(AppContext.BaseDirectory, $"{assemblyName.Name}.dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }

    private static string FindGameDataPath()
    {
        foreach (var path in CandidateGameDataPaths())
        {
            if (TryNormalizeGameDataPath(path, out var gameDataPath))
            {
                return gameDataPath;
            }
        }

        throw new InvalidOperationException(
            "BMR replay parsing requires FFXIV game data. Set FFXIV_GAME_PATH, XLCORE_GAME_PATH, or XIV_GAME_PATH to the install root, game directory, or sqpack directory.");
    }

    private static IEnumerable<string> CandidateGameDataPaths()
    {
        foreach (var variable in new[] { "FFXIV_GAME_PATH", "XLCORE_GAME_PATH", "XIV_GAME_PATH" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".xlcore", "patch", "game");
            yield return Path.Combine(home, "Games", "steam", "debian-installation", "steamapps", "common", "FINAL FANTASY XIV Online");
            yield return Path.Combine(home, "Games", "steam", "debian-installation", "steamapps", "common", "FINAL FANTASY XIV Online", "game");
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return Path.Combine(programFilesX86, "SquareEnix", "FINAL FANTASY XIV - A Realm Reborn", "game");
            yield return Path.Combine(programFilesX86, "Steam", "steamapps", "common", "FINAL FANTASY XIV Online", "game");
        }
    }

    private static bool TryNormalizeGameDataPath(string path, out string gameDataPath)
    {
        var directSqpackPath = Path.Combine(path, "sqpack");
        gameDataPath = directSqpackPath;
        if (Directory.Exists(directSqpackPath))
        {
            return true;
        }

        var nestedGamePath = Path.Combine(path, "game");
        var nestedSqpackPath = Path.Combine(nestedGamePath, "sqpack");
        if (Directory.Exists(nestedSqpackPath))
        {
            gameDataPath = nestedSqpackPath;
            return true;
        }

        if (Path.GetFileName(path).Equals("sqpack", StringComparison.OrdinalIgnoreCase) && Directory.Exists(path))
        {
            gameDataPath = path;
            return true;
        }

        return false;
    }

    public static BmrReplayData AutoMatch(string xcaiPath, XcaiLog xcai, string bmrDirectory, out MatchResult match)
    {
        var candidates = Directory.EnumerateFiles(bmrDirectory, "*.log", SearchOption.AllDirectories)
            .Select(path => new ReplayCandidate(path, EstimateReplayStartUtc(path), File.GetLastWriteTimeUtc(path)))
            .OrderBy(candidate => Math.Abs(((candidate.EstimatedStartUtc ?? candidate.LastWriteUtc) - xcai.Header.CombatStartUtc).TotalSeconds))
            .Take(8)
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException($"No BMR replay logs found under '{bmrDirectory}'.");
        }

        BmrReplayData? best = null;
        MatchResult? bestMatch = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var replay = Read(candidate.Path);
                var current = ScoreMatch(xcai, replay, candidate.EstimatedStartUtc ?? candidate.LastWriteUtc);
                if (bestMatch == null || current.Confidence > bestMatch.Confidence)
                {
                    best = replay;
                    bestMatch = current;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Skipping BMR candidate '{candidate.Path}': {ex.Message}");
            }
        }

        var selected = ValidateAutoMatchConfidence(best, bestMatch, bmrDirectory);
        match = selected.Match;
        Console.WriteLine($"Auto-matched BMR replay: {selected.Replay.Path}");
        Console.WriteLine($"Match confidence: {match.Confidence:0.00}");
        return selected.Replay;
    }

    internal static (BmrReplayData Replay, MatchResult Match) ValidateAutoMatchConfidence(BmrReplayData? best, MatchResult? bestMatch, string bmrDirectory)
    {
        if (best == null || bestMatch == null)
        {
            throw new InvalidOperationException($"No readable BMR replay logs found under '{bmrDirectory}'.");
        }

        if (bestMatch.Confidence < 0.45d)
        {
            throw new InvalidOperationException($"Best BMR auto-match confidence was {bestMatch.Confidence:0.00}; refusing to pick '{best.Path}'.");
        }

        return (best, bestMatch);
    }

    public static MatchResult ScoreExplicitMatch(XcaiLog xcai, BmrReplayData bmr)
    {
        return ScoreMatch(xcai, bmr, EstimateReplayStartUtc(bmr.Path) ?? bmr.Summary.Start ?? File.GetLastWriteTimeUtc(bmr.Path));
    }

    private static BmrSummary BuildSummary(string path, Replay replay)
    {
        var start = replay.Ops.Count == 0 ? (DateTime?)null : replay.Ops[0].Timestamp;
        var end = replay.Ops.Count == 0 ? (DateTime?)null : replay.Ops[^1].Timestamp;
        var participantLabels = BuildParticipantLabels(replay);
        var encounters = replay.Encounters
            .Select((encounter, index) => new BmrEncounterSummary(
                index,
                encounter.InstanceID,
                encounter.OID,
                encounter.Zone,
                encounter.Time.Start,
                encounter.Time.End,
                encounter.States.Count,
                encounter.Phases.Count,
                encounter.States.Select(state => new BmrEncounterStateSummary(
                    state.ID,
                    state.Name,
                    state.Comment,
                    state.ExpectedDuration,
                    state.Exit)).ToArray(),
                encounter.Phases.Select(phase => new BmrEncounterPhaseSummary(
                    phase.ID,
                    phase.LastStateID,
                    phase.Exit)).ToArray(),
                encounter.ParticipantsByOID
                    .OrderBy(entry => entry.Key)
                    .SelectMany(entry => entry.Value)
                    .Distinct()
                    .OrderBy(participant => participant.OID)
                    .ThenBy(participant => participant.InstanceID)
                    .Select(participant => new BmrEncounterParticipantSummary(
                        participant.InstanceID,
                        participant.OID,
                        participantLabels.GetValueOrDefault(participant.InstanceID, ParticipantFallbackLabel(participant)),
                        participant.WasAlly))
                    .ToArray()))
            .ToArray();

        return new BmrSummary(
            Path.GetFullPath(path),
            start,
            end,
            replay.Ops.Count,
            replay.Encounters.Count,
            encounters,
            ExtractParticipants(replay, participantLabels),
            ExtractEvents(replay));
    }

    private static IReadOnlyList<BmrParticipantSummary> ExtractParticipants(Replay replay, IReadOnlyDictionary<ulong, string> labels)
    {
        return replay.Participants
            .OrderBy(participant => participant.WasAlly ? 0 : 1)
            .ThenBy(participant => participant.OID)
            .ThenBy(participant => participant.InstanceID)
            .Select(participant => new BmrParticipantSummary(
                participant.InstanceID,
                participant.OID,
                labels.GetValueOrDefault(participant.InstanceID, ParticipantFallbackLabel(participant)),
                participant.Type.ToString(),
                participant.ZoneID,
                participant.CFCID,
                participant.EffectiveExistence.Start,
                participant.EffectiveExistence.End,
                participant.MinRadius == float.MaxValue ? 0f : participant.MinRadius,
                participant.MaxRadius == float.MinValue ? 0f : participant.MaxRadius,
                participant.WasAlly,
                participant.HasAnyActions,
                participant.HasAnyStatuses,
                participant.IsTargetOfAnyActions,
                SamplePositions(participant.PosRotHistory)))
            .ToArray();
    }

    private static Dictionary<ulong, string> BuildParticipantLabels(Replay replay)
    {
        var labels = new Dictionary<ulong, string>();
        var partyIndex = 1;
        foreach (var participant in replay.Participants.Where(p => p.WasAlly).OrderBy(p => p.InstanceID))
        {
            labels[participant.InstanceID] = partyIndex == 1 ? "Player" : $"Party-{partyIndex - 1}";
            partyIndex++;
        }

        foreach (var participant in replay.Participants.Where(p => !p.WasAlly).OrderBy(p => p.OID).ThenBy(p => p.InstanceID))
        {
            labels[participant.InstanceID] = ParticipantFallbackLabel(participant);
        }

        return labels;
    }

    private static string ParticipantFallbackLabel(Replay.Participant participant)
    {
        return participant.OID != 0
            ? $"Enemy-{participant.OID}"
            : $"Actor-{participant.InstanceID:X}";
    }

    private static IReadOnlyList<BmrPositionSample> SamplePositions(SortedList<DateTime, System.Numerics.Vector4> positions)
    {
        if (positions.Count == 0)
        {
            return [];
        }

        if (positions.Count <= MaxBmrParticipantPositionSamples)
        {
            return positions.Select(entry => ToPositionSample(entry.Key, entry.Value)).ToArray();
        }

        var result = new List<BmrPositionSample>(MaxBmrParticipantPositionSamples);
        for (var i = 0; i < MaxBmrParticipantPositionSamples; i++)
        {
            var index = (int)MathF.Round(i * (positions.Count - 1f) / (MaxBmrParticipantPositionSamples - 1));
            result.Add(ToPositionSample(positions.Keys[index], positions.Values[index]));
        }

        return result;
    }

    private static BmrPositionSample ToPositionSample(DateTime timestamp, System.Numerics.Vector4 posRot)
    {
        return new BmrPositionSample(timestamp, new Vec3(posRot.X, posRot.Y, posRot.Z), posRot.W);
    }

    private static IReadOnlyList<BmrEvent> ExtractEvents(Replay replay)
    {
        var events = new List<BmrEvent>(Math.Min(MaxBmrEvents, replay.Actions.Count + replay.Statuses.Count + replay.Tethers.Count + replay.Icons.Count));

        foreach (var action in replay.Actions.Take(MaxBmrEvents))
        {
            events.Add(new BmrEvent(
                "action",
                action.Timestamp,
                action.Source.InstanceID,
                action.Source.OID,
                action.MainTarget?.InstanceID,
                action.MainTarget?.OID,
                action.ID.Raw,
                $"{action.ID.Type}:{action.ID.ID}"));
        }

        foreach (var participant in replay.Participants)
        {
            foreach (var cast in participant.Casts)
            {
                if (events.Count >= MaxBmrEvents)
                {
                    break;
                }

                events.Add(new BmrEvent(
                    "cast",
                    cast.Time.Start,
                    participant.InstanceID,
                    participant.OID,
                    cast.Target?.InstanceID,
                    cast.Target?.OID,
                    cast.ID.Raw,
                    $"{cast.ID.Type}:{cast.ID.ID}"));
            }
        }

        foreach (var status in replay.Statuses.Take(Math.Max(0, MaxBmrEvents - events.Count)))
        {
            events.Add(new BmrEvent(
                "status",
                status.Time.Start,
                status.Source?.InstanceID ?? 0,
                status.Source?.OID ?? 0,
                status.Target.InstanceID,
                status.Target.OID,
                status.ID,
                status.ID.ToString(CultureInfo.InvariantCulture)));
        }

        foreach (var tether in replay.Tethers.Take(Math.Max(0, MaxBmrEvents - events.Count)))
        {
            events.Add(new BmrEvent(
                "tether",
                tether.Time.Start,
                tether.Source.InstanceID,
                tether.Source.OID,
                tether.Target.InstanceID,
                tether.Target.OID,
                tether.ID,
                tether.ID.ToString(CultureInfo.InvariantCulture)));
        }

        foreach (var icon in replay.Icons.Take(Math.Max(0, MaxBmrEvents - events.Count)))
        {
            events.Add(new BmrEvent(
                "icon",
                icon.Timestamp,
                icon.Source.InstanceID,
                icon.Source.OID,
                icon.Target?.InstanceID,
                icon.Target?.OID,
                icon.ID,
                icon.ID.ToString(CultureInfo.InvariantCulture)));
        }

        return events
            .OrderBy(e => e.Timestamp)
            .Take(MaxBmrEvents)
            .ToArray();
    }

    private static MatchResult ScoreMatch(XcaiLog xcai, BmrReplayData bmr, DateTime estimatedStartUtc)
    {
        var evidence = new List<string>();
        var score = 0d;

        var encounterMatch = ScoreBestEncounterMatch(xcai, bmr.Summary);
        if (encounterMatch != null)
        {
            score += encounterMatch.Score;
            evidence.AddRange(encounterMatch.Evidence);
        }
        else
        {
            score += ScoreReplayStartTime(xcai, estimatedStartUtc, evidence);
        }

        if (xcai.Header.ContentFinderConditionId != 0 &&
            bmr.Summary.Participants.Any(participant => participant.Cfcid == xcai.Header.ContentFinderConditionId))
        {
            score += 0.1d;
            evidence.Add($"CFCID matched participant data {xcai.Header.ContentFinderConditionId}");
        }

        var targetBaseIds = xcai.Frames
            .Select(frame => frame.TargetBaseId)
            .Where(id => id != 0)
            .Distinct()
            .ToHashSet();
        if (targetBaseIds.Count > 0 &&
            bmr.Summary.Participants.Any(participant => targetBaseIds.Contains(participant.Oid)))
        {
            score += 0.15d;
            evidence.Add("target OID matched replay participants");
        }

        var targetObjectIds = xcai.Frames
            .Select(frame => frame.TargetObjectId)
            .Where(id => id != 0)
            .Distinct()
            .ToHashSet();
        if (targetObjectIds.Count > 0 &&
            bmr.Summary.Participants.Any(participant => targetObjectIds.Contains(participant.InstanceId)))
        {
            score += 0.1d;
            evidence.Add("target object ID matched replay participants");
        }

        var pathScore = ScorePathSimilarity(xcai, bmr.Replay, encounterMatch?.ReferenceTime ?? ToBmrClock(xcai.Header.CombatStartUtc));
        if (pathScore > 0)
        {
            score += pathScore;
            evidence.Add($"player path similarity contributed {pathScore:0.00}");
        }

        if (!string.IsNullOrWhiteSpace(xcai.Header.BossModActiveModule) &&
            xcai.Header.BossModActiveModule != "<none>")
        {
            score += 0.05d;
            evidence.Add($"XCAI active BMR module was {xcai.Header.BossModActiveModule}");
        }

        return new MatchResult(bmr.Path, Math.Min(1d, score), evidence);
    }

    private static double ScoreReplayStartTime(XcaiLog xcai, DateTime estimatedStartUtc, List<string> evidence)
    {
        var timeDelta = Math.Abs((estimatedStartUtc - xcai.Header.CombatStartUtc).TotalSeconds);
        if (timeDelta <= 120)
        {
            evidence.Add($"start time within {timeDelta:0}s");
            return 0.3d;
        }

        if (timeDelta <= 600)
        {
            evidence.Add($"start time within {timeDelta:0}s");
            return 0.15d;
        }

        evidence.Add($"start time differs by {timeDelta:0}s");
        return 0d;
    }

    private static EncounterMatch? ScoreBestEncounterMatch(XcaiLog xcai, BmrSummary summary)
    {
        if (summary.Encounters.Count == 0)
        {
            return null;
        }

        var combatStart = ToBmrClock(xcai.Header.CombatStartUtc);
        var combatEnd = ToBmrClock(xcai.Header.CombatEndUtc);
        var targetBaseIds = xcai.Frames
            .Select(frame => frame.TargetBaseId)
            .Where(id => id != 0)
            .Distinct()
            .ToHashSet();
        var targetObjectIds = xcai.Frames
            .Select(frame => frame.TargetObjectId)
            .Where(id => id != 0)
            .Distinct()
            .ToHashSet();

        EncounterMatch? best = null;
        foreach (var encounter in summary.Encounters)
        {
            var encounterEvidence = new List<string>();
            var encounterScore = 0d;
            var distanceSeconds = WindowDistanceSeconds(combatStart, combatEnd, encounter.Start, encounter.End);
            if (distanceSeconds <= 0d)
            {
                encounterScore += 0.3d;
                encounterEvidence.Add($"XCAI combat overlaps BMR encounter #{encounter.Index}");
            }
            else if (distanceSeconds <= 120d)
            {
                encounterScore += 0.2d;
                encounterEvidence.Add($"nearest BMR encounter #{encounter.Index} is {distanceSeconds:0}s away");
            }
            else if (distanceSeconds <= 600d)
            {
                encounterScore += 0.1d;
                encounterEvidence.Add($"nearest BMR encounter #{encounter.Index} is {distanceSeconds:0}s away");
            }

            if (xcai.Header.TerritoryType != 0 && encounter.Zone == xcai.Header.TerritoryType)
            {
                encounterScore += 0.2d;
                encounterEvidence.Add($"territory matched BMR encounter zone {encounter.Zone}");
            }

            if (targetBaseIds.Count > 0 &&
                (targetBaseIds.Contains(encounter.Oid) ||
                 encounter.Participants.Any(participant => targetBaseIds.Contains(participant.Oid))))
            {
                encounterScore += 0.2d;
                encounterEvidence.Add("target OID matched BMR encounter actors");
            }

            if (targetObjectIds.Count > 0 &&
                (targetObjectIds.Contains(encounter.InstanceId) ||
                 encounter.Participants.Any(participant => targetObjectIds.Contains(participant.InstanceId))))
            {
                encounterScore += 0.1d;
                encounterEvidence.Add("target object ID matched BMR encounter actors");
            }

            if (best == null || encounterScore > best.Score)
            {
                best = new EncounterMatch(encounterScore, encounter.Start, encounterEvidence);
            }
        }

        return best?.Score > 0d ? best : null;
    }

    private static double ScorePathSimilarity(XcaiLog xcai, Replay replay, DateTime? referenceTime)
    {
        var firstPlayer = xcai.Frames.FirstOrDefault(frame => frame.PlayerPosition != null)?.PlayerPosition;
        if (firstPlayer == null || replay.Participants.Count == 0)
        {
            return 0d;
        }

        var replayStart = referenceTime ?? replay.Encounters.FirstOrDefault()?.Time.Start ?? replay.Ops.FirstOrDefault()?.Timestamp ?? default;
        if (replayStart == default)
        {
            return 0d;
        }

        var bestDistance = replay.Participants
            .Select(participant => participant.PosRotAt(replayStart))
            .Where(posRot => posRot != default)
            .Select(posRot => Vec3.Distance2D(firstPlayer, new Vec3(posRot.X, posRot.Y, posRot.Z)))
            .DefaultIfEmpty(float.MaxValue)
            .Min();

        return bestDistance switch
        {
            <= 2f => 0.2d,
            <= 6f => 0.1d,
            <= 12f => 0.05d,
            _ => 0d
        };
    }

    private static double WindowDistanceSeconds(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd)
    {
        if (aStart <= bEnd && aEnd >= bStart)
        {
            return 0d;
        }

        return aEnd < bStart
            ? (bStart - aEnd).TotalSeconds
            : (aStart - bEnd).TotalSeconds;
    }

    private static DateTime ToBmrClock(DateTime timestamp)
    {
        return timestamp.Kind == DateTimeKind.Utc
            ? timestamp.ToLocalTime()
            : timestamp;
    }

    private static DateTime? EstimateReplayStartUtc(string path)
    {
        var match = ReplayTimestampRegex().Match(Path.GetFileNameWithoutExtension(path));
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups[1].Value;
        return DateTime.TryParseExact(
            value,
            "yyyy_MM_dd_HH_mm_ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var local)
            ? local.ToUniversalTime()
            : null;
    }

    private sealed record ReplayCandidate(string Path, DateTime? EstimatedStartUtc, DateTime LastWriteUtc);

    private sealed record EncounterMatch(double Score, DateTime ReferenceTime, IReadOnlyList<string> Evidence);

    [GeneratedRegex(@"(\d{4}_\d{2}_\d{2}_\d{2}_\d{2}_\d{2})")]
    private static partial Regex ReplayTimestampRegex();
}
