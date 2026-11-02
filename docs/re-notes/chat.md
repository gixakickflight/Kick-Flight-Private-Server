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
