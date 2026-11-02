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
  feature flag TextChatSettingInfo.IsUseTextChat (ArchiveData.TextChatSetting @0x340). Mapping to home JSON key unverified.
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
