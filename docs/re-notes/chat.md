# Chat (Home global chat / lobby) - research notes (2026-11-01)

Source: dump.cs from worktree discs-balance-audit (.local/re/dump/dump.cs; hashes match base.apk 2.11.0,
libil2cpp sha256 c92a03cc..., metadata 8337973a...). REA/Ghidra provider failed, so these are dump-derived,
NOT REA-verified. Dump is absent from this checkout.

## Transport
- WRITE: HTTP `chatRoomComment/write` (literal 0x4753DE8). Request ChatRoomCommentWriteRequestData
  {chatRoomId:int, comment:string, stampId:int}; response has no fields. Server currently stubs it (`{}`) in
  DemoSessionApi.Stubs.cs, persists nothing.
- READ: NOT via server API. Firebase Realtime Database (`ChatRealtimeDatabase`, `FirebaseManager.SetupChatAsync/GetChannelQuery`,
  URL literal `https://{0}.firebaseio.com/` 0x4752CE8). Auth via Firebase custom token
  (`ArchiveData.ChatRoomConnectionCustomToken`, `ChatRoomConnectionId`, currently served as "" in DemoSessionApi.cs:1685).
  Messages are ChatDatabaseModel {userId,name,comment,stampId,honorId,insertDatetime}; Home toast = HomeChatNotificationView.OnChildAdded.
- Existing prod patch bypasses Firebase chat (0x17331C0 ret, docs/CONTINUATION_PROMPT.md:253) to avoid NRE.

## Limits / gates
- ChatManager.MAX_CHAT_COUNT = 100 (client-side log window), ChatView.MESSAGE_CHARACTER_MAX_COUNT = 140.
- Home button: MatchingHeaderModel.IsChatOpenButtonActive / MatchingUIManager.IsChatOpenButtonActive();
  feature flag TextChatSettingInfo.IsUseTextChat (ArchiveData._textChatSetting @0x50 (CORRECTED: 0x340 is ChatRoomConnectionCustomToken, 0x348 ChatRoomConnectionId)). Mapping to home JSON key unverified.
- Tabs: names come from ChatRoomMaster (ChatRoomMasterData has only `name`); Chatting/Team/Friend/Beginner semantics NOT found in code.
- Stamps: StampType enum (Win/Lose/Draw/TeamResult/Matching) + config/masters_stamp.json; chat uses stampId.
- Translation keys: chatRoomCommentMinLength/MaxLength/NgWord errors, consecutive-post attention.

## Lobby chat
- Photon Chat classes (ChatClient/ChatState) exist in dump; usage as lobby chat unconfirmed. No chat code in LuxonServer.

## Blocker
Read path needs a Firebase RTDB + Firebase Auth endpoint (hosts hard-wired to firebaseio.com / googleapis). A server-only
restore is not possible without either emulating Firebase (client would still target Google hosts) or a client patch. Not done.

## Unknowns
ChatRoomMaster rows/tab semantics, RTDB path layout (GetChannelQuery body), IsUseTextChat JSON key, ring-buffer trimming (client or DB rules).

# MAPPING PHASE (2nd pass) - read path, hook candidates, proposed patch design
Evidence: dump.cs (worktree discs-balance-audit .local/re/dump) + capstone disassembly of pristine libil2cpp.so
(sha256 c92a03cc...). REA/Ghidra provider STILL fails (procedure_info execution_failure, retried). Not REA-verified.
RVA = file offset. NOTE: 0x17331C0 is HomeChatNotificationView.OnCompleteChatSetup (already `ret`-stubbed), NOT SetupChatAsync.

## Read path (Firebase RTDB)
- FirebaseManager.SetupChatAsync 0x17BAD10 -> state machine d__2.MoveNext 0x15E9FFC: GoOnline 0x17BB06C, CheckSignedIn 0x17BB934,
  SignInAsObservable 0x17BB25C (custom token); REQUIRES ArchiveData.ChatRoomConnectionId (get 0x31F514C) non-empty, else returns WITHOUT
  calling onComplete; sets DB url https://{connId}.firebaseio.com/; root = DefaultInstance.GetReference(langCode); error -> onComplete(10006).
  Sole caller: ChatManager.<BeginAsync>d__23.MoveNext 0x31DFDE8 (bl at 0x31DFEB4). (Empty connId currently => ChatManager.BeginAsync never completes.)
