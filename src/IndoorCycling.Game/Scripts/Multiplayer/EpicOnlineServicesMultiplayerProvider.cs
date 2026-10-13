using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IndoorCycling.Game.Multiplayer;
using Wodra.Multiplayer.Protocol;

#if WODRA_EOS_SDK
using Epic.OnlineServices;
using Epic.OnlineServices.Connect;
using Epic.OnlineServices.Lobby;
using Epic.OnlineServices.P2P;
using Epic.OnlineServices.Platform;
#endif

/// <summary>EOS Connect + Lobbies + P2P transport for the provider-independent ride protocol.</summary>
public sealed class EpicOnlineServicesMultiplayerProvider : IMultiplayerProvider
{
    private const string SocketName = "WodraRideV2";
    private const int LobbyCapacity = 10;
    private const string RouteAttribute = "wodra_route_id";
    private const string FingerprintAttribute = "wodra_route_fingerprint";
    private const string ProtocolAttribute = "wodra_protocol_version";
    private readonly ConcurrentQueue<ProtocolMessage> _events = new();
    private readonly ConcurrentDictionary<string, byte> _members = new(StringComparer.Ordinal);
    private readonly EosPeerMembershipGate _peerMembershipGate = new();
    private readonly ConcurrentDictionary<string, byte> _connectedPeers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pendingEstablishedPeers = new(StringComparer.Ordinal);
#if WODRA_EOS_SDK
    private readonly ConcurrentDictionary<string, SocketId?> _pendingPeerRequests = new(StringComparer.Ordinal);
#endif
    private readonly ConcurrentDictionary<string, byte> _firstStateSendLogged = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _firstStateAttemptLogged = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _firstStateReceiveLogged = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _firstStateWireReceiveLogged = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _lastAcceptedStateAt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _receiveDiagnosticsLogged = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _firstRemoteStateAcceptedLogged = new(StringComparer.Ordinal);
#if DEBUG
    private int _firstSuccessfulReceiveLogged;
#endif
    private readonly PeerSequenceGate _sequenceGate = new();
    private string _status = "Offline";
    private string _lobbyId = "";
    private string _routeId = "";
    private string _routeFingerprint = "";
    private string _displayName = "";
    private string _riderId = "";
    private bool _disposed;
    private volatile bool _p2pFailed;

    private static EosClientConfiguration LoadConfiguration()
    {
        var configuration = EosClientConfiguration.LoadCurrent(Godot.ProjectSettings.GlobalizePath("res://"),
            Godot.Engine.IsEditorHint());
        var environmentOverrides = new[]
        {
            "WODRA_EOS_PRODUCT_ID", "WODRA_EOS_SANDBOX_ID", "WODRA_EOS_DEPLOYMENT_ID",
            "WODRA_EOS_CLIENT_ID", "WODRA_EOS_CLIENT_SECRET"
        }.Where(name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))).ToArray();

        if (configuration.IsInvalid)
        {
            GameLogger.Log("eos_configuration_invalid", new
            {
                path = configuration.SourcePath,
                diagnostic = configuration.LoadError
            });
        }
        else if (configuration.IsConfigured)
        {
            GameLogger.Log("eos_configuration_loaded", new
            {
                sourcePath = configuration.SourcePath,
                source = configuration.SourcePath is null ? "environment" : "json file",
                environmentOverrides
            });
        }
        return configuration;
    }

#if WODRA_EOS_SDK
    private static readonly object RuntimeLock = new();
    private static PlatformInterface? _platform;
    private static ConnectInterface? _connect;
    private static LobbyInterface? _lobby;
    private static P2PInterface? _p2p;
    private static ProductUserId? _localUser;
    private static Task<ProductUserId>? _loginTask;
    private static string? _expectedProductUserId;
    private static bool _shutdown;
    private readonly SocketId _socketId = new() { SocketName = SocketName };
    private ulong _lobbyStatusNotification;
    private ulong _peerRequestNotification;
    private ulong _peerClosedNotification;
    private ulong _peerInterruptedNotification;
    private ulong _peerEstablishedNotification;
    private ulong _authExpirationNotification;
    private bool _joined;
#endif

#if WODRA_EOS_SDK
    static EpicOnlineServicesMultiplayerProvider()
    {
        try { EosNativeLibraryLoader.RegisterFor(typeof(EpicOnlineServicesMultiplayerProvider).Assembly); }
        catch (Exception ex)
        {
            GameLogger.Log("eos_native_resolver_registration_failed", new
            {
                assembly = typeof(EpicOnlineServicesMultiplayerProvider).Assembly.GetName().Name,
                processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                exceptionType = ex.GetType().FullName,
                message = ex.Message
            });
        }
    }
