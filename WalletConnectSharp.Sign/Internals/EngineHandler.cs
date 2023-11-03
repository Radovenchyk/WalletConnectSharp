﻿using Newtonsoft.Json;
using WalletConnectSharp.Common.Logging;
using WalletConnectSharp.Common.Model.Errors;
using WalletConnectSharp.Common.Utils;
using WalletConnectSharp.Core.Models.Expirer;
using WalletConnectSharp.Network.Models;
using WalletConnectSharp.Sign.Interfaces;
using WalletConnectSharp.Sign.Models;
using WalletConnectSharp.Sign.Models.Engine.Events;
using WalletConnectSharp.Sign.Models.Engine.Methods;

namespace WalletConnectSharp.Sign
{
    public partial class Engine
    {
        async void ExpiredCallback(object sender, ExpirerEventArgs e)
        {
            var target = new ExpirerTarget(e.Target);

            if (target.Id != null && this.Client.PendingRequests.Keys.Contains((long)target.Id))
            {
                await PrivateThis.DeletePendingSessionRequest((long)target.Id,
                    Error.FromErrorType(ErrorType.EXPIRED), true);
                return;
            }

            if (!string.IsNullOrWhiteSpace(target.Topic))
            {
                var topic = target.Topic;
                if (!this.Client.Session.Keys.Contains(topic))
                {
                    return;
                }

                var session = this.Client.Session.Get(topic);
                await PrivateThis.DeleteSession(topic);
                this.SessionExpired?.Invoke(this, session);
            } 
            else if (target.Id != null)
            {
                await PrivateThis.DeleteProposal((long) target.Id);
            }
        }
        
        async Task IEnginePrivate.OnSessionProposeRequest(string topic, JsonRpcRequest<SessionPropose> payload)
        {
            var @params = payload.Params;
            var id = payload.Id;
            try
            {
                var expiry = Clock.CalculateExpiry(Clock.FIVE_MINUTES);
                var proposal = new ProposalStruct()
                {
                    Id = id,
                    PairingTopic = topic,
                    Expiry = expiry,
                    Proposer = @params.Proposer,
                    Relays = @params.Relays,
                    RequiredNamespaces = @params.RequiredNamespaces,
                    OptionalNamespaces = @params.OptionalNamespaces,
                    SessionProperties = @params.SessionProperties,
                };
                await PrivateThis.SetProposal(id, proposal);
                var hash = HashUtils.HashMessage(JsonConvert.SerializeObject(payload));
                var verifyContext = await this.VerifyContext(hash, proposal.Proposer.Metadata);
                this.SessionProposed?.Invoke(this, new SessionProposalEvent()
                {
                    Id = id,
                    Proposal = proposal,
                    VerifiedContext = verifyContext
                });
            }
            catch (WalletConnectException e)
            {
                await MessageHandler.SendError<SessionPropose, SessionProposeResponse>(id, topic,
                    Error.FromException(e));
            }
        }

        async Task IEnginePrivate.OnSessionProposeResponse(string topic, JsonRpcResponse<SessionProposeResponse> payload)
        {
            var id = payload.Id;
            logger.Log($"Got session propose response with id {id}");
            if (payload.IsError)
            {
                logger.LogError("response was error");
                await this.Client.Proposal.Delete(id, Error.FromErrorType(ErrorType.USER_DISCONNECTED));
                this.SessionConnectionErrored?.Invoke(this, payload.Error.ToException());
            }
            else
            {
                logger.Log("response was success");
                var result = payload.Result;
                var proposal = this.Client.Proposal.Get(id);
                var selfPublicKey = proposal.Proposer.PublicKey;
                var peerPublicKey = result.ResponderPublicKey;

                var sessionTopic = await this.Client.Core.Crypto.GenerateSharedKey(
                    selfPublicKey,
                    peerPublicKey
                );
                await this.Client.Core.Pairing.Activate(topic);
                logger.Log($"pairing activated for topic {topic}");
                
                // try to do this a couple of times .. do it until it works?
                int attempts = 5;
                do
                {
                    try
                    {
                        var subscriptionId = await this.Client.Core.Relayer.Subscribe(sessionTopic);
                        return;
                    }
                    catch (Exception e)
                    {
                        WCLogger.LogError($"Got error subscribing to topic, attempts left: {attempts}");
                        WCLogger.LogError(e);
                        attempts--;
                        await Task.Yield();
                    }
                } while (attempts > 0);

                throw new IOException($"Could not subscribe to session topic {sessionTopic}");
            }
        }

        async Task IEnginePrivate.OnSessionSettleRequest(string topic, JsonRpcRequest<SessionSettle> payload)
        {
            var id = payload.Id;
            var @params = payload.Params;
            logger.Log($"got session settle request with {id}");
            try
            {
                await PrivateThis.IsValidSessionSettleRequest(@params);
                var pairingTopic = @params.PairingTopic;
                var relay = @params.Relay;
                var controller = @params.Controller;
                var expiry = @params.Expiry;
                var namespaces = @params.Namespaces;

                var session = new SessionStruct()
                {
                    Topic = topic,
                    PairingTopic = pairingTopic,
                    Relay = relay,
                    Expiry = expiry,
                    Namespaces = namespaces,
                    Acknowledged = true,
                    Controller = controller.PublicKey,
                    Self = new Participant()
                    {
                        Metadata = this.Client.Metadata,
                        PublicKey = ""
                    },
                    Peer = new Participant()
                    {
                        PublicKey = controller.PublicKey,
                        Metadata = controller.Metadata
                    }
                };
                await MessageHandler.SendResult<SessionSettle, bool>(payload.Id, topic, true);
                this.SessionConnected?.Invoke(this, session);
            }
            catch (WalletConnectException e)
            {
                logger.LogError("got error while performing session settle");
                logger.LogError(e);
                await MessageHandler.SendError<SessionSettle, bool>(id, topic, Error.FromException(e));
            }
        }

