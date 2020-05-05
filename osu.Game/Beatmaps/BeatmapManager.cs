// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Extensions;
using osu.Framework.Graphics.Textures;
using osu.Framework.Lists;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Game.Beatmaps.Formats;
using osu.Game.Database;
using osu.Game.IO;
using osu.Game.IO.Archives;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Objects;
using Decoder = osu.Game.Beatmaps.Formats.Decoder;
using ZipArchive = SharpCompress.Archives.Zip.ZipArchive;

namespace osu.Game.Beatmaps
{
    /// <summary>
    /// Handles the storage and retrieval of Beatmaps/WorkingBeatmaps.
    /// </summary>
    public partial class BeatmapManager : DownloadableArchiveModelManager<BeatmapSetInfo, BeatmapSetFileInfo>
    {
        /// <summary>
        /// Fired when a single difficulty has been hidden.
        /// </summary>
        public event Action<BeatmapInfo> BeatmapHidden;

        /// <summary>
        /// Fired when a single difficulty has been restored.
        /// </summary>
        public event Action<BeatmapInfo> BeatmapRestored;

        /// <summary>
        /// A default representation of a WorkingBeatmap to use when no beatmap is available.
        /// </summary>
        public readonly WorkingBeatmap DefaultBeatmap;

        public override string[] HandledExtensions => new[] { ".osz" };

        protected override string[] HashableFileTypes => new[] { ".osu" };

        protected override string ImportFromStablePath => "Songs";

        private readonly RulesetStore rulesets;
        private readonly BeatmapStore beatmaps;
        private readonly AudioManager audioManager;
        private readonly GameHost host;
        private readonly BeatmapOnlineLookupQueue onlineLookupQueue;
        private readonly Storage exportStorage;

        public BeatmapManager(Storage storage, IDatabaseContextFactory contextFactory, RulesetStore rulesets, IAPIProvider api, AudioManager audioManager, GameHost host = null,
                              WorkingBeatmap defaultBeatmap = null)
            : base(storage, contextFactory, api, new BeatmapStore(contextFactory), host)
        {
            this.rulesets = rulesets;
            this.audioManager = audioManager;
            this.host = host;

            DefaultBeatmap = defaultBeatmap;

            beatmaps = (BeatmapStore)ModelStore;
            beatmaps.BeatmapHidden += b => BeatmapHidden?.Invoke(b);
            beatmaps.BeatmapRestored += b => BeatmapRestored?.Invoke(b);

            onlineLookupQueue = new BeatmapOnlineLookupQueue(api, storage);
            exportStorage = storage.GetStorageForDirectory("exports");
        }

        protected override ArchiveDownloadRequest<BeatmapSetInfo> CreateDownloadRequest(BeatmapSetInfo set, bool minimiseDownloadSize) =>
            new DownloadBeatmapSetRequest(set, minimiseDownloadSize);

        protected override bool ShouldDeleteArchive(string path) => Path.GetExtension(path)?.ToLowerInvariant() == ".osz";

        protected override async Task Populate(BeatmapSetInfo beatmapSet, ArchiveReader archive, CancellationToken cancellationToken = default)
        {
            if (archive != null)
                beatmapSet.Beatmaps = createBeatmapDifficulties(beatmapSet.Files);

            foreach (BeatmapInfo b in beatmapSet.Beatmaps)
            {
                // remove metadata from difficulties where it matches the set
                if (beatmapSet.Metadata.Equals(b.Metadata))
                    b.Metadata = null;

                b.BeatmapSet = beatmapSet;
            }

            validateOnlineIds(beatmapSet);

            bool hadOnlineBeatmapIDs = beatmapSet.Beatmaps.Any(b => b.OnlineBeatmapID > 0);

            await onlineLookupQueue.UpdateAsync(beatmapSet, cancellationToken);

            // ensure at least one beatmap was able to retrieve or keep an online ID, else drop the set ID.
            if (hadOnlineBeatmapIDs && !beatmapSet.Beatmaps.Any(b => b.OnlineBeatmapID > 0))
            {
                if (beatmapSet.OnlineBeatmapSetID != null)
                {
                    beatmapSet.OnlineBeatmapSetID = null;
                    LogForModel(beatmapSet, "Disassociating beatmap set ID due to loss of all beatmap IDs");
                }
            }
        }

