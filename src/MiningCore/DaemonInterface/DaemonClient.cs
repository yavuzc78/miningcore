﻿/*
Copyright 2017 Coin Foundry (coinfoundry.org)
Authors: Oliver Weichhold (oliver@weichhold.com)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiningCore.Buffers;
using MiningCore.Configuration;
using MiningCore.Extensions;
using MiningCore.JsonRpc;
using MiningCore.Stratum;
using MiningCore.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using Contract = MiningCore.Contracts.Contract;

namespace MiningCore.DaemonInterface
{
    /// <summary>
    /// Provides JsonRpc based interface to a cluster of blockchain daemons for improved fault tolerance
    /// </summary>
    public class DaemonClient
    {
        public DaemonClient(JsonSerializerSettings serializerSettings)
        {
            Contract.RequiresNonNull(serializerSettings, nameof(serializerSettings));

            this.serializerSettings = serializerSettings;

            serializer = new JsonSerializer
            {
                ContractResolver = serializerSettings.ContractResolver
            };
        }

        private readonly JsonSerializerSettings serializerSettings;
        private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

        protected DaemonEndpointConfig[] endPoints;
        private Dictionary<DaemonEndpointConfig, HttpClient> httpClients;
        private string rpcLocation;
        private readonly JsonSerializer serializer;

        #region API-Surface

        public void Configure(DaemonEndpointConfig[] endPoints, string rpcLocation = null,
            string digestAuthRealm = null)
        {
            Contract.RequiresNonNull(endPoints, nameof(endPoints));
            Contract.Requires<ArgumentException>(endPoints.Length > 0, $"{nameof(endPoints)} must not be empty");

            this.endPoints = endPoints;
            this.rpcLocation = rpcLocation;

            // create one HttpClient instance per endpoint that carries the associated credentials
            httpClients = endPoints.ToDictionary(endpoint => endpoint, endpoint =>
                new HttpClient(new HttpClientHandler
                {
                    Credentials = new NetworkCredential(endpoint.User, endpoint.Password),
                    PreAuthenticate = true,
                    AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip
                }));
        }

        /// <summary>
        /// Executes the request against all configured demons and returns their responses as an array
        /// </summary>
        /// <param name="method"></param>
        /// <returns></returns>
        public Task<DaemonResponse<JToken>[]> ExecuteCmdAllAsync(string method)
        {
            return ExecuteCmdAllAsync<JToken>(method);
        }

        /// <summary>
        /// Executes the request against all configured demons and returns their responses as an array
        /// </summary>
        /// <typeparam name="TResponse"></typeparam>
        /// <param name="method"></param>
        /// <param name="payload"></param>
        /// <returns></returns>
        public async Task<DaemonResponse<TResponse>[]> ExecuteCmdAllAsync<TResponse>(string method,
            object payload = null, JsonSerializerSettings payloadJsonSerializerSettings = null)
            where TResponse : class
        {
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(method), $"{nameof(method)} must not be empty");

            logger.LogInvoke(new[] { method });

            var tasks = endPoints.Select(endPoint => BuildRequestTask(endPoint, method, payload, payloadJsonSerializerSettings)).ToArray();

            try
            {
                await Task.WhenAll(tasks);
            }

            catch(Exception)
            {
                // ignored
            }

            var results = tasks.Select((x, i) => MapDaemonResponse<TResponse>(i, x))
                .ToArray();

            return results;
        }

        /// <summary>
        /// Executes the request against all configured demons and returns the first successful response
        /// </summary>
        /// <param name="method"></param>
        /// <returns></returns>
        public Task<DaemonResponse<JToken>> ExecuteCmdAnyAsync(string method)
        {
            return ExecuteCmdAnyAsync<JToken>(method);
        }

        /// <summary>
        /// Executes the request against all configured demons and returns the first successful response
        /// </summary>
        /// <typeparam name="TResponse"></typeparam>
        /// <param name="method"></param>
        /// <param name="payload"></param>
        /// <returns></returns>
        public async Task<DaemonResponse<TResponse>> ExecuteCmdAnyAsync<TResponse>(string method, object payload = null,
            JsonSerializerSettings payloadJsonSerializerSettings = null)
            where TResponse : class
        {
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(method), $"{nameof(method)} must not be empty");

            logger.LogInvoke(new[] { method });

            var tasks = endPoints.Select(endPoint => BuildRequestTask(endPoint, method, payload, payloadJsonSerializerSettings)).ToArray();

            var taskFirstCompleted = await Task.WhenAny(tasks);
            var result = MapDaemonResponse<TResponse>(0, taskFirstCompleted);
            return result;
        }

        /// <summary>
        /// Executes the request against all configured demons and returns the first successful response
        /// </summary>
        /// <param name="method"></param>
        /// <returns></returns>
        public Task<DaemonResponse<JToken>> ExecuteCmdSingleAsync(string method)
        {
            return ExecuteCmdAnyAsync<JToken>(method);
        }

        /// <summary>
        /// Executes the request against all configured demons and returns the first successful response
        /// </summary>
        /// <typeparam name="TResponse"></typeparam>
        /// <param name="method"></param>
        /// <param name="payload"></param>
        /// <returns></returns>
        public async Task<DaemonResponse<TResponse>> ExecuteCmdSingleAsync<TResponse>(string method, object payload = null,
            JsonSerializerSettings payloadJsonSerializerSettings = null)
            where TResponse : class
        {
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(method), $"{nameof(method)} must not be empty");

            logger.LogInvoke(new[] { method });

            var task = BuildRequestTask(endPoints.First(), method, payload, payloadJsonSerializerSettings);
            await task;

            var result = MapDaemonResponse<TResponse>(0, task);
            return result;
        }

        /// <summary>
        /// Executes the requests against all configured demons and returns the first successful response array
        /// </summary>
        /// <returns></returns>
        public async Task<DaemonResponse<JToken>[]> ExecuteBatchAnyAsync(params DaemonCmd[] batch)
        {
            Contract.RequiresNonNull(batch, nameof(batch));

            logger.LogInvoke(batch.Select(x => x.Method).ToArray());

            var tasks = endPoints.Select(endPoint => BuildBatchRequestTask(endPoint, batch)).ToArray();

            var taskFirstCompleted = await Task.WhenAny(tasks);
            var result = MapDaemonBatchResponse(0, taskFirstCompleted);
            return result;
        }

        /// <summary>
        /// Executes the request against all configured demons and returns the first successful response
        /// </summary>
        /// <typeparam name="TResponse"></typeparam>
        /// <param name="method"></param>
        /// <param name="payload"></param>
        /// <returns></returns>
        public IObservable<PooledArraySegment<byte>> WebsocketSubscribe(string method, object payload = null,
            JsonSerializerSettings payloadJsonSerializerSettings = null)
        {
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(method), $"{nameof(method)} must not be empty");

            logger.LogInvoke(new[] { method });

            return Observable.Merge(endPoints
                    .Where(endPoint=> endPoint.PortWs.HasValue)
                    .Select(endPoint => WebsocketSubscribeEndpoint(endPoint, method, payload, payloadJsonSerializerSettings)))
                .Publish()
                .RefCount();
        }

        #endregion // API-Surface

        private async Task<JsonRpcResponse> BuildRequestTask(DaemonEndpointConfig endPoint, string method, object payload,
            JsonSerializerSettings payloadJsonSerializerSettings = null)
        {
            var rpcRequestId = GetRequestId();

            // build rpc request
            var rpcRequest = new JsonRpcRequest<object>(method, payload, rpcRequestId);

            // build request url
            var requestUrl = $"http://{endPoint.Host}:{endPoint.Port}";
            if (!string.IsNullOrEmpty(rpcLocation))
                requestUrl += $"/{rpcLocation}";

            // build http request
            var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
            var json = JsonConvert.SerializeObject(rpcRequest, payloadJsonSerializerSettings ?? serializerSettings);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            // build auth header
            if (!string.IsNullOrEmpty(endPoint.User))
            {
                var auth = $"{endPoint.User}:{endPoint.Password}";
                var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(auth));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64);
            }

            // send request
            var httpClient = httpClients[endPoint];
            var response = await httpClient.SendAsync(request);

            // read response
            json = await response.Content.ReadAsStringAsync();

            // deserialize response
            var result = JsonConvert.DeserializeObject<JsonRpcResponse>(json, serializerSettings);
            return result;
        }

        private async Task<JsonRpcResponse<JToken>[]> BuildBatchRequestTask(DaemonEndpointConfig endPoint,
            DaemonCmd[] batch)
        {
            // build rpc request
            var rpcRequests = batch.Select(x => new JsonRpcRequest<object>(x.Method, x.Payload, GetRequestId()));

            // build request url
            var requestUrl = $"http://{endPoint.Host}:{endPoint.Port}";
            if (!string.IsNullOrEmpty(rpcLocation))
                requestUrl += $"/{rpcLocation}";

            // build http request
            var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
            var json = JsonConvert.SerializeObject(rpcRequests, serializerSettings);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            // build auth header
            if (!string.IsNullOrEmpty(endPoint.User))
            {
                var auth = $"{endPoint.User}:{endPoint.Password}";
                var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(auth));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64);
            }

            // send request
            var httpClient = httpClients[endPoint];
            var response = await httpClient.SendAsync(request);

            // check success
            if (!response.IsSuccessStatusCode)
                throw new DaemonClientException(response.StatusCode, response.ReasonPhrase);

            // read response
            json = await response.Content.ReadAsStringAsync();

            // deserialize response
            var result = JsonConvert.DeserializeObject<JsonRpcResponse<JToken>[]>(json, serializerSettings);
            return result;
        }

        protected string GetRequestId()
        {
            var rpcRequestId = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + StaticRandom.Next(10)).ToString();
            return rpcRequestId;
        }

        private DaemonResponse<TResponse> MapDaemonResponse<TResponse>(int i, Task<JsonRpcResponse> x)
            where TResponse : class
        {
            var resp = new DaemonResponse<TResponse>
            {
                Instance = endPoints[i]
            };

            if (x.IsFaulted)
            {
                Exception inner;

                if (x.Exception.InnerExceptions.Count == 1)
                    inner = x.Exception.InnerException;
                else
                    inner = x.Exception;

                resp.Error = new JsonRpcException(-500, x.Exception.Message, null, inner);
            }

            else
            {
                Debug.Assert(x.IsCompletedSuccessfully);

                if (x.Result?.Result is JToken token)
                    resp.Response = token?.ToObject<TResponse>(serializer);
                else
                    resp.Response = (TResponse) x.Result?.Result;

                resp.Error = x.Result?.Error;
            }

            return resp;
        }

        private DaemonResponse<JToken>[] MapDaemonBatchResponse(int i, Task<JsonRpcResponse<JToken>[]> x)
        {
            if (x.IsFaulted)
                return x.Result?.Select(y => new DaemonResponse<JToken>
                {
                    Instance = endPoints[i],
                    Error = new JsonRpcException(-500, x.Exception.Message, null)
                }).ToArray();

            Debug.Assert(x.IsCompletedSuccessfully);

            return x.Result?.Select(y => new DaemonResponse<JToken>
            {
                Instance = endPoints[i],
                Response = y.Result != null ? JToken.FromObject(y.Result) : null,
                Error = y.Error
            }).ToArray();
        }

        private IObservable<PooledArraySegment<byte>> WebsocketSubscribeEndpoint(DaemonEndpointConfig endPoint, string method, object payload = null,
            JsonSerializerSettings payloadJsonSerializerSettings = null)
        {
            return Observable.Defer(()=> Observable.Create<PooledArraySegment<byte>>(obs =>
            {
                var cts = new CancellationTokenSource();

                var task = new Task(async () =>
                {
                    using(cts)
                    {
                        while(!cts.IsCancellationRequested)
                        {
                            try
                            {
                                using (var plb = new PooledLineBuffer())
                                {
                                    using(var client = new ClientWebSocket())
                                    {
                                        // connect
                                        var uri = new Uri($"ws://{endPoint.Host}:{endPoint.PortWs.Value}");
                                        await client.ConnectAsync(uri, cts.Token);

                                        // subscribe
                                        var buf = new byte[0x400];
                                        var request = new JsonRpcRequest(method, payload, GetRequestId());
                                        var json = JsonConvert.SerializeObject(request, payloadJsonSerializerSettings).ToCharArray();
                                        var byteLength = Encoding.UTF8.GetBytes(json, 0, json.Length, buf, 0);
                                        var segment = new ArraySegment<byte>(buf, 0, byteLength);
                                        await client.SendAsync(segment, WebSocketMessageType.Text, true, cts.Token);

                                        // stream response
                                        while(!cts.IsCancellationRequested && client.State == WebSocketState.Open)
                                        {
                                            segment = new ArraySegment<byte>(buf);
                                            var response = await client.ReceiveAsync(buf, cts.Token);

                                            if (response.MessageType == WebSocketMessageType.Binary)
                                                throw new InvalidDataException("expected text, received binary data");

                                            plb.Receive(segment, response.Count,
                                                (buffer, arr, count) => Array.Copy(buffer.Array, buffer.Offset, arr, 0, count),
                                                data =>
                                                {
                                                    obs.OnNext(data);
                                                }, response.EndOfMessage);
                                        }
                                    }
                                }
                            }

                            catch (Exception ex)
                            {
                                logger.Error(()=> $"{ex.GetType().Name} '{ex.Message}' while streaming websocket responses. Reconnecting in 5s");
                            }

                            await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                        }
                    }
                }, TaskCreationOptions.LongRunning);

                task.Start();

                return Disposable.Create(() =>
                {
                    cts.Cancel();
                });
            }));
        }
    }
}
