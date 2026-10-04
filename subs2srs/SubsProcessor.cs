//  Copyright (C) 2009-2016 Christopher Brochtrup
//  Copyright (C) 2026 fkzys and contributors
//
//  This file is part of subs2srs.
//
//  subs2srs is free software: you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  subs2srs is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with subs2srs.  If not, see <http://www.gnu.org/licenses/>.
//
//////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace subs2srs
{
    class SubsProcessor
    {
        private DateTime workerStartTime;
        private int currentStep = 0;
        private string currentStepName = "";
        private int[] cardsPerEpisode = Array.Empty<int>();
        private string? importFile;

        /// <summary>
        /// A step returned null or false. The workers do that when the user cancelled, and the
        /// audio worker also when it failed (after showing why, which it passes here).
        /// </summary>
        private sealed class StepStoppedException : Exception
        {
            public StepStoppedException(string? error = null)
                : base(error ?? "it stopped without an error message") { }
        }

        /// <summary>
        /// Run the whole pipeline. When the preview already parsed and filtered the
        /// lines, pass its <paramref name="combinedAll"/> (and the join vectors it
        /// edited, <paramref name="joins"/>) so the user's edits are what gets generated.
        /// Returns how the run ended. The dialogs it shows through <see cref="UtilsMsg"/>
        /// are the GUI's as before: there a step that stopped reads "Action cancelled."
        /// whether the user cancelled or it failed; the result tells the two apart.
        /// </summary>
        public async Task<PipelineResult> StartAsync(IProgressReporter dialogProgress,
            List<List<InfoCombined>> combinedAll = null, List<bool[]> joins = null)
        {
            UtilsCommon.RegisterEncodings();

            try { createOutputDirStructure(); }
            catch (Exception ex)
            {
                UtilsMsg.showErrMsg("Cannot write to output directory.");
                return new PipelineResult(PipelineStatus.Failed,
                    oneLine("Cannot write to output directory. " + ex.Message));
            }
        
            Logger.Instance.info("SubsProcessor.start");
            Logger.Instance.writeSettingsToLog();
        
            WorkerVars workerVars = new WorkerVars(combinedAll,
                getMediaDir(Settings.Instance.OutputDir, Settings.Instance.DeckName),
                WorkerVars.SubsProcessingType.Normal);
            workerVars.Joins = joins;
            this.currentStep = 0;
            this.currentStepName = "";
            this.cardsPerEpisode = Array.Empty<int>();
            this.importFile = null;
            dialogProgress.StepsTotal = determineNumSteps(workerVars);
            this.workerStartTime = DateTime.Now;
        
            try
            {
                await Task.Run(() => DoWork(workerVars, dialogProgress));
            
                TimeSpan workerTotalTime = DateTime.Now - this.workerStartTime;
                string srsFormat = getSrsFormatList();
                string doneMessage = String.Format(
                    "Processing completed in {0:0.00} minutes.",
                    workerTotalTime.TotalMinutes);
                UtilsMsg.showInfoMsg(doneMessage + "\n\n" + srsFormat);
                return new PipelineResult(PipelineStatus.Completed, doneMessage, this.cardsPerEpisode, this.importFile);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException || ex is StepStoppedException)
                    UtilsMsg.showErrMsg("Action cancelled.");
                else
                    UtilsMsg.showErrMsg($"Error: {ex.Message}\n\n{ex.StackTrace}");

                // Only the reporter knows whether the user cancelled: a worker stops the same
                // way when it failed, and a cancelled ffmpeg can make a step throw.
                if (dialogProgress.Cancel || dialogProgress.Token.IsCancellationRequested)
                    return new PipelineResult(PipelineStatus.Cancelled, "Action cancelled.", this.cardsPerEpisode, this.importFile);

                return new PipelineResult(PipelineStatus.Failed,
                    oneLine($"{this.currentStepName} failed: {failureDetail(ex)}"), this.cardsPerEpisode, this.importFile);
            }
        }

        /// <summary>
        /// What stopped a step: the exception's message, or the first error of a step
        /// that works in parallel.
        /// </summary>
        private static string failureDetail(Exception ex)
        {
            if (ex is AggregateException aggregate)
            {
                var inner = aggregate.Flatten().InnerExceptions;
                if (inner.Count > 0) return inner[0].Message;
            }

            return ex.Message;
        }

        private static string oneLine(string message)
        {
            return String.Join(" ", message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>Show the next step's label and remember it for a failure message.</summary>
        private void nextStep(IProgressReporter dialogProgress, string description)
        {
            this.currentStepName = description;
            dialogProgress.NextStep(++currentStep, description);
        }

        private void DoWork(WorkerVars workerVars, IProgressReporter dialogProgress)
        {
            List<List<InfoCombined>> combinedAll = new List<List<InfoCombined>>();
            WorkerSubs subsWorker = new WorkerSubs();
            int totalLines = 0;
            bool needToGenerateCombinedAll = (workerVars.CombinedAll == null);


            if (needToGenerateCombinedAll)
            {
                nextStep(dialogProgress, "Combine subs");
                combinedAll = subsWorker.combineAllSubs(workerVars, dialogProgress);

                if (combinedAll != null) workerVars.CombinedAll = combinedAll;
                else throw new StepStoppedException();

                foreach (List<InfoCombined> combArray in workerVars.CombinedAll)
                    totalLines += combArray.Count;

                if (totalLines == 0)
                    throw new Exception("No lines of dialog could be parsed from the subtitle files.\nPlease check that they are valid.");

                nextStep(dialogProgress, "Inactivate lines");
                combinedAll = subsWorker.inactivateLines(workerVars, dialogProgress);

                if (combinedAll != null) workerVars.CombinedAll = combinedAll;
                else throw new StepStoppedException();
            }

            if (WorkerSubs.aiGroupingOnGoApplies(workerVars))
            {
                nextStep(dialogProgress, "AI grouping");
                combinedAll = subsWorker.runAiGrouping(workerVars, dialogProgress);

                if (combinedAll != null) workerVars.CombinedAll = combinedAll;
                else throw new StepStoppedException();
            }

            // Runs whether the lines came from the preview (reusing its join vectors)
            // or were just generated (deriving them from the settings).
            nextStep(dialogProgress, "Group into snippets");
            combinedAll = subsWorker.groupIntoSnippets(workerVars, dialogProgress);

            if (combinedAll != null) workerVars.CombinedAll = combinedAll;
            else throw new StepStoppedException();

            if ((Settings.Instance.ContextLeadingCount > 0) || (Settings.Instance.ContextTrailingCount > 0))
            {
                nextStep(dialogProgress, "Find context lines");
                combinedAll = subsWorker.markLinesOnlyNeededForContext(workerVars, dialogProgress);

                if (combinedAll != null) workerVars.CombinedAll = combinedAll;
                else throw new StepStoppedException();
            }

            nextStep(dialogProgress, "Remove inactive lines");
            combinedAll = subsWorker.removeInactiveLines(workerVars, dialogProgress, true);

            if (combinedAll != null) workerVars.CombinedAll = combinedAll;
            else throw new StepStoppedException();

            // One TSV line per card; lines kept only as a neighbour's context get none.
            this.cardsPerEpisode = workerVars.CombinedAll
                .Select(episode => episode.Count(comb => !comb.OnlyNeededForContext))
                .ToArray();

            totalLines = 0;
            foreach (List<InfoCombined> combArray in workerVars.CombinedAll)
                totalLines += combArray.Count;

            if (totalLines == 0)
                throw new Exception("No lines will be processed. Please check your settings to make\nsure that you are not mistakenly pruning too many lines.");

            try
            {
                if (!needToGenerateCombinedAll)
                {
                    if (!subsWorker.copyVobsubsFromPreviewDirToMediaDir(workerVars, dialogProgress))
                        throw new StepStoppedException();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (StepStoppedException) { throw; }
            catch (Exception ex) { Logger.Instance.info($"VobSub copy failed: {ex.Message}"); }

            nextStep(dialogProgress, "Generate import file");
            WorkerSrs srsWorker = new WorkerSrs();

            try
            {
                if (!srsWorker.genSrs(workerVars, dialogProgress))
                    throw new StepStoppedException();
            }
            finally
            {
                // Also when it stopped part-way: the file is there, partly written.
                this.importFile = srsWorker.ImportFile;
            }

            List<List<InfoCombined>> combinedAllWithContext = ObjectCopier.Clone<List<List<InfoCombined>>>(workerVars.CombinedAll);

            if (Settings.Instance.AudioClips.Enabled)
            {
                nextStep(dialogProgress, "Generate audio clips");

                if (((Settings.Instance.ContextLeadingCount > 0) && Settings.Instance.ContextLeadingIncludeAudioClips) || ((Settings.Instance.ContextTrailingCount > 0) && Settings.Instance.ContextTrailingIncludeAudioClips))
                    workerVars.CombinedAll = combinedAllWithContext;
                else
                    workerVars.CombinedAll = subsWorker.removeContextOnlyLines(combinedAllWithContext);

                WorkerAudio audioWorker = new WorkerAudio();
                if (!audioWorker.genAudioClip(workerVars, dialogProgress)) throw new StepStoppedException(audioWorker.Error);
            }

            if (Settings.Instance.Snapshots.Enabled)
            {
                nextStep(dialogProgress, "Generate snapshots");

                if (((Settings.Instance.ContextLeadingCount > 0) && Settings.Instance.ContextLeadingIncludeSnapshots) || ((Settings.Instance.ContextTrailingCount > 0) && Settings.Instance.ContextTrailingIncludeSnapshots))
                    workerVars.CombinedAll = combinedAllWithContext;
                else
                    workerVars.CombinedAll = subsWorker.removeContextOnlyLines(combinedAllWithContext);

                WorkerSnapshot snapshotWorker = new WorkerSnapshot();
                if (!snapshotWorker.genSnapshots(workerVars, dialogProgress)) throw new StepStoppedException();
            }

            if (Settings.Instance.AnimatedSnapshots.Enabled)
            {
                nextStep(dialogProgress, "Generate animated snapshots");

                // Same context rules as the still snapshots.
                if (((Settings.Instance.ContextLeadingCount > 0) && Settings.Instance.ContextLeadingIncludeSnapshots) || ((Settings.Instance.ContextTrailingCount > 0) && Settings.Instance.ContextTrailingIncludeSnapshots))
                    workerVars.CombinedAll = combinedAllWithContext;
                else
                    workerVars.CombinedAll = subsWorker.removeContextOnlyLines(combinedAllWithContext);

                WorkerAnimatedSnapshot animatedWorker = new WorkerAnimatedSnapshot();
                if (!animatedWorker.genAnimatedSnapshots(workerVars, dialogProgress)) throw new StepStoppedException();
            }

            if (Settings.Instance.VideoClips.Enabled)
            {
                nextStep(dialogProgress, "Generate video clips");

                if (((Settings.Instance.ContextLeadingCount > 0) && Settings.Instance.ContextLeadingIncludeVideoClips) || ((Settings.Instance.ContextTrailingCount > 0) && Settings.Instance.ContextTrailingIncludeVideoClips))
                    workerVars.CombinedAll = combinedAllWithContext;
                else
                    workerVars.CombinedAll = subsWorker.removeContextOnlyLines(combinedAllWithContext);

                WorkerVideo videoWorker = new WorkerVideo();
                if (!videoWorker.genVideoClip(workerVars, dialogProgress)) throw new StepStoppedException();
            }
        }

        private void createOutputDirStructure()
        {
            Directory.CreateDirectory(getMediaDir(Settings.Instance.OutputDir, Settings.Instance.DeckName));
        }

        internal static string getMediaDir(string outDir, string deckName)
        {
            return string.Format(@"{0}{1}{2}.media", outDir, Path.DirectorySeparatorChar, deckName);
        }

        private string getSrsFormatList()
        {
            string srsFormat = "Format of the Anki import file: \n";
            int listNum = 1;

            if (ConstantSettings.SrsTagFormat != "") { srsFormat += "\n" + listNum.ToString() + ") Tag"; listNum++; }
            if (ConstantSettings.SrsSequenceMarkerFormat != "") { srsFormat += "\n" + listNum.ToString() + ") Sequence Marker"; listNum++; }
            if (Settings.Instance.AudioClips.Enabled) { srsFormat += "\n" + listNum.ToString() + ") Audio clip"; listNum++; }
            if (Settings.Instance.Snapshots.Enabled) { srsFormat += "\n" + listNum.ToString() + ") Snapshot"; listNum++; }
            if (Settings.Instance.AnimatedSnapshots.Enabled) { srsFormat += "\n" + listNum.ToString() + ") Animated snapshot"; listNum++; }
            if (Settings.Instance.VideoClips.Enabled) { srsFormat += "\n" + listNum.ToString() + ") Video clip"; listNum++; }
            
            srsFormat += "\n" + listNum.ToString() + ") Line from Subs1"; listNum++;
            if (Settings.Instance.Subs[1].FilePattern != "") srsFormat += "\n" + listNum.ToString() + ") Line from Subs2";

            if (Settings.Instance.ContextLeadingCount > 0) srsFormat += "\n+ " + Settings.Instance.ContextLeadingCount + " leading line" + (Settings.Instance.ContextLeadingCount == 1 ? "" : "s");
            if (Settings.Instance.ContextTrailingCount > 0) srsFormat += "\n+ " + Settings.Instance.ContextTrailingCount + " trailing line" + (Settings.Instance.ContextTrailingCount == 1 ? "" : "s");

            return srsFormat;
        }

        private int determineNumSteps(WorkerVars workerVars)
        {
            int numSteps = workerVars.CombinedAll == null ? 5 : 3;
            if (WorkerSubs.aiGroupingOnGoApplies(workerVars)) numSteps++;
            if ((Settings.Instance.ContextLeadingCount > 0) || (Settings.Instance.ContextTrailingCount > 0)) numSteps++;
            if (Settings.Instance.AudioClips.Enabled) numSteps++;
            if (Settings.Instance.Snapshots.Enabled) numSteps++;
            if (Settings.Instance.AnimatedSnapshots.Enabled) numSteps++;
            if (Settings.Instance.VideoClips.Enabled) numSteps++;
            return numSteps;
        }
    }
}