        protected override void PreImport(BeatmapSetInfo beatmapSet)
        {
            if (beatmapSet.Beatmaps.Any(b => b.BaseDifficulty == null))
                throw new InvalidOperationException($"Cannot import {nameof(BeatmapInfo)} with null {nameof(BeatmapInfo.BaseDifficulty)}.");

            // check if a set already exists with the same online id, delete if it does.
            if (beatmapSet.OnlineBeatmapSetID != null)
            {
                var existingOnlineId = beatmaps.ConsumableItems.FirstOrDefault(b => b.OnlineBeatmapSetID == beatmapSet.OnlineBeatmapSetID);

                if (existingOnlineId != null)
                {
                    Delete(existingOnlineId);
                    beatmaps.PurgeDeletable(s => s.ID == existingOnlineId.ID);
                    LogForModel(beatmapSet, $"Found existing beatmap set with same OnlineBeatmapSetID ({beatmapSet.OnlineBeatmapSetID}). It has been purged.");
                }
            }
        }

        private void validateOnlineIds(BeatmapSetInfo beatmapSet)
        {
            var beatmapIds = beatmapSet.Beatmaps.Where(b => b.OnlineBeatmapID.HasValue).Select(b => b.OnlineBeatmapID).ToList();

            LogForModel(beatmapSet, "Validating online IDs...");

            // ensure all IDs are unique
            if (beatmapIds.GroupBy(b => b).Any(g => g.Count() > 1))
            {
                LogForModel(beatmapSet, "Found non-unique IDs, resetting...");
                resetIds();
                return;
            }

            // find any existing beatmaps in the database that have matching online ids
            var existingBeatmaps = QueryBeatmaps(b => beatmapIds.Contains(b.OnlineBeatmapID)).ToList();

            if (existingBeatmaps.Count > 0)
            {
                // reset the import ids (to force a re-fetch) *unless* they match the candidate CheckForExisting set.
                // we can ignore the case where the new ids are contained by the CheckForExisting set as it will either be used (import skipped) or deleted.
                var existing = CheckForExisting(beatmapSet);

                if (existing == null || existingBeatmaps.Any(b => !existing.Beatmaps.Contains(b)))
                {
                    LogForModel(beatmapSet, "Found existing import with IDs already, resetting...");
                    resetIds();
                }
            }

            void resetIds() => beatmapSet.Beatmaps.ForEach(b => b.OnlineBeatmapID = null);
        }

        protected override bool CheckLocalAvailability(BeatmapSetInfo model, IQueryable<BeatmapSetInfo> items)
            => base.CheckLocalAvailability(model, items)
               || (model.OnlineBeatmapSetID != null && items.Any(b => b.OnlineBeatmapSetID == model.OnlineBeatmapSetID));

        /// <summary>
        /// Delete a beatmap difficulty.
        /// </summary>
        /// <param name="beatmap">The beatmap difficulty to hide.</param>
        public void Hide(BeatmapInfo beatmap) => beatmaps.Hide(beatmap);

        /// <summary>
        /// Restore a beatmap difficulty.
        /// </summary>
        /// <param name="beatmap">The beatmap difficulty to restore.</param>
        public void Restore(BeatmapInfo beatmap) => beatmaps.Restore(beatmap);

