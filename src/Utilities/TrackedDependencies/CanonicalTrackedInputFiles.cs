﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Build.Framework;
using System.IO;
using Microsoft.Build.Shared;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics.CodeAnalysis;

namespace Microsoft.Build.Utilities
{
    /// <summary>
    /// This class is the filetracking log interpreter for .read. tracking logs in canonical form
    /// or those that have been rooted (^) to make them canonical
    /// </summary>
    public class CanonicalTrackedInputFiles
    {
        #region Member Data
        // The most recently modified output time
        private DateTime _outputNewestTime = DateTime.MinValue;
        // The most recently modified output
        private string _outputNewest = "";
        // The table of dependencies
        private Dictionary<string, Dictionary<string, string>> _dependencyTable;
        // The .read. tracking log files
        private ITaskItem[] _tlogFiles;
        // Primary source files
        private ITaskItem[] _sourceFiles;
        // The TaskLoggingHelper that we log progress to
        private TaskLoggingHelper _log;
        // Sources needing compilation
        private ITaskItem[] _sourcesNeedingCompilation = null;
        // The output graph
        private CanonicalTrackedOutputFiles _outputs;
        // Output files for all sources in the current set as a group
        private ITaskItem[] _outputFileGroup;
        // Output files that are manually specified
        private ITaskItem[] _outputFiles;
        // Use minimal rebuild optimization (WARNING: this may cause underbuild)
        private bool _useMinimalRebuildOptimization = false;
        // Are the tracking logs that we were constructed with actually available
        private bool _tlogAvailable;
        // Do we want to keep composite rooting markers around (many-to-one case) or
        // shred them (one-to-one or one-to-many case)
        private bool _maintainCompositeRootingMarkers = false;
        // The set of paths that contain files that are to be ignored during up to date check
        private HashSet<string> _excludedInputPaths = new HashSet<string>(StringComparer.Ordinal);
        // Cache of last write times
        private ConcurrentDictionary<string, DateTime> _lastWriteTimeCache = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        #endregion

        #region Properties

        // This is provided to facilitate unit testing
        internal ITaskItem[] SourcesNeedingCompilation
        {
            get { return _sourcesNeedingCompilation; }
            set { _sourcesNeedingCompilation = value; }
        }

        // Provide external access to the dependencyTable
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        public Dictionary<string, Dictionary<string, string>> DependencyTable
        {
            get { return _dependencyTable; }
        }
        #endregion

        #region Constructors
        /// <summary>
        /// Constructor for multiple input source files
        /// </summary>
        /// <param name="tlogFiles">The .read. tlog files to interpret</param>
        /// <param name="sourceFiles">The primary source files to interpret dependencies for</param>
        /// <param name="outputs">The output files produced by compiling this set of sources</param>
        /// <param name="useMinimalRebuildOptimization">WARNING: Minimal rebuild optimization requires 100% accurate computed outputs to be specified!</param>
        /// <param name="maintainCompositeRootingMarkers">True to keep composite rooting markers around (many-to-one case) or false to shred them (one-to-one or one-to-many case)</param>
        public CanonicalTrackedInputFiles(ITaskItem[] tlogFiles, ITaskItem[] sourceFiles, CanonicalTrackedOutputFiles outputs, bool useMinimalRebuildOptimization, bool maintainCompositeRootingMarkers)
        {
            InternalConstruct(null, tlogFiles, sourceFiles, null, null, outputs, useMinimalRebuildOptimization, maintainCompositeRootingMarkers);
        }

        /// <summary>
        /// Constructor for multiple input source files
        /// </summary>
        /// <param name="tlogFiles">The .read. tlog files to interpret</param>
        /// <param name="sourceFiles">The primary source files to interpret dependencies for</param>
        /// <param name="excludedInputPaths">The set of paths that contain files that are to be ignored during up to date check</param>
        /// <param name="outputs">The output files produced by compiling this set of sources</param>
        /// <param name="useMinimalRebuildOptimization">WARNING: Minimal rebuild optimization requires 100% accurate computed outputs to be specified!</param>
        /// <param name="maintainCompositeRootingMarkers">True to keep composite rooting markers around (many-to-one case) or false to shred them (one-to-one or one-to-many case)</param>
        public CanonicalTrackedInputFiles(ITaskItem[] tlogFiles, ITaskItem[] sourceFiles, ITaskItem[] excludedInputPaths, CanonicalTrackedOutputFiles outputs, bool useMinimalRebuildOptimization, bool maintainCompositeRootingMarkers)
        {
            InternalConstruct(null, tlogFiles, sourceFiles, null, excludedInputPaths, outputs, useMinimalRebuildOptimization, maintainCompositeRootingMarkers);
        }

        /// <summary>
        /// Constructor for multiple input source files
        /// </summary>
        /// <param name="ownerTask">The task that is using file tracker</param>
        /// <param name="tlogFiles">The .read. tlog files to interpret</param>
        /// <param name="sourceFiles">The primary source files to interpret dependencies for</param>
        /// <param name="excludedInputPaths">The set of paths that contain files that are to be ignored during up to date check</param>
        /// <param name="outputs">The output files produced by compiling this set of sources</param>
        /// <param name="useMinimalRebuildOptimization">WARNING: Minimal rebuild optimization requires 100% accurate computed outputs to be specified!</param>
        /// <param name="maintainCompositeRootingMarkers">True to keep composite rooting markers around (many-to-one case) or false to shred them (one-to-one or one-to-many case)</param>
        public CanonicalTrackedInputFiles(ITask ownerTask, ITaskItem[] tlogFiles, ITaskItem[] sourceFiles, ITaskItem[] excludedInputPaths, CanonicalTrackedOutputFiles outputs, bool useMinimalRebuildOptimization, bool maintainCompositeRootingMarkers)
        {
            InternalConstruct(ownerTask, tlogFiles, sourceFiles, null, excludedInputPaths, outputs, useMinimalRebuildOptimization, maintainCompositeRootingMarkers);
        }

