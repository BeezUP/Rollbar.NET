﻿[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("UnitTest.Rollbar")]

namespace Rollbar
{
    using System;
    using System.Linq;
    using System.Text;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;

    using Newtonsoft.Json;

    using Rollbar.DTOs;
    using Rollbar.Diagnostics;
    using Rollbar.Serialization.Json;
    using Rollbar.PayloadTruncation;
    using Rollbar.PayloadScrubbing;

    /// <summary>
    /// Client for accessing the Rollbar API
    /// </summary>
    internal class RollbarClient 
    {
        /// <summary>
        /// The rollbar logger
        /// </summary>
        private readonly RollbarLogger _rollbarLogger;

        /// <summary>
        /// The HTTP client
        /// </summary>
        private readonly HttpClient _httpClient;

        /// <summary>
        /// The payload post URI
        /// </summary>
        private readonly Uri _payloadPostUri;

        /// <summary>
        /// The payload truncation strategy
        /// </summary>
        private readonly IterativeTruncationStrategy _payloadTruncationStrategy;

        /// <summary>
        /// The payload scrubber
        /// </summary>
        private readonly RollbarPayloadScrubber _payloadScrubber;

        /// <summary>
        /// Initializes a new instance of the <see cref="RollbarClient" /> class.
        /// </summary>
        /// <param name="rollbarLogger">The rollbar logger.</param>
        public RollbarClient(RollbarLogger rollbarLogger)
        {
            Assumption.AssertNotNull(rollbarLogger, nameof(rollbarLogger));
            Assumption.AssertNotNull(rollbarLogger.Config, nameof(rollbarLogger.Config));

            this._rollbarLogger = rollbarLogger;

            this._payloadPostUri = 
                new Uri($"{this._rollbarLogger.Config.EndPoint}item/");
            this._httpClient = 
                RollbarQueueController.Instance.ProvideHttpClient(
                    this._rollbarLogger.Config.ProxyAddress,
                    this._rollbarLogger.Config.ProxyUsername,
                    this._rollbarLogger.Config.ProxyPassword
                    );

            var header = new MediaTypeWithQualityHeaderValue("application/json");
            if (!this._httpClient.DefaultRequestHeaders.Accept.Contains(header))
            {
                this._httpClient.DefaultRequestHeaders.Accept.Add(header);
            }

            var sp = ServicePointManager.FindServicePoint(new Uri(this._rollbarLogger.Config.EndPoint));
            try
            {
                sp.ConnectionLeaseTimeout = 60 * 1000; // 1 minute
            }
#pragma warning disable CS0168 // Variable is declared but never used
#pragma warning disable IDE0059 // Variable is declared but never used
            catch (NotImplementedException ex)
#pragma warning restore CS0168 // Variable is declared but never used
#pragma warning restore IDE0059 // Variable is declared but never used
            {
                // just a crash prevention.
                // this is a work around the unimplemented property within Mono runtime...
            }

            this._payloadTruncationStrategy = new IterativeTruncationStrategy();
            this._payloadScrubber = new RollbarPayloadScrubber(this._rollbarLogger.Config.GetFieldsToScrub());
        }

        /// <summary>
        /// Gets the configuration.
        /// </summary>
        /// <value>The configuration.</value>
        public IRollbarConfig Config { get { return this._rollbarLogger.Config; } }

        /// <summary>
        /// Posts as json.
        /// </summary>
        /// <param name="payloadBundle">The payload bundle.</param>
        /// <returns>RollbarResponse.</returns>
        public RollbarResponse PostAsJson(PayloadBundle payloadBundle)
        {
            Assumption.AssertNotNull(payloadBundle, nameof(payloadBundle));

            var task = this.PostAsJsonAsync(payloadBundle);

            task.Wait();

            return task.Result;
        }