        /// <summary>
        /// Saves an <see cref="IBeatmap"/> file against a given <see cref="BeatmapInfo"/>.
        /// </summary>
        /// <param name="info">The <see cref="BeatmapInfo"/> to save the content against. The file referenced by <see cref="BeatmapInfo.Path"/> will be replaced.</param>
        /// <param name="beatmapContent">The <see cref="IBeatmap"/> content to write.</param>
        public void Save(BeatmapInfo info, IBeatmap beatmapContent)
        {
            var setInfo = QueryBeatmapSet(s => s.Beatmaps.Any(b => b.ID == info.ID));

            using (var stream = new MemoryStream())
            {
                using (var sw = new StreamWriter(stream, Encoding.UTF8, 1024, true))
                    new LegacyBeatmapEncoder(beatmapContent).Encode(sw);

                stream.Seek(0, SeekOrigin.Begin);

                UpdateFile(setInfo, setInfo.Files.Single(f => string.Equals(f.Filename, info.Path, StringComparison.OrdinalIgnoreCase)), stream);
            }

            var working = workingCache.FirstOrDefault(w => w.BeatmapInfo?.ID == info.ID);
            if (working != null)
                workingCache.Remove(working);
        }

        /// <summary>
        /// Exports a <see cref="BeatmapSetInfo"/> to an .osz package.
        /// </summary>
        /// <param name="set">The <see cref="BeatmapSetInfo"/> to export.</param>
        public void Export(BeatmapSetInfo set)
        {
            var localSet = QueryBeatmapSet(s => s.ID == set.ID);

            using (var archive = ZipArchive.Create())
            {
                foreach (var file in localSet.Files)
                    archive.AddEntry(file.Filename, Files.Storage.GetStream(file.FileInfo.StoragePath));

                using (var outputStream = exportStorage.GetStream($"{set}.osz", FileAccess.Write, FileMode.Create))
                    archive.SaveTo(outputStream);

                exportStorage.OpenInNativeExplorer();
            }
        }

        private readonly WeakList<WorkingBeatmap> workingCache = new WeakList<WorkingBeatmap>();

        /// <summary>
        /// Retrieve a <see cref="WorkingBeatmap"/> instance for the provided <see cref="BeatmapInfo"/>
        /// </summary>
        /// <param name="beatmapInfo">The beatmap to lookup.</param>
        /// <param name="previous">The currently loaded <see cref="WorkingBeatmap"/>. Allows for optimisation where elements are shared with the new beatmap. May be returned if beatmapInfo requested matches</param>
        /// <returns>A <see cref="WorkingBeatmap"/> instance correlating to the provided <see cref="BeatmapInfo"/>.</returns>
        public WorkingBeatmap GetWorkingBeatmap(BeatmapInfo beatmapInfo, WorkingBeatmap previous = null)
        {
            if (beatmapInfo?.ID > 0 && previous != null && previous.BeatmapInfo?.ID == beatmapInfo.ID)
                return previous;

            if (beatmapInfo?.BeatmapSet == null || beatmapInfo == DefaultBeatmap?.BeatmapInfo)
                return DefaultBeatmap;

            if (beatmapInfo.BeatmapSet.Files == null)
            {
                var info = beatmapInfo;
                beatmapInfo = QueryBeatmap(b => b.ID == info.ID);
            }

            lock (workingCache)
            {
                var working = workingCache.FirstOrDefault(w => w.BeatmapInfo?.ID == beatmapInfo.ID);

                if (working == null)
                {
                    if (beatmapInfo.Metadata == null)
                        beatmapInfo.Metadata = beatmapInfo.BeatmapSet.Metadata;

                    workingCache.Add(working = new BeatmapManagerWorkingBeatmap(Files.Store,
                        new LargeTextureStore(host?.CreateTextureLoaderStore(Files.Store)), beatmapInfo, audioManager));
                }

                previous?.TransferTo(working);
                return working;
            }
        }

        /// <summary>
        /// Perform a lookup query on available <see cref="BeatmapSetInfo"/>s.
        /// </summary>
        /// <param name="query">The query.</param>
        /// <returns>The first result for the provided query, or null if no results were found.</returns>
        public BeatmapSetInfo QueryBeatmapSet(Expression<Func<BeatmapSetInfo, bool>> query) => beatmaps.ConsumableItems.AsNoTracking().FirstOrDefault(query);