        /// <summary>
        /// Constructor for multiple input source files
        /// </summary>
        /// <param name="ownerTask">The task that is using file tracker</param>
        /// <param name="tlogFiles">The .read. tlog files to interpret</param>
        /// <param name="sourceFiles">The primary source files to interpret dependencies for</param>
        /// <param name="excludedInputPaths">The set of paths that contain files that are to be ignored during up to date check</param>
        /// <param name="outputs">The output files produced by compiling this set of sources</param>
        /// <param name="useMinimalRebuildOptimization">WARNING: Minimal rebuild optimization requires 100% accurate computed outputs to be specified!</param>
        /// <param name="maintainCompositeRootingMarkers">True to keep composite rooting markers around (many-to-one case) or false to shred them (one-to-one or one-to-many case)</param>
        public CanonicalTrackedInputFiles(ITask ownerTask, ITaskItem[] tlogFiles, ITaskItem[] sourceFiles, ITaskItem[] excludedInputPaths, ITaskItem[] outputs, bool useMinimalRebuildOptimization, bool maintainCompositeRootingMarkers)
        {
            InternalConstruct(ownerTask, tlogFiles, sourceFiles, outputs, excludedInputPaths, null, useMinimalRebuildOptimization, maintainCompositeRootingMarkers);
        }

        /// <summary>
        /// Constructor for a single input source file
        /// </summary>
        /// <param name="ownerTask">The task that is using file tracker</param>
        /// <param name="tlogFiles">The .read. tlog files to interpret</param>
        /// <param name="sourceFile">The primary source file to interpret dependencies for</param>
        /// <param name="excludedInputPaths">The set of paths that contain files that are to be ignored during up to date check</param>
        /// <param name="outputs">The output files produced by compiling this source</param>
        /// <param name="useMinimalRebuildOptimization">WARNING: Minimal rebuild optimization requires 100% accurate computed outputs to be specified!</param>
        /// <param name="maintainCompositeRootingMarkers">True to keep composite rooting markers around (many-to-one case) or false to shred them (one-to-one or one-to-many case)</param>
        public CanonicalTrackedInputFiles(ITask ownerTask, ITaskItem[] tlogFiles, ITaskItem sourceFile, ITaskItem[] excludedInputPaths, CanonicalTrackedOutputFiles outputs, bool useMinimalRebuildOptimization, bool maintainCompositeRootingMarkers)
        {
            ITaskItem[] sourceFiles = new ITaskItem[] { sourceFile };
            InternalConstruct(ownerTask, tlogFiles, sourceFiles, null, excludedInputPaths, outputs, useMinimalRebuildOptimization, maintainCompositeRootingMarkers);
        }

        /// <summary>
        /// Common internal constructor
        /// </summary>
        /// <param name="ownerTask">The task that is using file tracker</param>
        /// <param name="tlogFiles">The .read. tlog files to interpret</param>
        /// <param name="sourceFiles">The primary source files to interpret dependencies for</param>
        /// <param name="outputs">The output files produced by compiling this set of sources</param>
        /// <param name="excludedInputPaths">The set of paths that contain files that are to be ignored during up to date check</param>
        /// <param name="useMinimalRebuildOptimization">WARNING: Minimal rebuild optimization requires 100% accurate computed outputs to be specified!</param>
        /// <param name="maintainCompositeRootingMarkers">True to keep composite rooting markers around (many-to-one case) or false to shred them (one-to-one or one-to-many case)</param>
        private void InternalConstruct(ITask ownerTask, ITaskItem[] tlogFiles, ITaskItem[] sourceFiles, ITaskItem[] outputFiles, ITaskItem[] excludedInputPaths, CanonicalTrackedOutputFiles outputs, bool useMinimalRebuildOptimization, bool maintainCompositeRootingMarkers)
        {
            if (ownerTask != null)
            {
                _log = new TaskLoggingHelper(ownerTask);
                _log.TaskResources = AssemblyResources.PrimaryResources;
                _log.HelpKeywordPrefix = "MSBuild.";
            }

            _tlogFiles = TrackedDependencies.ExpandWildcards(tlogFiles);
            _tlogAvailable = TrackedDependencies.ItemsExist(_tlogFiles);
            _sourceFiles = sourceFiles;
            _outputs = outputs;
            _outputFiles = outputFiles;
            _useMinimalRebuildOptimization = useMinimalRebuildOptimization;
            _maintainCompositeRootingMarkers = maintainCompositeRootingMarkers;

            if (excludedInputPaths != null)
            {
                // Assign our exclude paths to our lookup
                foreach (ITaskItem excludePath in excludedInputPaths)
                {
                    string fullexcludePath = FileUtilities.EnsureNoTrailingSlash(FileUtilities.NormalizePath(excludePath.ItemSpec)).ToUpperInvariant();
                    _excludedInputPaths.Add(fullexcludePath);
                }
            }

            _dependencyTable = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (_tlogFiles != null)
            {
                ConstructDependencyTable();
            }
        }
        #endregion

        #region Methods
        /// <summary>
        /// This method computes the sources that need to be compiled based on the output files and the
        /// full dependency graph of inputs
        /// </summary>
        /// <returns>Array of files that need to be compiled</returns>
        public ITaskItem[] ComputeSourcesNeedingCompilation()
        {
            return ComputeSourcesNeedingCompilation(true);
        }

        /// <summary>
        /// This method computes the sources that need to be compiled based on the output files and the
        /// full dependency graph of inputs, optionally searching composite rooting markers
        /// for subroots that may contain input files
        /// </summary>
        /// <returns>Array of files that need to be compiled</returns>
        public ITaskItem[] ComputeSourcesNeedingCompilation(bool searchForSubRootsInCompositeRootingMarkers)
        {
            if (_outputFiles != null)
            {
                _outputFileGroup = _outputFiles;
            }
            else if (_sourceFiles != null && _outputs != null && _maintainCompositeRootingMarkers)
            {
                _outputFileGroup = _outputs.OutputsForSource(_sourceFiles, searchForSubRootsInCompositeRootingMarkers);
            }
            else if (_sourceFiles != null && _outputs != null)
            {
                _outputFileGroup = _outputs.OutputsForNonCompositeSource(_sourceFiles);
            }

            if (_maintainCompositeRootingMarkers)
            {
                return ComputeSourcesNeedingCompilationFromCompositeRootingMarker(searchForSubRootsInCompositeRootingMarkers);
            }
            else
            {
                return ComputeSourcesNeedingCompilationFromPrimaryFiles();
            }
        }

