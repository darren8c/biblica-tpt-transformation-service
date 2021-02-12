﻿using Microsoft.Extensions.Configuration;
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
        /// IDTT directory config key.
        /// </summary>
        private const string IdttDirKey = "Docs:IDTT:Directory";

        /// <summary>
        /// Paratext directory config key.
        /// </summary>
        private const string ParatextDirKey = "Docs:Paratext:Directory";

        /// <summary>
        /// Type-specific logger (injected).
        /// </summary>
        private readonly ILogger<ProjectManager> _logger;

        /// <summary>
        /// IDTT directory (configured).
        /// </summary>
        private readonly DirectoryInfo _idttDirectory;

        /// <summary>
        /// Paratext directory (configured).
        /// </summary>
        private readonly DirectoryInfo _paratextDirectory;

        /// <summary>
        /// Found project details.
        /// </summary>
        private IDictionary<string, ProjectDetails> _projectDetails;

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

            _idttDirectory = new DirectoryInfo(configuration[IdttDirKey]
                                               ?? throw new ArgumentNullException(IdttDirKey));
            _paratextDirectory = new DirectoryInfo(configuration[ParatextDirKey]
                                                   ?? throw new ArgumentNullException(ParatextDirKey));

            if (!Directory.Exists(_idttDirectory.FullName))
            {
                Directory.CreateDirectory(_idttDirectory.FullName);
            }
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
                try
                {
                    _logger.LogDebug("Checking IDTT files...");

                    IDictionary<string, ProjectDetails> newProjectDetails = new SortedDictionary<string, ProjectDetails>();
                    foreach (var projectDir in _paratextDirectory.GetDirectories())
                    {
                        var sfmFiles = projectDir.GetFiles("*.SFM");
                        if (sfmFiles.Length > 0)
                        {
                            var projectName = projectDir.Name;
                            var formatDirs = new[] {
                                new DirectoryInfo(
                                    Path.Join(_idttDirectory.FullName,
                                        BookFormat.cav.ToString(),
                                        projectName)),
                                new DirectoryInfo(
                                    Path.Join(_idttDirectory.FullName,
                                        BookFormat.tbotb.ToString(),
                                        projectName))
                            };

                            if (formatDirs.All(dirItem => dirItem.Exists))
                            {
                                newProjectDetails[projectName] = new ProjectDetails
                                {
                                    ProjectName = projectName,
                                    ProjectUpdated =
                                        formatDirs
                                            .Select(dirItem => dirItem.LastWriteTimeUtc)
                                            .Aggregate(DateTime.MinValue,
                                                (lastTimeUtc, writeTimeUtc) =>
                                                    writeTimeUtc > lastTimeUtc ? writeTimeUtc : lastTimeUtc)
                                };
                            }
                        }
                    }

                    _projectDetails = newProjectDetails.ToImmutableDictionary();
                    _logger.LogDebug("...IDTT files checked.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Can't check IDTT files.");
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
                if (_projectDetails == null)
                {
                    CheckProjectFiles();
                }

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