        /// <summary>
        /// post as json as an asynchronous operation.
        /// </summary>
        /// <param name="payloadBundle">The payload bundle.</param>
        /// <returns>Task&lt;RollbarResponse&gt;.</returns>
        public async Task<RollbarResponse> PostAsJsonAsync(PayloadBundle payloadBundle)
        {
            Assumption.AssertNotNull(payloadBundle, nameof(payloadBundle));

            // make sure there anything meaningful to send:
            if (!EnsureHttpContentToSend(payloadBundle))
            {
                return null;
            }

            // build an HTTP request:
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, this._payloadPostUri);
            const string accessTokenHeader = "X-Rollbar-Access-Token";
            request.Headers.Add(accessTokenHeader, this._rollbarLogger.Config.AccessToken);
            request.Content = payloadBundle.AsHttpContentToSend;

            // send the request:
            var postResponse = await this._httpClient.SendAsync(request);

            RollbarResponse response = null;
            if (postResponse.IsSuccessStatusCode)
            {
                string reply = 
                    await postResponse.Content.ReadAsStringAsync();
                response = 
                    JsonConvert.DeserializeObject<RollbarResponse>(reply);
                response.RollbarRateLimit = 
                    new RollbarRateLimit(postResponse.Headers);
                response.HttpDetails =
                    $"Response: {postResponse}"
                    + Environment.NewLine
                    + $"Request: {postResponse.RequestMessage}"
                    + Environment.NewLine
                    ;
            }
            else
            {
                postResponse.EnsureSuccessStatusCode();
            }

            postResponse.Dispose();

            return response;
        }

        /// <summary>
        /// Ensures the HTTP content to send.
        /// </summary>
        /// <param name="payloadBundle">The payload bundle.</param>
        /// <returns><c>true</c> if XXXX, <c>false</c> otherwise.</returns>
        private bool EnsureHttpContentToSend(PayloadBundle payloadBundle)
        {
            if (payloadBundle.AsHttpContentToSend != null)
            {
                return true;
            }

            Payload payload = payloadBundle.GetPayload();
            Assumption.AssertNotNull(payload, nameof(payload));

            if (!TruncatePayload(payloadBundle))
            {
                return false;
            }

            if (!ScrubHttpMessages(payloadBundle))
            {
                return false;
            }

            string jsonData = SerializePayloadAsJsonString(payloadBundle);
            if (string.IsNullOrWhiteSpace(jsonData))
            {
                return false;
            }

            try
            {
                //jsonData = ScrubPayload(jsonData, this._rollbarLogger.Config.GetFieldsToScrub());
                jsonData = ScrubPayload(jsonData);
            }
            catch (System.Exception exception)
            {
                RollbarErrorUtility.Report(
                    this._rollbarLogger,
                    payload,
                    InternalRollbarError.PayloadScrubbingError,
                    "While scrubbing a payload...",
                    exception,
                    payloadBundle
                    );

                return false;
            }

            payloadBundle.AsHttpContentToSend =
                new StringContent(jsonData, Encoding.UTF8, "application/json"); //CONTENT-TYPE header

            Assumption.AssertNotNull(payloadBundle.AsHttpContentToSend, nameof(payloadBundle.AsHttpContentToSend));
            Assumption.AssertTrue(string.Equals(payload.AccessToken, this._rollbarLogger.Config.AccessToken), nameof(payload.AccessToken));

            return true;
        }

        /// <summary>
        /// Scrubs the HTTP messages.
        /// </summary>
        /// <param name="payloadBundle">The payload bundle.</param>
        /// <returns><c>true</c> if XXXX, <c>false</c> otherwise.</returns>
        private bool ScrubHttpMessages(PayloadBundle payloadBundle)
        {
            Payload payload = payloadBundle.GetPayload();

            DTOs.Request request = payload.Data.Request;
            if (request?.PostBody is string requestBody)
            {
                if (request.Headers.TryGetValue("Content-Type", out string contentTypeHeader))
                {
                    request.PostBody = 
                        this.ScrubHttpMessageBodyContentString(
                            requestBody, 
                            contentTypeHeader,
                            this._payloadScrubber.ScrubMask, 
                            this._payloadScrubber.PayloadFieldNames,
                            this._payloadScrubber.HttpRequestBodyPaths);
                }
            }

            DTOs.Response response = payload.Data.Response;
            if (response?.Body is string responseBody)
            {
                if (response.Headers.TryGetValue("Content-Type", out string contentTypeHeader))
                {
                    response.Body =
                        this.ScrubHttpMessageBodyContentString(
                            responseBody,
                            contentTypeHeader,
                            this._payloadScrubber.ScrubMask,
                            this._payloadScrubber.PayloadFieldNames,
                            this._payloadScrubber.HttpResponseBodyPaths);
                }
            }

            return true;
        }