        /// <summary>
        /// This method computes the sources that need to be compiled based on the output files and the
        /// full dependency graph of inputs, making the assumption that the source files are all primary
        /// files -- ie. there is either a one-to-one or a one-to-many correspondence between inputs
        /// and outputs
        /// </summary>
        /// <returns>Array of files that need to be compiled</returns>
        internal ITaskItem[] ComputeSourcesNeedingCompilationFromPrimaryFiles()
        {
            if (_sourcesNeedingCompilation == null)
            {
                ConcurrentQueue<ITaskItem> sourcesNeedingCompilationList = new ConcurrentQueue<ITaskItem>();
                bool allOutputFilesExist = false;

                if (_tlogAvailable)
                {
                    if (!_useMinimalRebuildOptimization)
                    {
                        allOutputFilesExist = FilesExistAndRecordNewestWriteTime(_outputFileGroup);
                    }
                }

                // If the TLOG file is not available, or not up to date then add source to sourcesNeedingCompilationList
                Parallel.For(0, _sourceFiles.Length, (index) =>
                {
                    CheckIfSourceNeedsCompilation(sourcesNeedingCompilationList, allOutputFilesExist, _sourceFiles[index]);
                });
                _sourcesNeedingCompilation = sourcesNeedingCompilationList.ToArray();
            }

            if (_sourcesNeedingCompilation.Length == 0)
            {
                FileTracker.LogMessageFromResources(_log, MessageImportance.Normal, "Tracking_AllOutputsAreUpToDate");
                _sourcesNeedingCompilation = new ITaskItem[0];
            }
            else
            {
                Array.Sort(_sourcesNeedingCompilation, new Comparison<ITaskItem>(CompareTaskItems));
                foreach (ITaskItem compileSource in _sourcesNeedingCompilation)
                {
                    string modifiedPath = compileSource.GetMetadata("_trackerModifiedPath");
                    string modifiedTime = compileSource.GetMetadata("_trackerModifiedTime");
                    string outputFilePath = compileSource.GetMetadata("_trackerOutputFile");
                    string trackerCompileReason = compileSource.GetMetadata("_trackerCompileReason");

                    if (String.Equals(trackerCompileReason, "Tracking_SourceWillBeCompiledDependencyWasModifiedAt", StringComparison.Ordinal))
                    {
                        FileTracker.LogMessageFromResources(_log, MessageImportance.Low, trackerCompileReason, compileSource.ItemSpec, modifiedPath, modifiedTime);
                    }
                    else if (String.Equals(trackerCompileReason, "Tracking_SourceWillBeCompiledMissingDependency", StringComparison.Ordinal))
                    {
                        FileTracker.LogMessageFromResources(_log, MessageImportance.Low, trackerCompileReason, compileSource.ItemSpec, modifiedPath);
                    }
                    else if (String.Equals(trackerCompileReason, "Tracking_SourceWillBeCompiledOutputDoesNotExist", StringComparison.Ordinal))
                    {
                        FileTracker.LogMessageFromResources(_log, MessageImportance.Low, trackerCompileReason, compileSource.ItemSpec, outputFilePath);
                    }
                    else
                    {
                        FileTracker.LogMessageFromResources(_log, MessageImportance.Low, trackerCompileReason, compileSource.ItemSpec);
                    }

                    // Now zero out the metadata that was set, so that it doesn't show up if these items
                    // flow through the task
                    compileSource.RemoveMetadata("_trackerModifiedPath");
                    compileSource.RemoveMetadata("_trackerModifiedTime");
                    compileSource.RemoveMetadata("_trackerOutputFile");
                    compileSource.RemoveMetadata("_trackerCompileReason");
                }
            }

            return _sourcesNeedingCompilation;
        }

        /// <summary>
        /// Check to see if the source specified needs compilation relative to its outputs
        /// </summary>
        private void CheckIfSourceNeedsCompilation(ConcurrentQueue<ITaskItem> sourcesNeedingCompilationList, bool allOutputFilesExist, ITaskItem source)
        {
            if ((!_tlogAvailable) || (_outputFileGroup == null))
            {
                source.SetMetadata("_trackerCompileReason", "Tracking_SourceWillBeCompiledAsNoTrackingLog");
                sourcesNeedingCompilationList.Enqueue(source);
            }
            else if (!_useMinimalRebuildOptimization && !allOutputFilesExist)
            {
                source.SetMetadata("_trackerCompileReason", "Tracking_SourceOutputsNotAvailable");
                sourcesNeedingCompilationList.Enqueue(source);
            }
            else if (!IsUpToDate(source))
            {
                if (String.IsNullOrEmpty(source.GetMetadata("_trackerCompileReason")))
                {
                    source.SetMetadata("_trackerCompileReason", "Tracking_SourceWillBeCompiled");
                }

                sourcesNeedingCompilationList.Enqueue(source);
            }
            else if (!_useMinimalRebuildOptimization && _outputNewestTime == DateTime.MinValue)
            {
                source.SetMetadata("_trackerCompileReason", "Tracking_SourceNotInTrackingLog");
                sourcesNeedingCompilationList.Enqueue(source);
            }
        }

        /// <summary>
        /// A very simple comparer for TaskItems so that up to date check results can be sorted.
        /// </summary>
        private int CompareTaskItems(ITaskItem left, ITaskItem right)
        {
            return String.Compare(left.ItemSpec, right.ItemSpec, StringComparison.Ordinal);
        }