        async Task IEnginePrivate.OnSessionSettleResponse(string topic, JsonRpcResponse<bool> payload)
        {
            var id = payload.Id;
            var session = this.Client.Session.Get(topic);
            if (payload.IsError)
            {
                var error = Error.FromErrorType(ErrorType.USER_DISCONNECTED);
                await this.Client.Session.Delete(topic, error);
                this.SessionRejected?.Invoke(this, session);
                
                // Still used do not remove
                this.sessionEventsHandlerMap[$"session_approve{id}"](this, payload);
            }
            else
            {
                await this.Client.Session.Update(topic, new SessionStruct()
                {
                    Acknowledged = true
                });
                this.SessionApproved?.Invoke(this, session);
                this.sessionEventsHandlerMap[$"session_approve{id}"](this, payload);
            }
        }

        async Task IEnginePrivate.OnSessionUpdateRequest(string topic, JsonRpcRequest<SessionUpdate> payload)
        {
            var @params = payload.Params;
            var id = payload.Id;
            try
            {
                await PrivateThis.IsValidUpdate(topic, @params.Namespaces);

                await this.Client.Session.Update(topic, new SessionStruct()
                {
                    Namespaces = @params.Namespaces
                });

                await MessageHandler.SendResult<SessionUpdate, bool>(id, topic, true);
                this.SessionUpdateRequest?.Invoke(this, new SessionUpdateEvent()
                {
                    Id = id,
                    Topic = topic,
                    Params = @params
                });
            }
            catch (WalletConnectException e)
            {
                await MessageHandler.SendError<SessionUpdate, bool>(id, topic, Error.FromException(e));
            }
        }

        async Task IEnginePrivate.OnSessionUpdateResponse(string topic, JsonRpcResponse<bool> payload)
        {
            var id = payload.Id;
            this.SessionUpdated?.Invoke(this, new SessionEvent()
            {
                Id = id,
                Topic = topic,
            });
            // Still used, do not remove
            this.sessionEventsHandlerMap[$"session_update{id}"](this, payload);
        }

        async Task IEnginePrivate.OnSessionExtendRequest(string topic, JsonRpcRequest<SessionExtend> payload)
        {
            var id = payload.Id;
            try
            {
                await PrivateThis.IsValidExtend(topic);
                await PrivateThis.SetExpiry(topic, Clock.CalculateExpiry(SessionExpiry));
                await MessageHandler.SendResult<SessionExtend, bool>(id, topic, true);
                this.SessionExtendRequest?.Invoke(this, new SessionEvent()
                {
                    Id = id,
                    Topic = topic
                });
            }
            catch (WalletConnectException e)
            {
                await MessageHandler.SendError<SessionExtend, bool>(id, topic, Error.FromException(e));
            }
        }

        async Task IEnginePrivate.OnSessionExtendResponse(string topic, JsonRpcResponse<bool> payload)
        {
            var id = payload.Id;
            this.SessionExtended?.Invoke(this, new SessionEvent()
            {
                Topic = topic,
                Id = id
            });
            // Still used, do not remove
            this.sessionEventsHandlerMap[$"session_extend{id}"](this, payload);
        }

        async Task IEnginePrivate.OnSessionPingRequest(string topic, JsonRpcRequest<SessionPing> payload)
        {
            var id = payload.Id;
            try
            {
                await PrivateThis.IsValidPing(topic);
                await MessageHandler.SendResult<SessionPing, bool>(id, topic, true);
                this.SessionPinged?.Invoke(this, new SessionEvent()
                {
                    Id = id,
                    Topic = topic
                });
            }
            catch (WalletConnectException e)
            {
                await MessageHandler.SendError<SessionPing, bool>(id, topic, Error.FromException(e));
            }
        }

        async Task IEnginePrivate.OnSessionPingResponse(string topic, JsonRpcResponse<bool> payload)
        {
            var id = payload.Id;
            
            // put at the end of the stack to avoid a race condition
            // where session_ping listener is not yet initialized
            await Task.Delay(500);
            
            this.SessionPinged?.Invoke(this, new SessionEvent()
            {
                Id = id,
                Topic = topic
            });

            // Still used, do not remove
            this.sessionEventsHandlerMap[$"session_ping{id}"](this, payload);
        }

        async Task IEnginePrivate.OnSessionDeleteRequest(string topic, JsonRpcRequest<SessionDelete> payload)
        {
            var id = payload.Id;
            try
            {
                await PrivateThis.IsValidDisconnect(topic, payload.Params);

                await MessageHandler.SendResult<SessionDelete, bool>(id, topic, true);
                await PrivateThis.DeleteSession(topic);
                this.SessionDeleted?.Invoke(this, new SessionEvent()
                {
                    Topic = topic,
                    Id = id
                });
            }
            catch (WalletConnectException e)
            {
                await MessageHandler.SendError<SessionDelete, bool>(id, topic, Error.FromException(e));
            }
        }
    }
}
