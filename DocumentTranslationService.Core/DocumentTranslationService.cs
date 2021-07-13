﻿using Azure.Storage.Blobs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Text.Json;
using System.Net.Http;
using System.Text;
using System.Linq;
using Azure.AI.Translation.Document;

namespace DocumentTranslationService.Core
{
    public partial class DocumentTranslationService
    {
        #region Properties
        /// <summary>
        /// The "Connection String" of the Azure blob storage resource. Get from properties of Azure storage.
        /// </summary>
        public string StorageConnectionString { get; } = string.Empty;

        /// <summary>
        /// Holds the Custom Translator category.
        /// </summary>
        public string Category { get; set; }

        /// <summary>
        /// Your Azure Translator subscription key. Get from properties of the Translator resource
        /// </summary>
        public string SubscriptionKey { get; } = string.Empty;

        /// <summary>
        /// The region of your Translator subscription.
        /// Needed only for text translation; can remain empty for document translation.
        /// </summary>
        public string AzureRegion { get; set; }

        /// <summary>
        /// The name of the Azure Translator resource
        /// </summary>
        public string AzureResourceName { get; } = string.Empty;

        /// <summary>
        /// In case of a service error exception, pick up the error message here. 
        /// </summary>
        public StatusResponse ErrorResponse { get; private set; }

        internal string ProcessingLocation { get; set; } = string.Empty;

        internal BlobContainerClient ContainerClientSource { get; set; }
        internal BlobContainerClient ContainerClientTarget { get; set; }

        private readonly DocumentTranslationClient documentTranslationClient;

        #endregion Properties
        #region Constants
        /// <summary>
        /// The base URL template for making translation requests.
        /// {0} is the name of the Translator resource.
        /// </summary>
        //private const string baseUriTemplate = ".cognitiveservices.azure.com/translator/text/batch/v1.0";
        private const string baseUriTemplate = ".cognitiveservices.azure.com/";
        #endregion Constants
        #region Methods

        /// <summary>
        /// Constructor
        /// </summary>
        public DocumentTranslationService(string SubscriptionKey, string AzureResourceName, string StorageConnectionString)
        {
            this.SubscriptionKey = SubscriptionKey;
            this.AzureResourceName = AzureResourceName;
            this.StorageConnectionString = StorageConnectionString;
            documentTranslationClient = new(new Uri("https://" + AzureResourceName + baseUriTemplate), new Azure.AzureKeyCredential(SubscriptionKey));
        }

        /// <summary>
        /// Fires when initialization is complete.
        /// </summary>
        public event EventHandler OnInitializeComplete;

        /// <summary>
        /// Fills the properties with values from the service. 
        /// </summary>
        /// <returns></returns>
        public async Task InitializeAsync()
        {
            if (String.IsNullOrEmpty(AzureResourceName)) throw new CredentialsException("name");
            if (String.IsNullOrEmpty(SubscriptionKey)) throw new CredentialsException("key");
            List<Task> tasks = new();
            tasks.Add(GetDocumentFormatsAsync());
            tasks.Add(GetGlossaryFormatsAsync());
            tasks.Add(GetLanguagesAsync());
            await Task.WhenAll(tasks);
            if (OnInitializeComplete is not null) OnInitializeComplete(this, EventArgs.Empty);
        }

        /// <summary>
        /// Retrieve the status of the translation progress.
        /// </summary>
        /// <returns></returns>
        public async Task<StatusResponse> CheckStatusAsync()
        {
            using HttpClient client = new();
            using HttpRequestMessage request = new() { Method = HttpMethod.Get, RequestUri = new Uri(ProcessingLocation) };
            request.Headers.Add("Ocp-Apim-Subscription-Key", SubscriptionKey);
            HttpResponseMessage response = await client.SendAsync(request);
            string result = await response.Content.ReadAsStringAsync();
            StatusResponse statusResponse = JsonSerializer.Deserialize<StatusResponse>(result, new JsonSerializerOptions { IncludeFields = true });
            Debug.WriteLine("CheckStatus: Status: " + statusResponse.status);
            Debug.WriteLine("CheckStatus: inProgress: " + statusResponse.summary.inProgress);
            Debug.WriteLine("Status Result: " + result.ToString());
            return statusResponse;
        }

        /// <summary>
        /// Cancels an ongoing translation run. 
        /// </summary>
        /// <returns></returns>
        public async Task<StatusResponse> CancelRunAsync()
        {
            using HttpClient client = new();
            using HttpRequestMessage request = new() { Method = HttpMethod.Delete, RequestUri = new Uri(ProcessingLocation) };
            request.Headers.Add("Ocp-Apim-Subscription-Key", SubscriptionKey);
            HttpResponseMessage response = await client.SendAsync(request);
            string result = await response.Content.ReadAsStringAsync();
            StatusResponse statusResponse = JsonSerializer.Deserialize<StatusResponse>(result, new JsonSerializerOptions { IncludeFields = true });
            Debug.WriteLine("CancelStatus: Status: " + statusResponse.status);
            Debug.WriteLine("CancelStatus: inProgress: " + statusResponse.summary.inProgress);
            Debug.WriteLine("CancelStatus Result: " + result.ToString());
            return statusResponse;
        }


        /// <summary>
        /// Format and submit the translation request to the Document Translation Service. 
        /// </summary>
        /// <param name="input">An object defining the input of what to translate</param>
        /// <returns>The status URL</returns>
        public async Task<string> SubmitTranslationRequestAsync(DocumentTranslationInput input)
        {
            if (String.IsNullOrEmpty(AzureResourceName)) throw new CredentialsException("name");
            if (String.IsNullOrEmpty(SubscriptionKey)) throw new CredentialsException("key");
            if (String.IsNullOrEmpty(StorageConnectionString)) throw new CredentialsException("storage");

            List<DocumentTranslationInput> documentTranslationInputs = new() { input };
            DocumentTranslationRequest documentTranslationRequest = new() { inputs = documentTranslationInputs };

            string requestJson = JsonSerializer.Serialize(documentTranslationRequest, new JsonSerializerOptions() { IncludeFields = true });
            Debug.WriteLine("SubmitTranslationRequest: RequestJson: " + requestJson);

            for (int i = 0; i < 3; i++)
            {
                HttpRequestMessage request = new();
                request.Method = HttpMethod.Post;
                request.RequestUri = new Uri("https://" + AzureResourceName + baseUriTemplate + "/batches");
                request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                request.Headers.Add("Ocp-Apim-Subscription-Key", SubscriptionKey);

                HttpClient client = new();
                HttpResponseMessage response = await client.SendAsync(request);
                Debug.WriteLine("Translation Request response code: " + response.StatusCode);

                if (response.IsSuccessStatusCode)
                {
                    if (response.Headers.TryGetValues("Operation-Location", out IEnumerable<string> values))
                    {
                        return values.First();
                    }
                }
                else
                {
                    string resp = await response.Content.ReadAsStringAsync();
                    Debug.WriteLine("Response content: " + resp);
                    if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    {
                        this.ErrorResponse = JsonSerializer.Deserialize<StatusResponse>(resp, new JsonSerializerOptions { IncludeFields = true });
                        throw new ServiceErrorException();
                    }
                    await Task.Delay(1000);
                }
            }
            return null;
        }

        #endregion Methods
    }
}