        /// <summary>
        /// Scrubs the HTTP message body content string.
        /// </summary>
        /// <param name="body">The body.</param>
        /// <param name="contentTypeHeaderValue">The content type header value.</param>
        /// <param name="scrubMask">The scrub mask.</param>
        /// <param name="scrubFields">The scrub fields.</param>
        /// <param name="scrubPaths">The scrub paths.</param>
        /// <returns>System.String.</returns>
        private string ScrubHttpMessageBodyContentString(
            string body, 
            string contentTypeHeaderValue, 
            string scrubMask, 
            string[] scrubFields, 
            string[] scrubPaths
            )
        {
            string contentType = contentTypeHeaderValue.ToLower();
            if (contentType.Contains("json"))
            {
                return new JsonStringScrubber(scrubMask, scrubFields, scrubPaths).Scrub(body);
            }
            else if (contentType.Contains("xml"))
            {
                return new XmlStringScrubber(scrubMask, scrubFields, scrubPaths).Scrub(body);
            }
            else if (contentType.Contains("form-data"))
            {
                return new FormDataStringScrubber(contentTypeHeaderValue, scrubMask, scrubFields, scrubPaths).Scrub(body);
            }
            else
            {
                return new StringScrubber(scrubMask, scrubFields, scrubPaths).Scrub(body);
            }
        }

        /// <summary>
        /// Serializes the payload as json string.
        /// </summary>
        /// <param name="payloadBundle">The payload bundle.</param>
        /// <returns>System.String.</returns>
        private string SerializePayloadAsJsonString(PayloadBundle payloadBundle)
        {
            Payload payload = payloadBundle.GetPayload();

            string jsonData;
            try
            {
                jsonData = JsonConvert.SerializeObject(payload);
            }
            catch (System.Exception exception)
            {
                RollbarErrorUtility.Report(
                    this._rollbarLogger,
                    payload,
                    InternalRollbarError.PayloadSerializationError,
                    "While serializing a payload...",
                    exception,
                    payloadBundle
                );

                return null;
            }

            return jsonData;
        }

        /// <summary>
        /// Truncates the payload.
        /// </summary>
        /// <param name="payloadBundle">The payload bundle.</param>
        /// <returns><c>true</c> if XXXX, <c>false</c> otherwise.</returns>
        private bool TruncatePayload(PayloadBundle payloadBundle)
        {
            Payload payload = payloadBundle.GetPayload();

            if (this._payloadTruncationStrategy.Truncate(payload) > this._payloadTruncationStrategy.MaxPayloadSizeInBytes)
            {
                var exception = new ArgumentOutOfRangeException(
                    paramName: nameof(payload),
                    message: $"Payload size exceeds {this._payloadTruncationStrategy.MaxPayloadSizeInBytes} bytes limit!"
                );

                RollbarErrorUtility.Report(
                    this._rollbarLogger,
                    payload,
                    InternalRollbarError.PayloadTruncationError,
                    "While truncating a payload...",
                    exception,
                    payloadBundle
                );

                return false;
            }

            return true;
        }

        /// <summary>
        /// Scrubs the payload.
        /// </summary>
        /// <param name="payload">The payload.</param>
        /// <returns>System.String.</returns>
        internal string ScrubPayload(string payload)
        {
            var scrubbedPayload = this._payloadScrubber.ScrubPayload(payload);
            return scrubbedPayload;
        }
    }
}