        /// <summary>
        /// This method computes the sources that need to be compiled based on the output files and the
        /// full dependency graph of inputs, making the assumption that the source files are the components
        /// of a composite rooting marker, as in the case where there is a many-to-one correspondence
        /// between inputs and outputs.
        /// </summary>
        /// <returns>Array of files that need to be compiled</returns>
        internal ITaskItem[] ComputeSourcesNeedingCompilationFromCompositeRootingMarker(bool searchForSubRootsInCompositeRootingMarkers)
        {
            // We need to find all the source dependencies for the outputs
            // Because we are assuming that this is a many-to-one situation, we need to
            // build a composite rooting marker from the source files and then look through
            // the dependency table to discover the dependencies of those sources.
            //
            // If any of the dependencies are newer than the outputs, then a rebuild is required.

            Dictionary<string, ITaskItem> sourcesNeedingCompilation = new Dictionary<string, ITaskItem>(StringComparer.OrdinalIgnoreCase);

            // There were no tlogs available, that means we need to build
            if (!_tlogAvailable)
            {
                return _sourceFiles;
            }

            // Construct a rooting marker from the set of sources
            string upperSourcesRoot = FileTracker.FormatRootingMarker(_sourceFiles);
            List<ITaskItem> sourcesNeedingCompilationList = new List<ITaskItem>();

            // Check each root in the table to see if it matches.
            foreach (string tableEntryRoot in _dependencyTable.Keys)
            {
                string upperTableEntryRoot = tableEntryRoot.ToUpperInvariant();

                if (searchForSubRootsInCompositeRootingMarkers)
                {
                    if (upperTableEntryRoot.Contains(upperSourcesRoot) ||
                        CanonicalTrackedFilesHelper.RootContainsAllSubRootComponents(upperSourcesRoot, upperTableEntryRoot))
                    {
                        // Gather the unique outputs for this root
                        SourceDependenciesForOutputRoot(sourcesNeedingCompilation, upperTableEntryRoot, _outputFileGroup);
                    }
                }
                else
                {
                    if (upperTableEntryRoot.Equals(upperSourcesRoot, StringComparison.Ordinal))
                    {
                        // Gather the unique outputs for this root
                        SourceDependenciesForOutputRoot(sourcesNeedingCompilation, upperTableEntryRoot, _outputFileGroup);
                    }
                }
            }

            // There were no outputs for the requested root
            if (sourcesNeedingCompilation.Count == 0)
            {
                FileTracker.LogMessageFromResources(_log, MessageImportance.Low, "Tracking_DependenciesForRootNotFound", upperSourcesRoot);
                return _sourceFiles;
            }

            // We have our set of outputs, construct our array
            sourcesNeedingCompilationList.AddRange(sourcesNeedingCompilation.Values);
            ITaskItem[] sourcesNeedingCompilationArray = sourcesNeedingCompilationList.ToArray();

            // now that we have our dependencies, we need to check if any of them are newer than the outputs.
            DateTime newestSourceDependencyTime;
            DateTime oldestOutputTime;
            string newestSourceDependencyFile = String.Empty;
            string oldestOutputFile = String.Empty;

            if (
                CanonicalTrackedFilesHelper.FilesExistAndRecordNewestWriteTime(sourcesNeedingCompilationArray, _log, out newestSourceDependencyTime, out newestSourceDependencyFile) &&
                CanonicalTrackedFilesHelper.FilesExistAndRecordOldestWriteTime(_outputFileGroup, _log, out oldestOutputTime, out oldestOutputFile)
                )
            {
                if (newestSourceDependencyTime <= oldestOutputTime)
                {
                    // All sources and outputs exist, and the oldest output is newer than the newest input -- we're up to date!
                    FileTracker.LogMessageFromResources(_log, MessageImportance.Normal, "Tracking_AllOutputsAreUpToDate");
                    return new ITaskItem[0];
                }
            }

            // Too much logging leads to poor performance
            if (sourcesNeedingCompilation.Count > CanonicalTrackedFilesHelper.MaxLogCount)
            {
                FileTracker.LogMessageFromResources(_log, MessageImportance.Low, "Tracking_InputsNotShown", sourcesNeedingCompilation.Count);
            }
            else
            {
                // We have our set of outputs, log the details
                FileTracker.LogMessageFromResources(_log, MessageImportance.Low, "Tracking_InputsFor", upperSourcesRoot);

                foreach (ITaskItem inputItem in sourcesNeedingCompilationArray)
                {
                    FileTracker.LogMessage(_log, MessageImportance.Low, "\t" + inputItem);
                }
            }

            // Log the reasons that we're not up to date
            FileTracker.LogMessageFromResources(_log, MessageImportance.Low, "Tracking_InputNewerThanOutput", newestSourceDependencyFile, oldestOutputFile);

            return _sourceFiles;
        }

        /// <summary>
        /// Given a composite output rooting marker, gathers up all the sources it depends on.
        /// </summary>
        /// <param name="outputs">List of outputs to populate</param>
        /// <param name="sourceKey">The source to gather outputs for</param>
        private void SourceDependenciesForOutputRoot(Dictionary<string, ITaskItem> sourceDependencies, string sourceKey, ITaskItem[] filesToIgnore)
        {
            Dictionary<string, string> dependencies;
            bool ignoreDependentFile;
            bool thereAreFilesToIgnore = filesToIgnore != null && filesToIgnore.Length > 0;

            if (_dependencyTable.TryGetValue(sourceKey, out dependencies))
            {
                foreach (string dependee in dependencies.Keys)
                {
                    ignoreDependentFile = false;

                    if (thereAreFilesToIgnore)
                    {
                        // This is probably OK, because "filesToIgnore" is expected to be small
                        foreach (ITaskItem fileToIgnore in filesToIgnore)
                        {
                            if (String.Equals(dependee, fileToIgnore.ItemSpec, StringComparison.OrdinalIgnoreCase))
                            {
                                // don't add this file to the dependency list
                                ignoreDependentFile = true;
                                break;
                            }
                        }
                    }

                    // add the dependency if it is not already in the dictionary and if
                    // it's not in the group of files we're explicitly ignoring
                    ITaskItem existingTaskItem;
                    if (!ignoreDependentFile && !sourceDependencies.TryGetValue(dependee, out existingTaskItem))
                    {
                        sourceDependencies.Add(dependee, new TaskItem(dependee));
                    }
                }
            }
        }

