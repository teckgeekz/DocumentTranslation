﻿using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace DocumentTranslationService.Core
{
    public class DocumentTranslationBusiness
    {
        #region Properties
        public DocumentTranslationService TranslationService { get; }

        public Glossary Glossary { get { return glossary; } }

        private Glossary glossary;

        public event EventHandler<StatusResponse> StatusUpdate;

        public event EventHandler OnDownloadComplete;

        #endregion Properties

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="documentTranslationService"></param>
        public DocumentTranslationBusiness(DocumentTranslationService documentTranslationService)
        {
            TranslationService = documentTranslationService;
        }

        public async Task RunAsync(string[] filestotranslate, string tolanguage)
        {
            List<string> list = new();
            foreach (string file in filestotranslate)
            {
                list.Add(file);
            }
            await RunAsync(list, tolanguage);
        }

        /// <summary>
        /// Perform a translation of a set of files using the TranslationService passed in the Constructor.
        /// </summary>
        /// <param name="filestotranslate">A list of files to translate. Can be a single file or a single directory.</param>
        /// <param name="tolanguage">A single target language</param>
        /// <param name="glossaryfiles">The glossary files</param>
        /// <returns></returns>
        public async Task RunAsync(List<string> filestotranslate, string tolanguage, List<string> glossaryfiles = null)
        {
            //Create the containers
            string containerNameBase = "doctr" + Guid.NewGuid().ToString();

            BlobContainerClient sourceContainer = new(TranslationService.StorageConnectionString, containerNameBase + "src");
            var sourceContainerTask = sourceContainer.CreateIfNotExistsAsync();
            TranslationService.ContainerClientSource = sourceContainer;
            BlobContainerClient targetContainer = new(TranslationService.StorageConnectionString, containerNameBase + "tgt");
            var targetContainerTask = targetContainer.CreateIfNotExistsAsync();
            TranslationService.ContainerClientTarget = targetContainer;
            Glossary glossary = new(TranslationService);
            this.glossary = glossary;
            if (glossaryfiles is null)
            {
                glossary.GlossaryFiles = glossaryfiles;
                await glossary.CreateContainerAsync(TranslationService.StorageConnectionString, containerNameBase);
            }


            //Upload documents
            if ((filestotranslate.Count == 1)
                && (File.GetAttributes(filestotranslate[0]) == FileAttributes.Directory))
            {
                foreach (var file in Directory.EnumerateFiles(filestotranslate[0]))
                {
                    filestotranslate.Add(file);
                }
                filestotranslate.RemoveAt(0);
            }
            List<string> discards;
            (filestotranslate, discards) = FilterByExtension(filestotranslate, TranslationService.Extensions);
            if (discards is not null)
            {
                foreach (string fileName in discards)
                {
                    Debug.WriteLine($"Discarded due to invalid file format for translation: {fileName}");
                }
            }

            await sourceContainerTask;
            Debug.WriteLine("Source container created");

            List<Task> uploadTasks = new();
            using (System.Threading.SemaphoreSlim semaphore = new(100))
            {
                foreach (var filename in filestotranslate)
                {
                    await semaphore.WaitAsync();
                    FileStream fileStream = File.OpenRead(filename);
                    BlobClient blobClient = new(TranslationService.StorageConnectionString, TranslationService.ContainerClientSource.Name, Normalize(filename));
                    try
                    {
                        uploadTasks.Add(blobClient.UploadAsync(fileStream, true));
                        semaphore.Release();
                    }
                    catch (System.AggregateException e)
                    {
                        Debug.WriteLine($"Uploading file {fileStream.Name} failed with {e.Message}");
                    }
                    Debug.WriteLine(String.Format($"File {filename} uploaded."));
                }
            }
            Debug.WriteLine("Awaiting upload task completion.");
            await Task.WhenAll(uploadTasks);
            Debug.WriteLine("Upload complete. {0} files uploaded.", uploadTasks.Count);

            //Upload Glossaries
            await glossary.UploadAsync();

            //Translate the container content
            Uri sasUriSource = sourceContainer.GenerateSasUri(BlobContainerSasPermissions.All, DateTimeOffset.UtcNow + TimeSpan.FromHours(1));
            await targetContainerTask;
            Uri sasUriTarget = targetContainer.GenerateSasUri(BlobContainerSasPermissions.All, DateTimeOffset.UtcNow + TimeSpan.FromHours(1));
            Uri sasUriGlossary = glossary.GenerateSasUri();
            DocumentTranslationSource documentTranslationSource = new() { SourceUrl = sasUriSource.ToString() };
            DocumentTranslationTarget documentTranslationTarget = new() { language = tolanguage, targetUrl = sasUriTarget.ToString() };
            List<DocumentTranslationTarget> documentTranslationTargets = new() { documentTranslationTarget };

            DocumentTranslationInput input = new() { storageType = "folder", source = documentTranslationSource, targets = documentTranslationTargets };

            TranslationService.ProcessingLocation = await TranslationService.SubmitTranslationRequest(input);
            Debug.WriteLine("Processing-Location: " + TranslationService.ProcessingLocation);
            if (TranslationService.ProcessingLocation is null)
            {
                Debug.WriteLine("ERROR: Start of translation job failed.");
                return;
            }

            //Check on status until status is in a final state
            StatusResponse statusResult;
            string lastActionTime = string.Empty;
            do
            {
                await Task.Delay(1000);
                statusResult = await TranslationService.CheckStatus();
                if (statusResult.lastActionDateTimeUtc != lastActionTime)
                {
                    //Raise the update event
                    if (StatusUpdate is not null) StatusUpdate(this, statusResult);
                    lastActionTime = statusResult.lastActionDateTimeUtc;
                }
            }
            while (
                  (statusResult.summary.inProgress != 0)
                ||(statusResult.status=="NotStarted")
                  );

            //Download the translations
            //Chance for optimization: Check status on the documents and start download immediately after each document is translated. 
            string directoryName = Path.GetDirectoryName(filestotranslate[0]);
            DirectoryInfo directory = Directory.CreateDirectory(directoryName + "." + tolanguage);
            List<Task> downloads = new();
            using (System.Threading.SemaphoreSlim semaphore = new(100))
            {
                await foreach (var blobItem in TranslationService.ContainerClientTarget.GetBlobsAsync())
                {
                    await semaphore.WaitAsync();
                    BlobClient blobClient = new(TranslationService.StorageConnectionString, TranslationService.ContainerClientTarget.Name, blobItem.Name);
                    BlobDownloadInfo blobDownloadInfo = await blobClient.DownloadAsync();
                    FileStream downloadFileStream = File.OpenWrite(directory.FullName + Path.DirectorySeparatorChar + blobItem.Name);
                    Task download = blobDownloadInfo.Content.CopyToAsync(downloadFileStream);
                    downloads.Add(download);
                    Debug.WriteLine("Downloaded: " + downloadFileStream.Name);
                    semaphore.Release();
                }
            }
            await Task.WhenAll(downloads);
            Debug.WriteLine("Download complete.");
            if (OnDownloadComplete is not null) OnDownloadComplete(this, EventArgs.Empty);
            await DeleteContainers();
            Debug.WriteLine("Run: Exiting.");
        }


        /// <summary>
        /// Delete the containers created by this instance.
        /// </summary>
        /// <param name="CleanUpAll">Optional: If set, delete all containers following the naming scheme that have been last accessed more than 10 days ago.</param>
        /// <returns>The task only</returns>
        private async Task DeleteContainers(bool CleanUpAll = false)
        {
            List<Task> deletionTasks = new();
            if (CleanUpAll)
            {
                BlobServiceClient blobServiceClient = new(TranslationService.StorageConnectionString);
                var resultSegment = blobServiceClient.GetBlobContainersAsync(BlobContainerTraits.None, BlobContainerStates.None, "doctr").AsPages();
                await foreach (Azure.Page<BlobContainerItem> containerPage in resultSegment)
                {
                    foreach (var containerItem in containerPage.Values)
                    {
                        BlobContainerClient client = new(TranslationService.StorageConnectionString, containerItem.Name);
                        if (containerItem.Name.EndsWith("src")
                            || (containerItem.Name.EndsWith("tgt"))
                            || (containerItem.Name.EndsWith("gls")))
                        {
                            if (containerItem.Properties.LastModified < (DateTimeOffset.UtcNow - TimeSpan.FromDays(10)))
                            deletionTasks.Add(client.DeleteAsync());
                        }
                    }
                }
            }
            else
            {
                deletionTasks.Add(TranslationService.ContainerClientSource.DeleteAsync());
                deletionTasks.Add(TranslationService.ContainerClientTarget.DeleteAsync());
                deletionTasks.Add(glossary.DeleteAsync());
            }
            await Task.WhenAll(deletionTasks);
            Debug.WriteLine("Containers deleted.");
        }

        public static string Normalize(string filename)
        {
            return Path.GetFileName(filename);
        }

        /// <summary>
        /// Filters the list of files to the ones matching the extension.
        /// </summary>
        /// <param name="fileNames">List of files to filter.</param>
        /// <param name="validExtensions">Hash of valid extensions.</param>
        /// <param name="discarded">The files that were discarded</param>
        /// <returns>Tuple of the filtered list and the discards.</returns>
        public static (List<string>, List<string>) FilterByExtension(List<string> fileNames, HashSet<string> validExtensions)
        {
            if (fileNames is null) return (null, null);
            List<string> validNames = new();
            List<string> discardedNames = new();
            foreach(string filename in fileNames)
            {
                if (validExtensions.Contains(Path.GetExtension(filename).ToLowerInvariant())) validNames.Add(filename);
                else discardedNames.Add(filename);
            }
            return (validNames, discardedNames);
        }

    }
}
