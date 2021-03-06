﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using GeoCoordinatePortable;
using Google.Protobuf;
using Google.Protobuf.Collections;
using POGOLib.Logging;
using POGOLib.Util;
using POGOProtos.Enums;
using POGOProtos.Map;
using POGOProtos.Networking.Envelopes;
using POGOProtos.Networking.Requests;
using POGOProtos.Networking.Requests.Messages;
using POGOProtos.Networking.Responses;
using System.Threading.Tasks;

namespace POGOLib.Net
{
    public class RpcClient : IDisposable
    {

        /// <summary>
        ///     The <see cref="HttpClient" /> used for communication with PokémonGo.
        /// </summary>
        private readonly HttpClient _httpClient;

        /// <summary>
        ///     The authenticated <see cref="Session" />.
        /// </summary>
        private readonly Session _session;

        /// <summary>
        ///     The class responsible for all encryption / signing regarding <see cref="RpcClient"/>.
        /// </summary>
        private readonly RpcEncryption _rpcEncryption;

        /// <summary>
        ///     The current 'unique' request id we are at.
        /// </summary>
        private ulong _requestId;

        /// <summary>
        ///     The rpc url we have to call.
        /// </summary>
        private string _requestUrl;

        private List<RequestType> _defaultRequests = new List<RequestType>
        {
            RequestType.CheckChallenge,
            RequestType.GetHatchedEggs,
            RequestType.GetInventory,
            RequestType.CheckAwardedBadges,
            RequestType.DownloadSettings
        };

        internal RpcClient(Session session)
        {
            _session = session;
            _rpcEncryption = new RpcEncryption(session);

            var httpClientHandler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            _httpClient = new HttpClient(httpClientHandler);
            _httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd(_session.Device.UserAgent);
            _httpClient.DefaultRequestHeaders.ExpectContinue = false;
            _requestId = (ulong)new Random().Next(100000000, 999999999);
        }

        internal DateTime LastRpcRequest { get; private set; }

        internal DateTime LastRpcMapObjectsRequest { get; private set; }

        internal GeoCoordinate LastGeoCoordinateMapObjectsRequest { get; private set; } = new GeoCoordinate();

        /// <summary>
        ///     Sends all requests which the (android-)client sends on startup
        /// </summary>
        internal async Task<bool> Startup()
        {
            try
            {
                // Send GetPlayer to check if we're connected and authenticated
                GetPlayerResponse playerResponse;
                do
                {
                    var response = await SendRemoteProcedureCall(new []
                    {
                        new Request
                        {
                            RequestType = RequestType.GetPlayer
                        },
                        new Request
                        {
                            RequestType = RequestType.CheckChallenge,
                            RequestMessage = new CheckChallengeMessage
                            {
                                DebugRequest = false
                            }.ToByteString()
                        }
                    });
                    playerResponse = GetPlayerResponse.Parser.ParseFrom(response);
                    if (!playerResponse.Success)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(1000));
                    }
                } while (!playerResponse.Success);

                _session.Player.Data = playerResponse.PlayerData;
            }
            catch (Exception)
            {
                return false;
            }

