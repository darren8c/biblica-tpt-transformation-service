﻿/*
Copyright © 2021 by Biblica, Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using TptMain.Models;
using TptMain.Util;

namespace TptMain.Projects
{
    /// <summary>
    /// Project manager, provider of Paratext project details.
    /// </summary>
    public class ProjectManager : IDisposable, IProjectManager
    {
        /// <summary>
        /// Paratext directory config key.
        /// </summary>
        public const string ParatextDirKey = "Docs:Paratext:Directory";

        /// <summary>
        /// IDTT update interval config key.
        /// </summary>
        public const string ProjectUpdateIntervalKey = "Docs:Paratext:CheckIntervalInSec";

        /// <summary>
        /// The number of seconds before updating the project details since the previous the update.
        /// </summary>
        private readonly int _checkIntervalInSec;

        /// <summary>
        /// Type-specific logger (injected).
        /// </summary>
        private readonly ILogger<ProjectManager> _logger;

        /// <summary>
        /// Paratext directory (configured).
        /// </summary>
        private readonly DirectoryInfo _paratextDirectory;

        /// <summary>
        /// Found project details.
        /// </summary>
        private IDictionary<string, ProjectDetails> _projectDetails;

        /// <summary>
        /// The project details last updated timestamp. This allows us to determine if it's to update again or not.
        /// </summary>
        private DateTime LastProjectUpdatedFileTime { get; set; }

        /// <summary>
        /// Basic ctor.
        /// </summary>
        /// <param name="logger">Type-specific logger (required).</param>
        /// <param name="configuration">System configuration (required).</param>
        public ProjectManager(
            ILogger<ProjectManager> logger,
            IConfiguration configuration)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _ = configuration ?? throw new ArgumentNullException(nameof(configuration));

            _paratextDirectory = new DirectoryInfo(configuration[ParatextDirKey]
                                                   ?? throw new ArgumentNullException(ParatextDirKey));

            // extract the project details cache time to live 
            _ = configuration[ProjectUpdateIntervalKey] ?? throw new ArgumentNullException(ProjectUpdateIntervalKey);
            _checkIntervalInSec = Int32.Parse(configuration[ProjectUpdateIntervalKey]);

            if (!Directory.Exists(_paratextDirectory.FullName))
            {
                Directory.CreateDirectory(_paratextDirectory.FullName);
            }
            _logger.LogDebug("ProjectManager()");
        }

        /// <summary>
        /// Inventories project files to build map.
        /// </summary>
        public virtual void CheckProjectFiles()
        {

            lock (this)
            {
                // update project details if never done or we've past the configured cache time-to-live.
                if (_projectDetails == null || DateTime.UtcNow > LastProjectUpdatedFileTime.AddSeconds(_checkIntervalInSec))
                {
                    try
                    {
                        _logger.LogDebug("Checking Paratext files...");

                        IDictionary<string, ProjectDetails> newProjectDetails = new SortedDictionary<string, ProjectDetails>();
                        foreach (var projectDir in _paratextDirectory.GetDirectories())
                        {
                            var projectFiles = projectDir.GetFiles("*.usx");
                            if (projectFiles.Length > 0)
                            {
                                var projectName = projectDir.Name;

                                newProjectDetails[projectName] = new ProjectDetails
                                {
                                    ProjectName = projectName,
                                    // Find the modified date of the latest generated file for the project
                                    ProjectUpdated =
                                        projectFiles
                                            .Select(fileItem => fileItem.LastWriteTimeUtc)
                                            .Aggregate(DateTime.MinValue,
                                                (lastTimeUtc, writeTimeUtc) =>
                                                    writeTimeUtc > lastTimeUtc ? writeTimeUtc : lastTimeUtc)
                                };
                            }
                        }

                        // Update the project details and the time in which we did so
                        LastProjectUpdatedFileTime = DateTime.UtcNow;
                        _projectDetails = newProjectDetails.ToImmutableDictionary();
                        _logger.LogDebug("...Paratext files checked.");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Can't check Paratext files.");
                    }
                }
            }
        }

        /// <summary>
        /// Gets a read-only copy of the current project details map, initiating an inventory if it hasn't happened yet.
        /// </summary>
        /// <param name="projectDetails">Immutable map of project names to details.</param>
        /// <returns>True if any project details found, false otherwise.</returns>
        public bool TryGetProjectDetails(out IDictionary<string, ProjectDetails> projectDetails)
        {
            _logger.LogDebug("TryGetProjectDetails().");
            lock (this)
            {
                CheckProjectFiles();

                projectDetails = _projectDetails;
                return (projectDetails.Count > 0);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _logger.LogDebug("Dispose().");
        }
    }
}