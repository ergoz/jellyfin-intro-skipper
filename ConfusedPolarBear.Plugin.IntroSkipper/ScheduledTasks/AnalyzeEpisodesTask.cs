using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// Analyze all television episodes for introduction sequences.
/// </summary>
public class AnalyzeEpisodesTask : IScheduledTask
{
    /// <summary>
    /// Seconds of audio in one fingerprint point.
    /// This value is defined by the Chromaprint library and should not be changed.
    /// </summary>
    private const double SamplesToSeconds = 0.128;

    private readonly ILogger<AnalyzeEpisodesTask> _logger;

    private readonly ILogger<QueueManager> _queueLogger;

    private readonly ILibraryManager? _libraryManager;

    /// <summary>
    /// Lock which guards the shared dictionary of intros.
    /// </summary>
    private readonly object _introsLock = new object();

    /// <summary>
    /// Minimum duration of similar audio that will be considered an introduction.
    /// </summary>
    private static int minimumIntroDuration = 15;

    private static int maximumDifferences = 6;

    private static int invertedIndexShift = 2;

    private static double maximumTimeSkip = 3.5;

    private static double silenceDetectionMinimumDuration = 0.33;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnalyzeEpisodesTask"/> class.
    /// </summary>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="libraryManager">Library manager.</param>
    public AnalyzeEpisodesTask(
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager) : this(loggerFactory)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AnalyzeEpisodesTask"/> class.
    /// </summary>
    /// <param name="loggerFactory">Logger factory.</param>
    public AnalyzeEpisodesTask(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<AnalyzeEpisodesTask>();
        _queueLogger = loggerFactory.CreateLogger<QueueManager>();

        EdlManager.Initialize(_logger);
    }

    /// <summary>
    /// Gets the task name.
    /// </summary>
    public string Name => "Detect Introductions";

    /// <summary>
    /// Gets the task category.
    /// </summary>
    public string Category => "Intro Skipper";

    /// <summary>
    /// Gets the task description.
    /// </summary>
    public string Description => "Analyzes the audio of all television episodes to find introduction sequences.";

    /// <summary>
    /// Gets the task key.
    /// </summary>
    public string Key => "CPBIntroSkipperDetectIntroductions";

    /// <summary>
    /// Analyze all episodes in the queue. Only one instance of this task should be run at a time.
    /// </summary>
    /// <param name="progress">Task progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (_libraryManager is null)
        {
            throw new InvalidOperationException("Library manager must not be null");
        }

        // Make sure the analysis queue matches what's currently in Jellyfin.
        var queueManager = new QueueManager(_queueLogger, _libraryManager);
        queueManager.EnqueueAllEpisodes();

        var queue = Plugin.Instance!.AnalysisQueue;

        if (queue.Count == 0)
        {
            throw new FingerprintException(
                "No episodes to analyze. If you are limiting the list of libraries to analyze, check that all library names have been spelled correctly.");
        }

        // Load analysis settings from configuration
        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        maximumDifferences = config.MaximumFingerprintPointDifferences;
        invertedIndexShift = config.InvertedIndexShift;
        maximumTimeSkip = config.MaximumTimeSkip;
        silenceDetectionMinimumDuration = config.SilenceDetectionMinimumDuration;

        // Log EDL settings
        EdlManager.LogConfiguration();

        var totalProcessed = 0;
        var options = new ParallelOptions()
        {
            MaxDegreeOfParallelism = Plugin.Instance!.Configuration.MaxParallelism
        };

        minimumIntroDuration = Plugin.Instance!.Configuration.MinimumIntroDuration;

        // TODO: if the queue is modified while the task is running, the task will fail.
        // clone the queue before running the task to prevent this.