        /// <summary>
        /// Check if the source file needs to be compiled
        /// </summary>
        /// <param name="sourceFile">The primary dependency</param>
        /// <returns>bool</returns>
        private bool IsUpToDate(ITaskItem sourceFile)
        {
            string sourceFullPath = FileUtilities.NormalizePath(sourceFile.ItemSpec);
            Dictionary<string, string> dependencies;
            bool dependenciesAvailable = _dependencyTable.TryGetValue(sourceFullPath, out dependencies);
            DateTime thisSourceOutputNewestTime = _outputNewestTime;

            if ((_useMinimalRebuildOptimization) && (_outputs != null) && dependenciesAvailable)
            {
                Dictionary<string, DateTime> outputFiles;

                thisSourceOutputNewestTime = DateTime.MinValue;
                // Missing outputs from the graph means that the source is out of date
                if (_outputs.DependencyTable.TryGetValue(sourceFullPath, out outputFiles))
                {
                    DateTime sourceTime = NativeMethodsShared.GetLastWriteFileUtcTime(sourceFullPath);

                    foreach (string outputFile in outputFiles.Keys)
                    {
                        DateTime outputFileTime = NativeMethodsShared.GetLastWriteFileUtcTime(outputFile);
                        // If the file exists
                        if (outputFileTime > DateTime.MinValue)
                        {
                            if (outputFileTime < sourceTime)
                            {
                                sourceFile.SetMetadata("_trackerCompileReason", "Tracking_SourceWillBeCompiledDependencyWasModifiedAt");
                                sourceFile.SetMetadata("_trackerModifiedPath", sourceFullPath);
                                sourceFile.SetMetadata("_trackerModifiedTime", sourceTime.ToLocalTime().ToString());
                                return false;
                            }
                            else if (outputFileTime > thisSourceOutputNewestTime)
                            {
                                thisSourceOutputNewestTime = outputFileTime;
                            }
                        }
                        else
                        {
                            sourceFile.SetMetadata("_trackerCompileReason", "Tracking_SourceWillBeCompiledOutputDoesNotExist");
                            sourceFile.SetMetadata("_trackerOutputFile", outputFile);
                            return false;
                        }
                    }
                }
                else
                {
                    sourceFile.SetMetadata("_trackerCompileReason", "Tracking_SourceOutputsNotAvailable");
                    return false;
                }
            }

            if (dependenciesAvailable)
            {
                foreach (string file in dependencies.Keys)
                {
                    // The file that we are encountering in the dependencies may be excluded from
                    // the dependency check, if so we don't want to go checking it
                    if (!FileIsExcludedFromDependencyCheck(file))
                    {
                        // If the file tracked during the build exists, then do a time-stamp check on it
                        // to determine up-to-dateness
                        DateTime dependeeTime = DateTime.MinValue;
                        if (!_lastWriteTimeCache.TryGetValue(file, out dependeeTime))
                        {
                            dependeeTime = NativeMethodsShared.GetLastWriteFileUtcTime(file);
                            _lastWriteTimeCache[file] = dependeeTime;
                        }

                        // If the file exists
                        if (dependeeTime > DateTime.MinValue)
                        {
                            if (dependeeTime > thisSourceOutputNewestTime)
                            {
                                sourceFile.SetMetadata("_trackerCompileReason", "Tracking_SourceWillBeCompiledDependencyWasModifiedAt");
                                sourceFile.SetMetadata("_trackerModifiedPath", file);
                                sourceFile.SetMetadata("_trackerModifiedTime", dependeeTime.ToLocalTime().ToString());
                                return false;
                            }
                        }
                        else // if the file no longer exists, then assume we are out of date and cause a compile
                        {
                            sourceFile.SetMetadata("_trackerCompileReason", "Tracking_SourceWillBeCompiledMissingDependency");
                            sourceFile.SetMetadata("_trackerModifiedPath", file);
                            return false;
                        }
                    }
                }
            }
            else
            {
                sourceFile.SetMetadata("_trackerCompileReason", "Tracking_SourceNotInTrackingLog");
                return false;
            }
            // It appears that all our dependencies are earlier than the outputs
            // So no need to compile
            return true;
        }

