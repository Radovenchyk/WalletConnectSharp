﻿using EventEmitter.NET;
using EventEmitter.NET.Model;
using WalletConnectSharp.Auth;
using WalletConnectSharp.Auth.Interfaces;
using WalletConnectSharp.Auth.Models;
using WalletConnectSharp.Common.Model.Errors;
using WalletConnectSharp.Network.Models;
using WalletConnectSharp.Sign;
using WalletConnectSharp.Sign.Interfaces;
using WalletConnectSharp.Sign.Models;
using WalletConnectSharp.Sign.Models.Engine;
using WalletConnectSharp.Sign.Models.Engine.Events;
using WalletConnectSharp.Web3Wallet.Interfaces;

namespace WalletConnectSharp.Web3Wallet.Controllers;

public class Web3WalletEngine : IWeb3WalletEngine
{
    private bool _initialized;

    public IDictionary<string, SessionStruct> ActiveSessions
    {
        get
        {
            return this.SignClient.Session.ToDictionary();
        }
    }

    public IDictionary<long, ProposalStruct> PendingSessionProposals
    {
        get
        {
            return this.SignClient.Proposal.ToDictionary();
        }
    }

    public PendingRequestStruct[] PendingSessionRequests
    {
        get
        {
            return this.SignClient.PendingSessionRequests;
        }
    }

    public IDictionary<long, PendingRequest> PendingAuthRequests
    {
        get
        {
            return this.AuthClient.PendingRequests;
        }
    }

    public ISignClient SignClient { get; private set; }
    public IAuthClient AuthClient { get; private set; }
    public IWeb3Wallet Client { get; }
    
    public Web3WalletEngine(IWeb3Wallet client)
    {
        Client = client;
    }
    
    public async Task Init()
    {
        this.SignClient = await WalletConnectSignClient.Init(new SignClientOptions()
        {
            Core = this.Client.Core, Metadata = this.Client.Metadata
        });

        this.AuthClient = await WalletConnectAuthClient.Init(new AuthOptions()
        {
            Core = this.Client.Core, ProjectId = this.Client.Core.ProjectId, Metadata = this.Client.Metadata
        });
        
        InitializeEventListeners();

        _initialized = true;
    }

    public async Task Pair(string uri, bool activatePairing = false)
    {
        IsInitialized();
        await this.Client.Core.Pairing.Pair(uri, activatePairing);
    }

    public async Task<SessionStruct> ApproveSession(long id, Namespaces namespaces, string relayProtocol = null)
    {
        var data = await this.SignClient.Approve(new ApproveParams()
        {
            Id = id, Namespaces = namespaces, RelayProtocol = relayProtocol
        });

        await data.Acknowledged();

        return this.SignClient.Session.Get(data.Topic);
    }

    public Task<SessionStruct> ApproveSession(ProposalStruct proposal, params string[] approvedAddresses)
    {
        var param = proposal.ApproveProposal(approvedAddresses);
        return ApproveSession(param.Id, param.Namespaces, param.RelayProtocol);
    }

    public Task RejectSession(long id, Error reason)
    {
        return this.SignClient.Reject(new RejectParams() { Id = id, Reason = reason });
    }

    public Task RejectSession(ProposalStruct proposal, Error reason)
    {
        var parm = proposal.RejectProposal(reason);
        return RejectSession(parm.Id, parm.Reason);
    }

    public Task RejectSession(ProposalStruct proposal, string reason)
    {
        var parm = proposal.RejectProposal(reason);
        return RejectSession(parm.Id, parm.Reason);
    }

    public async Task UpdateSession(string topic, Namespaces namespaces)
    {
        await (await this.SignClient.UpdateSession(topic, namespaces)).Acknowledged();
    }

    public async Task ExtendSession(string topic)
    {
        await (await this.SignClient.Extend(topic)).Acknowledged();
    }

    public async Task RespondSessionRequest<T, TR>(string topic, JsonRpcResponse<TR> response)
    {
        await this.SignClient.Respond<T, TR>(topic, response);
    }

    public async Task EmitSessionEvent<T>(string topic, EventData<T> eventData, string chainId)
    {
        await this.SignClient.Emit(topic, eventData, chainId);
    }

    public async Task DisconnectSession(string topic, Error reason)
    {
        await this.SignClient.Disconnect(topic, reason);
    }

    public async Task RespondAuthRequest(ResultResponse results, string iss)
    {
        await this.AuthClient.Respond(results, iss);
    }

    public async Task RespondAuthRequest(AuthErrorResponse error, string iss)
    {
        await this.AuthClient.Respond(error, iss);
    }

    public Task RespondAuthRequest(AuthRequest request, Error error, string iss)
    {
        return RespondAuthRequest(new AuthErrorResponse() { Id = request.Id, Error = error, }, iss);
    }

    public Task RespondAuthRequest(AuthRequest request, string signature, string iss, bool eip191 = true)
    {
        Cacao.CacaoSignature sig = eip191
            ? new Cacao.CacaoSignature.EIP191CacaoSignature(signature)
            : new Cacao.CacaoSignature.EIP1271CacaoSignature(signature);
        return RespondAuthRequest(new ResultResponse() { Id = request.Id, Signature = sig }, iss);
    }

    public string FormatMessage(Cacao.CacaoRequestPayload payload, string iss)
    {
        return this.AuthClient.FormatMessage(payload, iss);
    }

    private void PropagateEventToClient(string eventName, IEvents source)
    {
        EventHandler<GenericEvent<object>> eventHandler = (sender, @event) =>
        {
            this.Client.Events.TriggerType(eventName, @event.EventData, @event.EventData.GetType());
        };

        source.On(eventName, eventHandler);
    }

    private void InitializeEventListeners()
    {
        PropagateEventToClient("session_proposal", SignClient);
        PropagateEventToClient("session_request", SignClient);
        PropagateEventToClient("session_delete", SignClient);
        
        // Propagate auth events 
        AuthClient.AuthRequested += OnAuthRequest;
        AuthClient.AuthResponded += OnAuthResponse;
        AuthClient.AuthError += OnAuthResponse;
    }

    private void IsInitialized()
    {
        if (!_initialized)
        {
            throw WalletConnectException.FromType(ErrorType.NOT_INITIALIZED, "Web3WalletEngine");
        }
    }

    public event EventHandler<AuthRequest> AuthRequested;
    public event EventHandler<AuthResponse> AuthResponded;
    public event EventHandler<AuthErrorResponse> AuthError;

    void OnAuthRequest(object sender, AuthRequest request)
    {
        if (AuthRequested != null)
        {
            AuthRequested(sender, request);
        }
    }

    void OnAuthResponse(object sender, AuthErrorResponse errorResponse)
    {
        if (AuthError != null)
        {
            AuthError(sender, errorResponse);
        }
    }

    void OnAuthResponse(object sender, AuthResponse response)
    {
        if (AuthResponded != null)
        {
            AuthResponded(sender, response);
        }
    }
}