#endif

    public string Status
    {
        get
        {
#if WODRA_EOS_SDK
            if (_joined)
            {
                var riderCount = _members.Count + 1;
                var now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
                var activeStatePeers = _lastAcceptedStateAt.Count(item =>
                    _members.ContainsKey(item.Key) && now - item.Value <= IndoorCycling.Core.Simulation.RemoteRiderInterpolator.StaleAfterSeconds);
                return EosLobbyStatusText.ForMembership(riderCount, _connectedPeers.Count, activeStatePeers, _p2pFailed);
            }
#endif
            return _status;
        }
    }
    private static readonly Lazy<EosClientConfiguration> CachedConfiguration = new(LoadConfiguration);

    public static EosClientConfiguration Configuration => CachedConfiguration.Value;
    public static bool HasRequiredConfiguration => Configuration.IsConfigured;
    public static bool ConfigurationInvalid => Configuration.IsInvalid;
    public static IReadOnlyList<string> MissingConfigurationVariables => Configuration.MissingVariables;
    public bool IsConnected
    {
        get
        {
#if WODRA_EOS_SDK
            return _joined && !_disposed && _localUser?.IsValid() == true;
#else
            return false;
#endif
        }
    }
    public string RiderId => _riderId;
    public string DisplayName => _displayName;

    public static void TickPlatform(double deltaSeconds)
    {
#if WODRA_EOS_SDK
        PlatformInterface? platform;
        lock (RuntimeLock) platform = _platform;
        if (!_shutdown && platform is not null)
        {
            try { platform.Tick(); }
            catch (Exception ex) { GameLogger.Log("eos_tick_failed", new { message = ex.Message }); }
        }
#endif
    }

    public async Task<bool> CheckAvailabilityAsync(string serverAddress, CancellationToken cancellationToken = default)
    {
        var configuration = Configuration;
        if (configuration.IsInvalid)
        {
            _status = "EOS configuration invalid";
            return false;
        }
        var missingConfiguration = configuration.MissingVariables;
        if (missingConfiguration.Count > 0)
        {
            _status = "EOS not configured";
            GameLogger.Log("eos_configuration_missing", new { missingVariables = missingConfiguration });
            return false;
        }
#if WODRA_EOS_SDK
        try
        {
            _status = "Connecting…";
            _ = EosNativeLibraryLoader.EnsureLoaded();
            _ = await EnsureAuthenticatedAsync(_displayName, cancellationToken);
            _status = "EOS authenticated";
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { _status = "Authentication cancelled"; return false; }
        catch (Exception ex)
        {
            _status = EosNativeLibraryLoader.LoadFailed ? "EOS runtime unavailable" : "Online unavailable";
            GameLogger.Log("eos_availability_failed", new { exceptionType = ex.GetType().FullName, message = ex.Message });
            return false;
        }
#else
        await Task.CompletedTask;
        _status = "EOS unavailable";
        return false;
#endif
    }

    public async Task<RoutePresenceResult> GetRoutePresenceAsync(string serverAddress, string routeId,
        string routeFingerprint, CancellationToken cancellationToken = default)
    {
#if WODRA_EOS_SDK
        try
        {
            var user = await EnsureAuthenticatedAsync(_displayName, cancellationToken);
            var search = CreateRouteSearch(routeId, routeFingerprint);
            try
            {
                GameLogger.Log("eos_lobby_discovery_started", new { routeId, purpose = "presence" });
                var findResult = await FindLobbiesAsync(search, user, cancellationToken);
                uint total = 0;
                var countOptions = new LobbySearchGetSearchResultCountOptions();
                var count = search.GetSearchResultCount(ref countOptions);
                GameLogger.Log("eos_lobby_discovery_result", new
                {
                    routeId,
                    purpose = "presence",
                    findResult = findResult.ToString(),
                    resultCount = count
                });
                for (uint i = 0; i < count; i++)
                {
                    var details = GetSearchResult(search, i);
                    try
                    {
                        var info = GetLobbyInfo(details);
                        total += info.MaxMembers - info.AvailableSlots;
                    }
                    finally { details.Release(); }
                }
                return new RoutePresenceResult(true, checked((int)total));
            }
            finally { search.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return RoutePresenceResult.Unavailable("Presence check cancelled."); }
        catch (Exception ex) { return RoutePresenceResult.Unavailable("EOS lobby presence unavailable: " + ex.Message); }
#else
        await Task.CompletedTask;
        return RoutePresenceResult.Unavailable("EOS SDK not installed. Set EOS_SDK_ROOT to the extracted EOS C# SDK.");
#endif
    }

    public async Task JoinRouteSessionAsync(string serverAddress, string displayName, string sessionId,
        string routeId, string routeFingerprint, CancellationToken cancellationToken = default)
    {
#if WODRA_EOS_SDK
        if (_joined) await LeaveSessionAsync();
        if (string.IsNullOrWhiteSpace(routeId) || routeFingerprint is not { Length: 64 } || !routeFingerprint.All(Uri.IsHexDigit))
            throw new InvalidOperationException("EOS lobby join requires a valid route ID and SHA-256 route fingerprint.");
        _displayName = RiderDisplayName.Resolve(displayName);
        _routeId = routeId;
        _routeFingerprint = routeFingerprint.ToLowerInvariant();
        _status = "Connecting…";
        GameLogger.Log("eos_lobby_join_started", new { routeId });
        try
        {
            var user = await EnsureAuthenticatedAsync(_displayName, cancellationToken);
            _riderId = user.ToString();
            _status = "EOS authenticated · lobby searching…";
            var lobbyId = await FindOrCreateLobbyAsync(user, routeId, _routeFingerprint, cancellationToken);
            _lobbyId = lobbyId;
            RegisterNotifications(user);
            PopulateLobbyMembers(user, lobbyId);
            _joined = true;
            _status = "EOS lobby joined";
            GameLogger.Log("eos_lobby_joined", new { routeId, lobbyId, members = _members.Count + 1, otherMembers = _members.Count });
        }
        catch (Exception ex)
        {
            GameLogger.Log("eos_join_failed", new { routeId, message = ex.Message });
            await LeaveSessionAsync();
            _status = "EOS connection failed: " + ex.Message;
            throw;
        }
#else
        await Task.CompletedTask;
        throw new InvalidOperationException("EOS SDK not installed. Set EOS_SDK_ROOT to the extracted EOS C# SDK.");
#endif
    }

    public bool TryQueueState(RiderStateMessage state)
    {
#if WODRA_EOS_SDK
        if (!IsConnected) return false;
        var message = state with { RiderId = _riderId, DisplayName = _displayName };
        Broadcast(message, PacketReliability.UnreliableUnordered, channel: 0);
        return true;
#else
        return false;
#endif
    }

    public Task PublishRepositionAsync(RiderRepositionedMessage state, CancellationToken cancellationToken = default)
    {
#if WODRA_EOS_SDK
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConnected) throw new InvalidOperationException("EOS P2P lobby connection is not active.");
        if (!string.Equals(state.RouteId, _routeId, StringComparison.Ordinal) ||
            !string.Equals(state.RouteFingerprint, _routeFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Reposition event does not match the joined EOS route.");
        var failure = Broadcast(state with { RiderId = _riderId, DisplayName = _displayName }, PacketReliability.ReliableOrdered, channel: 1);
        if (failure is { } result)
            throw new InvalidOperationException("EOS could not queue the reliable reposition event: " + result);
#endif
        return Task.CompletedTask;
    }

    public IReadOnlyList<ProtocolMessage> DrainEvents()
    {
        var result = new List<ProtocolMessage>();
        while (_events.TryDequeue(out var message)) result.Add(message);
        return result;
    }

    public void Tick(double deltaSeconds)
    {
#if WODRA_EOS_SDK
        if (!_joined || _disposed) return;
        DrainPackets();
#endif
    }

    public async Task LeaveSessionAsync()
    {
#if WODRA_EOS_SDK
        var user = _localUser;
        if (_lobby is not null && user?.IsValid() == true && !string.IsNullOrEmpty(_lobbyId))
        {
            try
            {
                var options = new LeaveLobbyOptions { LocalUserId = user, LobbyId = _lobbyId };
                await AwaitResultAsync<LeaveLobbyCallbackInfo>(callback => _lobby.LeaveLobby(ref options, this,
                    (ref LeaveLobbyCallbackInfo info) => callback(ref info)), (ref LeaveLobbyCallbackInfo info) => info.ResultCode);
            }
            catch (Exception ex) { GameLogger.Log("eos_lobby_leave_failed", new { message = ex.Message }); }
        }
        UnregisterNotifications();
        ClosePeerConnections(user);
        foreach (var member in _members.Keys)
            _events.Enqueue(new RiderLeftMessage { RiderId = member, Reason = "left" });
        _members.Clear();
        _peerMembershipGate.Clear();
        _connectedPeers.Clear();
        _pendingEstablishedPeers.Clear();
        _pendingPeerRequests.Clear();
        _lastAcceptedStateAt.Clear();
        _sequenceGate.Clear();
        _lobbyId = "";
        _joined = false;
#endif
        _status = "Disconnected";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await LeaveSessionAsync();
    }

#if WODRA_EOS_SDK
    private static async Task<ProductUserId> EnsureAuthenticatedAsync(string displayName, CancellationToken cancellationToken)
    {
        EnsurePlatform();
        Task<ProductUserId> task;
        lock (RuntimeLock)
        {
            if (_localUser?.IsValid() == true && _connect?.GetLoginStatus(_localUser) == LoginStatus.LoggedIn)
                return _localUser;
            task = _loginTask ??= AuthenticateCoreAsync(displayName);
        }
        using var registration = cancellationToken.Register(() => { });
        return await task.WaitAsync(cancellationToken);
    }

    private static void EnsurePlatform()
    {
        lock (RuntimeLock)
        {
            if (_platform is not null) return;
            if (_shutdown) throw new InvalidOperationException("EOS has already shut down for this process.");
            _ = EosNativeLibraryLoader.EnsureLoaded();
            var config = Configuration;
            config.Validate();
            var initialize = new InitializeOptions { ProductName = "Wodra", ProductVersion = "0.4" };
            GameLogger.Log("eos_platform_initialization_started", new
            {
                nativeLibraryPath = EosNativeLibraryLoader.ResolvedPath,
                processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()
            });
            var initResult = PlatformInterface.Initialize(ref initialize);
            GameLogger.Log("eos_platform_initialization_result", new
            {
                result = initResult.ToString(),
                success = initResult is Result.Success or Result.AlreadyConfigured,
                nativeLibraryPath = EosNativeLibraryLoader.ResolvedPath,
                processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()
            });
            if (initResult != Result.Success && initResult != Result.AlreadyConfigured)
                throw new InvalidOperationException("EOS SDK initialization failed: " + initResult);
            var options = new Options
            {
                ProductId = config.ProductId,
                SandboxId = config.SandboxId,
                DeploymentId = config.DeploymentId,
                ClientCredentials = new ClientCredentials { ClientId = config.ClientId, ClientSecret = config.ClientSecret },
                IsServer = false,
                CacheDirectory = GetCacheDirectory(),
                TickBudgetInMilliseconds = 2
            };
            _platform = PlatformInterface.Create(ref options);
            if (_platform is null)
            {
                PlatformInterface.Shutdown();
                throw new InvalidOperationException("EOS Platform creation failed. Check product, sandbox, deployment and user-client credentials.");
            }
            _connect = _platform.GetConnectInterface();
            _lobby = _platform.GetLobbyInterface();
            _p2p = _platform.GetP2PInterface();
#if DEBUG
            GameLogger.Log("eos_debug_identity_context", new
            {
                cacheDirectory = options.CacheDirectory.ToString(),
                cacheDirectoryOverrideSet = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WODRA_EOS_CACHE_DIR")),
                note = "EOS Device ID credentials are scoped to the current OS user profile; CacheDirectory does not create a separate Device ID."
            });
#endif
            var relayOptions = new SetRelayControlOptions { RelayControl = RelayControl.AllowRelays };
            var relayResult = _p2p.SetRelayControl(ref relayOptions);
            if (relayResult != Result.Success)
                GameLogger.Log("eos_relay_control_default_retained", new { result = relayResult.ToString(), relay = "AllowRelays is the EOS default" });
            else
                GameLogger.Log("eos_relay_control_configured", new { relay = "AllowRelays" });
            GameLogger.Log("eos_platform_initialized", new
            {
                sdk = "C# SDK",
                platform = "Windows x64",
                nativeLibraryPath = EosNativeLibraryLoader.ResolvedPath
            });
        }
    }

    private static string GetCacheDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("WODRA_EOS_CACHE_DIR");
        var path = string.IsNullOrWhiteSpace(configured)
            ? System.IO.Path.Combine(Godot.ProjectSettings.GlobalizePath("user://settings"), "eos-cache")
            : System.IO.Path.GetFullPath(configured);
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<ProductUserId> AuthenticateCoreAsync(string displayName)
    {
        try
        {
            GameLogger.Log("eos_authentication_started", new { credentialType = "DeviceidAccessToken" });
            var connect = _connect ?? throw new InvalidOperationException("EOS Connect interface is unavailable.");
            var createOptions = new CreateDeviceIdOptions { DeviceModel = "Windows PC" };
            var deviceResult = await AwaitResultAsync<CreateDeviceIdCallbackInfo>(callback => connect.CreateDeviceId(ref createOptions, null!,
                (ref CreateDeviceIdCallbackInfo info) => callback(ref info)), (ref CreateDeviceIdCallbackInfo info) => info.ResultCode);
#if DEBUG
            GameLogger.Log("eos_debug_device_id_result", new
            {
                result = deviceResult.ToString(),
                reusedExistingDeviceId = deviceResult == Result.DuplicateNotAllowed
            });
#endif
            GameLogger.Log("eos_authentication_device_id_result", new { result = deviceResult.ToString() });
            if (deviceResult is not (Result.Success or Result.DuplicateNotAllowed))
                throw new InvalidOperationException("EOS Device ID creation failed: " + deviceResult);

            var credentials = new Credentials { Type = ExternalCredentialType.DeviceidAccessToken };
            var loginOptions = new LoginOptions
            {
                Credentials = credentials,
                UserLoginInfo = new UserLoginInfo { DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Rider" : displayName }
            };
            var login = await AwaitInfoAsync<LoginCallbackInfo>(callback => connect.Login(ref loginOptions, null!,
                (ref LoginCallbackInfo info) => callback(ref info)));
            GameLogger.Log("eos_authentication_login_result", new { result = login.ResultCode.ToString() });
            ProductUserId user;
            if (login.ResultCode == Result.Success)
                user = login.LocalUserId;
            else if (login.ResultCode == Result.InvalidUser && login.ContinuanceToken is not null)
            {
                var createUserOptions = new CreateUserOptions { ContinuanceToken = login.ContinuanceToken };
                var created = await AwaitInfoAsync<CreateUserCallbackInfo>(callback => connect.CreateUser(ref createUserOptions, null!,
                    (ref CreateUserCallbackInfo info) => callback(ref info)));
                GameLogger.Log("eos_authentication_product_user_create_result", new { result = created.ResultCode.ToString() });
                if (created.ResultCode != Result.Success || created.LocalUserId?.IsValid() != true)
                    throw new InvalidOperationException("EOS Product User ID creation failed: " + created.ResultCode);
                user = created.LocalUserId;
            }
            else
                throw new InvalidOperationException("EOS Connect Device ID login failed: " + login.ResultCode);

            if (user?.IsValid() != true) throw new InvalidOperationException("EOS returned an invalid Product User ID.");
            lock (RuntimeLock)
            {
                var id = user.ToString();
                if (_expectedProductUserId is not null && !string.Equals(_expectedProductUserId, id, StringComparison.Ordinal))
                    throw new InvalidOperationException("EOS Device ID resolved to a different Product User ID. Reauthentication was stopped to avoid changing rider identity.");
                _expectedProductUserId ??= id;
                _localUser = user;
            }
            GameLogger.Log("eos_authentication_succeeded", new { productUserIdValid = user?.IsValid() == true });
#if DEBUG
            GameLogger.Log("eos_debug_local_identity", new
            {
                productUserIdHash = EosIdentityDiagnostics.HashProductUserId(user.ToString()),
                processId = Environment.ProcessId
            });
#endif
            return user;
        }
        catch (Exception ex)
        {
            lock (RuntimeLock) _loginTask = null;
            GameLogger.Log("eos_authentication_failed", new { exceptionType = ex.GetType().FullName, message = ex.Message });
            throw;
        }
    }

    private delegate void RefCallback<TInfo>(ref TInfo info);

    private static Task<TInfo> AwaitInfoAsync<TInfo>(Action<RefCallback<TInfo>> begin) where TInfo : struct
    {
        var source = new TaskCompletionSource<TInfo>();
        begin((ref TInfo info) => source.TrySetResult(info));
        return source.Task;
    }

    private static Task<Result> AwaitResultAsync<TInfo>(Action<RefCallback<TInfo>> begin, ResultSelector<TInfo> result) where TInfo : struct
    {
        var source = new TaskCompletionSource<Result>();
        begin((ref TInfo info) => source.TrySetResult(result(ref info)));
        return source.Task;
    }

    private delegate Result ResultSelector<TInfo>(ref TInfo info);

    private async Task<string> FindOrCreateLobbyAsync(ProductUserId user, string routeId, string fingerprint,
        CancellationToken cancellationToken)
    {
        _status = "EOS authenticated · lobby searching…";
        GameLogger.Log("eos_lobby_discovery_started", new { routeId });
        var search = CreateRouteSearch(routeId, fingerprint);
        try
        {
            await FindLobbiesAsync(search, user, cancellationToken);
            var countOptions = new LobbySearchGetSearchResultCountOptions();
            var count = search.GetSearchResultCount(ref countOptions);
            GameLogger.Log("eos_lobby_discovery_result", new { routeId, resultCount = count });
            var candidates = new List<(LobbyDetails Details, LobbyDetailsInfo Info)>();
            for (uint i = 0; i < count; i++)
            {
                var details = GetSearchResult(search, i);
                var info = GetLobbyInfo(details);
                if (info.AvailableSlots > 0 && info.PermissionLevel == LobbyPermissionLevel.Publicadvertised)
                    candidates.Add((details, info));
                else details.Release();
            }
            if (candidates.Count > 0)
            {
                var selected = candidates.OrderByDescending(x => x.Info.MaxMembers - x.Info.AvailableSlots)
                    .ThenBy(x => x.Info.LobbyId.ToString(), StringComparer.Ordinal).First();
                foreach (var candidate in candidates)
                    if (!ReferenceEquals(candidate.Details, selected.Details)) candidate.Details.Release();
                try
                {
                    _status = "EOS lobby found · joining…";
                    var join = new JoinLobbyOptions { LobbyDetailsHandle = selected.Details, LocalUserId = user, PresenceEnabled = false };
                    var joinResult = await AwaitResultAsync<JoinLobbyCallbackInfo>(callback => _lobby!.JoinLobby(ref join, this,
                        (ref JoinLobbyCallbackInfo info) => callback(ref info)), (ref JoinLobbyCallbackInfo info) => info.ResultCode);
                    GameLogger.Log("eos_lobby_join_result", new { routeId, lobbyId = selected.Info.LobbyId.ToString(), result = joinResult.ToString() });
                    EnsureSuccess(joinResult, "join discovered lobby");
                    var lobbyId = selected.Info.LobbyId.ToString();
                    GameLogger.Log("eos_lobby_reused", new { routeId, lobbyId, existingMembers = selected.Info.MaxMembers - selected.Info.AvailableSlots });
                    return lobbyId;
                }
                finally { selected.Details.Release(); }
            }
        }
        finally { search.Release(); }

        cancellationToken.ThrowIfCancellationRequested();
        var bucketId = GetBucketId(routeId, fingerprint);
        _status = "EOS lobby searching · creating…";
        var create = new CreateLobbyOptions
        {
            LocalUserId = user,
            MaxLobbyMembers = LobbyCapacity,
            PermissionLevel = LobbyPermissionLevel.Publicadvertised,
            PresenceEnabled = false,
            AllowInvites = false,
            DisableHostMigration = false,
            EnableRTCRoom = false,
            BucketId = bucketId
        };
        var createdId = await AwaitLobbyIdAsync(callback => _lobby!.CreateLobby(ref create, this,
            (ref CreateLobbyCallbackInfo info) => callback(ref info)));
        await SetLobbyAttributesAsync(user, createdId, routeId, fingerprint);
        GameLogger.Log("eos_lobby_created", new { routeId, lobbyId = createdId, capacity = LobbyCapacity });

        // Give simultaneous starters time to publish, then converge on the fullest
        // compatible public lobby. The creator is already a member of createdId.
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        var convergenceSearch = CreateRouteSearch(routeId, fingerprint);
        try
        {
            await FindLobbiesAsync(convergenceSearch, user, cancellationToken);
            var convergenceCountOptions = new LobbySearchGetSearchResultCountOptions();
            var resultCount = convergenceSearch.GetSearchResultCount(ref convergenceCountOptions);
            var discovered = new List<(LobbyDetails Details, LobbyDetailsInfo Info)>();
            for (uint i = 0; i < resultCount; i++)
            {
                var details = GetSearchResult(convergenceSearch, i);
                var info = GetLobbyInfo(details);
                if (info.AvailableSlots > 0 && info.PermissionLevel == LobbyPermissionLevel.Publicadvertised)
                    discovered.Add((details, info));
                else details.Release();
            }
            var selected = discovered.OrderByDescending(x => x.Info.MaxMembers - x.Info.AvailableSlots)
                .ThenBy(x => x.Info.LobbyId.ToString(), StringComparer.Ordinal).FirstOrDefault();
            foreach (var item in discovered)
                if (selected.Details is null || !ReferenceEquals(item.Details, selected.Details)) item.Details.Release();
            if (selected.Details is not null)
            {
                var selectedId = selected.Info.LobbyId.ToString();
                try
                {
                    if (!string.Equals(selectedId, createdId, StringComparison.Ordinal))
                    {
                        _status = "EOS lobby found · joining…";
                        var join = new JoinLobbyOptions { LobbyDetailsHandle = selected.Details, LocalUserId = user, PresenceEnabled = false };
                        var joinResult = await AwaitResultAsync<JoinLobbyCallbackInfo>(callback => _lobby!.JoinLobby(ref join, this,
                            (ref JoinLobbyCallbackInfo info) => callback(ref info)), (ref JoinLobbyCallbackInfo info) => info.ResultCode);
                        GameLogger.Log("eos_lobby_join_result", new { routeId, lobbyId = selectedId, result = joinResult.ToString(), flow = "convergence" });
                        EnsureSuccess(joinResult, "join converged lobby");
                        await DestroyLobbyAsync(user, createdId);
                        GameLogger.Log("eos_lobby_converged", new { routeId, discardedLobbyId = createdId, lobbyId = selectedId });
                        return selectedId;
                    }
                }
                finally { selected.Details.Release(); }
            }
        }
        finally { convergenceSearch.Release(); }
        return createdId;
    }

    private static async Task DestroyLobbyAsync(ProductUserId user, string lobbyId)
    {
        var options = new DestroyLobbyOptions { LocalUserId = user, LobbyId = lobbyId };
        var result = await AwaitResultAsync<DestroyLobbyCallbackInfo>(callback => _lobby!.DestroyLobby(ref options, null!,
            (ref DestroyLobbyCallbackInfo info) => callback(ref info)), (ref DestroyLobbyCallbackInfo info) => info.ResultCode);
        if (result != Result.Success && result != Result.NotFound)
            GameLogger.Log("eos_duplicate_lobby_cleanup_failed", new { lobbyId, result = result.ToString() });
    }

    private static LobbySearch CreateRouteSearch(string routeId, string fingerprint)
    {
        if (_lobby is null) throw new InvalidOperationException("EOS Lobby interface is unavailable.");
        var create = new CreateLobbySearchOptions { MaxResults = 100 };
        EnsureSuccess(_lobby.CreateLobbySearch(ref create, out var search), "create lobby search");
        var bucket = new LobbySearchSetParameterOptions
        {
            Parameter = new AttributeData { Key = LobbyInterface.SEARCH_BUCKET_ID, Value = new AttributeDataValue { AsUtf8 = GetBucketId(routeId, fingerprint) } },
            ComparisonOp = ComparisonOp.Equal
        };
        EnsureSuccess(search.SetParameter(ref bucket), "set route compatibility filter");
        return search;
    }

    private static string GetBucketId(string routeId, string fingerprint)
    {
        var material = $"WodraLobby:{MultiplayerProtocol.CurrentVersion}:{routeId}:{fingerprint.ToLowerInvariant()}";
        return "wd-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..32];
    }

    private static Task<Result> FindLobbiesAsync(LobbySearch search, ProductUserId user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = new LobbySearchFindOptions { LocalUserId = user };
        return AwaitResultAsync<LobbySearchFindCallbackInfo>(callback => search.Find(ref options, null!,
            (ref LobbySearchFindCallbackInfo info) =>
            {
                GameLogger.Log("eos_lobby_search_result", new { result = info.ResultCode.ToString() });
                callback(ref info);
            }), (ref LobbySearchFindCallbackInfo info) => info.ResultCode);
    }

    private static LobbyDetails GetSearchResult(LobbySearch search, uint index)
    {
        var options = new LobbySearchCopySearchResultByIndexOptions { LobbyIndex = index };
        EnsureSuccess(search.CopySearchResultByIndex(ref options, out var details), "copy lobby search result");
        return details;
    }

    private static LobbyDetailsInfo GetLobbyInfo(LobbyDetails details)
    {
        var options = new LobbyDetailsCopyInfoOptions();
        EnsureSuccess(details.CopyInfo(ref options, out var info), "read lobby details");
        return info ?? throw new InvalidOperationException("EOS returned empty lobby details.");
    }

    private static async Task SetLobbyAttributesAsync(ProductUserId user, string lobbyId, string routeId, string fingerprint)
    {
        var options = new UpdateLobbyModificationOptions { LocalUserId = user, LobbyId = lobbyId };
        EnsureSuccess(_lobby!.UpdateLobbyModification(ref options, out var modification), "create lobby metadata update");
        try
        {
            AddAttribute(modification, RouteAttribute, routeId);
            AddAttribute(modification, FingerprintAttribute, fingerprint);
            AddAttribute(modification, ProtocolAttribute, MultiplayerProtocol.CurrentVersion.ToString());
            var update = new UpdateLobbyOptions { LobbyModificationHandle = modification };
            var result = await AwaitResultAsync<UpdateLobbyCallbackInfo>(callback => _lobby.UpdateLobby(ref update, null!,
                (ref UpdateLobbyCallbackInfo info) => callback(ref info)), (ref UpdateLobbyCallbackInfo info) => info.ResultCode);
            EnsureSuccess(result, "publish lobby compatibility metadata");
        }
        finally { modification.Release(); }
    }

    private static void AddAttribute(LobbyModification modification, string key, string value)
    {
        var attr = new LobbyModificationAddAttributeOptions
        {
            Attribute = new AttributeData { Key = key, Value = new AttributeDataValue { AsUtf8 = value } },
            Visibility = LobbyAttributeVisibility.Public
        };
        EnsureSuccess(modification.AddAttribute(ref attr), "add lobby attribute " + key);
    }

    private static Task<string> AwaitLobbyIdAsync(Action<RefCallback<CreateLobbyCallbackInfo>> begin)
    {
        var source = new TaskCompletionSource<string>();
        begin((ref CreateLobbyCallbackInfo info) =>
        {
            GameLogger.Log("eos_lobby_create_result", new
            {
                result = info.ResultCode.ToString(),
                success = info.ResultCode == Result.Success,
                lobbyIdPresent = !string.IsNullOrEmpty(info.LobbyId)
            });
            if (info.ResultCode == Result.Success && !string.IsNullOrEmpty(info.LobbyId)) source.TrySetResult(info.LobbyId.ToString());
            else source.TrySetException(new InvalidOperationException("EOS lobby creation failed: " + info.ResultCode));
        });
        return source.Task;
    }

    private void RegisterNotifications(ProductUserId user)
    {
        var lobby = _lobby ?? throw new InvalidOperationException("EOS Lobby interface is unavailable.");
        var connect = _connect ?? throw new InvalidOperationException("EOS Connect interface is unavailable.");
        var authOptions = new AddNotifyAuthExpirationOptions();
        _authExpirationNotification = connect.AddNotifyAuthExpiration(ref authOptions, this, OnAuthExpired);
        var lobbyOptions = new AddNotifyLobbyMemberStatusReceivedOptions();
        _lobbyStatusNotification = lobby.AddNotifyLobbyMemberStatusReceived(ref lobbyOptions, this, OnLobbyMemberStatus);
        var p2p = _p2p ?? throw new InvalidOperationException("EOS P2P interface is unavailable.");
        var requestOptions = new AddNotifyPeerConnectionRequestOptions { LocalUserId = user, SocketId = _socketId };
        _peerRequestNotification = p2p.AddNotifyPeerConnectionRequest(ref requestOptions, this, OnPeerConnectionRequest);
        var peerOptions = new AddNotifyPeerConnectionClosedOptions { LocalUserId = user };
        _peerClosedNotification = p2p.AddNotifyPeerConnectionClosed(ref peerOptions, this, OnPeerClosed);
        var interruptedOptions = new AddNotifyPeerConnectionInterruptedOptions { LocalUserId = user };
        _peerInterruptedNotification = p2p.AddNotifyPeerConnectionInterrupted(ref interruptedOptions, this, OnPeerInterrupted);
        var establishedOptions = new AddNotifyPeerConnectionEstablishedOptions { LocalUserId = user };
        _peerEstablishedNotification = p2p.AddNotifyPeerConnectionEstablished(ref establishedOptions, this, OnPeerEstablished);
    }

    private void OnAuthExpired(ref AuthExpirationCallbackInfo info)
    {
        if (info.LocalUserId?.IsValid() != true || !string.Equals(info.LocalUserId.ToString(), _riderId, StringComparison.Ordinal)) return;
        _joined = false;
        _status = "EOS authentication expired; reconnecting";
        foreach (var member in _members.Keys)
            _events.Enqueue(new RiderLeftMessage { RiderId = member, Reason = "authentication expired" });
        _members.Clear();
        _sequenceGate.Clear();
        lock (RuntimeLock)
        {
            _localUser = null;
            _loginTask = null;
        }
        GameLogger.Log("eos_auth_expired", new
        {
#if DEBUG
            productUserIdHash = EosIdentityDiagnostics.HashProductUserId(_riderId),
#endif
            expired = true
        });
    }

    private void PopulateLobbyMembers(ProductUserId user, string lobbyId)
    {
        var options = new CopyLobbyDetailsHandleOptions { LocalUserId = user, LobbyId = lobbyId };
        var copyResult = _lobby!.CopyLobbyDetailsHandle(ref options, out var details);
#if DEBUG
        GameLogger.Log("eos_debug_lobby_membership_query", new
        {
            lobbyId,
            result = copyResult.ToString(),
            success = copyResult == Result.Success
        });
#endif
        EnsureSuccess(copyResult, "copy joined lobby details");
        try
        {
            _members.Clear();
            var memberCountOptions = new LobbyDetailsGetMemberCountOptions();
            var count = details.GetMemberCount(ref memberCountOptions);
            var distinctMemberIds = new HashSet<string>(StringComparer.Ordinal);
            for (uint i = 0; i < count; i++)
            {
                var memberOptions = new LobbyDetailsGetMemberByIndexOptions { MemberIndex = i };
                var member = details.GetMemberByIndex(ref memberOptions);
                var memberId = member?.ToString();
                if (member is null || string.IsNullOrEmpty(memberId)) continue;
                distinctMemberIds.Add(memberId);
                if (memberId == user.ToString()) continue;
                _members.TryAdd(memberId, 0);
                var acceptDeferredRequest = _peerMembershipGate.AddMember(memberId);
                if (_pendingEstablishedPeers.TryRemove(memberId, out _))
                {
                    _connectedPeers[memberId] = 0;
                    _p2pFailed = false;
                }
#if DEBUG
                GameLogger.Log("eos_debug_outbound_peer_added", new
                {
                    peerIdentityHash = EosIdentityDiagnostics.HashProductUserId(memberId),
                    source = "lobby membership snapshot"
                });
#endif
                if (acceptDeferredRequest && _pendingPeerRequests.TryRemove(memberId, out var pendingSocket))
                {
                    var pendingUser = ProductUserId.FromString(memberId);
                    if (pendingUser?.IsValid() == true) AcceptPeerConnection(pendingUser, pendingSocket);
                }
                SendProtocol(member, new RiderJoinedMessage { Rider = new RiderDescriptor { RiderId = _riderId, DisplayName = _displayName } }, PacketReliability.ReliableOrdered, 1);
            }
#if DEBUG
            var ownerOptions = new LobbyDetailsGetLobbyOwnerOptions();
            var owner = details.GetLobbyOwner(ref ownerOptions);
            GameLogger.Log("eos_debug_lobby_membership", new
            {
                lobbyId,
                result = "success",
                localProductUserIdHash = EosIdentityDiagnostics.HashProductUserId(user.ToString()),
                lobbyOwnerProductUserIdHash = EosIdentityDiagnostics.HashProductUserId(owner?.ToString()),
                reportedMemberCount = count,
                distinctMemberCount = distinctMemberIds.Count,
                distinctMemberIdentityHashes = distinctMemberIds.Select(EosIdentityDiagnostics.HashProductUserId).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                otherMemberCount = _members.Count,
                localIdentityListed = distinctMemberIds.Contains(user.ToString())
            });
#endif
        }
        finally { details.Release(); }
    }

    private void OnLobbyMemberStatus(ref LobbyMemberStatusReceivedCallbackInfo info)
    {
        if (!_joined && string.IsNullOrEmpty(_lobbyId)) return;
        if (!string.Equals(info.LobbyId.ToString(), _lobbyId, StringComparison.Ordinal)) return;
        var memberId = info.TargetUserId?.ToString();
        if (string.IsNullOrEmpty(memberId) || memberId == _riderId) return;
#if DEBUG
        GameLogger.Log("eos_debug_lobby_member_notification", new
        {
            lobbyId = _lobbyId,
            memberIdentityHash = EosIdentityDiagnostics.HashProductUserId(memberId),
            status = info.CurrentStatus.ToString()
        });
#endif
        switch (info.CurrentStatus)
        {
            case LobbyMemberStatus.Joined:
                if (_members.TryAdd(memberId, 0))
                {
                    var acceptDeferredRequest = _peerMembershipGate.AddMember(memberId);
#if DEBUG
                    GameLogger.Log("eos_debug_outbound_peer_added", new
                    {
                        peerIdentityHash = EosIdentityDiagnostics.HashProductUserId(memberId),
                        memberCount = _members.Count + 1,
                        source = "lobby notification"
                    });
#endif
                    if (_pendingEstablishedPeers.TryRemove(memberId, out _))
                    {
                        _connectedPeers[memberId] = 0;
                        _p2pFailed = false;
                    }
                    if (acceptDeferredRequest && _pendingPeerRequests.TryRemove(memberId, out var pendingSocket) && info.TargetUserId?.IsValid() == true)
                        AcceptPeerConnection(info.TargetUserId, pendingSocket);
                    _events.Enqueue(new RiderJoinedMessage { Rider = new RiderDescriptor { RiderId = memberId, DisplayName = "Rider" } });
                    if (info.TargetUserId?.IsValid() == true)
                        SendProtocol(info.TargetUserId, new RiderJoinedMessage { Rider = new RiderDescriptor { RiderId = _riderId, DisplayName = _displayName } }, PacketReliability.ReliableOrdered, 1);
                    _status = $"Connected · EOS lobby {_members.Count + 1} rider(s)";
                }
                break;
            case LobbyMemberStatus.Left:
            case LobbyMemberStatus.Disconnected:
            case LobbyMemberStatus.Kicked:
            case LobbyMemberStatus.Closed:
                if (_members.TryRemove(memberId, out _))
                {
                    _peerMembershipGate.RemoveMember(memberId);
                    _connectedPeers.TryRemove(memberId, out _);
                    _pendingEstablishedPeers.TryRemove(memberId, out _);
                    _pendingPeerRequests.TryRemove(memberId, out _);
                    _lastAcceptedStateAt.TryRemove(memberId, out _);
                    _sequenceGate.Remove(memberId);
                    _events.Enqueue(new RiderLeftMessage { RiderId = memberId, Reason = info.CurrentStatus.ToString() });
                }
                _status = $"Connected · EOS lobby {_members.Count + 1} rider(s)";
                break;
            case LobbyMemberStatus.Promoted:
#if DEBUG
                GameLogger.Log("eos_lobby_owner_migrated", new
                {
                    lobbyId = _lobbyId,
                    newOwnerProductUserIdHash = EosIdentityDiagnostics.HashProductUserId(memberId)
                });
#else
                GameLogger.Log("eos_lobby_owner_migrated", new { lobbyId = _lobbyId });
#endif
                break;
        }
    }

    private void OnPeerConnectionRequest(ref OnIncomingConnectionRequestInfo info)
    {
        var remoteId = info.RemoteUserId?.ToString();
#if DEBUG
        GameLogger.Log("eos_debug_p2p_connection_request", new
        {
            peerIdentityHash = EosIdentityDiagnostics.HashProductUserId(remoteId),
            knownLobbyMember = !string.IsNullOrEmpty(remoteId) && _members.ContainsKey(remoteId)
        });
#endif
        if (string.IsNullOrEmpty(remoteId) || info.RemoteUserId?.IsValid() != true || _localUser is null) return;
        // The lobby-member and incoming-P2P callbacks can arrive in either order.
        // Hold a valid peer request until the lobby confirms that peer.
        if (!_peerMembershipGate.OnIncomingRequest(remoteId))
        {
            _pendingPeerRequests[remoteId] = info.SocketId ?? _socketId;
#if DEBUG
            GameLogger.Log("eos_debug_p2p_request_waiting_for_membership", new
            {
                peerIdentityHash = EosIdentityDiagnostics.HashProductUserId(remoteId),
                socketName = info.SocketId?.SocketName
            });
#endif
            return;
        }
        AcceptPeerConnection(info.RemoteUserId, info.SocketId);
    }

    private void AcceptPeerConnection(ProductUserId remoteUser, SocketId? socketId)
    {
        var remoteId = remoteUser.ToString();
        if (_localUser?.IsValid() != true || !_members.ContainsKey(remoteId)) return;
        var options = new AcceptConnectionOptions { LocalUserId = _localUser, RemoteUserId = remoteUser, SocketId = socketId };
        var result = _p2p!.AcceptConnection(ref options);
        if (result != Result.Success) _p2pFailed = true;
        GameLogger.Log(result == Result.Success ? "eos_p2p_connection_accepted" : "eos_p2p_accept_failed",
            new
            {
#if DEBUG
                peerIdentityHash = EosIdentityDiagnostics.HashProductUserId(remoteId),
#endif
                result = result.ToString(),
                success = result == Result.Success
            });
    }

    private void OnPeerClosed(ref OnRemoteConnectionClosedInfo info)
    {
        var id = info.RemoteUserId?.ToString();
        if (!string.IsNullOrEmpty(id) && _members.ContainsKey(id))
        {
            _connectedPeers.TryRemove(id, out _);
            _lastAcceptedStateAt.TryRemove(id, out _);
            _p2pFailed = true;
            GameLogger.Log("eos_p2p_peer_closed", new
            {
#if DEBUG
                peerIdentityHash = EosIdentityDiagnostics.HashProductUserId(id),
#endif
                reason = info.Reason.ToString()
            });
        }
    }

    private void OnPeerInterrupted(ref OnPeerConnectionInterruptedInfo info)
    {
        var id = info.RemoteUserId?.ToString();
        if (!string.IsNullOrEmpty(id) && _members.ContainsKey(id))
        {
            _connectedPeers.TryRemove(id, out _);
            _lastAcceptedStateAt.TryRemove(id, out _);
            _p2pFailed = true;
            GameLogger.Log("eos_p2p_peer_interrupted", new
            {
#if DEBUG
                peerIdentityHash = EosIdentityDiagnostics.HashProductUserId(id),
#endif
                socket = info.SocketId?.SocketName
            });
        }
    }

    private void OnPeerEstablished(ref OnPeerConnectionEstablishedInfo info)
    {
        var id = info.RemoteUserId?.ToString();
        if (!string.IsNullOrEmpty(id))
        {
            var knownLobbyMember = _members.ContainsKey(id);
            if (knownLobbyMember)
            {
                _connectedPeers[id] = 0;
                _p2pFailed = false;
            }
            else _pendingEstablishedPeers[id] = 0;
#if DEBUG
            GameLogger.Log("eos_p2p_peer_established", new
            {
                peerIdentityHash = EosIdentityDiagnostics.HashProductUserId(id),
                socket = info.SocketId?.SocketName,
                knownLobbyMember,
                deferredUntilMembership = !knownLobbyMember,
                success = true
            });
#else
            if (knownLobbyMember) GameLogger.Log("eos_p2p_peer_established", new { socket = info.SocketId?.SocketName, success = true });
#endif
        }
    }

    private Result? Broadcast(ProtocolMessage message, PacketReliability reliability, byte channel)
    {
        Result? failure = null;
        foreach (var memberId in _members.Keys)
        {
            var remote = ProductUserId.FromString(memberId);
            if (remote?.IsValid() != true) continue;
            var result = SendProtocol(remote, message, reliability, channel);
            if (result != Result.Success && reliability != PacketReliability.UnreliableUnordered)
                failure ??= result;
        }
        return failure;
    }

    private Result SendProtocol(ProductUserId remote, ProtocolMessage message, PacketReliability reliability, byte channel)
    {
        if (_localUser?.IsValid() != true || _p2p is null || remote?.IsValid() != true) return Result.InvalidParameters;
        var bytes = MultiplayerProtocol.Serialize(message);
        if (bytes.Length > MultiplayerProtocol.MaximumMessageBytes) return Result.LimitExceeded;
        var options = new SendPacketOptions
        {
            LocalUserId = _localUser,
            RemoteUserId = remote,
            SocketId = _socketId,
            Channel = channel,
            Data = new ArraySegment<byte>(bytes),
            AllowDelayedDelivery = reliability != PacketReliability.UnreliableUnordered,
            Reliability = reliability,
            DisableAutoAcceptConnection = false
        };
        var result = _p2p.SendPacket(ref options);
#if DEBUG
        if (message is RiderStateMessage && _firstStateAttemptLogged.TryAdd(remote.ToString(), 0))
            GameLogger.Log("eos_debug_first_rider_state_send_attempt", new
            {
                peerIdentityHash = EosIdentityDiagnostics.HashProductUserId(remote.ToString()),
                bytes = bytes.Length,
                channel,
                socketName = SocketName,
                result = result.ToString()
            });
#endif
        if (result == Result.Success && message is RiderStateMessage && _firstStateSendLogged.TryAdd(remote.ToString(), 0))
        {
#if DEBUG
            GameLogger.Log("eos_p2p_first_rider_state_sent", new
            {
                peerIdentityHash = EosIdentityDiagnostics.HashProductUserId(remote.ToString()),
                bytes = bytes.Length,
                channel,
                result = result.ToString()
            });
#endif
        }
        if (result != Result.Success && reliability != PacketReliability.UnreliableUnordered) _p2pFailed = true;
        if (result != Result.Success && reliability != PacketReliability.UnreliableUnordered)
        {
#if DEBUG
            GameLogger.Log("eos_p2p_send_failed", new
            {
                peerIdentityHash = EosIdentityDiagnostics.HashProductUserId(remote.ToString()),
                type = message.Type,
                result = result.ToString()
            });
#else
            GameLogger.Log("eos_p2p_send_failed", new { type = message.Type, result = result.ToString() });
#endif
        }
        return result;
    }

    private void DrainPackets()
    {
        if (_localUser is null || _p2p is null) return;
        var sizeOptions = new GetNextReceivedPacketSizeOptions { LocalUserId = _localUser };
        for (var i = 0; i < 128; i++)
        {
            var sizeResult = _p2p.GetNextReceivedPacketSize(ref sizeOptions, out var packetSize);
            if (sizeResult == Result.NotFound) return;
            if (sizeResult != Result.Success)
            {
                GameLogger.Log("eos_p2p_packet_size_invalid", new { result = sizeResult.ToString(), packetSize });
                return;
            }
            var receivePlan = EosReceiveBufferPlan.ForPacket(packetSize, MultiplayerProtocol.MaximumMessageBytes);
            var buffer = new byte[receivePlan.BufferLength];
            var peer = (ProductUserId)null!;
            var socket = default(SocketId);
            var options = new ReceivePacketOptions
            {
                LocalUserId = _localUser,
                MaxDataSizeBytes = receivePlan.MaxDataSizeBytes
            };
            var result = _p2p.ReceivePacket(ref options, ref peer, ref socket, out var channel, new ArraySegment<byte>(buffer), out var written);
            if (result != Result.Success)
            {
                if (_receiveDiagnosticsLogged.TryAdd("receive-result:" + result, 0))
                    GameLogger.Log("eos_p2p_packet_receive_failed", new
                    {
                        result = result.ToString(), packetSize,
                        bufferLength = buffer.Length,
                        maxDataSizeBytes = options.MaxDataSizeBytes,
                        written,
                        channel,
                        socketName = socket.SocketName,
                        peerIdentityHash = EosIdentityDiagnostics.HashProductUserId(peer?.ToString())
                    });
                continue;
            }
#if DEBUG
            if (!receivePlan.Oversized && written > 0 && written <= packetSize && written <= buffer.Length &&
                Interlocked.CompareExchange(ref _firstSuccessfulReceiveLogged, 1, 0) == 0)
                GameLogger.Log("eos_p2p_first_packet_receive_succeeded", new
                {
                    result = result.ToString(), packetSize,
                    bufferLength = buffer.Length,
                    maxDataSizeBytes = options.MaxDataSizeBytes,
                    written,
                    channel,
                    socketName = socket.SocketName,
                    peerIdentityHash = EosIdentityDiagnostics.HashProductUserId(peer?.ToString())
                });
#endif
            if (receivePlan.Oversized || written == 0 || written > packetSize || written > buffer.Length)
            {
                var reason = receivePlan.Oversized ? "message too large" : "invalid message size";
                if (_receiveDiagnosticsLogged.TryAdd("drop:" + reason, 0))
                    GameLogger.Log("eos_p2p_packet_dropped", new
                    {
                        reason, result = result.ToString(), packetSize,
                        bufferLength = buffer.Length,
                        maxDataSizeBytes = options.MaxDataSizeBytes,
                        written,
                        channel,
                        socketName = socket.SocketName,
                        peerIdentityHash = EosIdentityDiagnostics.HashProductUserId(peer?.ToString())
                    });
                continue;
            }
            var peerId = peer?.ToString();
            if (string.IsNullOrEmpty(peerId) || !_members.ContainsKey(peerId) || socket.SocketName != SocketName)
            {
#if DEBUG
                var reason = string.IsNullOrEmpty(peerId) ? "missing sender" : !_members.ContainsKey(peerId) ? "sender is not a lobby member" : "socket mismatch";
                var peerHash = EosIdentityDiagnostics.HashProductUserId(peerId);
                if (_receiveDiagnosticsLogged.TryAdd($"reject:{peerHash}:{reason}", 0))
                    GameLogger.Log("eos_debug_packet_rejected", new
                {
                    reason,
                    peerIdentityHash = peerHash,
                    channel,
                    socketName = socket.SocketName,
                    bytes = written
                });
#endif
                continue;
            }
            var message = MultiplayerProtocol.Deserialize(buffer.AsSpan(0, checked((int)written)));
            if (message is null)
            {
#if DEBUG
                if (_receiveDiagnosticsLogged.TryAdd("reject:" + EosIdentityDiagnostics.HashProductUserId(peerId) + ":deserialize", 0))
                    GameLogger.Log("eos_debug_packet_rejected", new { reason = "deserialize or protocol version failed", peerIdentityHash = EosIdentityDiagnostics.HashProductUserId(peerId), channel, bytes = written });
#endif
                continue;
            }
#if DEBUG
            if (message is RiderStateMessage && _firstStateWireReceiveLogged.TryAdd(peerId, 0))
                GameLogger.Log("eos_debug_rider_packet_received", new
                {
                    peerIdentityHash = EosIdentityDiagnostics.HashProductUserId(peerId),
                    type = message.Type,
                    channel,
                    bytes = written,
                    socketName = socket.SocketName
                });
#endif
            switch (message)
            {
                case RiderJoinedMessage joined when joined.Rider.RiderId == peerId:
                    _events.Enqueue(joined);
                    break;
                case RiderLeftMessage left when left.RiderId == peerId:
                    _members.TryRemove(peerId, out _);
                    _peerMembershipGate.RemoveMember(peerId);
                    _connectedPeers.TryRemove(peerId, out _);
                    _pendingEstablishedPeers.TryRemove(peerId, out _);
                    _lastAcceptedStateAt.TryRemove(peerId, out _);
                    _sequenceGate.Remove(peerId);
                    _events.Enqueue(left);
                    break;
                case RiderStateMessage state:
                    var validationError = channel != 0 ? "state on wrong channel" :
                        state.RiderId != peerId ? "sender identity mismatch" :
                        MultiplayerProtocol.ValidateState(state, peerId) ??
                        (_sequenceGate.TryAcceptSnapshot(peerId, state.Sequence) ? null : "stale or duplicate sequence");
                    if (validationError is not null)
                    {
#if DEBUG
                        if (_receiveDiagnosticsLogged.TryAdd("reject:" + EosIdentityDiagnostics.HashProductUserId(peerId) + ":" + validationError, 0))
                            GameLogger.Log("eos_debug_packet_rejected", new { reason = validationError, peerIdentityHash = EosIdentityDiagnostics.HashProductUserId(peerId), channel, sequence = state.Sequence });
#endif
                        break;
                    }
                    _lastAcceptedStateAt[peerId] = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
#if DEBUG
                    if (_firstRemoteStateAcceptedLogged.TryAdd(peerId, 0))
                        GameLogger.Log("multiplayer_remote_state_accepted", new
                        {
                            peerIdentityHash = EosIdentityDiagnostics.HashProductUserId(peerId),
                            channel,
                            sequence = state.Sequence,
                            distanceMeters = state.DistanceMeters
                        });
                    if (_firstStateReceiveLogged.TryAdd(peerId, 0))
                        GameLogger.Log("eos_p2p_first_rider_state_received", new
                        {
                            peerIdentityHash = EosIdentityDiagnostics.HashProductUserId(peerId),
                            channel,
                            sequence = state.Sequence
                        });
#endif
                    _events.Enqueue(state);
                    break;
                case RiderRepositionedMessage reposition when channel == 1 && reposition.RiderId == peerId &&
                    MultiplayerProtocol.ValidateReposition(reposition, peerId, _routeId, _routeFingerprint) is null &&
                    _sequenceGate.TryAcceptReposition(peerId, reposition.Sequence):
                    _events.Enqueue(reposition);
                    break;
            }
        }
    }

    private void UnregisterNotifications()
    {
        if (_lobbyStatusNotification != 0) _lobby?.RemoveNotifyLobbyMemberStatusReceived(_lobbyStatusNotification);
        if (_peerRequestNotification != 0) _p2p?.RemoveNotifyPeerConnectionRequest(_peerRequestNotification);
        if (_peerClosedNotification != 0) _p2p?.RemoveNotifyPeerConnectionClosed(_peerClosedNotification);
        if (_peerInterruptedNotification != 0) _p2p?.RemoveNotifyPeerConnectionInterrupted(_peerInterruptedNotification);
        if (_peerEstablishedNotification != 0) _p2p?.RemoveNotifyPeerConnectionEstablished(_peerEstablishedNotification);
        if (_authExpirationNotification != 0) _connect?.RemoveNotifyAuthExpiration(_authExpirationNotification);
        _lobbyStatusNotification = _peerRequestNotification = _peerClosedNotification = _peerInterruptedNotification = _peerEstablishedNotification = _authExpirationNotification = 0;
    }

    private void ClosePeerConnections(ProductUserId? user)
    {
        if (user?.IsValid() != true || _p2p is null) return;
        foreach (var memberId in _members.Keys)
        {
            var remote = ProductUserId.FromString(memberId);
            if (remote?.IsValid() != true) continue;
            var options = new CloseConnectionOptions { LocalUserId = user, RemoteUserId = remote, SocketId = _socketId };
            _p2p.CloseConnection(ref options);
        }
    }

    public static void ShutdownPlatform()
    {
        lock (RuntimeLock)
        {
            if (_shutdown) return;
            if (_platform is null) { _shutdown = true; return; }
            _shutdown = true;
            _platform = null;
            _connect = null;
            _lobby = null;
            _p2p = null;
            _localUser = null;
            try
            {
                var result = PlatformInterface.Shutdown();
                if (result != Result.Success) GameLogger.Log("eos_shutdown_failed", new { result = result.ToString() });
            }
            catch (Exception ex) { GameLogger.Log("eos_shutdown_exception", new { message = ex.Message }); }
        }
    }

    private static void EnsureSuccess(Result result, string operation)
    {
        if (result != Result.Success) throw new InvalidOperationException($"EOS {operation} failed: {result}");
    }
#endif
}