        protected override bool CanUndelete(BeatmapSetInfo existing, BeatmapSetInfo import)
        {
            if (!base.CanUndelete(existing, import))
                return false;

            var existingIds = existing.Beatmaps.Select(b => b.OnlineBeatmapID).OrderBy(i => i);
            var importIds = import.Beatmaps.Select(b => b.OnlineBeatmapID).OrderBy(i => i);

            // force re-import if we are not in a sane state.
            return existing.OnlineBeatmapSetID == import.OnlineBeatmapSetID && existingIds.SequenceEqual(importIds);
        }

        /// <summary>
        /// Returns a list of all usable <see cref="BeatmapSetInfo"/>s.
        /// </summary>
        /// <returns>A list of available <see cref="BeatmapSetInfo"/>.</returns>
        public List<BeatmapSetInfo> GetAllUsableBeatmapSets(IncludedDetails includes = IncludedDetails.All) => GetAllUsableBeatmapSetsEnumerable(includes).ToList();

        /// <summary>
        /// Returns a list of all usable <see cref="BeatmapSetInfo"/>s. Note that files are not populated.
        /// </summary>
        /// <param name="includes">The level of detail to include in the returned objects.</param>
        /// <returns>A list of available <see cref="BeatmapSetInfo"/>.</returns>
        public IEnumerable<BeatmapSetInfo> GetAllUsableBeatmapSetsEnumerable(IncludedDetails includes)
        {
            IQueryable<BeatmapSetInfo> queryable;

            switch (includes)
            {
                case IncludedDetails.Minimal:
                    queryable = beatmaps.BeatmapSetsOverview;
                    break;

                case IncludedDetails.AllButFiles:
                    queryable = beatmaps.BeatmapSetsWithoutFiles;
                    break;

                default:
                    queryable = beatmaps.ConsumableItems;
                    break;
            }

            // AsEnumerable used here to avoid applying the WHERE in sql. When done so, ef core 2.x uses an incorrect ORDER BY
            // clause which causes queries to take 5-10x longer.
            // TODO: remove if upgrading to EF core 3.x.
            return queryable.AsEnumerable().Where(s => !s.DeletePending && !s.Protected);
        }

        /// <summary>
        /// Perform a lookup query on available <see cref="BeatmapSetInfo"/>s.
        /// </summary>
        /// <param name="query">The query.</param>
        /// <returns>Results from the provided query.</returns>
        public IEnumerable<BeatmapSetInfo> QueryBeatmapSets(Expression<Func<BeatmapSetInfo, bool>> query) => beatmaps.ConsumableItems.AsNoTracking().Where(query);

        /// <summary>
        /// Perform a lookup query on available <see cref="BeatmapInfo"/>s.
        /// </summary>
        /// <param name="query">The query.</param>
        /// <returns>The first result for the provided query, or null if no results were found.</returns>
        public BeatmapInfo QueryBeatmap(Expression<Func<BeatmapInfo, bool>> query) => beatmaps.Beatmaps.AsNoTracking().FirstOrDefault(query);

        /// <summary>
        /// Perform a lookup query on available <see cref="BeatmapInfo"/>s.
        /// </summary>
        /// <param name="query">The query.</param>
        /// <returns>Results from the provided query.</returns>
        public IQueryable<BeatmapInfo> QueryBeatmaps(Expression<Func<BeatmapInfo, bool>> query) => beatmaps.Beatmaps.AsNoTracking().Where(query);

        protected override string HumanisedModelName => "beatmap";

