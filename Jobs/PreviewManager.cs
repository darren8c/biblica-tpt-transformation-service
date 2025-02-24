﻿/*
Copyright © 2021 by Biblica, Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/
using Amazon.S3.Transfer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TptMain.InDesign;
using TptMain.Models;
using TptMain.Util;

namespace TptMain.Jobs
{
    /// <summary>
    /// Preview Manager class that will address the preview generation portion of PreviewJobs.
    /// </summary>
    public class PreviewManager : IPreviewManager
    {
        /// <summary>
        /// Type-specific logger (injected).
        /// </summary>
        private readonly ILogger<PreviewManager> _logger;

        /// <summary>
        /// Job File Manager (injected).
        /// </summary>
        private readonly JobFileManager _jobFileManager;

        /// <summary>
        /// The S3Service to talk to S3 to verify status and get results (instantiated).
        /// </summary>
        private readonly S3Service _s3Service;

        /// <summary>
        /// IDS request timeout in seconds (configured).
        /// </summary>
        private readonly int _idsTimeoutInMSec;

        /// <summary>
        /// Preview script (JSX) path (configured).
        /// </summary>
        private readonly DirectoryInfo _idsPreviewScriptDirectory;

        /// <summary>
        /// All configured InDesign script runners.
        /// </summary>
        private List<InDesignScriptRunner> IndesignScriptRunners { get; } = new List<InDesignScriptRunner>();

        /// <summary>
        /// The map for tracking running tasks against IDS script runners.
        /// </summary>
        private Dictionary<InDesignScriptRunner, Task> IdsTaskMap { get; } = new Dictionary<InDesignScriptRunner, Task>();

        /// <summary>
        /// The map for cancellation token sources by jobs.
        /// </summary>
        private Dictionary<PreviewJob, CancellationTokenSource> CancellationSourceMap { get; } = new Dictionary<PreviewJob, CancellationTokenSource>();

        /// <summary>
        /// This FIFO collection tracks the order in which <code>PreviewJob</code>s came in so that they're processed in-order.
        /// </summary>
        private ConcurrentQueue<PreviewJob> JobQueue { get; } = new ConcurrentQueue<PreviewJob>();

        /// <summary>
        /// Basic ctor.
        /// </summary>
        /// <param name="logger">Type-specific logger (required).</param>
        /// <param name="configuration">System configuration (required).</param>
        /// <param name="jobFileManager">Job File Manager (required).</param>
        public PreviewManager(
            ILoggerFactory loggerFactory,
            IConfiguration configuration,
            JobFileManager jobFileManager)
        {
            _ = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _ = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _jobFileManager = jobFileManager ?? throw new ArgumentNullException(nameof(jobFileManager));
            _logger = loggerFactory.CreateLogger<PreviewManager>();

            // grab global settings that apply to every InDesignScriptRunner
            _idsTimeoutInMSec = (int)TimeSpan.FromSeconds(int.Parse(configuration[ConfigConsts.IdsTimeoutInSecKey]
                                                                     ?? throw new ArgumentNullException(ConfigConsts
                                                                         .IdsTimeoutInSecKey)))
                .TotalMilliseconds;
            _idsPreviewScriptDirectory = new DirectoryInfo((configuration[ConfigConsts.IdsPreviewScriptDirKey]
                                                            ?? throw new ArgumentNullException(ConfigConsts
                                                                .IdsPreviewScriptDirKey)));

            // grab the individual InDesignScriptRunner settings and create servers for each configuration
            var serversSection = configuration.GetSection(ConfigConsts.IdsServersSectionKey);
            var serversConfig = serversSection.Get<List<InDesignServerConfig>>();
            SetUpInDesignScriptRunners(loggerFactory, serversConfig);

            _s3Service = new S3Service();

            _logger.LogDebug("PreviewManager()");
        }

        /// <summary>
        /// Process a preview job.
        /// </summary>
        /// <param name="previewJob">Preview job to process (required).</param>
        public void ProcessJob(PreviewJob previewJob)
        {
            // validate inputs
            _ = previewJob ?? throw new ArgumentNullException(nameof(previewJob));

            // put the job on the queue for processing
            previewJob.State.Add(new PreviewJobState(JobStateEnum.GeneratingPreview, JobStateSourceEnum.PreviewGeneration));
            JobQueue.Enqueue(previewJob);

            CheckPreviewProcessing();
        }

        /// <summary>
        /// This function ensures that Jobs are continuously processing as InDesign runners become available.
        /// </summary>
        private void CheckPreviewProcessing()
        {
            lock (IdsTaskMap)
            {
                _logger.LogInformation("Checking and updating preview processing...");

                // Keep queuing jobs while we have a runner and jobs available.
                InDesignScriptRunner availableRunner = GetAvailableRunner();
                while (availableRunner != null && !JobQueue.IsEmpty)
                {
                    // grab the next prioritized preview job
                    if (!JobQueue.TryDequeue(out var previewJob))
                    {
                        // nothing to dequeue
                        return;
                    }

                    // hold on to the ability to cancel the task
                    var tokenSource = new CancellationTokenSource();

                    // track the token so that we can support job cancellation requests
                    CancellationSourceMap[previewJob] = tokenSource;

                    // we copy the reference of the chosen runner, otherwise the task may run with an unexpected runner due to looping
                    var taskRunner = availableRunner;
                    var task = new Task(() =>
                    {
                        _logger.LogDebug($"Assigning preview generation job '{previewJob.Id}' to IDS runner '{taskRunner.Name}'.");
                        var runner = taskRunner;

                        try
                        {
                            // download the input template and IDTT files
                            var transferUtilty = new TransferUtility(_s3Service.S3Client);
                            transferUtilty.DownloadDirectory(
                                _s3Service.BucketName,
                                $"jobs/{previewJob.Id}",
                                _jobFileManager.GetProjectDirectoryById(previewJob.Id).FullName
                                );

                            runner.CreatePreview(previewJob, tokenSource.Token);
                            previewJob.State.Add(new PreviewJobState(JobStateEnum.PreviewGenerated, JobStateSourceEnum.PreviewGeneration));
                        }
                        catch (Exception ex)
                        {
                            previewJob.SetError("An error occurred while generating preview.", ex.Message);
                        } 
                        finally
                        {
                            CancellationSourceMap.Remove(previewJob);
                        }
                    }, tokenSource.Token);

                    IdsTaskMap[taskRunner] = task;

                    task.Start();

                    // check to see if there's a still a runner available for the next job
                    availableRunner = GetAvailableRunner();
                }
            }
        }

        /// <summary>
        /// Query the status of the PreviewJob and update the job itself appropriately.
        /// </summary>
        /// <param name="previewJob">The PreviewJob to query the status of.</param>
        public void GetStatus(PreviewJob previewJob)
        {
            CheckPreviewProcessing();
        }

        /// <summary>
        /// Initiate the cancellation of a PreviewJob.
        /// </summary>
        /// <param name="previewJob">The PreviewJob to cancel.</param>
        public void CancelJob(PreviewJob previewJob)
        {
            lock (IdsTaskMap)
            {
                if (CancellationSourceMap.TryGetValue(previewJob, out var cancellationTokenSource))
                {
                    cancellationTokenSource.Cancel();
                    _logger.LogInformation($"Preview job '{previewJob.Id}' has been cancelled.");
                    previewJob.State.Add(new PreviewJobState(JobStateEnum.Cancelled, JobStateSourceEnum.PreviewGeneration));
                }
                else
                {
                    _logger.LogWarning($"Preview job '{previewJob.Id}' has no running task to cancel.");
                }
            }
        }

        /// <summary>
        /// Get the next available <code>InDesignScriptRunner</code>; Otherwise: null.
        /// </summary>
        /// <returns>The next available <code>InDesignScriptRunner</code>; Otherwise: null.</returns>
        private InDesignScriptRunner GetAvailableRunner()
        {
            InDesignScriptRunner availableRunner = null;

            IndesignScriptRunners.ForEach((idsRunner) => {

                // break out if we've found a runner already
                if (availableRunner == null)
                {
                    _logger.LogDebug($"Assessing '{idsRunner.Name}' for availability.");

                    // determine if we have any running tasks for the selected runner
                    IdsTaskMap.TryGetValue(idsRunner, out var task);

                    // track if there's any actively running task
                    if (task == null)
                    {
                        _logger.LogDebug($"'{idsRunner.Name}' has no task assigned and is available.");
                        availableRunner = idsRunner;
                    } else if (task.IsCompleted)
                    {
                        _logger.LogDebug($"'{idsRunner.Name}' has a task in the terminal state of '{task.Status}' and is available.");
                        availableRunner = idsRunner;
                    }
                    else
                    {
                        _logger.LogDebug($"'{idsRunner.Name}' is currently running a task in state '{task.Status}'.");
                    }
                }
            });

            if (availableRunner == null)
            {
                _logger.LogDebug("No available IDS runner found.");
            }

            return availableRunner;
        }

        /// <summary>
        /// Create an InDesignScriptRunner object for each server configuration.
        /// </summary>
        /// <param name="loggerFactory">Logger Factory (required).</param>
        /// <param name="serverConfigs">Server configurations (required).</param>
        private void SetUpInDesignScriptRunners(ILoggerFactory loggerFactory, List<InDesignServerConfig> serverConfigs)
        {
            if (serverConfigs == null || serverConfigs.Count <= 0)
            {
                throw new ArgumentException($"No server configurations were found in the configuration section '{ConfigConsts.IdsServersSectionKey}'");
            }

            _logger.LogDebug($"{serverConfigs.Count} InDesign Server configurations found. \r\n" + JsonConvert.SerializeObject(serverConfigs));

            foreach(var config in serverConfigs)
            {
                var serverName = config.Name;

                if (serverName == null || serverName.Trim().Length <= 0)
                {
                    throw new ArgumentException($"Server.Name cannot be null or empty.'");
                }

                var logger = loggerFactory.CreateLogger(nameof(InDesignScriptRunner) + $":{config.Name}");
                IndesignScriptRunners.Add(
                    new InDesignScriptRunner(
                        logger, 
                        config, 
                        _idsTimeoutInMSec,
                        _idsPreviewScriptDirectory,
                        _jobFileManager
                        ));
            }
        }
    }
}