- GetChannelReference 0x17BADA8: root.Child(channelId.ToString()). GetChannelQuery 0x17BADFC: ref.OrderByChild("insertDatetime").LimitToLast(count);
  null ref -> null. Path inferred <langCode>/<channelId>/<pushKey>.
- ChatManager (singleton, fields: _view 0x30, _currentChannelId 0x54, _query 0x78, _fetchChats List<ChatDatabaseModel> 0xB8, _lastFetchChatDatetime 0x60,
  LastAddChatDatetime 0x40):
  - Open 0x31DE9C4 (callers: MatchingHeaderView lambda b__18_1 tail 0x14DE54C; HomeChatNotificationView b__7_0 bl 0x1733800).
  - ActiveChannel 0x31DEBA0: GetChannelQuery(channel, count=100) -- `mov w2,#100` at 0x31DEBF8 (the only MAX_CHAT_COUNT use found; ring cap = Firebase LimitToLast(100));
    then _query.add_ChildAdded(OnChildAdded), ChatView.SetTabButtonActive, starts FetchAsync.
  - OnChildAdded 0x31DF5AC: new ChatDatabaseModel.SetData(snapshot); drop if insertDatetime <= _lastFetch (op_LessThanOrEqual 0x250C888); else _fetchChats.Add or ChatView.AddChat.
  - FetchAsync 0x31DF0C8 (d__34.MoveNext 0x31E0700): shows loading, reads DataSnapshot.Children, ChatView.SetVislbleList/ReloadChats, hides loading. Uses Firebase DataSnapshot types.
  - SwitchCurrentChannel 0x31DD238 (from ChatChannelTabButton click lambda, bl 0x31DD21C), DisactiveChannel 0x31DEFF0.
  - WriteChat 0x31DF204: client rate limit (consecutive-post popup), builds ChatRoomCommentWriteRequest (ctor 0x3220CE8) -> NetworkManager.SendRequest 0x1A19E3C.
    WriteComment(int,string,..) 0x31DF594, WriteStamp(int,int,..) 0x31DF184, ChatView.OnSendButtonClicked 0x31E18B8.
- ChatView.AddChat 0x31DF75C (tail -> ChatListView.AddChat 0x31DDBC4); ReloadChats(List<ChatDatabaseModel>) 0x31E09E4 (tail -> ChatListView.ReloadChats 0x31DE2E4);
  Initialize 0x31DFF08 builds tabs from MasterManager.get_ChatRoomMaster 0x1799CD8 (sole caller; ChatChannelTabButton.SetData(ChatRoomMasterData, Action)).
- ChatDatabaseModel (TypeDef 14623): UserId 0x18, Name 0x20, Comment 0x28 (strings), StampId 0x30, HonorId 0x34 (int), InsertDatetime 0x38 (string),
  InsertDatetimeOffset 0x40 (DateTimeOffset). ctor 0x31DD8E4; SetData(DataSnapshot) 0x31DD444 (built from snapshot, not JSON); CompareTo 0x31DD840.
  => To inject we must allocate ChatDatabaseModel objects and fill fields + InsertDatetimeOffset manually (SetData unusable without snapshot).
- ChatRealtimeDatabase 0x31E0B00.. : CreateMessagesList 0x31E0B90 / OnSuccessRead 0x31E0FF4 / OnSuccessReceive 0x31E1110 (used by non-ChatManager path; ChatManager uses raw Query).
- HomeChatNotificationView: Initialize 0x1732E04 (gated by IsUseTextChat at 0x1732E90), OnChildAdded 0x17332F8, PlayComment 0x17334C8, own _query 0x38.
  BattleDisplayView.OnCompletedChatSetup (0x188BBA8) is the caller of OnCompleteChatSetup (in-battle text chat also exists).