            return true;
        }

        public async Task<GetAssetDigestResponse> GetAssets()
        {
            // check if template cache has been set

            // Get DownloadRemoteConfig
            var remoteConfigResponse = await SendRemoteProcedureCall(new Request
            {
                RequestType = RequestType.DownloadRemoteConfigVersion,
                RequestMessage = new DownloadRemoteConfigVersionMessage
                {
                    Platform = Platform.Android,
                    AppVersion = 2903
                }.ToByteString()
            });

            var remoteConfigParsed = DownloadRemoteConfigVersionResponse.Parser.ParseFrom(remoteConfigResponse);
            var timestamp = (ulong)TimeUtil.GetCurrentTimestampInMilliseconds();

            // TODO: the timestamp comparisons seem to be used for determining if the stored data is invalid and needs refreshed,
            //       however, looking at this code I'm not sure it's implemented correctly - or if these refactors still match the behavior of
            //       the previous code... same concern with the next method GetItemTemplates()..

            var cachedMsg = _session.DataCache.GetCachedAssetDigest();
            if (cachedMsg != null && remoteConfigParsed.AssetDigestTimestampMs <= timestamp)
            {
                return cachedMsg;
            }
            else
            {
                // GetAssetDigest
                var assetDigestResponse = await SendRemoteProcedureCall(new Request
                {
                    RequestType = RequestType.GetAssetDigest,
                    RequestMessage = new GetAssetDigestMessage
                    {
                        Platform = Platform.Android,
                        AppVersion = 2903
                    }.ToByteString()
                });
                var msg = GetAssetDigestResponse.Parser.ParseFrom(assetDigestResponse);
                _session.DataCache.SaveData(DataCacheExtensions.AssetDigestFile, msg);
                return msg;
            }
        }

        public async Task<DownloadItemTemplatesResponse> GetItemTemplates()
        {
            // Get DownloadRemoteConfig
            var remoteConfigResponse = await SendRemoteProcedureCall(new Request
            {
                RequestType = RequestType.DownloadRemoteConfigVersion,
                RequestMessage = new DownloadRemoteConfigVersionMessage
                {
                    Platform = Platform.Android,
                    AppVersion = 2903
                }.ToByteString()
            });

            var remoteConfigParsed = DownloadRemoteConfigVersionResponse.Parser.ParseFrom(remoteConfigResponse);
            var timestamp = (ulong)TimeUtil.GetCurrentTimestampInMilliseconds();

            var cachedMsg = _session.DataCache.GetCachedItemTemplates();
            if (cachedMsg != null && remoteConfigParsed.AssetDigestTimestampMs <= timestamp)
            {
                return cachedMsg;
            }
            else
            {
                // GetAssetDigest
                var itemTemplateResponse = await SendRemoteProcedureCall(new Request
                {
                    RequestType = RequestType.DownloadItemTemplates
                });
                var msg = DownloadItemTemplatesResponse.Parser.ParseFrom(itemTemplateResponse);
                _session.DataCache.SaveData(DataCacheExtensions.ItemTemplatesFile, msg);
                return msg;
            }
        }

        /// <summary>
        ///     It is not recommended to call this. Map objects will update automatically and fire the map update event.
        /// </summary>
        public async Task RefreshMapObjects()
        {
            var cellIds = MapUtil.GetCellIdsForLatLong(_session.Player.Coordinate.Latitude, _session.Player.Coordinate.Longitude);
            var sinceTimeMs = new List<long>(cellIds.Length);

            for (var i = 0; i < cellIds.Length; i++)
            {
                sinceTimeMs.Add(0);
            }

            var response = await SendRemoteProcedureCall(new Request
            {
                RequestType = RequestType.GetMapObjects,
                RequestMessage = new GetMapObjectsMessage
                {
                    CellId =
                    {
                        cellIds
                    },
                    SinceTimestampMs =
                    {
                        sinceTimeMs.ToArray()
                    },
                    Latitude = _session.Player.Coordinate.Latitude,
                    Longitude = _session.Player.Coordinate.Longitude
                }.ToByteString()
            });

            var mapObjects = GetMapObjectsResponse.Parser.ParseFrom(response);

            if (mapObjects.Status == MapObjectsStatus.Success)
            {
                Logger.Debug($"Received '{mapObjects.MapCells.Count}' map cells.");
                Logger.Debug($"Received '{mapObjects.MapCells.SelectMany(c => c.CatchablePokemons).Count()}' pokemons.");
                Logger.Debug($"Received '{mapObjects.MapCells.SelectMany(c => c.Forts).Count()}' forts.");
                if (mapObjects.MapCells.Count == 0)
                {
                    Logger.Error("We received 0 map cells, are your GPS coordinates correct?");
                    return;
                }
                _session.Map.Cells = mapObjects.MapCells;
            }
            else
            {
                Logger.Error($"GetMapObjects status is: '{mapObjects.Status}'.");
            }
        }

        /// <summary>
        ///     Gets the next <see cref="_requestId" /> for the <see cref="RequestEnvelope" />.
        /// </summary>
        /// <returns></returns>
        private ulong GetNextRequestId()
        {
            return _requestId++;
        }

        /// <summary>
        ///     Gets a collection of requests that should be sent in every request to PokémonGo along with your own
        ///     <see cref="Request" />.
        /// </summary>
        /// <returns></returns>
        private IEnumerable<Request> GetDefaultRequests()
        {
            var request = new List<Request>
            {
                new Request
                {
                    RequestType = RequestType.CheckChallenge,
                    RequestMessage = new CheckChallengeMessage
                    {
                        DebugRequest = false
                    }.ToByteString()
                },
                new Request
                {
                    RequestType = RequestType.GetHatchedEggs
                },
                new Request
                {
                    RequestType = RequestType.GetInventory,
                    RequestMessage = new GetInventoryMessage
                    {
                        LastTimestampMs = _session.Player.Inventory.LastInventoryTimestampMs
                    }.ToByteString()
                },
                new Request
                {
                    RequestType = RequestType.CheckAwardedBadges
                }
            };

            if (string.IsNullOrEmpty(_session.GlobalSettingsHash))
            {
                request.Add(new Request
                {
                    RequestType = RequestType.DownloadSettings
                });
            }
            else
            {
                request.Add(new Request
                {
                    RequestType = RequestType.DownloadSettings,
                    RequestMessage = new DownloadSettingsMessage
                    {
                        Hash = _session.GlobalSettingsHash
                    }.ToByteString()
                });
            }


            //If Incense is active we add this:
            //request.Add(new Request
            //{
            //    RequestType = RequestType.GetIncensePokemon,
            //    RequestMessage = new GetIncensePokemonMessage
            //    {
            //        PlayerLatitude = _session.Player.Coordinate.Latitude,
            //        PlayerLongitude = _session.Player.Coordinate.Longitude
            //    }.ToByteString()
            //});

            return request;
        }

        /// <summary>
        ///     Gets a <see cref="RequestEnvelope" /> with authentication data.
        /// </summary>
        /// <param name="request"></param>
        /// <param name="addDefaultRequests"></param>
        /// <returns></returns>
        private async Task<RequestEnvelope> GetRequestEnvelope(Request[] request, bool addDefaultRequests)
        {
            var requestEnvelope = new RequestEnvelope
            {
                StatusCode = 2,
                RequestId = GetNextRequestId(),
                Latitude = _session.Player.Coordinate.Latitude,
                Longitude = _session.Player.Coordinate.Longitude,
                Accuracy = _session.Player.Coordinate.HorizontalAccuracy,
                MsSinceLastLocationfix = 123 // TODO: Figure this out.
            };

            requestEnvelope.Requests.AddRange(request);

            if (addDefaultRequests)
                requestEnvelope.Requests.AddRange(GetDefaultRequests());

            if (_session.AccessToken.AuthTicket == null || _session.AccessToken.IsExpired)
            {
                if (_session.AccessToken.IsExpired)
                {
                    await _session.Reauthenticate();
                }

                requestEnvelope.AuthInfo = new RequestEnvelope.Types.AuthInfo
                {
                    Provider = _session.AccessToken.ProviderID,
                    Token = new RequestEnvelope.Types.AuthInfo.Types.JWT
                    {
                        Contents = _session.AccessToken.Token,
                        Unknown2 = 59
                    }
                };
            }
            else
            {
                requestEnvelope.AuthTicket = _session.AccessToken.AuthTicket;
            }

            requestEnvelope.PlatformRequests.Add(_rpcEncryption.GenerateSignature(requestEnvelope));

            return requestEnvelope;
        }

        /// <summary>
        ///     Prepares the <see cref="RequestEnvelope" /> to be sent with <see cref="_httpClient" />.
        /// </summary>
        /// <param name="requestEnvelope">The <see cref="RequestEnvelope" /> that will be send.</param>
        /// <returns><see cref="StreamContent" /> to be sent with <see cref="_httpClient" />.</returns>
        private ByteArrayContent PrepareRequestEnvelope(RequestEnvelope requestEnvelope)
        {
            var messageBytes = requestEnvelope.ToByteArray();

            // TODO: Compression?

            return new ByteArrayContent(messageBytes);
        }

        public async Task<ByteString> SendRemoteProcedureCall(RequestType requestType)
        {
            return await SendRemoteProcedureCall(new Request
            {
                RequestType = requestType
            });
        }

        public async Task<ByteString> SendRemoteProcedureCall(Request request)
        {
            return await SendRemoteProcedureCall(await GetRequestEnvelope(new[] {request}, true));
        }

        public async Task<ByteString> SendRemoteProcedureCall(Request[] request)
        {
            return await SendRemoteProcedureCall(await GetRequestEnvelope(request, false));
        }

        private async Task<ByteString> SendRemoteProcedureCall(RequestEnvelope requestEnvelope)
        {
            try
            {
                using (var requestData = PrepareRequestEnvelope(requestEnvelope))
                {
                    Logger.Debug($"Sending RPC Request: '{string.Join(", ", requestEnvelope.Requests.Select(x => x.RequestType))}'");

                    using (var response = await _httpClient.PostAsync(_requestUrl ?? Constants.ApiUrl, requestData))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            Logger.Debug(await response.Content.ReadAsStringAsync());

                            throw new Exception("Received a non-success HTTP status code from the RPC server, see the console for the response.");
                        }

                        var responseBytes = response.Content.ReadAsByteArrayAsync().Result;
                        var responseEnvelope = ResponseEnvelope.Parser.ParseFrom(responseBytes);

                        switch (responseEnvelope.StatusCode)
                        {
                            // Valid response.
                            case ResponseEnvelope.Types.StatusCode.Ok: 
                                // Success!?
                                break;
                        
                            // Valid response and new rpc url.
                            case ResponseEnvelope.Types.StatusCode.OkRpcUrlInResponse:
                                if (Regex.IsMatch(responseEnvelope.ApiUrl, "pgorelease\\.nianticlabs\\.com\\/plfe\\/\\d+"))
                                {
                                    _requestUrl = $"https://{responseEnvelope.ApiUrl}/rpc";
                                }
                                else
                                {
                                    throw new Exception($"Received an incorrect API url: '{responseEnvelope.ApiUrl}', status code was: '{responseEnvelope.StatusCode}'.");
                                }
                                break;

                            // The response envelope has api_rul set. TODO: Use this
//                        case ResponseEnvelope.Types.StatusCode.OkRpcUrlInResponse: 
//                            Logger.Warn($"We are sending requests too fast, sleeping for {Configuration.SlowServerTimeout} milliseconds.");
//
//                            await Task.Delay(TimeSpan.FromMilliseconds(Configuration.SlowServerTimeout));
//
//                            return await SendRemoteProcedureCall(request);

                            // A new rpc endpoint is available.
                            case ResponseEnvelope.Types.StatusCode.Redirect: 
                                if (Regex.IsMatch(responseEnvelope.ApiUrl, "pgorelease\\.nianticlabs\\.com\\/plfe\\/\\d+"))
                                {
                                    _requestUrl = $"https://{responseEnvelope.ApiUrl}/rpc";

                                    return await SendRemoteProcedureCall(requestEnvelope);
                                }
                                throw new Exception($"Received an incorrect API url: '{responseEnvelope.ApiUrl}', status code was: '{responseEnvelope.StatusCode}'.");

                            // The login token is invalid.
                            case ResponseEnvelope.Types.StatusCode.InvalidAuthToken:
                                Logger.Debug("Received StatusCode 102, reauthenticating.");

                                _session.AccessToken.Expire();
                                await _session.Reauthenticate();

                                return await SendRemoteProcedureCall(requestEnvelope);

                            default:
                                Logger.Info($"Unknown status code: {responseEnvelope.StatusCode}");
                                break;
                        }

                        LastRpcRequest = DateTime.UtcNow;

                        if (requestEnvelope.Requests[0].RequestType == RequestType.GetMapObjects)
                        {
                            LastRpcMapObjectsRequest = LastRpcRequest;
                            LastGeoCoordinateMapObjectsRequest = _session.Player.Coordinate;
                        }

                        if (responseEnvelope.AuthTicket != null)
                        {
                            _session.AccessToken.AuthTicket = responseEnvelope.AuthTicket;
                            Logger.Debug("Received a new AuthTicket from Pokemon!");
                        }

                        return HandleResponseEnvelope(requestEnvelope, responseEnvelope);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error($"SendRemoteProcedureCall exception: {e}");
                return null;
            }
        }

        /// <summary>
        ///     Responsible for handling the received <see cref="ResponseEnvelope" />.
        /// </summary>
        /// <param name="request"></param>
        /// <param name="requestEnvelope"></param>
        /// <param name="responseEnvelope">
        ///     The <see cref="ResponseEnvelope" /> received from
        ///     <see cref="SendRemoteProcedureCall(Request)" />.
        /// </param>
        /// <returns>Returns the <see cref="ByteString" /> response of the <see cref="Request" />.</returns>
        private ByteString HandleResponseEnvelope(RequestEnvelope requestEnvelope, ResponseEnvelope responseEnvelope)
        {
            if (responseEnvelope.Returns.Count == 0)
            {
                throw new Exception("There were 0 responses.");
            }

            // Take requested response and remove from returns.
            var requestResponse = responseEnvelope.Returns[0];

            // Handle the default responses.
            HandleDefaultResponses(requestEnvelope, responseEnvelope.Returns);

            // Handle responses which affect the inventory
            HandleInventoryResponses(requestEnvelope.Requests[0], requestResponse);

            return requestResponse;
        }

        private void HandleInventoryResponses(Request request, ByteString requestResponse)
        {
            ulong pokemonId = 0;
            switch (request.RequestType)
            {
                case RequestType.ReleasePokemon:
                    var releaseResponse = ReleasePokemonResponse.Parser.ParseFrom(requestResponse);
                    if (releaseResponse.Result == ReleasePokemonResponse.Types.Result.Success ||
                        releaseResponse.Result == ReleasePokemonResponse.Types.Result.Failed)
                    {
                        var releaseMessage = ReleasePokemonMessage.Parser.ParseFrom(request.RequestMessage);
                        pokemonId = releaseMessage.PokemonId;
                    }
                    break;

                case RequestType.EvolvePokemon:
                    var evolveResponse = EvolvePokemonResponse.Parser.ParseFrom(requestResponse);
                    if (evolveResponse.Result == EvolvePokemonResponse.Types.Result.Success ||
                        evolveResponse.Result == EvolvePokemonResponse.Types.Result.FailedPokemonMissing)
                    {
                        var releaseMessage = ReleasePokemonMessage.Parser.ParseFrom(request.RequestMessage);
                        pokemonId = releaseMessage.PokemonId;
                    }
                    break;
            }
            if (pokemonId > 0)
            {
                var pokemons = _session.Player.Inventory.InventoryItems.Where(
                    i =>
                        i?.InventoryItemData?.PokemonData != null &&
                        i.InventoryItemData.PokemonData.Id.Equals(pokemonId));
                _session.Player.Inventory.RemoveInventoryItems(pokemons);
            }
        }

        /// <summary>
        ///     Handles the default heartbeat responses.
        /// </summary>
        /// <param name="requestEnvelope"></param>
        /// <param name="returns">The payload of the <see cref="ResponseEnvelope" />.</param>
        private void HandleDefaultResponses(RequestEnvelope requestEnvelope, RepeatedField<ByteString> returns)
        {
            var responseIndexes = new Dictionary<int, RequestType>();
            
            for (var i = 0; i < requestEnvelope.Requests.Count; i++)
            {
                var request = requestEnvelope.Requests[i];
                if (_defaultRequests.Contains(request.RequestType))
                    responseIndexes.Add(i, request.RequestType);
            }

            foreach (var responseIndex in responseIndexes)
            {
                var bytes = returns[responseIndex.Key];

                switch (responseIndex.Value)
                {
                    case RequestType.GetHatchedEggs: // Get_Hatched_Eggs
                        var hatchedEggs = GetHatchedEggsResponse.Parser.ParseFrom(bytes);
                        if (hatchedEggs.Success)
                        {
                            // TODO: Throw event, wrap in an object.
                        }
                        break;

                    case RequestType.GetInventory: // Get_Inventory
                        var inventory = GetInventoryResponse.Parser.ParseFrom(bytes);
                        if (inventory.Success)
                        {
                            if (inventory.InventoryDelta.NewTimestampMs >=
                                _session.Player.Inventory.LastInventoryTimestampMs)
                            {
                                _session.Player.Inventory.LastInventoryTimestampMs =
                                    inventory.InventoryDelta.NewTimestampMs;
                                if (inventory.InventoryDelta != null &&
                                    inventory.InventoryDelta.InventoryItems.Count > 0)
                                {
                                    _session.Player.Inventory.UpdateInventoryItems(inventory.InventoryDelta);
                                }
                            }
                        }
                        break;

                    case RequestType.CheckAwardedBadges: // Check_Awarded_Badges
                        var awardedBadges = CheckAwardedBadgesResponse.Parser.ParseFrom(bytes);
                        if (awardedBadges.Success)
                        {
                            // TODO: Throw event, wrap in an object.
                        }
                        break;

                    case RequestType.DownloadSettings: // Download_Settings
                        var downloadSettings = DownloadSettingsResponse.Parser.ParseFrom(bytes);
                        if (string.IsNullOrEmpty(downloadSettings.Error))
                        {
                            if (downloadSettings.Settings == null)
                            {
                                continue;
                            }
                            if (_session.GlobalSettings == null || _session.GlobalSettingsHash != downloadSettings.Hash)
                            {
                                _session.GlobalSettingsHash = downloadSettings.Hash;
                                _session.GlobalSettings = downloadSettings.Settings;
                            }
                            else
                            {
                                _session.GlobalSettings = downloadSettings.Settings;
                            }
                        }
                        else
                        {
                            Logger.Debug($"DownloadSettingsResponse.Error: '{downloadSettings.Error}'");
                        }
                        break;
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                _httpClient?.Dispose();
            }
        }
    }
}