        /// <summary>
        /// Test to see if the specified file is excluded from tracked dependency checking
        /// </summary>
        /// <param name="fileName">
        /// Full path of the file to test
        /// </param>
        public bool FileIsExcludedFromDependencyCheck(string fileName)
        {
            string fileDirectoryName = FileUtilities.GetDirectoryNameOfFullPath(fileName);

            if (_excludedInputPaths.Contains(fileDirectoryName))
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        private bool FilesExistAndRecordNewestWriteTime(ITaskItem[] files)
        {
            return CanonicalTrackedFilesHelper.FilesExistAndRecordNewestWriteTime(files, _log, out _outputNewestTime, out _outputNewest);
        }

        /// <summary>
        /// Construct our dependency table for our source files.
        /// </summary>
        private void ConstructDependencyTable()
        {
            string tLogRootingMarker = null;
            try
            {
                // construct a rooting marker from the tlog files
                tLogRootingMarker = DependencyTableCache.FormatNormalizedTlogRootingMarker(_tlogFiles);
            }
            catch (ArgumentException e)
            {
                FileTracker.LogWarningWithCodeFromResources(_log, "Tracking_RebuildingDueToInvalidTLog", e.Message);
                return;
            }

            // Record the current directory (which under normal circumstances will be the project directory)
            // so that we can compare tracked paths against it for inclusion in the dependency graph
            string currentProjectDirectory = FileUtilities.EnsureTrailingSlash(Directory.GetCurrentDirectory());

            if (!_tlogAvailable)
            {
                foreach (ITaskItem tlogFileName in _tlogFiles)
                {
                    if (!FileUtilities.FileExistsNoThrow(tlogFileName.ItemSpec))
                    {
                        FileTracker.LogMessageFromResources(_log, MessageImportance.Low, "Tracking_SingleLogFileNotAvailable", tlogFileName.ItemSpec);
                    }
                }

                lock (DependencyTableCache.DependencyTable)
                {
                    // The tracking logs are not available, they may have been deleted at some point.
                    // Be safe and remove any references from the cache.
                    if (DependencyTableCache.DependencyTable.ContainsKey(tLogRootingMarker))
                    {
                        DependencyTableCache.DependencyTable.Remove(tLogRootingMarker);
                    }
                }
                return;
            }

            DependencyTableCacheEntry cachedEntry = null;
            lock (DependencyTableCache.DependencyTable)
            {
                // Look in the dependency table cache to see if its available and up to date
                cachedEntry = DependencyTableCache.GetCachedEntry(tLogRootingMarker);
            }

            // We have an up to date cached entry
            if (cachedEntry != null)
            {
                _dependencyTable = (Dictionary<string, System.Collections.Generic.Dictionary<string, string>>)cachedEntry.DependencyTable;
                // Log information about what we're using
                FileTracker.LogMessageFromResources(_log, MessageImportance.Low, "Tracking_ReadTrackingCached");
                foreach (ITaskItem tlogItem in cachedEntry.TlogFiles)
                {
                    FileTracker.LogMessage(_log, MessageImportance.Low, "\t{0}", tlogItem.ItemSpec);
                }
                return;
            }

            // Now we need to construct a dependency table for the primary sources from the TLOG files
            // If there are any errors in the tlogs, we want to warn, stop parsing tlogs, and empty
            // out the dependency table, essentially forcing a rebuild.
            bool encounteredInvalidTLogContents = false;
            bool exceptionCaught = false;
            string invalidTLogName = null;
            FileTracker.LogMessageFromResources(_log, MessageImportance.Low, "Tracking_ReadTrackingLogs");
            foreach (ITaskItem tlogFileName in _tlogFiles)
            {
                try
                {
                    FileTracker.LogMessage(_log, MessageImportance.Low, "\t{0}", tlogFileName.ItemSpec);

                    using (StreamReader tlog = File.OpenText(tlogFileName.ItemSpec))
                    {
                        string tlogEntry = tlog.ReadLine();

                        while (tlogEntry != null)
                        {
                            if (tlogEntry.Length == 0)
                            {
                                encounteredInvalidTLogContents = true;
                                invalidTLogName = tlogFileName.ItemSpec;
                                break;
                            }

                            if (tlogEntry[0] != '#') // command marker
                            {
                                bool rootingRecord = false;
                                // If this is a rooting record, remove the rooting marker
                                if (tlogEntry[0] == '^')
                                {
                                    tlogEntry = tlogEntry.Substring(1);

                                    if (tlogEntry.Length == 0)
                                    {
                                        encounteredInvalidTLogContents = true;
                                        invalidTLogName = tlogFileName.ItemSpec;
                                        break;
                                    }

                                    rootingRecord = true;
                                }

                                // found one of our primary sources
                                if (rootingRecord)
                                {
                                    // dependency table for the source file
                                    Dictionary<string, string> dependencies;
                                    Dictionary<string, string> primaryFiles;

                                    if (!_maintainCompositeRootingMarkers)
                                    {
                                        primaryFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                                        if (tlogEntry.Contains("|"))
                                        {
                                            foreach (ITaskItem file in _sourceFiles)
                                            {
                                                if (!primaryFiles.ContainsKey(FileUtilities.NormalizePath(file.ItemSpec)))
                                                {
                                                    primaryFiles.Add(FileUtilities.NormalizePath(file.ItemSpec), null);
                                                }
                                            }
                                        }
                                        else
                                        {
                                            primaryFiles.Add(tlogEntry, null);
                                        }
                                    }
                                    else
                                    {
                                        primaryFiles = null;
                                    }

                                    // We haven't seen this source before in the tracking log
                                    // so create a new dependency table and add the source file(s)
                                    if (!_dependencyTable.TryGetValue(tlogEntry, out dependencies))
                                    {
                                        dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                                        if (!_maintainCompositeRootingMarkers)
                                        {
                                            dependencies.Add(tlogEntry, null);
                                        }

                                        _dependencyTable.Add(tlogEntry, dependencies);
                                    }

                                    tlogEntry = tlog.ReadLine();

                                    if (_maintainCompositeRootingMarkers)
                                    {
                                        // Process each file encountered until we reach:
                                        // the end of the or,
                                        // A command marker or,
                                        // we hit a rooting marker
                                        while (tlogEntry != null)
                                        {
                                            if (tlogEntry.Length == 0)
                                            {
                                                encounteredInvalidTLogContents = true;
                                                invalidTLogName = tlogFileName.ItemSpec;
                                                break;
                                            }
                                            else if ((tlogEntry[0] != '#') && (tlogEntry[0] != '^'))
                                            {
                                                if (!dependencies.ContainsKey(tlogEntry))
                                                {
                                                    if (FileTracker.FileIsUnderPath(tlogEntry, currentProjectDirectory) || !FileTracker.FileIsExcludedFromDependencies(tlogEntry))
                                                    {
                                                        dependencies.Add(tlogEntry, null);
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                break;
                                            }

                                            tlogEntry = tlog.ReadLine();
                                        }
                                    }
                                    else
                                    {
                                        while (tlogEntry != null)
                                        {
                                            if (tlogEntry.Length == 0)
                                            {
                                                encounteredInvalidTLogContents = true;
                                                invalidTLogName = tlogFileName.ItemSpec;
                                                break;
                                            }
                                            else if (tlogEntry[0] != '#' && tlogEntry[0] != '^')
                                            {
                                                if (primaryFiles.ContainsKey(tlogEntry))
                                                {
                                                    // if this is a primary file, we need to add it to the dependency table, and we need
                                                    // to reset "dependencies" so that the following dependencies get written into this
                                                    // primary file's table instead of the previous one.
                                                    if (!_dependencyTable.TryGetValue(tlogEntry, out dependencies))
                                                    {
                                                        dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                                        dependencies.Add(tlogEntry, null);

                                                        _dependencyTable.Add(tlogEntry, dependencies);
                                                    }
                                                }
                                                else if (!dependencies.ContainsKey(tlogEntry))
                                                {
                                                    // however, if it's not a primary file, just add it to the current dependency table
                                                    if (FileTracker.FileIsUnderPath(tlogEntry, currentProjectDirectory) || !FileTracker.FileIsExcludedFromDependencies(tlogEntry))
                                                    {
                                                        dependencies.Add(tlogEntry, null);
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                break;
                                            }


                                            tlogEntry = tlog.ReadLine();
                                        }
                                    }
                                }
                                else // don't know what this entry is, so skip it
                                {
                                    tlogEntry = tlog.ReadLine();
                                }
                            }
                            else // skip over the initial '#' line
                            {
                                tlogEntry = tlog.ReadLine();
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    if (ExceptionHandling.NotExpectedException(e))
                    {
                        throw;
                    }

                    FileTracker.LogWarningWithCodeFromResources(_log, "Tracking_RebuildingDueToInvalidTLog", e.Message);
                    break;
                }

                if (encounteredInvalidTLogContents)
                {
                    FileTracker.LogWarningWithCodeFromResources(_log, "Tracking_RebuildingDueToInvalidTLogContents", invalidTLogName);
                    break;
                }
            }

            lock (DependencyTableCache.DependencyTable)
            {
                // There were problems with the tracking logs -- we've already warned or errored; now we want to make
                // sure that we essentially force a rebuild of this particular root.
                if (encounteredInvalidTLogContents || exceptionCaught)
                {
                    if (DependencyTableCache.DependencyTable.ContainsKey(tLogRootingMarker))
                    {
                        DependencyTableCache.DependencyTable.Remove(tLogRootingMarker);
                    }

                    _dependencyTable = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    // Record the newly built dependency table in the cache
                    DependencyTableCache.DependencyTable[tLogRootingMarker] = new DependencyTableCacheEntry(_tlogFiles, _dependencyTable);
                }
            }
        }

        /// <summary>
        /// This method will re-write the tlogs from the current output table new entries will
        /// be tracked.
        /// </summary>
        public void SaveTlog()
        {
            SaveTlog(null);
        }

        /// <summary>
        /// This method will re-write the tlogs from the current dependency. As the sources are compiled,
        /// new entries willbe tracked.
        /// </summary>
        /// <param name="includeInTLog">
        /// Delegate used to determine whether a particular file should
        /// be included in the compacted tlog.
        /// </param>
        [SuppressMessage("Microsoft.Naming", "CA1702:CompoundWordsShouldBeCasedCorrectly", MessageId = "TLog", Justification = "Has now shipped as public API; plus it's unclear whether 'Tlog' or 'TLog' is the preferred casing")]
        public void SaveTlog(DependencyFilter includeInTLog)
        {
            // If there are no tlog files, then this will be a clean build
            // so there is no need to write a new tlog
            if (_tlogFiles != null && (_tlogFiles.Length > 0))
            {
                string tLogRootingMarker = DependencyTableCache.FormatNormalizedTlogRootingMarker(_tlogFiles);

                lock (DependencyTableCache.DependencyTable)
                {
                    // The tracking logs in the cache will be invalidated by this compaction
                    // remove the cached entries
                    if (DependencyTableCache.DependencyTable.ContainsKey(tLogRootingMarker))
                    {
                        DependencyTableCache.DependencyTable.Remove(tLogRootingMarker);
                    }
                }

                string firstTlog = _tlogFiles[0].ItemSpec;

                // empty all tlogs
                foreach (ITaskItem tlogFile in _tlogFiles)
                {
                    File.WriteAllText(tlogFile.ItemSpec, "", System.Text.Encoding.Unicode);
                }

                // Write out the remaining dependency information as a new tlog
                using (StreamWriter inputs = new StreamWriter(firstTlog, false, System.Text.Encoding.Unicode))
                {
                    if (!_maintainCompositeRootingMarkers)
                    {
                        foreach (string primaryFile in _dependencyTable.Keys)
                        {
                            if (!primaryFile.Contains("|")) // composite roots are not needed
                            {
                                Dictionary<string, string> dependencies = _dependencyTable[primaryFile];
                                inputs.WriteLine("^" + primaryFile);
                                foreach (string file in dependencies.Keys)
                                {
                                    // We only want to write the tlog entry if it isn't the primary file
                                    // and we aren't being asked to filter it out
                                    if (file != primaryFile && (includeInTLog == null || includeInTLog(file)))
                                    {
                                        inputs.WriteLine(file);
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // Just output the rooting markers and their dependencies -- we don't want to
                        // compact out the composite ones.
                        foreach (string rootingMarker in _dependencyTable.Keys)
                        {
                            Dictionary<string, string> dependencies = _dependencyTable[rootingMarker];
                            inputs.WriteLine("^" + rootingMarker);
                            foreach (string file in dependencies.Keys)
                            {
                                // Give the task a chance to filter dependencies out of the written TLog
                                if (includeInTLog == null || includeInTLog(file))
                                {
                                    // Write out the entry
                                    inputs.WriteLine(file);
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Remove the output graph entries for the given sources and corresponding outputs
        /// </summary>
        /// <param name="source">Source that should be removed from the graph</param>
        /// <param name="correspondingOutputs">Outputs that correspond ot the sources (used for same file processing)</param>
        public void RemoveEntriesForSource(ITaskItem source)
        {
            RemoveEntriesForSource(new ITaskItem[] { source });
        }

        /// <summary>
        /// Remove the output graph entries for the given sources and corresponding outputs
        /// </summary>
        /// <param name="source">Sources that should be removed from the graph</param>
        /// <param name="correspondingOutputs">Outputs that correspond ot the sources (used for same file processing)</param>
        public void RemoveEntriesForSource(ITaskItem[] source)
        {
            // construct a root marker for the sources and outputs to remove from the graph
            string rootMarkerToRemove = FileTracker.FormatRootingMarker(source);

            // remove the entry from the graph for the combined root
            _dependencyTable.Remove(rootMarkerToRemove);

            // remove the entry for each source item
            foreach (ITaskItem sourceItem in source)
            {
                _dependencyTable.Remove(FileUtilities.NormalizePath(sourceItem.ItemSpec));
            }
        }

        /// <summary>
        /// Remove the entry in the input dependency graph corresponding to the rooting marker 
        /// passed in. 
        /// </summary>
        /// <param name="rootingMarker">The root to remove</param>
        public void RemoveEntryForSourceRoot(string rootingMarker)
        {
            _dependencyTable.Remove(rootingMarker);
        }

        /// <summary>
        /// Remove the output graph entries for the given sources and corresponding outputs
        /// </summary>
        /// <param name="sources">Sources that should be removed from the graph</param>
        public void RemoveDependencyFromEntry(ITaskItem[] sources, ITaskItem dependencyToRemove)
        {
            string rootingMarker = FileTracker.FormatRootingMarker(sources);
            RemoveDependencyFromEntry(rootingMarker, dependencyToRemove);
        }

        /// <summary>
        /// Remove the output graph entries for the given source and corresponding outputs
        /// </summary>
        /// <param name="source">Source that should be removed from the graph</param>
        public void RemoveDependencyFromEntry(ITaskItem source, ITaskItem dependencyToRemove)
        {
            string rootingMarker = FileTracker.FormatRootingMarker(source);
            RemoveDependencyFromEntry(rootingMarker, dependencyToRemove);
        }

        /// <summary>
        /// Remove the output graph entries for the given sources and corresponding outputs
        /// </summary>
        /// <param name="source">Sources that should be removed from the graph</param>
        private void RemoveDependencyFromEntry(string rootingMarker, ITaskItem dependencyToRemove)
        {
            // construct a root marker for the source that will remove the dependency from
            Dictionary<string, string> dependencies;

            if (_dependencyTable.TryGetValue(rootingMarker, out dependencies))
            {
                dependencies.Remove(FileUtilities.NormalizePath(dependencyToRemove.ItemSpec));
            }
            else
            {
                FileTracker.LogMessageFromResources(_log, MessageImportance.Normal, "Tracking_ReadLogEntryNotFound", rootingMarker);
            }
        }

        /// <summary>
        /// Remove the output graph entries for the given sources and corresponding outputs
        /// </summary>
        /// <param name="source">Source that should be removed from the graph</param>
        public void RemoveDependenciesFromEntryIfMissing(ITaskItem source)
        {
            RemoveDependenciesFromEntryIfMissing(new ITaskItem[] { source }, null);
        }

        /// <summary>
        /// Remove the output graph entries for the given sources and corresponding outputs
        /// </summary>
        /// <param name="source">Source that should be removed from the graph</param>
        /// <param name="correspondingOutputs">Output that correspond ot the sources (used for same file processing)</param>
        public void RemoveDependenciesFromEntryIfMissing(ITaskItem source, ITaskItem correspondingOutput)
        {
            RemoveDependenciesFromEntryIfMissing(new ITaskItem[] { source }, new ITaskItem[] { correspondingOutput });
        }

        /// <summary>
        /// Remove the output graph entries for the given sources and corresponding outputs
        /// </summary>
        /// <param name="source">Sources that should be removed from the graph</param>
        public void RemoveDependenciesFromEntryIfMissing(ITaskItem[] source)
        {
            RemoveDependenciesFromEntryIfMissing(source, null);
        }

        /// <summary>
        /// Remove the output graph entries for the given sources and corresponding outputs
        /// </summary>
        /// <param name="source">Sources that should be removed from the graph</param>
        /// <param name="correspondingOutputs">Outputs that correspond ot the sources (used for same file processing)</param>
        public void RemoveDependenciesFromEntryIfMissing(ITaskItem[] source, ITaskItem[] correspondingOutputs)
        {
            if (correspondingOutputs != null)
            {
                ErrorUtilities.VerifyThrowArgument(source.Length == correspondingOutputs.Length, "Tracking_SourcesAndCorrespondingOutputMismatch");
            }

            // construct a combined root marker for the sources and outputs to remove from the graph
            string rootingMarker = FileTracker.FormatRootingMarker(source, correspondingOutputs);

            RemoveDependenciesFromEntryIfMissing(rootingMarker);

            // Remove entries for each individual source
            for (int sourceIndex = 0; sourceIndex < source.Length; sourceIndex++)
            {
                if (correspondingOutputs != null)
                {
                    rootingMarker = FileTracker.FormatRootingMarker(source[sourceIndex], correspondingOutputs[sourceIndex]);
                }
                else
                {
                    rootingMarker = FileTracker.FormatRootingMarker(source[sourceIndex]);
                }

                RemoveDependenciesFromEntryIfMissing(rootingMarker);
            }
        }

        /// <summary>
        /// Remove the output graph entries for the given rooting marker
        /// </summary>
        /// <param name="rootingMarker"></param>
        private void RemoveDependenciesFromEntryIfMissing(string rootingMarker)
        {
            Dictionary<string, string> dependencies;

            // In the event of incomplete tracking information (i.e. this root was not present), just continue quietly
            // as the user could have killed the tool being tracked, or another error occured during its execution.
            if (_dependencyTable.TryGetValue(rootingMarker, out dependencies))
            {
                Dictionary<string, string> dependenciesWithoutMissingFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int keyIndex = 0;

                foreach (string file in dependencies.Keys)
                {
                    if (keyIndex++ > 0)
                    {
                        // If we are ignoring missing files, then only record those that exist
                        if (FileUtilities.FileExistsNoThrow(file))
                        {
                            dependenciesWithoutMissingFiles.Add(file, dependencies[file]);
                        }
                    }
                    else
                    {
                        dependenciesWithoutMissingFiles.Add(file, file);
                    }
                }

                _dependencyTable[rootingMarker] = dependenciesWithoutMissingFiles;
            }
        }
        #endregion
    }
}