## HTTP plumbing
- ChatRoomCommentWriteRequest : RequestBase<ChatRoomCommentWriteData, ...RequestData, ...ResponseData>; get_Url 0x3220CA0 ("chatRoomComment/write");
  ctor(int chatRoomId, string comment, int stampId, Action<UnityWebRequest,Resp> onSuccess, Action<UnityWebRequest> onFailed) 0x3220CE8.
  RequestData fields chatRoomId 0x10, comment 0x18, stampId 0x20. ResponseData has NO fields (0x3220E24 ctor).
- NetworkManager.SendRequest<> (generic, instance 0x1A19E3C). Response deserializer for the success path NOT identified (JsonUtility only seen in GetErrorResponse).
  => UNKNOWN whether adding fields to the response class would be parsed; managed-layer change of the response class is not possible without il2cpp metadata edit anyway.

## Channels / tabs
- ChatRoomMasterData has only `name` (0x18). No tab-type column; no Chatting/Team/Friend/Beginner enum or per-tab filtering found in client code.
  Tabs = rows of ChatRoomMaster (id = chatRoomId = Firebase channel id); filtering, if any, is server-side/Firebase-rule semantics (original). ArchiveData._currentChannelId @0x184.
  Server must serve masters_chat_room (rows unknown; names e.g. Chatting/Team/Friend/Beginner are the user's recollection only). Open to all is the only client-enforced behavior.

## Home button gate
- MatchingUIManager.IsChatOpenButtonActive 0x14DC3BC: Archive.TextChatSetting (get 0x31F48F4) .IsUseTextChat (0x1616578, field 0x10) -> if false exit (0x14DC4B0); then CurrentDisplay + MatchingManager.IsCurrentState checks.
  Same gate: HomeChatNotificationView.Initialize, SettingTextChatCategoryView.Initialize, BattleDisplayView.StartUpdate/OnClosedSetting.
- TextChatSettingInfo is a user setting (settings screen "setting.textChat"), not a master; source JSON key for ArchiveData._textChatSetting NOT identified (likely user-setting response). StartupResponseData has chatRoomConnectionId (0xC8) / chatRoomConnectionCustomToken (0xC0).
  Server must serve those non-empty (any value if SetupChatAsync is bypassed) - connId empty currently hangs BeginAsync.

## Lobby chat
- No game-code use of Photon Chat (ChatGui is the SDK demo). Lobby/matching chat = same ChatManager (button on MatchingHeaderView, ChatManager.Open). Unverified beyond callers above.

## Dead bodies / patch map (scripts/patch-il2cpp-endpoints.py)
- Existing: 0x17331C0 ret stub (OnCompleteChatSetup). Body 0x17331C4..0x17332F8 already hosts non-chat prod caves (0x17331D0, 0x17331F0, 0x1733230, 0x1733260, 0x1733284, 0x17332B0).
- No existing patch touches FirebaseManager/ChatManager/ChatRealtimeDatabase. Free per README: SetDisplayAngles 0x13BC318-0x13BC3A8 (144 B); small gaps need branch-sweep.
- If Firebase is bypassed, FirebaseManager.SetupChatAsync d__2.MoveNext (0x15E9FFC, large), GetChannelQuery, WriteChatAsync 0x17BAE98, ChatRealtimeDatabase.* become candidates but must be proven zero-incoming (xref.py) before reuse. NOT yet done.

## Proposed patch design (NOT implemented)
Goal: chat reads from our server, reuse managed code.
1. Gate: serve chatRoomConnectionId="local" so SetupChatAsync passes id check; patch SetupChatAsync (0x17BAD10) entry: `b` cave that invokes onComplete(0) (Action<FirebaseStatusCode>.Invoke) and returns an empty IEnumerator, or patch d__2.MoveNext to skip sign-in/URL (~10 insns). Need Firebase success code value (0 presumed; unverified).
2. Read: replace ActiveChannel's query/ChildAdded section (0x31DEBA0..) and FetchAsync's DataSnapshot loop with a poll: cave calls a NEW request (needs a new RequestBase subclass: cannot be created from ASM cheaply) OR reuses ChatRoomCommentWriteRequest as poll (comment="", stampId=0, chatRoomId=channel) and returns messages in response -> blocked because ResponseData has no fields and deserializer path is unknown.
   Alternative that avoids client request classes entirely: redirect the Firebase endpoints to our server (Firebase RTDB speaks websocket/long-poll with firebaseio.com host derived from connId -> `https://{connId}.firebaseio.com/`; with connId crafted it still resolves *.firebaseio.com). Needs a host-string patch of the format literal (DNS/TLS to our server) + RTDB wire-protocol emulation + custom-token sign-in at securetoken/identitytoolkit googleapis hosts. Highest server cost, lowest ASM cost, but patches several host literals (like existing serverBaseUrl patching) and needs TLS bypass (already exists per notes).
   Option C (ASM-heavy): cave in ChatManager that, on Open/ActiveChannel/poll tick (ManagedUpdate 0x31DE818), sends ChatRoomCommentWriteRequest-like request and parses via managed JSON; infeasible without a response class carrying data.
3. Write path already works against our /chatRoomComment/write (server persists to ring buffer 100/channel); for options needing RTDB the server would push the written message to RTDB listeners.
Recommendation: Option B (RTDB emulation behind patched host literals) unless a response-with-data path (A/C) is proven; both need further RE: Firebase C# SDK host resolution (FirebaseDatabase uses the `Uri` via set_DatabaseUrl; ws://s-xxx.firebaseio.com), sign-in hosts (identitytoolkit.googleapis.com), and TLS validation.
Risks: Firebase SDK native/managed plugin (libFirebaseCppApp) performs the networking natively (not IL2CPP) -> host literals live in the native .so, cert pinning/TLS unknown; high. Unknown: ring cap server vs client LimitToLast(100) at 0x31DEBF8.
Effort rough: Option A/C ASM: 2-4 days + new request class risk; Option B: 3-6 days (RTDB websocket protocol server + native lib host patch + token auth), uncertain.

# EMULATOR-MODE FEASIBILITY (3rd pass, static only)
Evidence: base.apk 2.11.0 sha256 ffa620d2...; REA search_strings failed again (no Evidence IDs); Python fallback.

## Verdict: NO-GO for Firebase useEmulator smali patch. Redirect-by-URL route = UNPROVEN (needs runtime test).
- Native: libFirebaseCppApp-6_11_0.so (4.27MB), libFirebaseCppAuth.so, libFirebaseCppDatabase.so (+Analytics/Crashlytics/InstanceId/Messaging).
  Java: firebase-database 19.2.1, firebase-auth 19.2.0, firebase-common 19.3.0. Emulator APIs arrived in database 19.4.0 / auth 20.0.0 => absent.
- Dex FirebaseDatabase: getInstance(), (FirebaseApp), (FirebaseApp,String), (String), getReference, goOnline/Offline, setLogLevel, setPersistence*. FirebaseAuth: getInstance,
  signInWithCustomToken(String), getAccessToken. DatabaseConfig has no host override. Literal search useEmulator / 10.0.2.2: 0 hits in dex and Firebase .so.
- JNI refs to com/google/firebase/database/FirebaseDatabase and auth/FirebaseAuth live in libFirebaseCppApp (not the Database/Auth libs; which lib calls getInstance(app,url) unconfirmed).
  libFirebaseCppDatabase exports Database::GetInstance(App*, const char* url, InitResult*) -> explicit URL accepted.
- Manifest: FirebaseInitProvider (initOrder 100), Application = io.repro.android.UnityApplication, usesCleartextTraffic=true, network_security_config base-config cleartext permitted (any host).
- App config: firebase_database_url=https://colorful-production.firebaseio.com, project_id=colorful-production, google_app_id 1:475128159434:android:b07cdc4bc1b8561b, package jp.grenge.kickflight;
  assets/google-services-desktop.json matches. FirebaseApp init without Play Services: UNKNOWN.
- URL: metadata literal "https://{0}.firebaseio.com/" (metadata off 0x864B5), {0} = ChatRoomConnectionId (SetupChatAsync). Also "guest/{0}/host" (invite DB).
- Auth: managed FirebaseAuthManager.SignInByCustomTokenAsync(customToken) <- SignInAsObservable/CheckSignedIn; token = ArchiveData.ChatRoomConnectionCustomToken.
  Java SDK 19.x signInWithCustomToken talks to Google identitytoolkit/securetoken over HTTPS; no host literal found in dex => host construction UNKNOWN (likely hard-coded in obfuscated/gms-free
  firebase-auth REST code not visible by string search). Without emulator support, auth cannot be redirected cleanly; needs real signed custom token or an unauthenticated RTDB (rules open) path - UNPROVEN.
- FirebaseStatusCode: Success=0, NotSignedIn=10000, AlreadySignedIn=10001, CustomTokenExpired=10002, SignInFailed=10003, WritingFailed=10004, ReadingFailed=10005,
  FirebaseSdkNotAvailable=10006, TimeOutWithRetry=10007. (So SetupChatAsync error 10006 = SdkNotAvailable; success callback value 0.)
- IsUseTextChat: TextChatSettingInfo._isUseTextChat / ArchiveData._textChatSetting are [SerializeField]; JSON key = field name presumed (_textChatSetting?) - serializer unknown.
- GetChannelQuery root/field literals and langCode source: NOT resolved. Build-pipeline smali hooks: not inspected.

## Remaining candidate routes (all need runtime proof)
1. Patch format literal "https://{0}.firebaseio.com/" (global-metadata.dat string, same length constraint or repoint) so Database::GetInstance gets e.g. http://<host>:<port>?ns=<id>;
   Java 19.2.1 parses Repo info with host/secure/namespace; plain ws possible if URL scheme http. Auth step still hits Google => skip auth by patching SetupChatAsync MoveNext
   to not require sign-in (RTDB with open rules). Server = minimal RTDB websocket server (handshake, listen q/n, put/merge pushes) in C#. Needs one runtime experiment first.
2. Smali: wrap FirebaseDatabase.getInstance(FirebaseApp,String) in dex to rewrite URL to our host (no metadata edit; keeps IL2CPP untouched). Cleartext already allowed. Candidate smali point: FirebaseDatabase.getInstance(FirebaseApp,String) in classes*.dex.
3. Server effort: custom RTDB wire-protocol subset (connect handshake, 'q' listen with orderBy=insertDatetime limitToLast=100, 'd' data/'m' merge pushes, ping) ~3-5 days; official Emulator Suite needs Node+Java, same wire protocol but needs real Auth => not applicable (no emulator client mode).
Risks: Java SDK wire details (ws path /.ws?v=5&ns=), Play Services-less init, iOS not covered, Auth still on Google unless bypassed.
Next step before any decision: runtime experiment on emulator (Frida/logcat of FirebaseDatabase Repo connection host) with a stub websocket server.

# RUNTIME EXPERIMENT (4th pass, 2026-11-02, emulator-5554 API35, KF_CHAT_EXP=1 build)
Build: scripts/patch-il2cpp-endpoints.py KF_CHAT_EXP=1 => (a) metadata literal "https://{0}.firebaseio.com/" -> "http://10.0.2.2:5099?ns={0}" (27 B, in place, unique at 0x864A9);
(b) native 0x17BB934 FirebaseManager.CheckSignedIn -> `mov w0,#1; ret` (guard 0860403968000034). Server: KF_CHAT_EXP=1 serves chatRoomConnectionId="kfchat" + dummy token;
new master "ChatRoom" (config/masters_chat_room.json: 1 Chatting, 2 Team, 3 Friend, 4 Beginner). Stub: scripts/chat_rtdb_stub.py (logs frames). APK built via .local/build-fedora.sh,
re-signed with the key matching the installed cert (discs-balance-audit .jks, sha256 4b0793de...). Logs: .local/run/logcat-chat-exp.txt, chat-exp-stub.log, chat-exp-api.log.

## Findings (observed at runtime)
- Firebase initializes WITHOUT Play Services; no crash/exception during startup, home load or chat open. Java SDK 19.2.1 parses http://10.0.2.2:5099?ns=kfchat as namespace kfchat, secure=false, and
  connects plain ws: `GET /.ws?ns=kfchat&v=5` (+ `&ls=<session>` on reconnect), UA `Firebase/5/19.2.1/35/Android`. Connection happens right after login (SetupChatAsync) and the stub sees
  {"t":"d","d":{"a":"s","r":0,"b":{"c":{"sdk.android.19-2-1":1}}}} (stats); NO auth frame (sign-in skipped). Idle connection closed by server after ~60 s; SDK reconnects on demand.
- Home chat button APPEARS (bottom-left speech bubble) with chatRoomConnectionId set; IsUseTextChat gate passed with default settings (no server field needed for a new account).
- Opening chat: tabs Chatting/Team/Friend/Beginner render from the ChatRoom master (so master name "ChatRoom" and row shape {id,name} are correct); input bar + stamp button + Send render.
- Listen request on open (GetChannelQuery strings resolved): `{"t":"d","d":{"a":"q","r":N,"b":{"p":"en/1","q":{"vf":"r","i":"insertDatetime","l":100},"t":TAG,"h":""}}}`
  => path = "<langCode>/<chatRoomId>" ("en" here), orderBy child insertDatetime, limitToLast 100, tag TAG. One listen per open/switch; no get/unlisten seen.
- Sending a message from the client issues POST /chatRoomComment/write (confirmed in API log); nothing is written to RTDB by the client.
- Handshake reply `{"t":"c","d":{"t":"h","d":{"ts":..,"v":"5","h":"<host>","s":"<sid>"}}}` and `{"r":N,"b":{"s":"ok","d":{}}}` are accepted by the SDK. Data push needs the query tag (`b.t`).
- FirebaseStatusCode.Success=0; SetupChatAsync success path never calls onComplete (ChatManager passes null).
- Not crashed anywhere (no fault addr / SIGSEGV).

## Not resolved
- Fake messages did NOT render, in 3 variants: keys "-Kfake..." (non-integer) / "1001","1002" (CreateMessagesList does Int32.Parse(key)); insertDatetime ISO "+09:00", "yyyy-MM-dd HH:mm:ss",
  ISO with real offset and 1 h in the past; tagged push. No exception logged. Suspects: ChatDatabaseModel.SetData value types/DateTime parse format (0x31DD444), FetchAsync/OnChildAdded
  datetime threshold direction (0x31DF5AC), lists reload only on Open vs push timing. REA was down (provider timeouts), so SetData/FetchAsync were not disassembled in this pass.
  Next: disassemble SetData + FetchAsync d__34.MoveNext (0x31E0700) and OnChildAdded (non-REA a64dis is acceptable per AGENTS.md), then fix the stub payload; or test pushing after open (child_added events, "a":"d" with partial path en/1/<key>).
- Tab semantics, IsUseTextChat JSON key (not needed for new accounts), lobby chat distinct from Home button (Matching screen button = same ChatManager.Open).

## Next steps for real C# RTDB server (prod wss)
Implement /.ws endpoint (WebSocket, ns query param; reply handshake; ack every request with r; handle a(listen q with path/params/tag, n unlisten), push `d` with tag;
`s` stats ack; ping "0"). Store messages per "<lang>/<chatRoomId>" ring buffer (100) written by /chatRoomComment/write, and broadcast child additions to listeners
(`{"a":"d","b":{"p":"en/1/<key>","d":{...}}}` once message format is confirmed). Prod: change format literal to a fixed wss host form that Utilities.parseUrl accepts
(careful: when URL has no explicit port the SDK forces secure=true regardless of scheme - fine for wss) and keep ns from query. Patch CheckSignedIn (or serve a verifiable
token path) in prod build behind a flag.