        protected override BeatmapSetInfo CreateModel(ArchiveReader reader)
        {
            // let's make sure there are actually .osu files to import.
            string mapName = reader.Filenames.FirstOrDefault(f => f.EndsWith(".osu"));

            if (string.IsNullOrEmpty(mapName))
            {
                Logger.Log($"No beatmap files found in the beatmap archive ({reader.Name}).", LoggingTarget.Database);
                return null;
            }

            Beatmap beatmap;
            using (var stream = new LineBufferedReader(reader.GetStream(mapName)))
                beatmap = Decoder.GetDecoder<Beatmap>(stream).Decode(stream);

            return new BeatmapSetInfo
            {
                OnlineBeatmapSetID = beatmap.BeatmapInfo.BeatmapSet?.OnlineBeatmapSetID,
                Beatmaps = new List<BeatmapInfo>(),
                Metadata = beatmap.Metadata,
                DateAdded = DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Create all required <see cref="BeatmapInfo"/>s for the provided archive.
        /// </summary>
        private List<BeatmapInfo> createBeatmapDifficulties(List<BeatmapSetFileInfo> files)
        {
            var beatmapInfos = new List<BeatmapInfo>();

            foreach (var file in files.Where(f => f.Filename.EndsWith(".osu")))
            {
                using (var raw = Files.Store.GetStream(file.FileInfo.StoragePath))
                using (var ms = new MemoryStream()) // we need a memory stream so we can seek
                using (var sr = new LineBufferedReader(ms))
                {
                    raw.CopyTo(ms);
                    ms.Position = 0;

                    var decoder = Decoder.GetDecoder<Beatmap>(sr);
                    IBeatmap beatmap = decoder.Decode(sr);

                    string hash = ms.ComputeSHA2Hash();

                    if (beatmapInfos.Any(b => b.Hash == hash))
                        continue;

                    beatmap.BeatmapInfo.Path = file.Filename;
                    beatmap.BeatmapInfo.Hash = hash;
                    beatmap.BeatmapInfo.MD5Hash = ms.ComputeMD5Hash();

                    var ruleset = rulesets.GetRuleset(beatmap.BeatmapInfo.RulesetID);
                    beatmap.BeatmapInfo.Ruleset = ruleset;

                    // TODO: this should be done in a better place once we actually need to dynamically update it.
                    beatmap.BeatmapInfo.StarDifficulty = ruleset?.CreateInstance().CreateDifficultyCalculator(new DummyConversionBeatmap(beatmap)).Calculate().StarRating ?? 0;
                    beatmap.BeatmapInfo.Length = calculateLength(beatmap);
                    beatmap.BeatmapInfo.BPM = beatmap.ControlPointInfo.BPMMode;

                    beatmapInfos.Add(beatmap.BeatmapInfo);
                }
            }

            return beatmapInfos;
        }

        private double calculateLength(IBeatmap b)
        {
            if (!b.HitObjects.Any())
                return 0;

            var lastObject = b.HitObjects.Last();

            //TODO: this isn't always correct (consider mania where a non-last object may last for longer than the last in the list).
            double endTime = lastObject.GetEndTime();
            double startTime = b.HitObjects.First().StartTime;

            return endTime - startTime;
        }

        /// <summary>
        /// A dummy WorkingBeatmap for the purpose of retrieving a beatmap for star difficulty calculation.
        /// </summary>
        private class DummyConversionBeatmap : WorkingBeatmap
        {
            private readonly IBeatmap beatmap;

            public DummyConversionBeatmap(IBeatmap beatmap)
                : base(beatmap.BeatmapInfo, null)
            {
                this.beatmap = beatmap;
            }

            protected override IBeatmap GetBeatmap() => beatmap;
            protected override Texture GetBackground() => null;
            protected override Track GetTrack() => null;
        }
    }

    /// <summary>
    /// The level of detail to include in database results.
    /// </summary>
    public enum IncludedDetails
    {
        /// <summary>
        /// Only include beatmap difficulties and set level metadata.
        /// </summary>
        Minimal,

        /// <summary>
        /// Include all difficulties, rulesets, difficulty metadata but no files.
        /// </summary>
        AllButFiles,

        /// <summary>
        /// Include everything.
        /// </summary>
        All
    }
}