        // Analyze all episodes in the queue using the degrees of parallelism the user specified.
        Parallel.ForEach(queue, options, (season) =>
        {
            var (episodes, unanalyzed) = VerifyEpisodes(season.Value.AsReadOnly());
            if (episodes.Count == 0)
            {
                return;
            }

            var first = episodes[0];
            var writeEdl = false;

            if (!unanalyzed)
            {
                _logger.LogDebug(
                    "All episodes in {Name} season {Season} have already been analyzed",
                    first.SeriesName,
                    first.SeasonNumber);

                return;
            }

            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                // Increment totalProcessed by the number of episodes in this season that were actually analyzed
                // (instead of just using the number of episodes in the current season).
                var analyzed = AnalyzeSeason(episodes, cancellationToken);
                Interlocked.Add(ref totalProcessed, analyzed);
                writeEdl = analyzed > 0 || Plugin.Instance!.Configuration.RegenerateEdlFiles;
            }
            catch (FingerprintException ex)
            {
                _logger.LogWarning(
                    "Unable to analyze {Series} season {Season}: unable to fingerprint: {Ex}",
                    first.SeriesName,
                    first.SeasonNumber,
                    ex);
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogWarning(
                    "Unable to analyze {Series} season {Season}: cache miss: {Ex}",
                    first.SeriesName,
                    first.SeasonNumber,
                    ex);
            }

            if (writeEdl && Plugin.Instance!.Configuration.EdlAction != EdlAction.None)
            {
                EdlManager.UpdateEDLFiles(episodes);
            }

            progress.Report((totalProcessed * 100) / Plugin.Instance!.TotalQueued);
        });

        // Turn the regenerate EDL flag off after the scan completes.
        if (Plugin.Instance!.Configuration.RegenerateEdlFiles)
        {
            _logger.LogInformation("Turning EDL file regeneration flag off");
            Plugin.Instance!.Configuration.RegenerateEdlFiles = false;
            Plugin.Instance!.SaveConfiguration();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Verify that all episodes in a season exist in Jellyfin and as a file in storage.
    /// </summary>
    /// <param name="candidates">QueuedEpisodes.</param>
    /// <returns>Verified QueuedEpisodes and a flag indicating if any episode in this season has not been analyzed yet.</returns>
    private (
        ReadOnlyCollection<QueuedEpisode> VerifiedEpisodes,
        bool AnyUnanalyzed)
        VerifyEpisodes(ReadOnlyCollection<QueuedEpisode> candidates)
    {
        var unanalyzed = false;
        var verified = new List<QueuedEpisode>();

        foreach (var candidate in candidates)
        {
            try
            {
                // Verify that the episode exists in Jellyfin and in storage
                var path = Plugin.Instance!.GetItemPath(candidate.EpisodeId);

                if (File.Exists(path))
                {
                    verified.Add(candidate);
                }

                // Flag this season for analysis if the current episode hasn't been analyzed yet
                if (!Plugin.Instance.Intros.ContainsKey(candidate.EpisodeId))
                {
                    unanalyzed = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    "Skipping analysis of {Name} ({Id}): {Exception}",
                    candidate.Name,
                    candidate.EpisodeId,
                    ex);
            }
        }

        return (verified.AsReadOnly(), unanalyzed);
    }

    /// <summary>
    /// Count the number of previously processed episodes to ensure the reported progress is correct.
    /// </summary>
    /// <returns>Number of previously processed episodes.</returns>
    private int CountProcessedEpisodes()
    {
        var previous = 0;

        foreach (var season in Plugin.Instance!.AnalysisQueue)
        {
            foreach (var episode in season.Value)
            {
                if (!Plugin.Instance!.Intros.TryGetValue(episode.EpisodeId, out var intro) || !intro.Valid)
                {
                    continue;
                }

                previous++;
            }
        }

        return previous;
    }

    /// <summary>
    /// Fingerprints all episodes in the provided season and stores the timestamps of all introductions.
    /// </summary>
    /// <param name="episodes">Episodes in this season.</param>
    /// <param name="cancellationToken">Cancellation token provided by the scheduled task.</param>
    /// <returns>Number of episodes from the provided season that were analyzed.</returns>
    private int AnalyzeSeason(
        ReadOnlyCollection<QueuedEpisode> episodes,
        CancellationToken cancellationToken)
    {
        // All intros for this season.
        var seasonIntros = new Dictionary<Guid, Intro>();

        // Cache of all fingerprints for this season.
        var fingerprintCache = new Dictionary<Guid, uint[]>();

        // Episode analysis queue.
        var episodeAnalysisQueue = new List<QueuedEpisode>(episodes);

        // Skip seasons with an insufficient number of episodes.
        if (episodes.Count <= 1)
        {
            return episodes.Count;
        }

        // Only analyze specials (season 0) if the user has opted in.
        var first = episodes[0];
        if (first.SeasonNumber == 0 && !Plugin.Instance!.Configuration.AnalyzeSeasonZero)
        {
            return 0;
        }

        _logger.LogInformation(
            "Analyzing {Count} episodes from {Name} season {Season}",
            episodes.Count,
            first.SeriesName,
            first.SeasonNumber);

        // Compute fingerprints for all episodes in the season
        foreach (var episode in episodeAnalysisQueue)
        {
            try
            {
                fingerprintCache[episode.EpisodeId] = FFmpegWrapper.Fingerprint(episode);

                if (cancellationToken.IsCancellationRequested)
                {
                    return episodes.Count;
                }
            }
            catch (FingerprintException ex)
            {
                _logger.LogWarning("Caught fingerprint error: {Ex}", ex);

                // Fallback to an empty fingerprint on any error
                fingerprintCache[episode.EpisodeId] = Array.Empty<uint>();
            }
        }

        // While there are still episodes in the queue
        while (episodeAnalysisQueue.Count > 0)
        {
            // Pop the first episode from the queue
            var currentEpisode = episodeAnalysisQueue[0];
            episodeAnalysisQueue.RemoveAt(0);

            // Search through all remaining episodes.
            foreach (var remainingEpisode in episodeAnalysisQueue)
            {
                // Compare the current episode to all remaining episodes in the queue.
                var (currentIntro, remainingIntro) = CompareEpisodes(
                    currentEpisode.EpisodeId,
                    fingerprintCache[currentEpisode.EpisodeId],
                    remainingEpisode.EpisodeId,
                    fingerprintCache[remainingEpisode.EpisodeId]);

                // Ignore this comparison result if:
                // - one of the intros isn't valid, or
                // - the introduction exceeds the configured limit
                if (
                    !remainingIntro.Valid ||
                    remainingIntro.Duration > Plugin.Instance!.Configuration.MaximumIntroDuration)
                {
                    continue;
                }

                // Only save the discovered intro if it is:
                // - the first intro discovered for this episode
                // - longer than the previously discovered intro
                if (
                    !seasonIntros.TryGetValue(currentIntro.EpisodeId, out var savedCurrentIntro) ||
                    currentIntro.Duration > savedCurrentIntro.Duration)
                {
                    seasonIntros[currentIntro.EpisodeId] = currentIntro;
                }

                if (
                    !seasonIntros.TryGetValue(remainingIntro.EpisodeId, out var savedRemainingIntro) ||
                    remainingIntro.Duration > savedRemainingIntro.Duration)
                {
                    seasonIntros[remainingIntro.EpisodeId] = remainingIntro;
                }

                break;
            }

            // If no intro is found at this point, the popped episode is not reinserted into the queue.
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return episodes.Count;
        }

        // Adjust all introduction end times so that they end at silence.
        seasonIntros = AdjustIntroEndTimes(episodes, seasonIntros);

        // Ensure only one thread at a time can update the shared intro dictionary.
        lock (_introsLock)
        {
            foreach (var intro in seasonIntros)
            {
                Plugin.Instance!.Intros[intro.Key] = intro.Value;
            }

            Plugin.Instance!.SaveTimestamps();
        }

        return episodes.Count;
    }

    /// <summary>
    /// Analyze two episodes to find an introduction sequence shared between them.
    /// </summary>
    /// <param name="lhsId">First episode id.</param>
    /// <param name="lhsPoints">First episode fingerprint points.</param>
    /// <param name="rhsId">Second episode id.</param>
    /// <param name="rhsPoints">Second episode fingerprint points.</param>
    /// <returns>Intros for the first and second episodes.</returns>
    public (Intro Lhs, Intro Rhs) CompareEpisodes(
        Guid lhsId,
        uint[] lhsPoints,
        Guid rhsId,
        uint[] rhsPoints)
    {
        // Creates an inverted fingerprint point index for both episodes.
        // For every point which is a 100% match, search for an introduction at that point.
        var (lhsRanges, rhsRanges) = SearchInvertedIndex(lhsId, lhsPoints, rhsId, rhsPoints);

        if (lhsRanges.Count > 0)
        {
            _logger.LogTrace("Index search successful");

            return GetLongestTimeRange(lhsId, lhsRanges, rhsId, rhsRanges);
        }

        _logger.LogTrace(
            "Unable to find a shared introduction sequence between {LHS} and {RHS}",
            lhsId,
            rhsId);

        return (new Intro(lhsId), new Intro(rhsId));
    }

    /// <summary>
    /// Locates the longest range of similar audio and returns an Intro class for each range.
    /// </summary>
    /// <param name="lhsId">First episode id.</param>
    /// <param name="lhsRanges">First episode shared timecodes.</param>
    /// <param name="rhsId">Second episode id.</param>
    /// <param name="rhsRanges">Second episode shared timecodes.</param>
    /// <returns>Intros for the first and second episodes.</returns>
    private (Intro Lhs, Intro Rhs) GetLongestTimeRange(
        Guid lhsId,
        List<TimeRange> lhsRanges,
        Guid rhsId,
        List<TimeRange> rhsRanges)
    {
        // Store the longest time range as the introduction.
        lhsRanges.Sort();
        rhsRanges.Sort();

        var lhsIntro = lhsRanges[0];
        var rhsIntro = rhsRanges[0];

        // If the intro starts early in the episode, move it to the beginning.
        if (lhsIntro.Start <= 5)
        {
            lhsIntro.Start = 0;
        }

        if (rhsIntro.Start <= 5)
        {
            rhsIntro.Start = 0;
        }

        // Create Intro classes for each time range.
        return (new Intro(lhsId, lhsIntro), new Intro(rhsId, rhsIntro));
    }

    /// <summary>
    /// Search for a shared introduction sequence using inverted indexes.
    /// </summary>
    /// <param name="lhsId">LHS ID.</param>
    /// <param name="lhsPoints">Left episode fingerprint points.</param>
    /// <param name="rhsId">RHS ID.</param>
    /// <param name="rhsPoints">Right episode fingerprint points.</param>
    /// <returns>List of shared TimeRanges between the left and right episodes.</returns>
    private (List<TimeRange> Lhs, List<TimeRange> Rhs) SearchInvertedIndex(
        Guid lhsId,
        uint[] lhsPoints,
        Guid rhsId,
        uint[] rhsPoints)
    {
        var lhsRanges = new List<TimeRange>();
        var rhsRanges = new List<TimeRange>();

        // Generate inverted indexes for the left and right episodes.
        var lhsIndex = FFmpegWrapper.CreateInvertedIndex(lhsId, lhsPoints);
        var rhsIndex = FFmpegWrapper.CreateInvertedIndex(rhsId, rhsPoints);
        var indexShifts = new HashSet<int>();

        // For all audio points in the left episode, check if the right episode has a point which matches exactly.
        // If an exact match is found, calculate the shift that must be used to align the points.
        foreach (var kvp in lhsIndex)
        {
            var originalPoint = kvp.Key;

            for (var i = -1 * invertedIndexShift; i <= invertedIndexShift; i++)
            {
                var modifiedPoint = (uint)(originalPoint + i);

                if (rhsIndex.ContainsKey(modifiedPoint))
                {
                    var lhsFirst = (int)lhsIndex[originalPoint];
                    var rhsFirst = (int)rhsIndex[modifiedPoint];
                    indexShifts.Add(rhsFirst - lhsFirst);
                }
            }
        }

        // Use all discovered shifts to compare the episodes.
        foreach (var shift in indexShifts)
        {
            var (lhsIndexContiguous, rhsIndexContiguous) = FindContiguous(lhsPoints, rhsPoints, shift);
            if (lhsIndexContiguous.End > 0 && rhsIndexContiguous.End > 0)
            {
                lhsRanges.Add(lhsIndexContiguous);
                rhsRanges.Add(rhsIndexContiguous);
            }
        }

        return (lhsRanges, rhsRanges);
    }

    /// <summary>
    /// Finds the longest contiguous region of similar audio between two fingerprints using the provided shift amount.
    /// </summary>
    /// <param name="lhs">First fingerprint to compare.</param>
    /// <param name="rhs">Second fingerprint to compare.</param>
    /// <param name="shiftAmount">Amount to shift one fingerprint by.</param>
    private static (TimeRange Lhs, TimeRange Rhs) FindContiguous(
        uint[] lhs,
        uint[] rhs,
        int shiftAmount)
    {
        var leftOffset = 0;
        var rightOffset = 0;

        // Calculate the offsets for the left and right hand sides.
        if (shiftAmount < 0)
        {
            leftOffset -= shiftAmount;
        }
        else
        {
            rightOffset += shiftAmount;
        }

        // Store similar times for both LHS and RHS.
        var lhsTimes = new List<double>();
        var rhsTimes = new List<double>();
        var upperLimit = Math.Min(lhs.Length, rhs.Length) - Math.Abs(shiftAmount);

        // XOR all elements in LHS and RHS, using the shift amount from above.
        for (var i = 0; i < upperLimit; i++)
        {
            // XOR both samples at the current position.
            var lhsPosition = i + leftOffset;
            var rhsPosition = i + rightOffset;
            var diff = lhs[lhsPosition] ^ rhs[rhsPosition];

            // If the difference between the samples is small, flag both times as similar.
            if (CountBits(diff) > maximumDifferences)
            {
                continue;
            }

            var lhsTime = lhsPosition * SamplesToSeconds;
            var rhsTime = rhsPosition * SamplesToSeconds;

            lhsTimes.Add(lhsTime);
            rhsTimes.Add(rhsTime);
        }

        // Ensure the last timestamp is checked
        lhsTimes.Add(double.MaxValue);
        rhsTimes.Add(double.MaxValue);

        // Now that both fingerprints have been compared at this shift, see if there's a contiguous time range.
        var lContiguous = TimeRangeHelpers.FindContiguous(lhsTimes.ToArray(), maximumTimeSkip);
        if (lContiguous is null || lContiguous.Duration < minimumIntroDuration)
        {
            return (new TimeRange(), new TimeRange());
        }

        // Since LHS had a contiguous time range, RHS must have one also.
        var rContiguous = TimeRangeHelpers.FindContiguous(rhsTimes.ToArray(), maximumTimeSkip)!;

        // Tweak the end timestamps just a bit to ensure as little content as possible is skipped over.
        if (lContiguous.Duration >= 90)
        {
            lContiguous.End -= 2 * maximumTimeSkip;
            rContiguous.End -= 2 * maximumTimeSkip;
        }
        else if (lContiguous.Duration >= 30)
        {
            lContiguous.End -= maximumTimeSkip;
            rContiguous.End -= maximumTimeSkip;
        }

        return (lContiguous, rContiguous);
    }

    /// <summary>
    /// Adjusts the end timestamps of all intros so that they end at silence.
    /// </summary>
    /// <param name="episodes">QueuedEpisodes to adjust.</param>
    /// <param name="originalIntros">Original introductions.</param>
    private Dictionary<Guid, Intro> AdjustIntroEndTimes(
        ReadOnlyCollection<QueuedEpisode> episodes,
        Dictionary<Guid, Intro> originalIntros)
    {
        // The minimum duration of audio that must be silent before adjusting the intro's end.
        var minimumSilence = Plugin.Instance!.Configuration.SilenceDetectionMinimumDuration;

        Dictionary<Guid, Intro> modifiedIntros = new();

        // For all episodes
        foreach (var episode in episodes)
        {
            _logger.LogTrace(
                "Adjusting introduction end time for {Name} ({Id})",
                episode.Name,
                episode.EpisodeId);

            // If no intro was found for this episode, skip it.
            if (!originalIntros.TryGetValue(episode.EpisodeId, out var originalIntro))
            {
                _logger.LogTrace("{Name} does not have an intro", episode.Name);
                continue;
            }

            // Only adjust the end timestamp of the intro
            var originalIntroEnd = new TimeRange(originalIntro.IntroEnd - 15, originalIntro.IntroEnd);

            _logger.LogTrace(
                "{Name} original intro: {Start} - {End}",
                episode.Name,
                originalIntro.IntroStart,
                originalIntro.IntroEnd);

            // Detect silence in the media file up to the end of the intro.
            var silence = FFmpegWrapper.DetectSilence(episode, (int)originalIntro.IntroEnd + 2);

            // For all periods of silence
            foreach (var currentRange in silence)
            {
                _logger.LogTrace(
                    "{Name} silence: {Start} - {End}",
                    episode.Name,
                    currentRange.Start,
                    currentRange.End);

                // Ignore any silence that:
                // * doesn't intersect the ending of the intro, or
                // * is shorter than the user defined minimum duration, or
                // * starts before the introduction does
                if (
                    !originalIntroEnd.Intersects(currentRange) ||
                    currentRange.Duration < silenceDetectionMinimumDuration ||
                    currentRange.Start < originalIntro.IntroStart)
                {
                    continue;
                }

                // Adjust the end timestamp of the intro to match the start of the silence region.
                originalIntro.IntroEnd = currentRange.Start;
                break;
            }

            _logger.LogTrace(
                "{Name} adjusted intro: {Start} - {End}",
                episode.Name,
                originalIntro.IntroStart,
                originalIntro.IntroEnd);

            // Add the (potentially) modified intro back.
            modifiedIntros[episode.EpisodeId] = originalIntro;
        }

        return modifiedIntros;
    }

    /// <summary>
    /// Count the number of bits that are set in the provided number.
    /// </summary>
    /// <param name="number">Number to count bits in.</param>
    /// <returns>Number of bits that are equal to 1.</returns>
    public static int CountBits(uint number)
    {
        return BitOperations.PopCount(number);
    }

    /// <summary>
    /// Get task triggers.
    /// </summary>
    /// <returns>Task triggers.</returns>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(0).Ticks
            }
        };
    }
}
